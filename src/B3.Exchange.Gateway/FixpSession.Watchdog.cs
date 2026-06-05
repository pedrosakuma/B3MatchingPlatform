using B3.EntryPoint.Wire;
using System.Buffers;
using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using ContractsSessionId = B3.Exchange.Contracts.SessionId;

namespace B3.Exchange.Gateway;


/// <summary>
/// FIXP liveness watchdog loop: polls the last-inbound timestamp
/// and emits a Sequence probe (or terminates the session) when the
/// peer goes silent past the negotiated keep-alive interval. Split
/// out from FixpSession.Inbound.cs (issue #139).
/// </summary>
public sealed partial class FixpSession
{
    /// <summary>
    /// Periodic watchdog that drives the FIXP-style heartbeat + idle-timeout
    /// policy. Runs on its own task; never touches the socket directly —
    /// instead it enqueues <c>Sequence</c> frames into the transport's
    /// bounded channel, so all writes remain serialised.
    /// </summary>
    private async Task RunWatchdogLoopAsync(CancellationToken ct)
    {
        // Capture the transport this loop is bound to so a stale tick
        // after a re-attach (#69b-2) can be detected and a teardown
        // refused. Without this guard, a watchdog from the OLD transport
        // could complete its Task.Delay just as the cancellation arrives,
        // observe stale liveness fields, and call Close("idle-timeout")
        // against the freshly attached session.
        var ownTransport = _transport;
        // Tick at a fraction of the smallest configured interval so we react
        // promptly without busy-polling. Capped to keep tests responsive.
        // Includes the (possibly negotiated) KeepAliveIntervalMs so that a
        // sub-second client-requested interval drives the loop fast enough
        // to actually send heartbeats inside the client's stale window
        // (#161).
        int hbiMs = EffectiveHeartbeatIntervalMs();
        int idleMs = EffectiveIdleTimeoutMs();
        int graceMs = _options.TestRequestGraceMs;
        // The watchdog may start in the Negotiated state, before Establish
        // commits a (much smaller) keepAlive. Bound the very first sleep so it
        // cannot oversleep the smallest negotiable terminate threshold
        // (3×MinKeepAliveInterval = 3000ms); the loop re-resolves tickMs from
        // the actual negotiated intervals on every subsequent iteration.
        const int preNegotiationMaxTickMs = 1000;
        int tickMs = Math.Max(1, Math.Min(preNegotiationMaxTickMs,
            Math.Min(hbiMs, Math.Min(idleMs, graceMs)) / 4));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(tickMs, ct).ConfigureAwait(false);
                if (!IsOpen) return;
                // A re-attach replaced the transport this loop was bound
                // to; surrender to the new watchdog and exit.
                if (!ReferenceEquals(_transport, ownTransport)) return;

                long now = NowMs();
                long sinceIn = now - Volatile.Read(ref _lastInboundMs);
                long sinceOut = now - ownTransport.LastOutboundTickMs;

                // Re-resolve every tick: KeepAliveIntervalMs is committed
                // by Establish and stays stable once set, but the watchdog
                // may start ticking before Establish lands (during
                // Negotiated state). Recomputing each tick keeps the
                // thresholds correct across the state transition without
                // restarting the loop. (#161)
                hbiMs = EffectiveHeartbeatIntervalMs();
                idleMs = EffectiveIdleTimeoutMs();
                // Recompute the tick cadence from the freshly-resolved
                // intervals so a small client-negotiated keepAlive shrinks
                // the loop delay too. Without this the next Task.Delay would
                // keep using the coarse pre-negotiation cadence and could
                // sleep past the (now ~3×keepAlive) terminate threshold when
                // a large static grace was configured.
                tickMs = Math.Max(1, Math.Min(hbiMs, Math.Min(idleMs, graceMs)) / 4);

                // Idle teardown: if a probe is outstanding and the peer has
                // stayed silent past the terminate threshold, close. Per spec
                // §4.5.4 the server SHOULD tolerate ~3×keepAlive of inbound
                // silence (three missed heartbeats) before tearing the session
                // down. The probe is sent earlier (at EffectiveIdleTimeoutMs,
                // ~1.5×keepAlive) so a live-but-slow peer has a full keepAlive
                // window to answer before the 3× terminate fires.
                int terminateMs = EffectiveTerminateTimeoutMs();
                if (sinceIn >= terminateMs &&
                    Volatile.Read(ref _probeOutstanding) == 1)
                {
                    // Generation-aware close: another reattach could have
                    // raced in between the snapshot above and the close
                    // call below; CloseIfTransportCurrent re-validates
                    // under _attachLock.
                    await SendTerminateIfTransportCurrentAndCloseAsync(
                        SessionRejectEncoder.TerminationCode.KeepaliveIntervalLapsed,
                        "idle-timeout", ownTransport, CloseKind.KeepaliveLapsed).ConfigureAwait(false);
                    return;
                }

                // Probe (FIXP TestRequest equivalent): if we've been silent on
                // the inbound side past the idle threshold and we haven't
                // already sent a probe in this idle stretch, send one.
                if (sinceIn >= idleMs &&
                    Interlocked.CompareExchange(ref _probeOutstanding, 1, 0) == 0)
                {
                    EnqueueSequence();
                    continue;
                }

                // Regular heartbeat: only when no other outbound traffic has
                // been sent within the heartbeat interval. This naturally
                // suppresses heartbeats while ER traffic is flowing.
                if (sinceOut >= hbiMs)
                {
                    EnqueueSequence();
                }
            }
        }
        catch (OperationCanceledException) { /* expected: heartbeat cancelled during shutdown */ }
        catch (Exception)
        {
            // Watchdog must never crash the session silently; tear down on
            // unexpected error so the listener can drop the connection.
            Close("watchdog-error");
        }
    }

    /// <summary>
    /// Resolves the effective server-side heartbeat interval. Once
    /// <see cref="FixpSession.KeepAliveIntervalMs"/> is committed by an
    /// Establish (negotiated by the client), the server honors that
    /// value: heartbeats are emitted at most every keepAlive ms so the
    /// client sees inbound liveness well inside its own stale window
    /// (clients consider the peer stale at 1.5×keepAlive, see
    /// <c>FixpClient.IsPeerStale</c>). Without the negotiated value
    /// (e.g. mid-handshake) we fall back to the static option default.
    /// (#161)
    /// </summary>
    private int EffectiveHeartbeatIntervalMs()
    {
        long negotiated = KeepAliveIntervalMs;
        if (negotiated > 0)
            return (int)Math.Min(int.MaxValue, negotiated);
        return _options.HeartbeatIntervalMs;
    }

    /// <summary>
    /// Resolves the effective inbound-silence threshold before the
    /// watchdog probes the peer. When the client negotiated a keepAlive
    /// interval, we mirror its own staleness logic and set the probe
    /// threshold to 1.5×keepAlive — that is, we tolerate one missed
    /// client heartbeat (clients send at keepAlive/2 cadence) before
    /// asking the peer to prove it is still alive. (#161)
    /// </summary>
    private int EffectiveIdleTimeoutMs()
    {
        long negotiated = KeepAliveIntervalMs;
        if (negotiated > 0)
            return (int)Math.Min(int.MaxValue, negotiated + (negotiated / 2));
        return _options.IdleTimeoutMs;
    }

    /// <summary>
    /// Resolves the effective inbound-silence threshold at which the
    /// watchdog tears the session down with
    /// <c>Terminate(KEEPALIVE_INTERVAL_LAPSED)</c>. When the client
    /// negotiated a keepAlive interval we honor the FIXP §4.5.4
    /// recommendation of tolerating ~3×keepAlive (three missed
    /// heartbeats) of silence before terminating, giving a live-but-slow
    /// peer a full keepAlive window to answer the probe sent at
    /// <see cref="EffectiveIdleTimeoutMs"/> (~1.5×keepAlive). Without a
    /// negotiated value (static-config / mid-handshake) we preserve the
    /// historical <c>IdleTimeoutMs + TestRequestGraceMs</c> threshold.
    /// </summary>
    private int EffectiveTerminateTimeoutMs()
    {
        long negotiated = KeepAliveIntervalMs;
        if (negotiated > 0)
            return (int)Math.Min(int.MaxValue, negotiated * 3);
        return (int)Math.Min((long)int.MaxValue, (long)_options.IdleTimeoutMs + _options.TestRequestGraceMs);
    }

    private void EnqueueSequence() => _retransmitController.EnqueueSequence();
}
