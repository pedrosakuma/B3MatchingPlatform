using B3.Exchange.Contracts.Time;
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

    private const int FrameSizeMax =
        WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.SecDefBodyTotalOneUnderlying;

    public byte ChannelNumber { get; }
    public ushort SequenceVersion { get; }
    public uint SequenceNumber { get; private set; }
    public TimeSpan Cadence { get; }

    private readonly IReadOnlyList<Instrument> _instruments;
    private readonly IUmdfPacketSink _sink;
    private readonly INanosTimeSource _timeSource;
    private readonly byte[] _packetBuf = new byte[MaxPacketBytes];
    private readonly object _publishLock = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public InstrumentDefinitionPublisher(
        byte channelNumber,
        IReadOnlyList<Instrument> instruments,
        IUmdfPacketSink sink,
        TimeSpan cadence,
        INanosTimeSource? timeSource = null,
        ushort sequenceVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(sink);
        if (cadence <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cadence), "cadence must be positive");
        // SecDef option frame (one NoUnderlyings entry) is ~279 bytes; we
        // need to fit at least one with the 16-byte PacketHeader inside
        // MaxPacketBytes — guaranteed by const, but keep the assertion in
        // case future schema bumps grow the body.
        if (FrameSizeMax + WireOffsets.PacketHeaderSize > MaxPacketBytes)
            throw new InvalidOperationException(
                $"SecurityDefinition frame ({FrameSizeMax} bytes) does not fit in InstrumentDef packet ({MaxPacketBytes} bytes).");

        ChannelNumber = channelNumber;
        _instruments = instruments;
        _sink = sink;
        _timeSource = timeSource ?? SystemNanosTimeSource.Instance;
        Cadence = cadence;
        SequenceVersion = sequenceVersion;
        SequenceNumber = 0;
    }


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
        try { PublishOnce(); } catch (OperationCanceledException) { return; /* expected: publisher cancelled before first cycle */ }

        using var timer = new PeriodicTimer(Cadence);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                PublishOnce();
            }
        }
        catch (OperationCanceledException) { /* expected: periodic publisher cancelled during shutdown */ }
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
                var inst = _instruments[i];
                int frameSize = SecDefFrameSizeFor(inst);
                if (packetWritten + frameSize > MaxPacketBytes)
                {
                    FlushPacket(packetWritten);
                    packetWritten = StartPacket();
                }

                var optionFields = BuildOptionFields(inst);
                long validityTs = optionFields.HasValue
                    ? ComputeOptionValidityUtcSeconds(inst.ExpirationDate!.Value)
                    : 0L;
                int n = UmdfWireEncoder.WriteSecurityDefinitionFrame(
                    _packetBuf.AsSpan(packetWritten),
                    securityId: inst.SecurityId,
                    symbol: inst.Symbol,
                    isin: inst.Isin,
                    securityTypeByte: SecurityTypeMap.ToSbeByte(inst.SecurityType),
                    totNoRelatedSym: (uint)_instruments.Count,
                    securityValidityTimestamp: validityTs,
                    optionFields: optionFields);
                packetWritten += n;
            }
            if (packetWritten > WireOffsets.PacketHeaderSize)
                FlushPacket(packetWritten);
        }
    }

    private static int SecDefFrameSizeFor(Instrument inst)
    {
        int body = InstrumentSecurityTypes.IsOption(inst.SecurityType)
            ? WireOffsets.SecDefBodyTotalOneUnderlying
            : WireOffsets.SecDefBodyTotalNoUnderlyings;
        return WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + body;
    }

    /// <summary>
    /// Translates an option <see cref="Instrument"/> into the
    /// <see cref="UmdfWireEncoder.OptionDefinitionFields"/> payload expected
    /// by the encoder. Returns <c>null</c> for non-option instruments so the
    /// encoder writes SBE NULL sentinels for every option field. Throws when
    /// an option instrument is missing a required field — the loader and
    /// trading-rules layer should have rejected such a config long before
    /// we get here, but a clear error here beats a silently-mis-encoded
    /// frame on the wire.
    /// </summary>
    private static UmdfWireEncoder.OptionDefinitionFields? BuildOptionFields(Instrument inst)
    {
        if (!InstrumentSecurityTypes.IsOption(inst.SecurityType))
            return null;

        decimal strike = inst.StrikePrice
            ?? throw new InvalidOperationException($"Option instrument {inst.Symbol} ({inst.SecurityId}) is missing required StrikePrice.");
        DateOnly expiry = inst.ExpirationDate
            ?? throw new InvalidOperationException($"Option instrument {inst.Symbol} ({inst.SecurityId}) is missing required ExpirationDate.");
        var putOrCall = inst.PutOrCall
            ?? throw new InvalidOperationException($"Option instrument {inst.Symbol} ({inst.SecurityId}) is missing required PutOrCall.");
        var exerciseStyle = inst.ExerciseStyle
            ?? throw new InvalidOperationException($"Option instrument {inst.Symbol} ({inst.SecurityId}) is missing required ExerciseStyle.");
        long underlyingId = inst.UnderlyingSecurityId
            ?? throw new InvalidOperationException($"Option instrument {inst.Symbol} ({inst.SecurityId}) is missing required UnderlyingSecurityId.");
        string underlyingSymbol = inst.UnderlyingSymbol
            ?? throw new InvalidOperationException($"Option instrument {inst.Symbol} ({inst.SecurityId}) is missing required UnderlyingSymbol.");
        decimal multiplier = inst.ContractMultiplier
            ?? throw new InvalidOperationException($"Option instrument {inst.Symbol} ({inst.SecurityId}) is missing required ContractMultiplier.");

        return new UmdfWireEncoder.OptionDefinitionFields
        {
            StrikePrice = strike,
            ContractMultiplier = multiplier,
            ExpirationDate = expiry,
            PutOrCallByte = putOrCall switch
            {
                B3.Exchange.Instruments.PutOrCall.Put => (byte)B3.Umdf.Mbo.Sbe.V16.PutOrCall.PUT,
                B3.Exchange.Instruments.PutOrCall.Call => (byte)B3.Umdf.Mbo.Sbe.V16.PutOrCall.CALL,
                _ => throw new InvalidOperationException($"Unsupported PutOrCall value: {putOrCall}"),
            },
            ExerciseStyleByte = exerciseStyle switch
            {
                B3.Exchange.Instruments.ExerciseStyle.European => (byte)B3.Umdf.Mbo.Sbe.V16.ExerciseStyle.EUROPEAN,
                B3.Exchange.Instruments.ExerciseStyle.American => (byte)B3.Umdf.Mbo.Sbe.V16.ExerciseStyle.AMERICAN,
                _ => throw new InvalidOperationException($"Unsupported ExerciseStyle value: {exerciseStyle}"),
            },
            OptPayoutTypeByte = inst.OptPayoutType switch
            {
                null => null,
                B3.Exchange.Instruments.OptPayoutType.Vanilla => (byte)B3.Umdf.Mbo.Sbe.V16.OptPayoutType.VANILLA,
                B3.Exchange.Instruments.OptPayoutType.Capped => (byte)B3.Umdf.Mbo.Sbe.V16.OptPayoutType.CAPPED,
                B3.Exchange.Instruments.OptPayoutType.Binary => (byte)B3.Umdf.Mbo.Sbe.V16.OptPayoutType.BINARY,
                _ => throw new InvalidOperationException($"Unsupported OptPayoutType value: {inst.OptPayoutType}"),
            },
            UnderlyingSecurityId = underlyingId,
            UnderlyingSymbol = underlyingSymbol,
        };
    }

    // B3 venue timezone. The RFC requires the option securityValidityTimestamp
    // to be derived from ExpirationDate end-of-day in venue local time, then
    // expressed as a UTC second count.
    private static readonly TimeZoneInfo VenueTimezone = ResolveVenueTimezone();

    private static TimeZoneInfo ResolveVenueTimezone()
    {
        // America/Sao_Paulo is the IANA id and also works on Windows via the
        // ICU TZ database that ships with .NET 6+. Fall back to UTC only if
        // the host has no TZ data at all — better to encode UTC seconds than
        // to crash the publisher.
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// Returns the UTC Unix-seconds timestamp marking the end of the
    /// expiration day in the venue timezone (the moment trading for the
    /// option is no longer valid). End-of-day is defined as the last second
    /// of the calendar date in venue local time (23:59:59 BRT/BRST).
    /// </summary>
    internal static long ComputeOptionValidityUtcSeconds(DateOnly expirationDate)
    {
        var localEod = new DateTime(
            expirationDate.Year, expirationDate.Month, expirationDate.Day,
            23, 59, 59, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(localEod, VenueTimezone);
        return new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();
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
            SequenceNumber, _timeSource.NowNanos());
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
