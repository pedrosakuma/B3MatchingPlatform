using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using B3.Exchange.Core;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Persistence;

internal readonly record struct WalReplayResult(
    IReadOnlyList<WalRecord> Records,
    int CorruptCount,
    int LegacyCount);

/// <summary>
/// Mutable counter holder so callers observe partial counts even
/// when <see cref="WalReplay.ReadAll"/> throws — preserving the
/// pre-refactor behaviour where <c>LastReadCorruptCount</c> was
/// incremented inline before raising
/// <see cref="WalCorruptionException"/>.
/// </summary>
internal sealed class WalReplayCounters
{
    public int CorruptCount;
    public int LegacyCount;
}

/// <summary>
/// Pure-function WAL reader extracted from
/// <see cref="FileChannelWriteAheadLog"/>. Parses the JSON-Lines +
/// per-record Crc32C framing, distinguishes torn-final writes
/// (tolerated, return the prefix) from mid-stream corruption
/// (throws <see cref="WalCorruptionException"/>), and counts legacy
/// records (pre-#285 lines without a CRC suffix).
/// </summary>
internal static class WalReplay
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };

    public static WalReplayResult ReadAll(string path, byte channelNumber, ILogger logger, WalReplayCounters? counters = null)
    {
        counters ??= new WalReplayCounters();
        counters.CorruptCount = 0;
        counters.LegacyCount = 0;
        if (!File.Exists(path))
        {
            return new WalReplayResult(Array.Empty<WalRecord>(), 0, 0);
        }
        var result = new List<WalRecord>();
        List<string> rawLines;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            rawLines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                rawLines.Add(line);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "channel {ChannelNumber}: failed to read WAL at {Path}; treating as empty",
                channelNumber, path);
            return new WalReplayResult(Array.Empty<WalRecord>(), 0, 0);
        }

        int lastNonEmptyIdx = -1;
        for (int i = rawLines.Count - 1; i >= 0; i--)
        {
            if (rawLines[i].Length > 0) { lastNonEmptyIdx = i; break; }
        }

        long? prevSeq = null;
        for (int i = 0; i <= lastNonEmptyIdx; i++)
        {
            var line = rawLines[i];
            int lineNumber = i + 1;
            bool isLastNonEmpty = i == lastNonEmptyIdx;
            if (line.Length == 0) continue;

            int tab = line.LastIndexOf('\t');
            if (tab >= 0 && line.Length - tab - 1 == 8)
            {
                var crcHex = line.AsSpan(tab + 1, 8);
                if (!TryParseHex32(crcHex, out uint storedCrc))
                {
                    if (!ConsumeLegacyLine(line, lineNumber, isLastNonEmpty, channelNumber, logger, result, ref prevSeq, counters))
                        break;
                    continue;
                }
                var jsonBytes = Encoding.UTF8.GetBytes(line[..tab]);
                uint computedCrc = Crc32C.Compute(jsonBytes);
                if (computedCrc != storedCrc)
                {
                    counters.CorruptCount++;
                    if (isLastNonEmpty)
                    {
                        logger.LogWarning(
                            "channel {ChannelNumber}: WAL line {LineNumber} CRC mismatch on the FINAL record (stored=0x{StoredCrc:X8} computed=0x{ComputedCrc:X8}); treating as torn-tail and truncating replay here",
                            channelNumber, lineNumber, storedCrc, computedCrc);
                        break;
                    }
                    var msg = $"channel {channelNumber}: WAL line {lineNumber} CRC mismatch (stored=0x{storedCrc:X8} computed=0x{computedCrc:X8}) and a well-formed record exists after it; refusing to replay around mid-stream corruption";
                    logger.LogCritical("{Message}", msg);
                    throw new WalCorruptionException(channelNumber, lineNumber, msg);
                }
                WalRecord? rec;
                try
                {
                    rec = JsonSerializer.Deserialize<WalRecord>(jsonBytes, JsonOptions);
                }
                catch (Exception ex)
                {
                    counters.CorruptCount++;
                    var msg = $"channel {channelNumber}: WAL line {lineNumber} JSON parse failed despite CRC match: {ex.Message}";
                    logger.LogCritical(ex, "{Message}", msg);
                    throw new WalCorruptionException(channelNumber, lineNumber, msg, ex);
                }
                if (rec is null)
                {
                    var msg = $"channel {channelNumber}: WAL line {lineNumber} deserialized to null";
                    logger.LogCritical("{Message}", msg);
                    throw new WalCorruptionException(channelNumber, lineNumber, msg);
                }
                CheckSeqContiguity(rec.Seq, lineNumber, channelNumber, logger, ref prevSeq);
                result.Add(rec);
            }
            else
            {
                if (!ConsumeLegacyLine(line, lineNumber, isLastNonEmpty, channelNumber, logger, result, ref prevSeq, counters))
                    break;
            }
        }
        return new WalReplayResult(result, counters.CorruptCount, counters.LegacyCount);
    }

    private static void CheckSeqContiguity(long currentSeq, int lineNumber, byte channelNumber, ILogger logger, ref long? prevSeq)
    {
        if (prevSeq.HasValue && currentSeq != prevSeq.Value + 1)
        {
            var msg = $"channel {channelNumber}: WAL line {lineNumber} seq={currentSeq} is not contiguous after seq={prevSeq.Value}; refusing to replay around the gap";
            logger.LogCritical("{Message}", msg);
            throw new WalCorruptionException(channelNumber, lineNumber, msg);
        }
        prevSeq = currentSeq;
    }

    private static bool ConsumeLegacyLine(
        string line,
        int lineNumber,
        bool isLastNonEmpty,
        byte channelNumber,
        ILogger logger,
        List<WalRecord> result,
        ref long? prevSeq,
        WalReplayCounters counters)
    {
        WalRecord? rec;
        try
        {
            rec = JsonSerializer.Deserialize<WalRecord>(line, JsonOptions);
        }
        catch (Exception ex)
        {
            if (isLastNonEmpty)
            {
                logger.LogWarning(ex,
                    "channel {ChannelNumber}: legacy WAL line {LineNumber} failed to parse on the FINAL record; treating as torn-tail and truncating replay here",
                    channelNumber, lineNumber);
                return false;
            }
            var msg = $"channel {channelNumber}: legacy WAL line {lineNumber} failed to parse and a well-formed record exists after it; refusing to replay around mid-stream corruption";
            logger.LogCritical(ex, "{Message}", msg);
            throw new WalCorruptionException(channelNumber, lineNumber, msg, ex);
        }
        if (rec is null)
        {
            if (isLastNonEmpty) return false;
            var msg = $"channel {channelNumber}: legacy WAL line {lineNumber} deserialized to null mid-stream";
            logger.LogCritical("{Message}", msg);
            throw new WalCorruptionException(channelNumber, lineNumber, msg);
        }
        CheckSeqContiguity(rec.Seq, lineNumber, channelNumber, logger, ref prevSeq);
        result.Add(rec);
        counters.LegacyCount++;
        return true;
    }

    private static bool TryParseHex32(ReadOnlySpan<char> chars, out uint value)
    {
        value = 0;
        if (chars.Length != 8) return false;
        for (int i = 0; i < 8; i++)
        {
            int d = HexDigit(chars[i]);
            if (d < 0) return false;
            value = (value << 4) | (uint)d;
        }
        return true;

        static int HexDigit(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }
}
