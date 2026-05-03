using B3.EntryPoint.Wire;
using System.Buffers;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using ContractsSessionId = B3.Exchange.Contracts.SessionId;

namespace B3.Exchange.Gateway;

/// <summary>
/// Inbound dispatch surface of <see cref="FixpSession"/>: receive loop,
/// frame decoder, FIXP control-plane handlers (Negotiate / Establish /
/// RetransmitRequest), business-frame validation, throttle, and the
/// liveness watchdog. Split out as a partial class file (issue #121,
/// step 4/4) so the FixpSession surface is grouped by concern instead
/// of one ~2000-line file. No public surface change; this is a
/// physical-organization refactor only.
/// </summary>
public sealed partial class FixpSession
{
    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        // Capture the transport this loop is bound to so a stale callback
        // after a re-attach (#69b-2) can be detected and ignored: a new
        // recv loop bound to a fresh transport will be the only owner of
        // the close decision for the new transport.
        var ownTransport = _transport;
        var stream = ownTransport.Stream;
        var headerBuf = new byte[InboundHeaderSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReadExactlyAsync(stream, headerBuf, ct).ConfigureAwait(false);
                if (!EntryPointFrameReader.TryParseInboundHeader(headerBuf, out var info, out var headerError, out var hdrMsg))
                {
                    _sink.OnDecodeError(Identity, hdrMsg ?? headerError.ToString());
                    await TerminateAndCloseAsync(MapHeaderErrorToTerminationCode(headerError), $"decode-error:{headerError}").ConfigureAwait(false);
                    return;
                }
                var bodyBuf = ArrayPool<byte>.Shared.Rent(info.BodyLength);
                try
                {
                    await ReadExactlyAsync(stream, bodyBuf.AsMemory(0, info.BodyLength), ct).ConfigureAwait(false);
                    // Any well-framed inbound frame counts as liveness, including
                    // session-layer Sequence (heartbeat) frames.
                    Volatile.Write(ref _lastInboundMs, NowMs());
                    Volatile.Write(ref _lastInboundUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Volatile.Write(ref _probeOutstanding, 0);
                    if (!await DispatchInboundAsync(info, bodyBuf, info.BodyLength).ConfigureAwait(false))
                    {
                        // DispatchInboundAsync already wrote Terminate + closed.
                        return;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bodyBuf);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "fixp session {ConnectionId} receive IO error", ConnectionId);
        }
        finally
        {
            // Transport-side EOF: defer to the central handler so the
            // Established → Suspended demotion (issue #69) is observed
            // identically to a transport-callback-driven teardown. Pass
            // the captured transport so a stale callback from a previous
            // generation (post-rebind) is filtered out by
            // OnTransportClosed.
            OnTransportClosed("recv-eof", ownTransport);
        }
    }

    /// <summary>
    /// Maps a header-parse <see cref="EntryPointFrameReader.HeaderError"/>
    /// to the FIXP <c>TerminationCode</c> the gateway must send before
    /// dropping the connection (spec §4.5.7 / §4.10).
    /// </summary>
    internal static byte MapHeaderErrorToTerminationCode(EntryPointFrameReader.HeaderError error) => error switch
    {
        EntryPointFrameReader.HeaderError.InvalidSofhEncodingType => SessionRejectEncoder.TerminationCode.InvalidSofh,
        EntryPointFrameReader.HeaderError.InvalidSofhMessageLength => SessionRejectEncoder.TerminationCode.InvalidSofh,
        EntryPointFrameReader.HeaderError.UnsupportedTemplate => SessionRejectEncoder.TerminationCode.UnrecognizedMessage,
        EntryPointFrameReader.HeaderError.UnsupportedSchema => SessionRejectEncoder.TerminationCode.UnrecognizedMessage,
        EntryPointFrameReader.HeaderError.BlockLengthMismatch => SessionRejectEncoder.TerminationCode.DecodingError,
        EntryPointFrameReader.HeaderError.MessageLengthMismatch => SessionRejectEncoder.TerminationCode.DecodingError,
        EntryPointFrameReader.HeaderError.ShortHeader => SessionRejectEncoder.TerminationCode.DecodingError,
        _ => SessionRejectEncoder.TerminationCode.Unspecified,
    };

    /// <summary>
    /// Sends a Terminate frame with <paramref name="terminationCode"/>
    /// directly to the underlying stream (bypassing the send queue), and
    /// closes the session.
    /// </summary>
    private async Task TerminateAndCloseAsync(byte terminationCode, string reason)
    {
        if (!IsOpen) { Close(reason); return; }
        var frame = new byte[SessionRejectEncoder.TerminateTotal];
        SessionRejectEncoder.EncodeTerminate(frame, SessionId, 0, terminationCode);
        try
        {
            await _transport.SendDirectAsync(frame).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "fixp session {ConnectionId} failed to write Terminate({Code})", ConnectionId, terminationCode);
        }
        Close(reason);
    }

    private static async Task ReadExactlyAsync(Stream s, byte[] buf, CancellationToken ct)
        => await ReadExactlyAsync(s, buf.AsMemory(), ct).ConfigureAwait(false);

    private static async Task ReadExactlyAsync(Stream s, Memory<byte> dst, CancellationToken ct)
    {
        int total = 0;
        while (total < dst.Length)
        {
            int n = await s.ReadAsync(dst.Slice(total), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            total += n;
        }
    }
}
