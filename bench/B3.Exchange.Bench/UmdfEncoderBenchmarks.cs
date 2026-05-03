using BenchmarkDotNet.Attributes;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.Bench;

/// <summary>
/// UMDF wire-encoder hot-path benchmarks. Each benchmark writes one frame
/// into a pre-allocated buffer, so timings reflect pure SBE/byte-layout cost
/// (no allocations expected; verified via <see cref="MemoryDiagnoserAttribute"/>).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class UmdfEncoderBenchmarks
{
    private byte[] _buf = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 1500 bytes is more than enough for any single UMDF frame we emit.
        _buf = new byte[1500];
    }

    [Benchmark]
    public int PacketHeader()
    {
        return UmdfWireEncoder.WritePacketHeader(_buf, 1, 1, 12345, 1_700_000_000_000_000_000UL);
    }

    [Benchmark]
    public int OrderAdded_BidLevel()
    {
        return UmdfWireEncoder.WriteOrderAddedFrame(
            _buf,
            securityId: BenchInstruments.PetrSecId,
            secondaryOrderId: 1234,
            mdEntryType: UmdfWireEncoder.MdEntryTypeBid,
            priceMantissa: BenchInstruments.Px(32.00m),
            size: 100,
            rptSeq: 42,
            insertTimestampNanos: 1_700_000_000_000_000_000UL);
    }

    [Benchmark]
    public int OrderDeleted()
    {
        return UmdfWireEncoder.WriteOrderDeletedFrame(
            _buf,
            securityId: BenchInstruments.PetrSecId,
            secondaryOrderId: 1234,
            mdEntryType: UmdfWireEncoder.MdEntryTypeBid,
            size: 100,
            rptSeq: 43,
            transactTimeNanos: 1_700_000_000_000_000_000UL,
            priceMantissa: BenchInstruments.Px(32.00m));
    }

    [Benchmark]
    public int Trade_FullyTagged()
    {
        return UmdfWireEncoder.WriteTradeFrame(
            _buf,
            securityId: BenchInstruments.PetrSecId,
            priceMantissa: BenchInstruments.Px(32.00m),
            size: 100,
            tradeId: 99,
            tradeDate: 9234,
            transactTimeNanos: 1_700_000_000_000_000_000UL,
            rptSeq: 44,
            buyerFirm: 7,
            sellerFirm: 8);
    }
}
