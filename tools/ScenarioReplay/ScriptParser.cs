using System.Text.Json;

namespace B3.Exchange.ScenarioReplay;

/// <summary>
/// Parses a JSONL replay script. One event per non-blank, non-comment line.
/// Lines starting with <c>#</c> or <c>//</c> are ignored to ease hand-editing.
/// Unknown JSON properties are tolerated so older scripts survive schema
/// growth — the runner only consumes the documented fields.
/// </summary>
public static class ScriptParser
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<ScriptEvent> Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var list = new List<ScriptEvent>();
        string? line;
        int lineNo = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNo++;
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty) continue;
            if (trimmed[0] == '#') continue;
            if (trimmed.Length >= 2 && trimmed[0] == '/' && trimmed[1] == '/') continue;
            ScriptEvent ev;
            try
            {
                ev = ParseLine(line, lineNo);
            }
            catch (JsonException jx)
            {
                throw new FormatException($"line {lineNo}: invalid JSON ({jx.Message})", jx);
            }
            list.Add(ev);
        }
        // Stable sort by AtMs preserving original line order on ties.
        var sorted = list.Select((e, i) => (e, i))
            .OrderBy(t => t.e.AtMs)
            .ThenBy(t => t.i)
            .Select(t => t.e)
            .ToList();
        return sorted;
    }

    public static IReadOnlyList<ScriptEvent> ParseFile(string path)
    {
        using var sr = new StreamReader(path);
        return Parse(sr);
    }

    private static ScriptEvent ParseLine(string raw, int lineNo)
    {
        using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var root = doc.RootElement;

        long atMs = ReadLong(root, "atMs", lineNo, required: true);
        var kindStr = ReadString(root, "kind", lineNo, required: true)!;
        var kind = kindStr.ToLowerInvariant() switch
        {
            "new" => ScriptEventKind.New,
            "cancel" => ScriptEventKind.Cancel,
            _ => throw new FormatException($"line {lineNo}: unknown kind '{kindStr}'"),
        };
        ulong clOrdId = (ulong)ReadLong(root, "clOrdId", lineNo, required: true);
        long securityId = ReadLong(root, "securityId", lineNo, required: true);
        var sideStr = ReadString(root, "side", lineNo, required: true)!;
        var side = sideStr.ToLowerInvariant() switch
        {
            "buy" or "b" or "1" => Side.Buy,
            "sell" or "s" or "2" => Side.Sell,
            _ => throw new FormatException($"line {lineNo}: unknown side '{sideStr}'"),
        };

        if (kind == ScriptEventKind.Cancel)
        {
            ulong origClOrdId = (ulong)ReadLong(root, "origClOrdId", lineNo, required: true);
            return new ScriptEvent(atMs, kind, clOrdId, securityId, side,
                OrderType.Limit, Tif.Day, 0, 0, origClOrdId, lineNo);
        }

        var typeStr = ReadString(root, "type", lineNo, required: false) ?? "limit";
        var type = typeStr.ToLowerInvariant() switch
        {
            "limit" => OrderType.Limit,
            "market" => OrderType.Market,
            _ => throw new FormatException($"line {lineNo}: unknown type '{typeStr}'"),
        };
        var tifStr = ReadString(root, "tif", lineNo, required: false) ?? "day";
        var tif = tifStr.ToLowerInvariant() switch
        {
            "day" => Tif.Day,
            "ioc" => Tif.IOC,
            "fok" => Tif.FOK,
            _ => throw new FormatException($"line {lineNo}: unknown tif '{tifStr}'"),
        };
        long qty = ReadLong(root, "qty", lineNo, required: true);
        long px = type == OrderType.Market ? 0 : ReadLong(root, "px", lineNo, required: true);
        if (qty <= 0) throw new FormatException($"line {lineNo}: qty must be > 0");
        return new ScriptEvent(atMs, kind, clOrdId, securityId, side, type, tif, qty, px, 0, lineNo);
    }

    private static long ReadLong(JsonElement root, string name, int lineNo, bool required)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new FormatException($"line {lineNo}: missing required field '{name}'");
            return 0;
        }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        throw new FormatException($"line {lineNo}: field '{name}' is not an integer");
    }

    private static string? ReadString(JsonElement root, string name, int lineNo, bool required)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new FormatException($"line {lineNo}: missing required field '{name}'");
            return null;
        }
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        throw new FormatException($"line {lineNo}: field '{name}' is not a string");
    }
}
