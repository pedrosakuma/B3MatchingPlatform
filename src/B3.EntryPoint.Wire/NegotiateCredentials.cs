using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace B3.EntryPoint.Wire;

/// <summary>
/// Parsed FIXP <c>credentials</c> blob. Per spec §4.5.2 the field is a
/// JSON document with three required string properties:
/// <code>{ "auth_type": "basic", "username": "...", "access_key": "..." }</code>
/// Only <c>auth_type == "basic"</c> is supported today; anything else
/// must produce a <c>NegotiateReject(Credentials)</c>.
/// </summary>
public readonly record struct NegotiateCredentials(string AuthType, string Username, string AccessKey)
{
    /// <summary>Maximum size (bytes) the SBE schema reserves for the
    /// <c>credentials</c> varData segment in <c>Negotiate</c>.</summary>
    public const int MaxByteLength = 128;

    /// <summary>
    /// Strict JSON parser. Returns <c>false</c> on any structural or
    /// semantic problem (truncated bytes, wrong root type, missing /
    /// non-string / empty required field, embedded NUL byte, additional
    /// unrecognised properties ignored).
    /// </summary>
    /// <remarks>
    /// We deliberately use <see cref="Utf8JsonReader"/> directly (no
    /// allocations beyond the three returned strings) and only accept
    /// strict ASCII for the credential strings to avoid Unicode tricks
    /// (homoglyph attacks against the username comparator). Property
    /// matching is case-sensitive per the spec sample.
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<byte> json, out NegotiateCredentials parsed, out string? error)
    {
        parsed = default;
        error = null;

        if (json.IsEmpty)
        {
            error = "credentials: empty";
            return false;
        }
        if (json.Length > MaxByteLength)
        {
            error = $"credentials: length {json.Length} exceeds max {MaxByteLength}";
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(json,
                new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                error = "credentials: not a JSON object";
                return false;
            }
            string? authType = null, username = null, accessKey = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    error = "credentials: unexpected token";
                    return false;
                }
                var name = reader.GetString();
                if (!reader.Read())
                {
                    error = "credentials: truncated";
                    return false;
                }
                if (reader.TokenType != JsonTokenType.String)
                {
                    // Skip non-string values (forward-compatibility).
                    reader.Skip();
                    continue;
                }
                var value = reader.GetString();
                switch (name)
                {
                    case "auth_type": authType = value; break;
                    case "username": username = value; break;
                    case "access_key": accessKey = value; break;
                }
            }
            if (string.IsNullOrEmpty(authType) || string.IsNullOrEmpty(username) || accessKey is null)
            {
                error = "credentials: missing required field (auth_type/username/access_key)";
                return false;
            }
            if (!IsAsciiPrintable(authType) || !IsAsciiPrintable(username) || !IsAsciiPrintable(accessKey))
            {
                error = "credentials: non-printable-ASCII byte in field";
                return false;
            }
            parsed = new NegotiateCredentials(authType, username, accessKey);
            return true;
        }
        catch (JsonException ex)
        {
            error = "credentials: " + ex.Message;
            return false;
        }
    }

    private static bool IsAsciiPrintable(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < 0x20 || c > 0x7E) return false;
        }
        return true;
    }
}
