using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using B3.Exchange.Gateway;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.SyntheticTrader.Fixp;

/// <summary>
/// Disposition of an inbound FIXP frame after <see cref="FixpClient"/>
/// has inspected it. The owning <c>EntryPointClient</c> uses this to
/// decide whether to keep dispatching the frame to its application
/// parser or stop further processing.
/// </summary>
public enum InboundDisposition
{
    /// <summary>Application frame the caller should keep parsing.</summary>
    PassThrough,
    /// <summary>FIXP session-layer frame already handled.</summary>
    HandledControl,
    /// <summary>Peer sent <c>Terminate</c>; caller should close.</summary>
    PeerTerminated,
    /// <summary>Unrecoverable framing/protocol error; caller should close.</summary>
    ProtocolError,
}

/// <summary>
/// Client-side FIXP session layer. Owns the FIXP state machine, the
/// outbound application sequence number (idempotent flow), and the
/// inbound application sequence tracking (recoverable flow). It does
/// <em>not</em> own the network read loop — the embedding
/// <c>EntryPointClient</c> drives reads and forwards each decoded frame
/// to <see cref="HandleInboundFrame"/>. Outbound writes go through
/// <see cref="WriteAsync"/> and are mutually excluded with the
/// session-layer frames this class sends (heartbeat, NotApplied response,
/// RetransmitRequest, Terminate).
/// </summary>
public sealed class FixpClient : IAsyncDisposable
{
    private readonly NetworkStream _stream;
    private readonly FixpClientOptions _options;
    private readonly Action<string>? _logDebug;
    private readonly Action<string>? _logWarn;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource<FixpFrameCodec.NegotiateResponseFrame> _negotiateTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<FixpFrameCodec.EstablishAckFrame> _establishTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _seqLock = new();

    private volatile FixpClientState _state = FixpClientState.Init;
    private uint _sessionIdNumeric;
    private ulong _sessionVerId;
    private uint _nextOutboundSeqNo = 1;
    private uint _expectedInboundSeqNo = 1;
    private long _lastInboundTicks;
    private long _lastOutboundTicks;
    private bool _disposed;

    public FixpClient(NetworkStream stream, FixpClientOptions options,
        Action<string>? logDebug = null, Action<string>? logWarn = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (!uint.TryParse(_options.SessionId, out _sessionIdNumeric) || _sessionIdNumeric == 0
            || _options.SessionId.StartsWith('0'))
        {
            throw new ArgumentException(
                $"FixpClientOptions.SessionId must be a decimal uint32 string >0 with no leading zeros (got '{_options.SessionId}')",
                nameof(options));
        }
        _logDebug = logDebug;
        _logWarn = logWarn;
        Touch(inbound: true);
        Touch(inbound: false);
    }

    public FixpClientState State => _state;
    public uint SessionIdNumeric => _sessionIdNumeric;
    public ulong SessionVerId => _sessionVerId;
    public uint NextOutboundSeqNo { get { lock (_seqLock) return _nextOutboundSeqNo; } }
    public uint ExpectedInboundSeqNo { get { lock (_seqLock) return _expectedInboundSeqNo; } }
    public DateTime LastInboundUtc => new(Interlocked.Read(ref _lastInboundTicks), DateTimeKind.Utc);
    public DateTime LastOutboundUtc => new(Interlocked.Read(ref _lastOutboundTicks), DateTimeKind.Utc);
    public TimeSpan KeepAlive => TimeSpan.FromMilliseconds(_options.KeepAliveIntervalMillis);

