using System.Text;
using System.Text.Json;

namespace B3.Exchange.ScenarioReplay;

/// <summary>
/// Append-only JSONL writer for replay tape files. One line per record so
/// the result can be diffed line-for-line against a golden tape with
/// <c>diff</c>/<c>jq</c>. Thread-safe — the runner emits ER frames from the
/// EntryPointClient's recv thread while the multicast capture writes from
/// its UDP receive loop.
/// </summary>
public sealed class CaptureWriter : IDisposable
{
    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly object _gate = new();
    private readonly Func<long> _timestampFn;
    private bool _disposed;

    public CaptureWriter(TextWriter writer, Func<long>? timestampFn = null, bool ownsWriter = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _ownsWriter = ownsWriter;
        _timestampFn = timestampFn ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public static CaptureWriter OpenFile(string path, Func<long>? timestampFn = null)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var sw = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        sw.NewLine = "\n";
        return new CaptureWriter(sw, timestampFn, ownsWriter: true);
    }

    public void WriteEr(string execType, ulong clOrdId, long securityId, long orderId,
        long lastQty = 0, long lastPxMantissa = 0, long leavesQty = 0, long cumQty = 0,
        ulong origClOrdId = 0, byte ordRejReason = 0, string side = "", string? session = null)
    {
        var rec = new
        {
            t = _timestampFn(),
            src = "er",
            session,
            execType,
            clOrdId,
            securityId,
            orderId,
            side = string.IsNullOrEmpty(side) ? null : side,
            lastQty = NullIfZero(lastQty),
            lastPxMantissa = NullIfZero(lastPxMantissa),
            leavesQty = NullIfZero(leavesQty),
            cumQty = NullIfZero(cumQty),
            origClOrdId = origClOrdId == 0 ? (ulong?)null : origClOrdId,
            ordRejReason = ordRejReason == 0 ? (byte?)null : ordRejReason,
        };
        WriteLine(rec);
    }

    public void WriteMulticast(byte channel, ushort sequenceVersion, uint sequenceNumber,
        int messageCount, ReadOnlySpan<byte> payload)
    {
        var rec = new
        {
            t = _timestampFn(),
            src = "mcast",
            channel,
            sequenceVersion,
            sequenceNumber,
            messageCount,
            bytes = Convert.ToHexString(payload),
        };
        WriteLine(rec);
    }

    public void WriteEvent(string @event, string? detail = null, string? session = null)
    {
        var rec = new { t = _timestampFn(), src = "evt", session, @event, detail };
        WriteLine(rec);
    }

    private static long? NullIfZero(long v) => v == 0 ? null : v;

    private void WriteLine<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        lock (_gate)
        {
            if (_disposed) return;
            _writer.WriteLine(json);
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer.Flush(); } catch { }
            if (_ownsWriter) { try { _writer.Dispose(); } catch { } }
        }
    }
}
