using B3.Exchange.Instruments;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.Core;

/// <summary>
/// Periodically emits <c>SecurityDefinition_12</c> ("SecurityDefinition_d"
/// in FIX terms) for every configured instrument on a dedicated
/// InstrumentDef UMDF channel. Lets late-joining consumers resolve the
/// SecurityIDs they will see on MBO / Trade frames without a pre-loaded
/// instrument list.
///
/// Threading: a single <see cref="System.Threading.PeriodicTimer"/>-driven
/// background loop owns all writes — the same dispatcher-thread discipline
/// the matching engine uses. <see cref="PublishOnce"/> is also serialized
/// with the timer loop via <c>_publishLock</c> so tests can drive cycles
/// deterministically without racing the timer.
///
/// Wire layout per cycle: one or more UDP datagrams, each starting with the
/// 16-byte <c>PacketHeader</c> followed by as many SecurityDefinition_12
/// frames as fit in <see cref="MaxPacketBytes"/>. Sequence numbers are
/// monotonic across cycles for the lifetime of the publisher.
/// </summary>
public sealed class InstrumentDefinitionPublisher : IAsyncDisposable
{
    /// <summary>
    /// Hard upper bound on a single InstrumentDef UDP datagram. Same value
    /// the incremental dispatcher uses; chosen to stay well under a 1500-byte
    /// MTU including IP+UDP headers.
    /// </summary>
    public const int MaxPacketBytes = 1400;

    private const int FrameSize =
        WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.SecDefBodyTotal;

    public byte ChannelNumber { get; }
    public ushort SequenceVersion { get; }
    public uint SequenceNumber { get; private set; }
    public TimeSpan Cadence { get; }

    private readonly IReadOnlyList<Instrument> _instruments;
    private readonly IUmdfPacketSink _sink;
    private readonly Func<ulong> _nowNanos;
    private readonly byte[] _packetBuf = new byte[MaxPacketBytes];
    private readonly object _publishLock = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public InstrumentDefinitionPublisher(
        byte channelNumber,
        IReadOnlyList<Instrument> instruments,
        IUmdfPacketSink sink,
        TimeSpan cadence,
        Func<ulong>? nowNanos = null,
        ushort sequenceVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(sink);
        if (cadence <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cadence), "cadence must be positive");
        // SecDef frame is ~251 bytes; we need to fit at least one with the
        // 16-byte PacketHeader inside MaxPacketBytes — guaranteed by const,
        // but keep the assertion in case future schema bumps grow the body.
        if (FrameSize + WireOffsets.PacketHeaderSize > MaxPacketBytes)
            throw new InvalidOperationException(
                $"SecurityDefinition frame ({FrameSize} bytes) does not fit in InstrumentDef packet ({MaxPacketBytes} bytes).");

        ChannelNumber = channelNumber;
        _instruments = instruments;
        _sink = sink;
        _nowNanos = nowNanos ?? DefaultNowNanos;
        Cadence = cadence;
        SequenceVersion = sequenceVersion;
        SequenceNumber = 0;
    }

    private static ulong DefaultNowNanos()
        => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;

    /// <summary>
    /// Starts the periodic loop. Idempotent: a second call is a no-op.
    /// </summary>
    public void Start()
    {
        if (_loopTask != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loopTask = Task.Factory.StartNew(
            () => RunLoopAsync(ct), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Fire one cycle immediately so consumers connecting at startup can
        // resolve symbols within Cadence rather than after one full interval.
        try { PublishOnce(); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Cadence);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                PublishOnce();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Emits one full cycle synchronously (one packet per ≤1400-byte chunk
    /// of SecurityDefinition frames). Safe to call from any thread; calls
    /// are serialized with the periodic-timer loop.
    /// </summary>
    public void PublishOnce()
    {
        lock (_publishLock)
        {
            if (_instruments.Count == 0) return;

            int packetWritten = StartPacket();
            for (int i = 0; i < _instruments.Count; i++)
            {
                if (packetWritten + FrameSize > MaxPacketBytes)
                {
                    FlushPacket(packetWritten);
                    packetWritten = StartPacket();
                }

                var inst = _instruments[i];
                int n = UmdfWireEncoder.WriteSecurityDefinitionFrame(
                    _packetBuf.AsSpan(packetWritten),
                    securityId: inst.SecurityId,
                    symbol: inst.Symbol,
                    isin: inst.Isin,
                    securityTypeByte: SecurityTypeMap.ToSbeByte(inst.SecurityType),
                    totNoRelatedSym: (uint)_instruments.Count);
                packetWritten += n;
            }
            if (packetWritten > WireOffsets.PacketHeaderSize)
                FlushPacket(packetWritten);
        }
    }

    private int StartPacket()
    {
        UmdfWireEncoder.WritePacketHeader(_packetBuf,
            ChannelNumber, SequenceVersion, sequenceNumber: 0, sendingTimeNanos: 0);
        return WireOffsets.PacketHeaderSize;
    }

    private void FlushPacket(int written)
    {
        SequenceNumber++;
        UmdfWireEncoder.PatchPacketHeader(
            _packetBuf.AsSpan(0, WireOffsets.PacketHeaderSize),
            SequenceNumber, _nowNanos());
        _sink.Publish(ChannelNumber, _packetBuf.AsSpan(0, written));
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
        {
            try { _cts.Cancel(); } catch { }
        }
        if (_loopTask != null)
        {
            try { await _loopTask.ConfigureAwait(false); } catch { }
            _loopTask = null;
        }
        _cts?.Dispose();
        _cts = null;
    }
}