    /// <summary>
    /// Performs the synchronous Negotiate→NegotiateResponse→Establish→
    /// EstablishAck handshake. Caller must invoke
    /// <see cref="HandleInboundFrame"/> from its read loop while this is
    /// awaiting (the loop typically starts before <c>NegotiateAndEstablishAsync</c>
    /// completes — that's fine, this method only writes outbound frames
    /// and awaits the inbound completion sources).
    /// </summary>
    public async Task NegotiateAndEstablishAsync(CancellationToken ct)
    {
        if (_state != FixpClientState.Init)
            throw new InvalidOperationException($"NegotiateAndEstablishAsync called in state {_state}");

        // Spec §4.5.2: sessionVerID must be incremented for each Negotiate
        // on a given sessionId. We pick "now-as-nanoseconds" so independent
        // synthetic-trader processes never collide on a given session.
        _sessionVerId = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ulong negotiateTs = NowNanos();
        byte[] credentials = BuildCredentialsJson(_options.SessionId, _options.AccessKey);
        byte[] buf = new byte[1024];
        int len = FixpFrameCodec.EncodeNegotiate(buf, _sessionIdNumeric, _sessionVerId, negotiateTs,
            _options.EnteringFirm, onBehalfFirm: null,
            credentials: credentials,
            clientIp: ReadOnlySpan<byte>.Empty,
            clientAppName: Encoding.ASCII.GetBytes(_options.ClientAppName),
            clientAppVersion: Encoding.ASCII.GetBytes(_options.ClientAppVersion));

        _state = FixpClientState.NegotiateSent;
        await WriteRawAsync(buf.AsMemory(0, len), ct).ConfigureAwait(false);
        _logDebug?.Invoke($"fixp Negotiate sent sessionId={_sessionIdNumeric} verId={_sessionVerId}");

        var negResp = await _negotiateTcs.Task.WaitAsync(_options.HandshakeTimeout, ct).ConfigureAwait(false);
        _logDebug?.Invoke($"fixp NegotiateResponse received semVer={negResp.SemVerMajor}.{negResp.SemVerMinor}.{negResp.SemVerPatch}");
        _state = FixpClientState.Negotiated;

        ulong establishTs = NowNanos();
        ushort codType = _options.CancelOnDisconnect ? (ushort)4 : (ushort)0;
        len = FixpFrameCodec.EncodeEstablish(buf, _sessionIdNumeric, _sessionVerId, establishTs,
            keepAliveIntervalMillis: _options.KeepAliveIntervalMillis,
            nextSeqNo: NextOutboundSeqNo,
            cancelOnDisconnectType: codType,
            codTimeoutWindowMillis: 0,
            credentials: BuildCredentialsJson(_options.SessionId, _options.AccessKey));
        _state = FixpClientState.EstablishSent;
        await WriteRawAsync(buf.AsMemory(0, len), ct).ConfigureAwait(false);
        _logDebug?.Invoke($"fixp Establish sent keepAlive={_options.KeepAliveIntervalMillis}ms cod={codType} nextSeq={NextOutboundSeqNo}");

        var ack = await _establishTcs.Task.WaitAsync(_options.HandshakeTimeout, ct).ConfigureAwait(false);
        lock (_seqLock)
        {
            // Server's lastIncomingSeqNo tells us what it has acked from
            // any prior session version; for a fresh session this is 0
            // and we keep our nextOutboundSeqNo at 1.
            _expectedInboundSeqNo = ack.NextSeqNo;
        }
        _state = FixpClientState.Established;
        _logDebug?.Invoke($"fixp Established serverNextSeq={ack.NextSeqNo} serverLastIncoming={ack.LastIncomingSeqNo}");
    }

    /// <summary>
    /// Patches the 18-byte <c>InboundBusinessHeader</c> at the start of
    /// <paramref name="bodyAfterWireHeader"/>: <c>sessionID</c>(4) at
    /// offset 0 and <c>msgSeqNum</c>(4) at offset 4. Returns the assigned
    /// <c>msgSeqNum</c>. Thread-safe; serializes assignment so the wire
    /// order matches numeric order.
    /// </summary>
    public uint AssignOutboundBusinessHeader(Span<byte> bodyAfterWireHeader)
    {
        if (bodyAfterWireHeader.Length < 8)
            throw new ArgumentException("body too small for InboundBusinessHeader", nameof(bodyAfterWireHeader));
        uint assigned;
        lock (_seqLock)
        {
            assigned = _nextOutboundSeqNo++;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bodyAfterWireHeader.Slice(0, 4), _sessionIdNumeric);
        BinaryPrimitives.WriteUInt32LittleEndian(bodyAfterWireHeader.Slice(4, 4), assigned);
        return assigned;
    }

