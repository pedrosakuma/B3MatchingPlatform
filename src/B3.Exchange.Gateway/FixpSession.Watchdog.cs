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
        int tickMs = Math.Max(1, Math.Min(hbiMs, Math.Min(idleMs, graceMs)) / 4);
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

                // Idle teardown: if a probe is outstanding and grace elapsed
                // without any inbound, close. The grace clock starts from the
                // moment the probe was sent (i.e. last outbound tick was
                // bumped), so we measure the additional silence beyond
                // idleTimeoutMs.
                if (sinceIn >= (long)idleMs + graceMs &&
                    Volatile.Read(ref _probeOutstanding) == 1)
                {
                    // Generation-aware close: another reattach could have
                    // raced in between the snapshot above and the close
                    // call below; CloseIfTransportCurrent re-validates
                    // under _attachLock.
                    CloseIfTransportCurrent("idle-timeout", ownTransport);
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
        catch (OperationCanceledException) { }
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

    private void EnqueueSequence() => _retransmitController.EnqueueSequence();
}
