using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using MatchingRejectEvent = B3.Exchange.Matching.RejectEvent;
using MatchingSide = B3.Exchange.Matching.Side;

namespace B3.Exchange.Core;

/// <summary>
/// Write-Ahead Log facet of <see cref="ChannelDispatcher"/> (issue
/// #269). Owns the <c>_wal</c> append/truncate hooks consumed by
/// <c>ProcessOne</c> / <c>OnAfterCommandFlushed</c>, the boot-time
/// replay path, and the no-op <see cref="IUmdfPacketSink"/> /
/// <see cref="ICoreOutbound"/> adapters that suppress side effects
/// while replaying.
/// </summary>
public sealed partial class ChannelDispatcher
{
    /// <summary>
    /// Returns the next sequence number for a state-mutating command
    /// and, when a WAL is wired and we are not currently replaying,
    /// appends the corresponding <see cref="WalRecord"/> before the
    /// engine observes the command. Called from <c>ProcessOne</c>
    /// before each <c>WorkKind.New</c> / <c>Cancel</c> /
    /// <c>Replace</c> branch.
    /// </summary>
    private void WalAppendIfEnabled(in WorkItem item)
    {
        // Always advance the applied counter so snapshot.LastAppliedSeq
        // is meaningful even when WAL is disabled (lets a future WAL
        // turn-on observe a sane "starting point").
        _lastAppliedSeq++;
        if (_wal is null || _replayMode) return;
        WalRecord? rec = item.Kind switch
        {
            WorkKind.New when item.NewOrder is not null => new WalRecord(
                _lastAppliedSeq, WalRecordKind.NewOrder,
                item.Session.Value ?? string.Empty, item.Firm,
                item.ClOrdId, item.OrigClOrdId,
                item.NewOrder, null, null),
            WorkKind.Cancel when item.Cancel is not null => new WalRecord(
                _lastAppliedSeq, WalRecordKind.Cancel,
                item.Session.Value ?? string.Empty, item.Firm,
                item.ClOrdId, item.OrigClOrdId,
                null, item.Cancel, null),
            WorkKind.Replace when item.Replace is not null => new WalRecord(
                _lastAppliedSeq, WalRecordKind.Replace,
                item.Session.Value ?? string.Empty, item.Firm,
                item.ClOrdId, item.OrigClOrdId,
                null, null, item.Replace),
            _ => null,
        };
        if (rec is null) return;
        try
        {
            // Best-effort byte accounting: Append re-serializes the
            // record internally; we approximate the byte count from
            // the JSON serializer here so the metric stays meaningful
            // without requiring the Append signature to leak the size.
            long approxBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(rec).Length + 1;
            _wal.Append(rec);
            _metrics?.IncWalAppend(approxBytes);
        }
        catch (Exception ex)
        {
            // WAL append failure is a durability fault but not a
            // correctness fault: log and continue. The dispatcher
            // crash counter is bumped so operators alert on it; the
            // command itself still runs so live consumers see no gap.
            _metrics?.IncDispatcherCrashes();
            _logger.LogError(ex,
                "channel {ChannelNumber}: WAL append failed (seq={Seq} kind={Kind}); command will run but is not durable",
                ChannelNumber, _lastAppliedSeq, item.Kind);
        }
    }