    /// <summary>
    /// Writes a fully-framed (SOFH+SBE header + body) buffer to the wire,
    /// serialising against any concurrent session-layer frames the client
    /// emits (heartbeat, Terminate, RetransmitRequest).
    /// </summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await WriteRawAsync(frame, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inspects an inbound frame after the SOFH+SBE header has been
    /// parsed by the embedding read loop. For session-layer messages
    /// (<c>NegotiateResponse</c>, <c>EstablishAck</c>, <c>Sequence</c>,
    /// <c>NotApplied</c>, <c>Retransmission</c>, <c>RetransmitReject</c>,
    /// <c>Terminate</c>, both <c>*Reject</c> handshake frames) the
    /// disposition is <see cref="InboundDisposition.HandledControl"/>
    /// (or <see cref="InboundDisposition.PeerTerminated"/> for
    /// <c>Terminate</c>); for application frames the disposition is
    /// <see cref="InboundDisposition.PassThrough"/>.
    /// </summary>
    public InboundDisposition HandleInboundFrame(ushort templateId, ReadOnlySpan<byte> body)
    {
        Touch(inbound: true);
        switch (templateId)
        {
            case EntryPointFrameReader.TidNegotiateResponse:
                if (FixpFrameCodec.TryDecodeNegotiateResponse(body, out var nr))
                    _negotiateTcs.TrySetResult(nr);
                else
                    _negotiateTcs.TrySetException(new InvalidOperationException("malformed NegotiateResponse"));
                return InboundDisposition.HandledControl;

            case EntryPointFrameReader.TidNegotiateReject:
                if (FixpFrameCodec.TryDecodeNegotiateReject(body, out var nrj))
                {
                    _state = FixpClientState.Failed;
                    _negotiateTcs.TrySetException(new FixpHandshakeRejectedException(
                        $"Negotiate rejected (code={nrj.RejectCode}, currentSessionVerId={nrj.CurrentSessionVerId})"));
                }
                else
                {
                    _negotiateTcs.TrySetException(new InvalidOperationException("malformed NegotiateReject"));
                }
                return InboundDisposition.HandledControl;

            case EntryPointFrameReader.TidEstablishAck:
                if (FixpFrameCodec.TryDecodeEstablishAck(body, out var ea))
                    _establishTcs.TrySetResult(ea);
                else
                    _establishTcs.TrySetException(new InvalidOperationException("malformed EstablishAck"));
                return InboundDisposition.HandledControl;

            case EntryPointFrameReader.TidEstablishReject:
                if (FixpFrameCodec.TryDecodeEstablishReject(body, out var erj))
                {
                    _state = FixpClientState.Failed;
                    _establishTcs.TrySetException(new FixpHandshakeRejectedException(
                        $"Establish rejected (code={erj.RejectCode}, lastIncomingSeqNo={erj.LastIncomingSeqNo})"));
                }
                else
                {
                    _establishTcs.TrySetException(new InvalidOperationException("malformed EstablishReject"));
                }
                return InboundDisposition.HandledControl;

            case EntryPointFrameReader.TidSequence:
                if (FixpFrameCodec.TryDecodeSequence(body, out var seq))
                {
                    // Spec §4.5.5: a Sequence message restates the sender's nextSeqNo;
                    // it does not consume a sequence number itself. We use it both as
                    // a peer-aliveness signal and to detect gaps in the recoverable
                    // (server→client) stream.
                    HandleSequenceMaybeGap(seq.NextSeqNo);
                }
                return InboundDisposition.HandledControl;

            case EntryPointFrameReader.TidNotApplied:
                if (FixpFrameCodec.TryDecodeNotApplied(body, out var na))
                {
                    // Idempotent client→server flow: server already advanced
                    // its expected past the gap and accepted the latest message.
                    // We log the loss; a future enhancement could replay from
                    // an outbound retransmit ring buffer if the embedding test
                    // demands strict at-least-once semantics.
                    _logWarn?.Invoke($"fixp NotApplied: server gap fromSeqNo={na.FromSeqNo} count={na.Count} (idempotent flow — not resending)");
                }
                return InboundDisposition.HandledControl;

            case EntryPointFrameReader.TidRetransmission:
                if (FixpFrameCodec.TryDecodeRetransmission(body, out var rt))
                {
                    // The retransmitted business messages follow as
                    // independent SBE frames on the wire after this
                    // Retransmission header. The read loop will see them
                    // as ordinary application frames and call
                    // TrackInboundBusinessSeq(...) on each. We just align
                    // our expected counter to the start of the burst so
                    // gap detection treats them as in-order.
                    lock (_seqLock) _expectedInboundSeqNo = rt.NextSeqNo;
                    _logDebug?.Invoke($"fixp Retransmission: next={rt.NextSeqNo} count={rt.Count}");
                }
                return InboundDisposition.HandledControl;

            case EntryPointFrameReader.TidRetransmitReject:
                if (FixpFrameCodec.TryDecodeRetransmitReject(body, out var rrj))
                {
                    _logWarn?.Invoke($"fixp RetransmitReject: code={rrj.RejectCode} requestTs={rrj.RequestTimestampNanos}");
                }
                return InboundDisposition.HandledControl;

            case EntryPointFrameReader.TidTerminate:
                if (FixpFrameCodec.TryDecodeTerminate(body, out var tm))
                {
                    _logDebug?.Invoke($"fixp Terminate received code={tm.TerminationCode}");
                }
                _state = FixpClientState.Closed;
                return InboundDisposition.PeerTerminated;

            default:
                return InboundDisposition.PassThrough;
        }
    }

