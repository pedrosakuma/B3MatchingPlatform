using B3.EntryPoint.Wire;
using System.Buffers;
using B3.Exchange.Contracts;
using B3.Exchange.Core;
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
        int tickMs = Math.Max(1, Math.Min(_options.HeartbeatIntervalMs,
            Math.Min(_options.IdleTimeoutMs, _options.TestRequestGraceMs)) / 4);
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

                // Idle teardown: if a probe is outstanding and grace elapsed
                // without any inbound, close. The grace clock starts from the
                // moment the probe was sent (i.e. last outbound tick was
                // bumped), so we measure the additional silence beyond
                // idleTimeoutMs.
                if (sinceIn >= (long)_options.IdleTimeoutMs + _options.TestRequestGraceMs &&
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
                if (sinceIn >= _options.IdleTimeoutMs &&
                    Interlocked.CompareExchange(ref _probeOutstanding, 1, 0) == 0)
                {
                    EnqueueSequence();
                    continue;
                }

                // Regular heartbeat: only when no other outbound traffic has
                // been sent within the heartbeat interval. This naturally
                // suppresses heartbeats while ER traffic is flowing.
                if (sinceOut >= _options.HeartbeatIntervalMs)
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

    private void EnqueueSequence() => _retransmitController.EnqueueSequence();
}