    /// <summary>
    /// Truncates the WAL after a successful synchronous snapshot
    /// persist. Called from <c>OnAfterCommandFlushed</c> on the
    /// dispatch thread.
    /// </summary>
    private void TruncateWalAfterSyncSave()
    {
        if (_wal is null) return;
        try
        {
            _wal.Truncate();
            _metrics?.IncWalTruncation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "channel {ChannelNumber}: WAL truncation failed after snapshot save",
                ChannelNumber);
        }
    }

    /// <summary>
    /// Post-save callback invoked by <see cref="BackgroundSnapshotWriter"/>
    /// on the writer thread when async-writer mode is enabled. Truncates
    /// the WAL on that thread so the on-disk WAL only loses records that
    /// the matching snapshot has already absorbed.
    /// </summary>
    private void OnAsyncSnapshotSaved(ChannelStateSnapshot _)
    {
        if (_wal is null) return;
        try
        {
            _wal.Truncate();
            _metrics?.IncWalTruncation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "channel {ChannelNumber}: WAL truncation failed after async snapshot save",
                ChannelNumber);
        }
    }

    /// <summary>
    /// Reads the WAL and replays any record whose seq is greater than
    /// the snapshot's <c>LastAppliedSeq</c>. Runs on the dispatch
    /// thread immediately after <c>RestoreChannelState</c> (or, when
    /// no snapshot existed, against an empty engine — the WAL by
    /// itself is enough to rebuild a fresh book). Each replayed
    /// command goes through the normal <c>ProcessOne</c> path so the
    /// engine's state-machine and counter advances exactly match the
    /// pre-crash sequence; the no-op sinks ensure no UMDF packet or
    /// ExecutionReport reaches the wire.
    /// </summary>
    private void ReplayWalOnLoopThread(long snapshotLastAppliedSeq)
    {
        if (_wal is null) return;
        IReadOnlyList<WalRecord> records;
        try { records = _wal.ReadAll(); }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "channel {ChannelNumber}: WAL ReadAll failed — skipping replay",
                ChannelNumber);
            return;
        }
        if (records.Count == 0) return;
        int replayed = 0;
        int skipped = 0;
        _replayMode = true;
        _packetSink = NoOpPacketSink;
        _outbound = NoOpOutbound;
        try
        {
            foreach (var rec in records)
            {
                if (rec.Seq <= snapshotLastAppliedSeq) { skipped++; continue; }
                var item = TryBuildReplayWorkItem(rec);
                if (item is null) continue;
                try
                {
                    ProcessOne(item);
                    replayed++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "channel {ChannelNumber}: WAL replay record seq={Seq} kind={Kind} threw; continuing",
                        ChannelNumber, rec.Seq, rec.Kind);
                }
            }
        }
        finally
        {
            _replayMode = false;
            _packetSink = _liveSink;
            _outbound = _liveOutbound;
        }
        if (replayed > 0)
        {
            _metrics?.AddWalReplays(replayed);
            // After replay the live counter must match the highest seq
            // we just applied — _lastAppliedSeq has been advanced by
            // ProcessOne via WalAppendIfEnabled (which still increments
            // the counter even in replay mode for a consistent view).
        }
        _logger.LogInformation(
            "channel {ChannelNumber}: WAL replay complete — replayed={Replayed} skipped={Skipped} (snapshotLastAppliedSeq={SnapshotSeq})",
            ChannelNumber, replayed, skipped, snapshotLastAppliedSeq);
    }

    private static WorkItem? TryBuildReplayWorkItem(WalRecord rec)
    {
        var session = new SessionId(rec.SessionValue);
        return rec.Kind switch
        {
            WalRecordKind.NewOrder when rec.NewOrder is not null => new WorkItem(
                WorkKind.New, session, rec.Firm, HasSession: rec.SessionValue.Length > 0,
                rec.ClOrdId, rec.OrigClOrdId, rec.NewOrder, null, null, null),
            WalRecordKind.Cancel when rec.Cancel is not null => new WorkItem(
                WorkKind.Cancel, session, rec.Firm, HasSession: rec.SessionValue.Length > 0,
                rec.ClOrdId, rec.OrigClOrdId, null, rec.Cancel, null, null),
            WalRecordKind.Replace when rec.Replace is not null => new WorkItem(
                WorkKind.Replace, session, rec.Firm, HasSession: rec.SessionValue.Length > 0,
                rec.ClOrdId, rec.OrigClOrdId, null, null, rec.Replace, null),
            _ => null,
        };
    }

    /// <summary>
    /// <see cref="IUmdfPacketSink"/> implementation that drops every
    /// packet on the floor. Installed as <c>_packetSink</c> for the
    /// duration of WAL replay so engine-emitted UMDF frames never
    /// reach the multicast group.
    /// </summary>
    private sealed class NoOpPacketSinkImpl : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }

    /// <summary>
    /// <see cref="ICoreOutbound"/> implementation that drops every
    /// ExecutionReport. Installed as <c>_outbound</c> for the
    /// duration of WAL replay so the gateway never tries to deliver
    /// historical ERs to long-gone sessions.
    /// </summary>
    private sealed class NoOpCoreOutboundImpl : ICoreOutbound
    {
        public bool WriteExecutionReportNew(SessionId session, uint enteringFirm, ulong clOrdIdValue,
            in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue) => true;
        public bool WriteExecutionReportTrade(SessionId session, in TradeEvent e, bool isAggressor,
            long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId ownerSession, ulong ownerClOrdId,
            long restingOrderId, in TradeEvent e, long leavesQty, long cumQty) => true;
        public bool WriteExecutionReportPassiveCancel(SessionId ownerSession, ulong ownerClOrdId,
            long orderId, in OrderCanceledEvent e, ulong requesterClOrdIdOrZero,
            ulong receivedTimeNanos = ulong.MaxValue) => true;
        public bool WriteExecutionReportModify(SessionId session, long securityId, long orderId,
            ulong clOrdIdValue, ulong origClOrdIdValue,
            MatchingSide side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos,
            uint rptSeq, ulong receivedTimeNanos = ulong.MaxValue) => true;
        public bool WriteExecutionReportReject(SessionId session, in MatchingRejectEvent e,
            ulong clOrdIdValue) => true;
    }
}