    /// <summary>
    /// Tracks an inbound business message's <c>msgSeqNum</c> (read from
    /// body[4..8] by the caller). Detects gaps in the recoverable
    /// server→client stream and, when <see cref="FixpClientOptions.RetransmitOnGap"/>
    /// is true, fires off a <c>RetransmitRequest</c>. Returns
    /// <c>true</c> if the message is in-order or a valid retransmission;
    /// <c>false</c> if it is a duplicate that the caller should drop.
    /// </summary>
    public bool TrackInboundBusinessSeq(uint msgSeqNum)
    {
        uint expected;
        bool gap = false;
        uint gapFrom = 0, gapCount = 0;
        lock (_seqLock)
        {
            expected = _expectedInboundSeqNo;
            if (msgSeqNum == 0)
            {
                // Pre-handshake legacy frame (msgSeqNum unset). Accept and don't advance.
                return true;
            }
            if (msgSeqNum < expected)
            {
                _logWarn?.Invoke($"fixp duplicate inbound msgSeqNum={msgSeqNum} expected={expected}");
                return false;
            }
            if (msgSeqNum > expected)
            {
                gap = true;
                gapFrom = expected;
                gapCount = msgSeqNum - expected;
            }
            _expectedInboundSeqNo = msgSeqNum + 1;
        }
        if (gap && _options.RetransmitOnGap && _state == FixpClientState.Established)
        {
            _ = Task.Run(() => SendRetransmitRequestAsync(gapFrom, gapCount, CancellationToken.None));
        }
        return true;
    }

    /// <summary>
    /// Emits a <c>Sequence</c> heartbeat if no outbound traffic has been
    /// sent for ≥ keepAlive/2. Safe to call from a periodic timer.
    /// </summary>
    public async Task TickHeartbeatAsync(CancellationToken ct)
    {
        if (_state != FixpClientState.Established) return;
        var sinceOut = DateTime.UtcNow - LastOutboundUtc;
        if (sinceOut < KeepAlive / 2) return;
        byte[] buf = new byte[FixpFrameCodec.SequenceBlock + EntryPointFrameReader.WireHeaderSize];
        int len = FixpFrameCodec.EncodeSequence(buf, NextOutboundSeqNo);
        await WriteRawAsync(buf.AsMemory(0, len), ct).ConfigureAwait(false);
        _logDebug?.Invoke($"fixp Sequence heartbeat sent next={NextOutboundSeqNo}");
    }

    /// <summary>
    /// Returns true when the peer has been silent for &gt; 1.5 × keepAlive,
    /// which the caller should treat as a dead session per spec §4.6.4.
    /// </summary>
    public bool IsPeerStale()
    {
        if (_state != FixpClientState.Established) return false;
        var since = DateTime.UtcNow - LastInboundUtc;
        return since > (KeepAlive + KeepAlive / 2);
    }

