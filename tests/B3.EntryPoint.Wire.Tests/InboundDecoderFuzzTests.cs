using B3.EntryPoint.Wire;

namespace B3.EntryPoint.Wire.Tests;

/// <summary>
/// Issue #178 (D2): property-style fuzz tests for the inbound decoders.
///
/// Property invariants:
///   1. <c>EntryPointFrameReader.TryParseInboundHeader</c> NEVER throws,
///      NEVER infinite-loops, and returns a structured <c>HeaderError</c>
///      on any malformed input (including arbitrary random bytes,
///      truncated headers, and "semi-valid" headers with valid SOFH but
///      garbage SBE).
///   2. <c>EntryPointVarData.ValidateDetailed</c> NEVER throws on any
///      input length / contents / spec combination — every malformed
///      blob is classified as <c>ProtocolError</c>, <c>FieldTooLong</c>,
///      or <c>ContainsLineBreaks</c>.
///   3. The <c>EntryPointFixpFrameCodec.TryDecode*</c> family NEVER
///      throws on random bodies — short bodies must return false; longer
///      bodies must either succeed or return false without OOB reads.
///
/// Iterations: 2000 per property (well above the 1000+ acceptance bar)
/// using deterministic seeded <see cref="System.Random"/> so failures
/// are reproducible from the seed value alone.
/// </summary>
public class InboundDecoderFuzzTests
{
    private const int Iterations = 2000;
    private const int Seed = 0x5BE5_D7CA;

    [Fact]
    public void TryParseInboundHeader_NeverThrows_OnRandomBytes()
    {
        var rng = new Random(Seed);
        var buf = new byte[512];
        int falseCount = 0, trueCount = 0;
        for (int i = 0; i < Iterations; i++)
        {
            int len = rng.Next(0, buf.Length + 1);
            rng.NextBytes(buf.AsSpan(0, len));
            bool ok = EntryPointFrameReader.TryParseInboundHeader(
                buf.AsSpan(0, len),
                out var info,
                out var err,
                out var msg);

            if (ok)
            {
                trueCount++;
                Assert.Equal(EntryPointFrameReader.HeaderError.None, err);
                Assert.True(info.MessageLength >= EntryPointFrameReader.WireHeaderSize);
                Assert.True(info.MessageLength <= EntryPointFrameReader.MaxInboundMessageLength);
                Assert.True(info.BlockLength >= 0);
                Assert.True(info.VarDataLength >= 0);
            }
            else
            {
                falseCount++;
                Assert.NotEqual(EntryPointFrameReader.HeaderError.None, err);
                Assert.NotNull(msg);
            }
        }
        // Random bytes almost certainly fail; accidental success is fine
        // but we should have observed *plenty* of false outcomes.
        Assert.True(falseCount > Iterations / 2,
            $"expected most random inputs to be rejected, got false={falseCount} true={trueCount}");
    }

    [Fact]
    public void TryParseInboundHeader_NeverThrows_OnSemiValidFrames()
    {
        // Generate frames with a *valid* SOFH header (encoding=0xEB50,
        // length in range) but garbage SBE/template bytes. Exercises the
        // schema/template/blockLength classification paths.
        var rng = new Random(Seed ^ 0x1357);
        var buf = new byte[512];
        for (int i = 0; i < Iterations; i++)
        {
            int messageLength = rng.Next(
                EntryPointFrameReader.WireHeaderSize,
                EntryPointFrameReader.MaxInboundMessageLength + 1);
            buf[0] = (byte)(messageLength & 0xFF);
            buf[1] = (byte)((messageLength >> 8) & 0xFF);
            buf[2] = 0x50; // SofhEncodingType = 0xEB50 LE
            buf[3] = 0xEB;
            // Garbage SBE header + payload up to messageLength.
            rng.NextBytes(buf.AsSpan(4, messageLength - 4));

            bool ok = EntryPointFrameReader.TryParseInboundHeader(
                buf.AsSpan(0, messageLength),
                out _,
                out var err,
                out var msg);

            if (!ok)
            {
                Assert.NotEqual(EntryPointFrameReader.HeaderError.None, err);
                Assert.NotNull(msg);
            }
        }
    }

