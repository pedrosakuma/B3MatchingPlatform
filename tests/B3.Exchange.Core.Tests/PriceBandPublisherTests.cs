using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Umdf.Mbo.Sbe.V17;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.Core.Tests;

public sealed class PriceBandPublisherTests
{
    private sealed class FrameSink : IUmdfFrameSink
    {
        private readonly byte[] _buffer = new byte[2048];
        public int Written { get; private set; }
        public ReadOnlySpan<byte> Data => _buffer.AsSpan(0, Written);

        public Span<byte> Reserve(int size) => _buffer.AsSpan(Written, size);
        public void Commit(int written) => Written += written;
    }

    [Fact]
    public void PublishOnce_AllocatesRptSeqIndependentlyForEverySecurity()
    {
        var publisher = new PriceBandPublisher(
            [
                Instrument("PETR4", 1),
                Instrument("VALE3", 2),
                Instrument("ITUB4", 3),
            ],
            TimeSpan.FromSeconds(1));
        var sink = new FrameSink();
        var counters = new Dictionary<long, uint>
        {
            [1] = 7,
            [2] = 2,
            [3] = 11,
        };

        int published = publisher.PublishOnce(
            sink,
            securityId => Assert.True(counters.ContainsKey(securityId)),
            securityId => counters[securityId] = unchecked(counters[securityId] + 1),
            mdEntryTimestampNanos: 123);

        Assert.Equal(3, published);
        Assert.Equal(8u, ReadRptSeq(sink.Data, frameIndex: 0));
        Assert.Equal(3u, ReadRptSeq(sink.Data, frameIndex: 1));
        Assert.Equal(12u, ReadRptSeq(sink.Data, frameIndex: 2));
    }

    [Fact]
    public void PublishOnce_PreflightsEverySecurityBeforeWritingAnyFrame()
    {
        var publisher = new PriceBandPublisher(
            [
                Instrument("PETR4", 1),
                Instrument("VALE3", 2),
                Instrument("ITUB4", 3),
            ],
            TimeSpan.FromSeconds(1));
        var sink = new FrameSink();
        int allocations = 0;

        Assert.Throws<RptSeqExhaustedException>(() => publisher.PublishOnce(
            sink,
            securityId =>
            {
                if (securityId == 3) throw new RptSeqExhaustedException(securityId);
            },
            _ =>
            {
                allocations++;
                return (uint)allocations;
            },
            mdEntryTimestampNanos: 123));

        Assert.Equal(0, allocations);
        Assert.Equal(0, sink.Written);
    }

    private static uint ReadRptSeq(ReadOnlySpan<byte> data, int frameIndex)
    {
        int frameStart = 0;
        for (int i = 0; i < frameIndex; i++)
            frameStart += System.Runtime.InteropServices.MemoryMarshal.Read<ushort>(data[frameStart..]);
        int bodyStart = frameStart + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize;
        Assert.True(PriceBand_22Data.TryParse(
            data.Slice(bodyStart, WireOffsets.PriceBandBlockLength),
            out var reader));
        Assert.True(reader.Data.RptSeq.HasValue);
        return reader.Data.RptSeq.GetValueOrDefault();
    }

    private static Instrument Instrument(string symbol, long securityId) => new()
    {
        Symbol = symbol,
        SecurityId = securityId,
        TickSize = 0.01m,
        LotSize = 100,
        MinPrice = 0.01m,
        MaxPrice = 1_000m,
        LowerPriceBand = 1m,
        UpperPriceBand = 100m,
        Currency = "BRL",
        Isin = $"BR{symbol}000000",
        SecurityType = "EQUITY",
    };
}