    public async Task TerminateAsync(byte code, CancellationToken ct)
    {
        if (_state == FixpClientState.Closed || _state == FixpClientState.Terminating)
            return;
        _state = FixpClientState.Terminating;
        try
        {
            byte[] buf = new byte[FixpFrameCodec.TerminateBlock + EntryPointFrameReader.WireHeaderSize];
            int len = FixpFrameCodec.EncodeTerminate(buf, _sessionIdNumeric, _sessionVerId, code);
            await WriteRawAsync(buf.AsMemory(0, len), ct).ConfigureAwait(false);
            _logDebug?.Invoke($"fixp Terminate sent code={code}");
        }
        catch (Exception ex)
        {
            _logWarn?.Invoke($"fixp Terminate write failed: {ex.Message}");
        }
        finally
        {
            _state = FixpClientState.Closed;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_state == FixpClientState.Established)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await TerminateAsync((byte)FixpSbe.TerminationCode.FINISHED, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort on dispose.
            }
        }
        _writeLock.Dispose();
    }

    // ===== internals ========================================================

    private async Task SendRetransmitRequestAsync(uint fromSeqNo, uint count, CancellationToken ct)
    {
        try
        {
            byte[] buf = new byte[FixpFrameCodec.RetransmitRequestBlock + EntryPointFrameReader.WireHeaderSize];
            int len = FixpFrameCodec.EncodeRetransmitRequest(buf, _sessionIdNumeric,
                NowNanos(), fromSeqNo, count);
            await WriteRawAsync(buf.AsMemory(0, len), ct).ConfigureAwait(false);
            _logDebug?.Invoke($"fixp RetransmitRequest sent from={fromSeqNo} count={count}");
        }
        catch (Exception ex)
        {
            _logWarn?.Invoke($"fixp RetransmitRequest write failed: {ex.Message}");
        }
    }

    private void HandleSequenceMaybeGap(uint peerNextSeq)
    {
        uint expected;
        bool gap = false;
        uint from = 0, count = 0;
        lock (_seqLock)
        {
            expected = _expectedInboundSeqNo;
            if (peerNextSeq > expected)
            {
                gap = true;
                from = expected;
                count = peerNextSeq - expected;
                // Don't move expected past the gap; let the retransmission fill it.
            }
        }
        if (gap)
        {
            _logWarn?.Invoke($"fixp inbound gap (Sequence): expected={expected} peerNext={peerNextSeq}");
            if (_options.RetransmitOnGap && _state == FixpClientState.Established)
            {
                _ = Task.Run(() => SendRetransmitRequestAsync(from, count, CancellationToken.None));
            }
        }
    }

    private async Task WriteRawAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            Touch(inbound: false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Touch(bool inbound)
    {
        long ticks = DateTime.UtcNow.Ticks;
        if (inbound) Interlocked.Exchange(ref _lastInboundTicks, ticks);
        else Interlocked.Exchange(ref _lastOutboundTicks, ticks);
    }

    private static ulong NowNanos() => (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L);

    /// <summary>
    /// Builds the gateway-expected credentials JSON document
    /// (<c>{"auth_type":"basic","username":"&lt;sessionId&gt;","access_key":"&lt;key&gt;"}</c>)
    /// per <c>NegotiateCredentials.TryParse</c> in the gateway. The
    /// JSON is encoded as UTF-8 ASCII; both fields are validated by the
    /// gateway as printable ASCII so we keep them raw.
    /// </summary>
    private static byte[] BuildCredentialsJson(string sessionId, string accessKey)
    {
        var json = $"{{\"auth_type\":\"basic\",\"username\":\"{sessionId}\",\"access_key\":\"{accessKey}\"}}";
        return Encoding.ASCII.GetBytes(json);
    }
}

/// <summary>
/// Thrown when the server rejects a Negotiate or Establish handshake.
/// Carries the rejection code in the message; the caller should treat
/// this as fatal for the session.
/// </summary>
public sealed class FixpHandshakeRejectedException : Exception
{
    public FixpHandshakeRejectedException(string message) : base(message) { }
}
