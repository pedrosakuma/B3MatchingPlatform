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
    ///
    /// <para>Returns <c>true</c> when the caller may safely execute
    /// the command. Returns <c>false</c> only when the configured
    /// <see cref="WalAppendFailurePolicy"/> is
    /// <see cref="WalAppendFailurePolicy.Halt"/> AND the append
    /// threw — the command must be skipped (no engine mutation,
    /// no UMDF emission, no ExecutionReport) and the channel is
    /// permanently marked WAL-halted (issue #286).</para>
    ///
    /// <para><b>Determinism invariant (issue #287):</b> the engine state
    /// produced by feeding a command stream through this method must be a
    /// pure function of that stream — no wall-clock reads (each
    /// <c>WalRecord</c> carries the original <c>EnteredAtNanos</c>), no
    /// RNG, no externally-injected sequence numbers. This is what makes
    /// the snapshot-fallback recovery path safe: loading an older
    /// snapshot at sequence <c>K</c> and replaying records
    /// <c>(K, N]</c> from the WAL must converge to the same state as
    /// applying <c>[1..N]</c> from a clean engine. Guarded by
    /// <c>WalReplayIdempotencyTests</c>.</para>
    /// </summary>
    private bool WalAppendIfEnabled(in WorkItem item)
    {
        // Always advance the applied counter so snapshot.LastAppliedSeq
        // is meaningful even when WAL is disabled (lets a future WAL
        // turn-on observe a sane "starting point").
        _lastAppliedSeq++;
        if (_wal is null || _replayMode) return true;
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
        if (rec is null) return true;
        try
        {
            // Single serialization (issue: WAL hot-path double serialize).
            // The dispatcher previously called SerializeToUtf8Bytes here
            // *only* to get the byte count, then Append re-serialized — so
            // we return the on-disk bytes from this single call.
            long approxBytes = _wal.Append(rec);
            _metrics?.IncWalAppend(approxBytes);
            _metrics?.SetWalSizeBytes(_wal.CurrentSizeBytes);
            _metrics?.SetWalDropsOnFull(_wal.DropsOnFullCount);
            return true;
        }
        catch (WalSizeCapExceededException ex)
        {
            // Issue #291: cap breach is a configuration/capacity bug,
            // not a transient IO fault — always halt regardless of
            // _walAppendFailurePolicy. We never want to silently
            // degrade durability when the operator has explicitly
            // opted into a hard cap.
            _metrics?.IncWalAppendFailure();
            _metrics?.IncDispatcherCrashes();
            _metrics?.SetWalSizeBytes(_wal.CurrentSizeBytes);
            Volatile.Write(ref _walHalted, 1);
            _logger.LogCritical(ex,
                "channel {ChannelNumber}: WAL size cap exceeded (seq={Seq} kind={Kind} current={CurrentBytes}B max={MaxBytes}B); channel marked unhealthy and command refused; restart the host after raising the cap or resolving the snapshot fault",
                ChannelNumber, _lastAppliedSeq, item.Kind, ex.CurrentSizeBytes, ex.MaxBytes);
            return false;
        }
        catch (Exception ex)
        {
            // Issue #286: track the WAL-specific append failure
            // separately from the generic dispatcher crash counter so
            // alert routing can target the persistence on-call story.
            _metrics?.IncWalAppendFailure();
            _metrics?.IncDispatcherCrashes();
            if (_walAppendFailurePolicy == WalAppendFailurePolicy.Halt)
            {
                // Sticky: the first failure flips the channel to
                // halted; every subsequent producer-side enqueue and
                // every readiness probe observe IsWalHealthy=false
                // until the host is restarted. The command is not
                // executed — the engine MUST NOT observe a mutation
                // that did not reach the WAL, otherwise replay would
                // diverge from the live consumer view.
                Volatile.Write(ref _walHalted, 1);
                _logger.LogCritical(ex,
                    "channel {ChannelNumber}: WAL append failed (seq={Seq} kind={Kind}); WAL-halt policy → channel marked unhealthy and command refused; restart the host after resolving the storage fault",
                    ChannelNumber, _lastAppliedSeq, item.Kind);
                return false;
            }
            // Continue policy (default, pre-#286 behaviour): durability
            // fault but not a correctness fault; let the command run so
            // live consumers see no gap. Operators alert on the new
            // exch_wal_append_failures_total counter.
            _logger.LogError(ex,
                "channel {ChannelNumber}: WAL append failed (seq={Seq} kind={Kind}); command will run but is not durable",
                ChannelNumber, _lastAppliedSeq, item.Kind);
            return true;
        }
    }

    /// <summary>
    /// Truncates the WAL after a successful synchronous snapshot
    /// persist. Called from <c>OnAfterCommandFlushed</c> on the
    /// dispatch thread.
    ///
    /// <para>Issue #329 PR-4: gated on the post-trade audit watermark.
    /// The truncate proceeds only when every WAL record up to
    /// <paramref name="snapshotLastAppliedSeq"/> has produced trades
    /// that are fsync'd to the audit log. If not, the truncate is
    /// deferred — the WAL keeps growing until the next snapshot, at
    /// which point we try again. The no-op audit sink reports
    /// <see cref="long.MaxValue"/> so this is a no-op gate when audit
    /// is disabled (default).</para>
    /// </summary>
    private void TruncateWalAfterSyncSave(long snapshotLastAppliedSeq)
    {
        // Issue #329 PR-4 / #352 follow-up: the audit-log Checkpoint
        // (fsync .log/.idx + write watermark sidecar) MUST run after
        // every successful snapshot save regardless of whether a WAL
        // is configured — otherwise audit-only channels never fsync
        // their per-trade data and never advance the durability
        // watermark. The WAL truncation itself remains gated on _wal
        // being non-null AND the watermark covering the snapshot seq.
        bool watermarkOk = CheckpointAndGateAuditWatermark(snapshotLastAppliedSeq, async: false);
        if (_wal is null) return;
        if (!watermarkOk) return;
        try
        {
            _wal.Truncate();
            _metrics?.IncWalTruncation();
            _metrics?.SetWalSizeBytes(_wal.CurrentSizeBytes);
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
    ///
    /// <para>Issue #329 PR-4: gated on the audit watermark (see
    /// <see cref="TruncateWalAfterSyncSave(long)"/>). The audit sink's
    /// Checkpoint is safe to call cross-thread.</para>
    /// </summary>
    private void OnAsyncSnapshotSaved(ChannelStateSnapshot snap)
    {
        // Issue #329 PR-4 / #352 follow-up: Checkpoint the audit sink
        // even when no WAL is configured (see TruncateWalAfterSyncSave
        // above for the same rationale). The audit sink's Checkpoint
        // is safe to call cross-thread.
        bool watermarkOk = CheckpointAndGateAuditWatermark(snap.LastAppliedSeq, async: true);
        if (_wal is null) return;
        if (!watermarkOk) return;
        try
        {
            // Issue #348: prefix-truncate, NOT full truncate. Between
            // BackgroundSnapshotWriter.Submit(snap) and this onSaved
            // callback firing, the dispatch thread may have appended
            // records with Seq > snap.LastAppliedSeq that are NOT
            // covered by the just-saved snapshot. A full Truncate()
            // would drop them and a subsequent crash would lose them.
            _wal.TruncateThrough(snap.LastAppliedSeq);
            _metrics?.IncWalTruncation();
            _metrics?.SetWalSizeBytes(_wal.CurrentSizeBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "channel {ChannelNumber}: WAL truncation failed after async snapshot save",
                ChannelNumber);
        }
    }

    /// <summary>
    /// Issue #329 PR-4 audit-watermark gate. Forces an audit-log
    /// <c>Checkpoint</c> (fsync) and returns true iff the watermark
    /// covers <paramref name="snapshotLastAppliedSeq"/>. On Checkpoint
    /// failure (I/O fault) the watermark stays put and we deny the
    /// truncate — the WAL keeps the records the audit log might not have.
    /// </summary>
    private bool CheckpointAndGateAuditWatermark(long snapshotLastAppliedSeq, bool async)
    {
        try
        {
            _postTradeSink.Checkpoint();
        }
        catch (Exception ex)
        {
            _metrics?.IncAuditWalTruncateDeferred();
            _logger.LogError(ex,
                "channel {ChannelNumber}: audit log Checkpoint failed ({Path}); WAL truncation deferred (snapshotSeq={SnapshotSeq})",
                ChannelNumber, async ? "async" : "sync", snapshotLastAppliedSeq);
            return false;
        }
        long durable = _postTradeSink.DurableThroughCommandSeq;
        if (durable < snapshotLastAppliedSeq)
        {
            _metrics?.IncAuditWalTruncateDeferred();
            _logger.LogWarning(
                "channel {ChannelNumber}: WAL truncation deferred — audit watermark behind snapshot (durable={Durable}, snapshotSeq={SnapshotSeq}, path={Path})",
                ChannelNumber, durable, snapshotLastAppliedSeq, async ? "async" : "sync");
            return false;
        }
        return true;
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
    ///
    /// <para><b>Idempotency (issue #287):</b> any (snapshot, WAL-tail)
    /// split must converge to the same final state as the original
    /// command stream. The invariant is asserted by
    /// <c>WalReplayIdempotencyTests</c> across multiple seeded
    /// sequences and split points; see also the
    /// <see cref="WalAppendIfEnabled"/> determinism contract.</para>
    /// </summary>
    private void ReplayWalOnLoopThread(long snapshotLastAppliedSeq)
    {
        if (_wal is null) return;
        IReadOnlyList<WalRecord> records;
        try { records = _wal.ReadAll(); }
        catch (B3.Exchange.Core.WalCorruptionException ex)
        {
            // Issue #285 follow-up (gpt-5.5 review of PR #293): a
            // mid-stream WAL corruption — distinct from a torn final
            // write — would force replay to fabricate a book state
            // that never existed if we silently skipped past the bad
            // record. Halt the channel via the issue #286 mechanism
            // instead. Operators see IsWalHealthy=false on /readyz and
            // a CRITICAL log line; the engine remains at the snapshot
            // baseline (no partial replay).
            _metrics?.IncWalAppendFailure();
            _metrics?.AddWalRecordCorruptions(_wal.LastReadCorruptCount);
            _metrics?.AddWalRecordsLegacy(_wal.LastReadLegacyCount);
            Volatile.Write(ref _walHalted, 1);
            _logger.LogCritical(ex,
                "channel {ChannelNumber}: WAL corruption detected at line {RecordNumber} during boot replay; channel marked WAL-halted, engine left at snapshot baseline (LastAppliedSeq={SnapshotSeq})",
                ChannelNumber, ex.RecordNumber, snapshotLastAppliedSeq);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "channel {ChannelNumber}: WAL ReadAll failed — skipping replay",
                ChannelNumber);
            return;
        }
        if (records.Count == 0)
        {
            // Even an empty record list can carry post-read counters
            // (e.g. a WAL composed entirely of corrupt records).
            _metrics?.AddWalRecordCorruptions(_wal.LastReadCorruptCount);
            _metrics?.AddWalRecordsLegacy(_wal.LastReadLegacyCount);
            return;
        }
        int replayed = 0;
        int skipped = 0;
        _replayMode = true;
        // Issue #329 PR-5: snapshot the audit sink's durability watermark
        // BEFORE replay so OnTrade can gate duplicate emissions. We read
        // the value here (not lazily inside OnTrade) so any later sink
        // mutation by the replay itself cannot move the bar. A value of
        // 0 (no sidecar / fresh install / sink without persistence)
        // means every replayed trade is re-emitted — the conservative
        // pre-PR-5 behaviour.
        _bootAuditDurableSeq = _postTradeSink.DurableThroughCommandSeq;
        _packetSink = NoOpPacketSink;
        _outbound = NoOpOutbound;
        try
        {
            bool boundaryChecked = false;
            foreach (var rec in records)
            {
                if (rec.Seq <= snapshotLastAppliedSeq) { skipped++; continue; }
                if (!boundaryChecked)
                {
                    boundaryChecked = true;
                    // Issue #285 follow-up (gpt-5.5 round-2 review of PR
                    // #298): FileChannelWriteAheadLog.ReadAll only
                    // enforces contiguity among records physically
                    // present in the WAL — it cannot detect a gap
                    // straddling the snapshot/WAL boundary (e.g.
                    // snapshot LastAppliedSeq=2 with WAL starting at
                    // Seq=4). Replaying that record would fabricate
                    // state that never existed, the same failure mode
                    // PR #298 made fatal. Halt the channel here.
                    long expected = snapshotLastAppliedSeq + 1;
                    if (rec.Seq != expected)
                    {
                        _metrics?.IncWalAppendFailure();
                        _metrics?.AddWalRecordCorruptions(_wal.LastReadCorruptCount);
                        _metrics?.AddWalRecordsLegacy(_wal.LastReadLegacyCount);
                        Volatile.Write(ref _walHalted, 1);
                        _logger.LogCritical(
                            "channel {ChannelNumber}: WAL replay aborted — snapshot/WAL boundary gap (snapshotLastAppliedSeq={SnapshotSeq}, firstReplayableSeq={FirstSeq}, expected={Expected}); channel marked WAL-halted, engine left at snapshot baseline",
                            ChannelNumber, snapshotLastAppliedSeq, rec.Seq, expected);
                        return;
                    }
                }
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
            _bootAuditDurableSeq = long.MaxValue;
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
        // Issue #285: surface CRC-mismatch and legacy-record counts to
        // observability so operators see storage-layer integrity loss
        // and migration progress immediately after boot.
        _metrics?.AddWalRecordCorruptions(_wal.LastReadCorruptCount);
        _metrics?.AddWalRecordsLegacy(_wal.LastReadLegacyCount);
        _logger.LogInformation(
            "channel {ChannelNumber}: WAL replay complete — replayed={Replayed} skipped={Skipped} corrupt={Corrupt} legacy={Legacy} (snapshotLastAppliedSeq={SnapshotSeq})",
            ChannelNumber, replayed, skipped, _wal.LastReadCorruptCount, _wal.LastReadLegacyCount, snapshotLastAppliedSeq);
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
            in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue,
            DurabilityHandle durability = default) => true;
        public bool WriteExecutionReportTrade(SessionId session, in TradeEvent e, bool isAggressor,
            long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty,
            DurabilityHandle durability = default) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId ownerSession, ulong ownerClOrdId,
            long restingOrderId, in TradeEvent e, long leavesQty, long cumQty,
            DurabilityHandle durability = default) => true;
        public bool WriteExecutionReportPassiveCancel(SessionId ownerSession, ulong ownerClOrdId,
            long orderId, in OrderCanceledEvent e, ulong requesterClOrdIdOrZero,
            ulong receivedTimeNanos = ulong.MaxValue,
            DurabilityHandle durability = default) => true;
        public bool WriteExecutionReportModify(SessionId session, long securityId, long orderId,
            ulong clOrdIdValue, ulong origClOrdIdValue,
            MatchingSide side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos,
            uint rptSeq, ulong receivedTimeNanos = ulong.MaxValue,
            DurabilityHandle durability = default) => true;
        public bool WriteExecutionReportReject(SessionId session, in MatchingRejectEvent e,
            ulong clOrdIdValue, DurabilityHandle durability = default) => true;
    }
}