    [Fact]
    public void ValidateDetailed_NeverThrows_OnRandomVarData()
    {
        var rng = new Random(Seed ^ 0x2468);
        // Cover every template's varData spec including the empty None case.
        ushort[] templates =
        {
            EntryPointFrameReader.TidSimpleNewOrder,
            EntryPointFrameReader.TidSimpleModifyOrder,
            EntryPointFrameReader.TidNewOrderSingle,
            EntryPointFrameReader.TidOrderCancelReplaceRequest,
            EntryPointFrameReader.TidOrderCancelRequest,
            EntryPointFrameReader.TidNegotiate,
            EntryPointFrameReader.TidEstablish,
            // unknown -> empty spec
            9999,
        };
        var buf = new byte[300];
        for (int i = 0; i < Iterations; i++)
        {
            ushort tid = templates[rng.Next(templates.Length)];
            ushort version = tid switch
            {
                EntryPointFrameReader.TidSimpleNewOrder => 2,
                EntryPointFrameReader.TidSimpleModifyOrder => 2,
                EntryPointFrameReader.TidNewOrderSingle => 2,
                EntryPointFrameReader.TidOrderCancelReplaceRequest => 2,
                EntryPointFrameReader.TidOrderCancelRequest => 0,
                EntryPointFrameReader.TidNegotiate => 0,
                EntryPointFrameReader.TidEstablish => 0,
                _ => 0,
            };
            int len = rng.Next(0, buf.Length + 1);
            rng.NextBytes(buf.AsSpan(0, len));

            var spec = EntryPointVarData.ExpectedFor(tid, version);
            var result = EntryPointVarData.ValidateDetailed(buf.AsSpan(0, len), spec);

            // Property: result.Kind is one of the four enum values; if
            // a BMR text is expected it is non-null/non-empty.
            Assert.True(Enum.IsDefined(typeof(EntryPointVarData.FailureKind), result.Kind));
            if (result.IsBusinessReject)
            {
                var text = result.BmrText();
                Assert.False(string.IsNullOrEmpty(text));
            }
        }
    }

    [Fact]
    public void TryDecodeFixpFrames_NeverThrow_OnRandomBodies()
    {
        var rng = new Random(Seed ^ 0x9ABC);
        var buf = new byte[256];
        for (int i = 0; i < Iterations; i++)
        {
            int len = rng.Next(0, buf.Length + 1);
            rng.NextBytes(buf.AsSpan(0, len));
            var span = buf.AsSpan(0, len);

            // Each TryDecode is a black-box: must not throw on any input.
            _ = EntryPointFixpFrameCodec.TryDecodeNegotiateResponse(span, out _);
            _ = EntryPointFixpFrameCodec.TryDecodeNegotiateReject(span, out _);
            _ = EntryPointFixpFrameCodec.TryDecodeEstablishAck(span, out _);
            _ = EntryPointFixpFrameCodec.TryDecodeEstablishReject(span, out _);
            _ = EntryPointFixpFrameCodec.TryDecodeSequence(span, out _);
            _ = EntryPointFixpFrameCodec.TryDecodeNotApplied(span, out _);
            _ = EntryPointFixpFrameCodec.TryDecodeRetransmission(span, out _);
            _ = EntryPointFixpFrameCodec.TryDecodeTerminate(span, out _);
            _ = EntryPointFixpFrameCodec.TryDecodeRetransmitReject(span, out _);
        }
    }

    [Fact]
    public void TryParseInboundHeader_BoundaryLengths_NeverThrow()
    {
        // Targeted boundary fuzzing around WireHeaderSize and MaxInboundMessageLength.
        var rng = new Random(Seed ^ 0xDEAD);
        int[] lengths =
        {
            0, 1, 2, 3,
            EntryPointFrameReader.WireHeaderSize - 1,
            EntryPointFrameReader.WireHeaderSize,
            EntryPointFrameReader.WireHeaderSize + 1,
            EntryPointFrameReader.MaxInboundMessageLength - 1,
            EntryPointFrameReader.MaxInboundMessageLength,
            EntryPointFrameReader.MaxInboundMessageLength + 1,
        };
        var buf = new byte[EntryPointFrameReader.MaxInboundMessageLength + 16];
        foreach (var len in lengths)
        {
            for (int rep = 0; rep < 200; rep++)
            {
                rng.NextBytes(buf.AsSpan(0, Math.Min(len, buf.Length)));
                _ = EntryPointFrameReader.TryParseInboundHeader(
                    buf.AsSpan(0, Math.Min(len, buf.Length)),
                    out _, out _, out _);
            }
        }
    }
}
