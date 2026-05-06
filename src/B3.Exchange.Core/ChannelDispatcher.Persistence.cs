using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using Side = B3.Exchange.Matching.Side;

namespace B3.Exchange.Core;

/// <summary>
/// Persistence facet of <see cref="ChannelDispatcher"/> (issue #260).
/// Owns the snapshot/restore plumbing wired through
/// <see cref="IChannelStatePersister"/>; integrates with
/// <c>ProcessOne</c> via <see cref="OnAfterCommandFlushed"/> and with
/// <c>RunLoopAsync</c> via <see cref="LoadPersistedStateOnLoopThread"/>.
///
/// <para>All state mutation paths assume single-threaded invocation on
/// the dispatch loop thread (mirrors the engine's invariant). The
/// persister itself is invoked synchronously inside the dispatch turn so
/// any consumer-visible event has a corresponding durable snapshot before
/// the next command is observed.</para>
/// </summary>
public sealed partial class ChannelDispatcher
{
    /// <summary>
    /// Captures a full per-channel snapshot of dispatcher + engine state.
    /// Invoked on the dispatch thread; safe to call between commands.
    /// </summary>
    public ChannelStateSnapshot CaptureChannelState()
    {
        AssertOnLoopThread();
        var owners = new List<OrderOwnerSnapshot>(_orders.Count);
        foreach (var entry in _orders.EnumerateAll())
        {
            owners.Add(new OrderOwnerSnapshot(
                OrderId: entry.OrderId,
                SessionValue: entry.State.Session.Value ?? string.Empty,
                Firm: entry.State.Firm,
                ClOrdId: entry.State.ClOrdId,
                Side: entry.State.Side,
                SecurityId: entry.State.SecurityId));
        }
        return new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: ChannelNumber,
            SequenceNumber: _sequenceNumber,
            SequenceVersion: _sequenceVersion,
            Engine: _engine.CaptureState(),
            Owners: owners);
    }

    /// <summary>
    /// Restores dispatcher + engine state from <paramref name="snapshot"/>.
    /// Must be invoked on the dispatch thread, before any command has been
    /// processed. Refuses to overwrite a non-default state.
    /// </summary>
    public void RestoreChannelState(ChannelStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        AssertOnLoopThread();
        if (snapshot.Version != ChannelStateSnapshot.CurrentVersion)
            throw new InvalidOperationException(
                $"channel {ChannelNumber}: snapshot schema version {snapshot.Version} unsupported (expected {ChannelStateSnapshot.CurrentVersion})");
        if (snapshot.ChannelNumber != ChannelNumber)
            throw new InvalidOperationException(
                $"snapshot is for channel {snapshot.ChannelNumber}, not {ChannelNumber}");
        if (_orders.Count != 0)
            throw new InvalidOperationException("RestoreChannelState requires an empty OrderRegistry on the target dispatcher");

        _engine.RestoreState(snapshot.Engine);

        // Re-publish counters via the cross-thread-readable Volatile
        // backing fields so the HTTP scrape sees the restored values
        // immediately after the loop becomes ready.
        Volatile.Write(ref _sequenceNumber, snapshot.SequenceNumber);
        Volatile.Write(ref _sequenceVersion, snapshot.SequenceVersion);

        foreach (var o in snapshot.Owners)
        {
            _orders.Register(o.OrderId, new SessionId(o.SessionValue), o.ClOrdId, o.Firm, o.Side, o.SecurityId);
        }

        _logger.LogInformation(
            "channel {ChannelNumber}: restored snapshot — seq={SequenceNumber}/{SequenceVersion} owners={OwnerCount}",
            ChannelNumber, snapshot.SequenceNumber, snapshot.SequenceVersion, snapshot.Owners.Count);
    }

    /// <summary>
    /// Loads the persisted snapshot (when a persister is wired) and
    /// applies it on the dispatch thread. Called from
    /// <see cref="RunLoopAsync"/> immediately after
    /// <c>BindToDispatchThread</c>, so the engine's owner thread is the
    /// loop thread for both the restore and every subsequent dispatch.
    /// Errors are logged and swallowed: a corrupt snapshot must not stop
    /// the channel from accepting new orders (operators can fix the
    /// underlying file or set <c>--reset-state</c>; future PR).
    /// </summary>
    private void LoadPersistedStateOnLoopThread()
    {
        if (_persister is null) return;
        ChannelStateSnapshot? snapshot;
        try
        {
            snapshot = _persister.TryLoad(ChannelNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "channel {ChannelNumber}: persister TryLoad threw; starting from empty state", ChannelNumber);
            return;
        }
        if (snapshot is null) return;
        try
        {
            RestoreChannelState(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "channel {ChannelNumber}: RestoreChannelState failed; channel may be partially restored", ChannelNumber);
        }
    }

    /// <summary>
    /// Hook invoked by <c>ProcessOne</c> after a successful flush.
    /// Captures the channel state and forwards it to the persister
    /// best-effort — exceptions are logged and swallowed so persistence
    /// failure never crashes the dispatcher.
    /// </summary>
    private void OnAfterCommandFlushed()
    {
        if (_persister is null) return;
        try
        {
            var snap = CaptureChannelState();
            _persister.Save(snap);
        }
        catch (Exception ex)
        {
            _metrics?.IncDispatcherCrashes();
            _logger.LogError(ex, "channel {ChannelNumber}: persister Save failed", ChannelNumber);
        }
    }
}
