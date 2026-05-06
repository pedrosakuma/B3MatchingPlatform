using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using OrderType = B3.Exchange.Matching.OrderType;
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
        // Issue #260 follow-up (review feedback on PR #261): only owners
        // for orders that the engine considers live belong in the snapshot.
        // Issue #262: stops are now persisted, so the "live" set is
        // (resting in books) ∪ (parked stops). Owners for stop orders can
        // therefore be persisted; owners for in-flight or fully-filled
        // orders are still dropped.
        var engineSnap = _engine.CaptureState();
        var restingIds = new HashSet<long>();
        foreach (var book in engineSnap.Books)
            foreach (var o in book.Orders)
                restingIds.Add(o.OrderId);
        if (engineSnap.Stops is { } stops)
            foreach (var s in stops)
                restingIds.Add(s.OrderId);
        var owners = new List<OrderOwnerSnapshot>(restingIds.Count);
        int dropped = 0;
        foreach (var entry in _orders.EnumerateAll())
        {
            if (!restingIds.Contains(entry.OrderId))
            {
                dropped++;
                continue;
            }
            owners.Add(new OrderOwnerSnapshot(
                OrderId: entry.OrderId,
                SessionValue: entry.State.Session.Value ?? string.Empty,
                Firm: entry.State.Firm,
                ClOrdId: entry.State.ClOrdId,
                Side: entry.State.Side,
                SecurityId: entry.State.SecurityId));
        }
        if (dropped > 0)
        {
            _logger.LogDebug(
                "channel {ChannelNumber}: snapshot dropped {Dropped} non-resting owners (in-flight)",
                ChannelNumber, dropped);
        }
        return new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: ChannelNumber,
            SequenceNumber: _sequenceNumber,
            SequenceVersion: _sequenceVersion,
            Engine: engineSnap,
            Owners: owners);
    }

    /// <summary>
    /// Restores dispatcher + engine state from <paramref name="snapshot"/>.
    /// Must be invoked on the dispatch thread, before any command has been
    /// processed. Refuses to overwrite a non-default state.
    ///
    /// <para>Issue #260 follow-up (review feedback on PR #261): all
    /// structural validation runs <em>before</em> any mutation so the
    /// engine cannot end up half-restored. If validation fails the
    /// snapshot is rejected with no side effects; if engine/registry
    /// rebuild fails after validation, the exception is propagated and
    /// the channel must fail-closed (the loop terminates → heartbeat
    /// goes stale → <c>/health/live</c> trips).</para>
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

        ValidateSnapshotStructure(snapshot);

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
    /// Pure-read structural validation of <paramref name="snapshot"/>.
    /// Throws <see cref="InvalidOperationException"/> with a precise
    /// reason when the snapshot is malformed (duplicate orderIds across
    /// books, owners referencing orderIds not present in any book, etc.).
    /// Runs before any mutation so RestoreChannelState is all-or-nothing.
    /// </summary>
    private static void ValidateSnapshotStructure(ChannelStateSnapshot snapshot)
    {
        var seenOrderIds = new HashSet<long>();
        foreach (var book in snapshot.Engine.Books)
        {
            foreach (var o in book.Orders)
            {
                if (o.RemainingQuantity <= 0)
                    throw new InvalidOperationException(
                        $"snapshot orderId {o.OrderId} on securityId {book.SecurityId} has non-positive remainingQuantity {o.RemainingQuantity}");
                if (!seenOrderIds.Add(o.OrderId))
                    throw new InvalidOperationException(
                        $"snapshot contains duplicate orderId {o.OrderId} (in book for securityId {book.SecurityId})");
                if (o.OrderId >= snapshot.Engine.NextOrderId)
                    throw new InvalidOperationException(
                        $"snapshot orderId {o.OrderId} is >= NextOrderId {snapshot.Engine.NextOrderId} (would collide with future allocations)");
            }
        }
        // Issue #262: validate stop records in the same id namespace as
        // book orders — engine OrderIds are unique across (books ∪ stops).
        if (snapshot.Engine.Stops is { } stops)
        {
            foreach (var s in stops)
            {
                if (s.Quantity <= 0)
                    throw new InvalidOperationException(
                        $"snapshot stop orderId {s.OrderId} has non-positive quantity {s.Quantity}");
                if (s.StopPxMantissa <= 0)
                    throw new InvalidOperationException(
                        $"snapshot stop orderId {s.OrderId} has non-positive stopPx {s.StopPxMantissa}");
                if (s.StopType != OrderType.StopLoss && s.StopType != OrderType.StopLimit)
                    throw new InvalidOperationException(
                        $"snapshot stop orderId {s.OrderId} has non-stop OrderType {s.StopType}");
                if (s.StopType == OrderType.StopLimit && s.LimitPriceMantissa <= 0)
                    throw new InvalidOperationException(
                        $"snapshot stop orderId {s.OrderId} (StopLimit) has non-positive limit price {s.LimitPriceMantissa}");
                if (s.StopType == OrderType.StopLoss && s.LimitPriceMantissa != 0)
                    throw new InvalidOperationException(
                        $"snapshot stop orderId {s.OrderId} (StopLoss) must not carry a limit price (got {s.LimitPriceMantissa})");
                if (!seenOrderIds.Add(s.OrderId))
                    throw new InvalidOperationException(
                        $"snapshot contains duplicate orderId {s.OrderId} (stop on securityId {s.SecurityId})");
                if (s.OrderId >= snapshot.Engine.NextOrderId)
                    throw new InvalidOperationException(
                        $"snapshot stop orderId {s.OrderId} is >= NextOrderId {snapshot.Engine.NextOrderId}");
            }
        }
        foreach (var owner in snapshot.Owners)
        {
            if (!seenOrderIds.Contains(owner.OrderId))
                throw new InvalidOperationException(
                    $"snapshot owner refers to orderId {owner.OrderId} which is not present in any restored book or stop");
        }
    }

    /// <summary>
    /// Loads the persisted snapshot (when a persister is wired) and
    /// applies it on the dispatch thread. Called from
    /// <see cref="RunLoopAsync"/> immediately after
    /// <c>BindToDispatchThread</c>, so the engine's owner thread is the
    /// loop thread for both the restore and every subsequent dispatch.
    ///
    /// <para>Issue #260 follow-up (review feedback on PR #261): a
    /// corrupt/partial snapshot is fatal. We rethrow so the dispatch
    /// loop exits, the heartbeat goes stale and <c>/health/live</c>
    /// trips — that is preferable to silently running the channel with
    /// a half-restored book (would risk duplicate IDs, missing
    /// ownership, incorrect future matching). Operators must remove
    /// or repair the snapshot file (or use a future <c>--reset-state</c>
    /// flag) before restarting. <c>TryLoad</c>-time IO failures are
    /// also fatal for the same reason.</para>
    /// </summary>
    private void LoadPersistedStateOnLoopThread()
    {
        if (_persister is null) return;
        ChannelStateSnapshot? snapshot;
        var loadStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            snapshot = _persister.TryLoad(ChannelNumber);
        }
        catch (Exception ex)
        {
            _metrics?.IncSnapshotRestoreFailure();
            _logger.LogError(ex,
                "channel {ChannelNumber}: persister TryLoad threw — failing channel closed",
                ChannelNumber);
            throw;
        }
        if (snapshot is null)
        {
            _metrics?.SnapshotLoad.ObserveTicks(System.Diagnostics.Stopwatch.GetTimestamp() - loadStart);
            return;
        }
        try
        {
            RestoreChannelState(snapshot);
            _metrics?.SnapshotLoad.ObserveTicks(System.Diagnostics.Stopwatch.GetTimestamp() - loadStart);
        }
        catch (InvalidOperationException ex)
        {
            // ValidateSnapshotStructure rejects → bump the validation
            // counter so operators can alert on "snapshot rejected"
            // independently of generic IO/restore failures.
            _metrics?.IncSnapshotValidationFailure();
            _logger.LogError(ex,
                "channel {ChannelNumber}: snapshot structural validation failed — failing channel closed (snapshot must be repaired or removed)",
                ChannelNumber);
            throw;
        }
        catch (Exception ex)
        {
            _metrics?.IncSnapshotRestoreFailure();
            _logger.LogError(ex,
                "channel {ChannelNumber}: RestoreChannelState failed — failing channel closed (snapshot must be repaired or removed)",
                ChannelNumber);
            throw;
        }
    }

    /// <summary>
    /// Hook invoked by <c>ProcessOne</c> after a successful flush.
    /// Captures the channel state and forwards it to the persister
    /// best-effort — exceptions are logged and swallowed so persistence
    /// failure never crashes the dispatcher.
    ///
    /// <para>Issue #267: when <paramref name="force"/> is <c>false</c>
    /// (regular order-flow commands) the persist may be skipped per the
    /// configured <see cref="SnapshotThrottlePolicy"/>; the dispatcher
    /// then marks itself dirty so cooperative shutdown will flush a
    /// final snapshot. Operator commands set <paramref name="force"/>
    /// to <c>true</c> so SequenceVersion bumps, TradeBust replays and
    /// trading-phase changes always persist immediately.</para>
    /// </summary>
    private void OnAfterCommandFlushed(bool force = false)
    {
        if (_persister is null) return;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!force)
        {
            // First-ever call: no prior persist → treat msSinceLastSave
            // as 0 so a configured MinIntervalMs grace window applies
            // from boot rather than triggering immediately.
            long sinceMs = _lastPersistUnixMs == 0 ? 0 : nowMs - _lastPersistUnixMs;
            if (!_snapshotThrottle.ShouldPersist(_commandsSincePersist + 1, sinceMs))
            {
                _commandsSincePersist++;
                _pendingDirty = true;
                _metrics?.IncSnapshotSkippedByThrottle();
                return;
            }
        }
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var snap = CaptureChannelState();
            var bytes = _persister.Save(snap);
            var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            if (_metrics is { } m)
            {
                m.SnapshotWrite.ObserveTicks(elapsed);
                m.IncSnapshotSaveOk();
                if (bytes > 0) m.SetSnapshotLastSizeBytes(bytes);
                m.SetSnapshotLastSuccessUnixMs(nowMs);
            }
            _commandsSincePersist = 0;
            _lastPersistUnixMs = nowMs;
            _pendingDirty = false;
        }
        catch (Exception ex)
        {
            _metrics?.IncSnapshotSaveFailure();
            _metrics?.IncDispatcherCrashes();
            _logger.LogError(ex, "channel {ChannelNumber}: persister Save failed", ChannelNumber);
        }
    }

    /// <summary>
    /// Forces a final snapshot if the throttle policy has accumulated
    /// pending state (issue #267). Called from the cooperative shutdown
    /// path so a quiet period after the last command flush does not
    /// silently lose work that the throttle deferred.
    /// </summary>
    internal void FlushPendingSnapshotOnShutdown()
    {
        if (_persister is null) return;
        if (!_pendingDirty) return;
        OnAfterCommandFlushed(force: true);
    }
}
