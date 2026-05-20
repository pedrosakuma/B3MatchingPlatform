using Microsoft.Extensions.Primitives;

namespace B3.Exchange.Host;

internal static class HttpInternals
{
    internal static readonly DateOnly LocalMktDateEpoch = new(1970, 1, 1);

    internal static readonly TimeSpan PhaseChangeTimeout = TimeSpan.FromSeconds(5);

    internal static readonly System.Text.Json.JsonSerializerOptions AdminJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
        },
    };

    internal static bool TryParseLong(StringValues values, out long value, bool allowMissing = false)
    {
        value = 0;
        var s = values.ToString();
        if (string.IsNullOrEmpty(s)) return allowMissing;
        return long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    internal static bool TryParseULong(StringValues values, out ulong value, bool allowMissing = false)
    {
        value = 0;
        var s = values.ToString();
        if (string.IsNullOrEmpty(s)) return allowMissing;
        return ulong.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    internal static string JsonEscape(string s)
    {
        bool needs = false;
        foreach (var c in s)
        {
            if (c == '\\' || c == '"' || c < 0x20) { needs = true; break; }
        }
        if (!needs) return s;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20)
                        sb.Append("\\u").Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
