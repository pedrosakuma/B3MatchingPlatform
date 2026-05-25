using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace B3.Exchange.Instruments;

/// <summary>
/// Loads and validates instrument JSON files for the exchange simulator.
///
/// Wire format: a top-level JSON array of instrument objects. Field names
/// are camelCase (symbol, securityId, tickSize, …). Decimal values
/// (tickSize, minPx, maxPx) accept either a JSON number or a quoted
/// string — strings are preferred to avoid binary-float surprises on
/// values like 0.01.
/// </summary>
public static class InstrumentLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new FlexibleDecimalConverter() },
    };

    public static IReadOnlyList<Instrument> LoadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, sourceName: path);
    }

    public static IReadOnlyList<Instrument> LoadFromString(string json, string sourceName = "<inline>")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream(bytes);
        return Load(stream, sourceName);
    }

    public static IReadOnlyList<Instrument> Load(Stream stream, string sourceName = "<stream>")
    {
        List<RawInstrument>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<RawInstrument>>(stream, Options);
        }
        catch (JsonException ex)
        {
            throw new InstrumentConfigException($"Failed to parse instrument JSON from '{sourceName}': {ex.Message}", ex);
        }

        if (raw is null)
            throw new InstrumentConfigException($"Instrument JSON from '{sourceName}' is null.");

        var result = new List<Instrument>(raw.Count);
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
        var seenSecurityIds = new HashSet<long>();

        for (var i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            var inst = ValidateAndConvert(r, i, sourceName);

            if (!seenSymbols.Add(inst.Symbol))
                throw new InstrumentConfigException($"Duplicate symbol '{inst.Symbol}' at index {i} in '{sourceName}'.");

            if (!seenSecurityIds.Add(inst.SecurityId))
                throw new InstrumentConfigException($"Duplicate securityId {inst.SecurityId} at index {i} in '{sourceName}'.");

            result.Add(inst);
        }

        return result;
    }

    private static Instrument ValidateAndConvert(RawInstrument r, int index, string sourceName)
    {
        string Where(string field) => $"instrument index {index} in '{sourceName}': {field}";

        static string? NormalizeOptionalText(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value;

        static TEnum? ParseEnum<TEnum>(string? value, string field, Func<string, string> where, bool required)
            where TEnum : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required)
                    throw new InstrumentConfigException(where($"{field} is required"));
                return null;
            }

            if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
                return parsed;

            throw new InstrumentConfigException(where($"{field} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}"));
        }

        static DateOnly? ParseDateOnly(string? value, string field, Func<string, string> where, bool required)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required)
                    throw new InstrumentConfigException(where($"{field} is required"));
                return null;
            }

            if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;

            throw new InstrumentConfigException(where($"{field} must be YYYY-MM-DD"));
        }

        if (string.IsNullOrWhiteSpace(r.Symbol))
            throw new InstrumentConfigException(Where("symbol is required"));
        if (r.SecurityId is null || r.SecurityId.Value <= 0)
            throw new InstrumentConfigException(Where("securityId must be > 0"));
        if (r.TickSize is null || r.TickSize.Value <= 0)
            throw new InstrumentConfigException(Where("tickSize must be > 0"));
        if (string.IsNullOrWhiteSpace(r.Currency))
            throw new InstrumentConfigException(Where("currency is required"));
        if (string.IsNullOrWhiteSpace(r.Isin))
            throw new InstrumentConfigException(Where("isin is required"));
        if (string.IsNullOrWhiteSpace(r.SecurityType))
            throw new InstrumentConfigException(Where("securityType is required"));

        var isOption = InstrumentSecurityTypes.IsOption(r.SecurityType);
        var lotSize = r.LotSize;
        if (isOption)
        {
            if (lotSize is null)
                lotSize = 1;
            else if (lotSize.Value != 1)
                throw new InstrumentConfigException(Where("lotSize must be 1 for option securityTypes"));
        }
        else if (lotSize is null || lotSize.Value <= 0)
        {
            throw new InstrumentConfigException(Where("lotSize must be > 0"));
        }

        if (r.MinPrice is null || r.MinPrice.Value < 0 || (!isOption && r.MinPrice.Value == 0))
            throw new InstrumentConfigException(Where(isOption ? "minPx must be >= 0" : "minPx must be > 0"));
        if (r.MaxPrice is null || r.MaxPrice.Value <= 0)
            throw new InstrumentConfigException(Where("maxPx must be > 0"));
        if (r.MaxPrice.Value < r.MinPrice.Value)
            throw new InstrumentConfigException(Where($"maxPx ({r.MaxPrice.Value}) must be >= minPx ({r.MinPrice.Value})"));

        if (decimal.Remainder(r.MinPrice.Value, r.TickSize.Value) != 0)
            throw new InstrumentConfigException(Where($"minPx ({r.MinPrice.Value}) is not a multiple of tickSize ({r.TickSize.Value})"));
        if (decimal.Remainder(r.MaxPrice.Value, r.TickSize.Value) != 0)
            throw new InstrumentConfigException(Where($"maxPx ({r.MaxPrice.Value}) is not a multiple of tickSize ({r.TickSize.Value})"));

        var strikePrice = r.StrikePrice;
        if (isOption && strikePrice is null)
            throw new InstrumentConfigException(Where("strikePrice is required"));

        var expirationDate = ParseDateOnly(r.ExpirationDate, "expirationDate", Where, required: isOption);
        if (expirationDate is { } parsedExpirationDate && parsedExpirationDate < DateOnly.FromDateTime(DateTime.UtcNow))
            throw new InstrumentConfigException(Where("expirationDate must be today or later"));

        var putOrCall = ParseEnum<PutOrCall>(r.PutOrCall, "putOrCall", Where, required: isOption);
        var exerciseStyle = ParseEnum<ExerciseStyle>(r.ExerciseStyle, "exerciseStyle", Where, required: isOption);
        var optPayoutType = ParseEnum<OptPayoutType>(r.OptPayoutType, "optPayoutType", Where, required: false);

        if (r.UnderlyingSecurityId is <= 0)
            throw new InstrumentConfigException(Where("underlyingSecurityId must be > 0"));
        if (isOption && r.UnderlyingSecurityId is null)
            throw new InstrumentConfigException(Where("underlyingSecurityId is required"));

        var underlyingSymbol = NormalizeOptionalText(r.UnderlyingSymbol);
        if (isOption && underlyingSymbol is null)
            throw new InstrumentConfigException(Where("underlyingSymbol is required"));

        if (r.ContractMultiplier is <= 0)
            throw new InstrumentConfigException(Where("contractMultiplier must be > 0"));
        if (isOption && r.ContractMultiplier is null)
            throw new InstrumentConfigException(Where("contractMultiplier is required"));

        return new Instrument
        {
            Symbol = r.Symbol!,
            SecurityId = r.SecurityId.Value,
            TickSize = r.TickSize.Value,
            LotSize = lotSize!.Value,
            MinPrice = r.MinPrice.Value,
            MaxPrice = r.MaxPrice.Value,
            Currency = r.Currency!,
            Isin = r.Isin!,
            SecurityType = r.SecurityType!,
            StrikePrice = strikePrice,
            ExpirationDate = expirationDate,
            PutOrCall = putOrCall,
            ExerciseStyle = exerciseStyle,
            UnderlyingSecurityId = r.UnderlyingSecurityId,
            UnderlyingSymbol = underlyingSymbol,
            ContractMultiplier = r.ContractMultiplier,
            OptPayoutType = optPayoutType ?? (isOption ? B3.Exchange.Instruments.OptPayoutType.Vanilla : null),
        };
    }

    private sealed class RawInstrument
    {
        [JsonPropertyName("symbol")] public string? Symbol { get; set; }
        [JsonPropertyName("securityId")] public long? SecurityId { get; set; }
        [JsonPropertyName("tickSize")] public decimal? TickSize { get; set; }
        [JsonPropertyName("lotSize")] public int? LotSize { get; set; }
        [JsonPropertyName("minPx")] public decimal? MinPrice { get; set; }
        [JsonPropertyName("maxPx")] public decimal? MaxPrice { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("isin")] public string? Isin { get; set; }
        [JsonPropertyName("securityType")] public string? SecurityType { get; set; }
        [JsonPropertyName("strikePrice")] public decimal? StrikePrice { get; set; }
        [JsonPropertyName("expirationDate")] public string? ExpirationDate { get; set; }
        [JsonPropertyName("putOrCall")] public string? PutOrCall { get; set; }
        [JsonPropertyName("exerciseStyle")] public string? ExerciseStyle { get; set; }
        [JsonPropertyName("underlyingSecurityId")] public long? UnderlyingSecurityId { get; set; }
        [JsonPropertyName("underlyingSymbol")] public string? UnderlyingSymbol { get; set; }
        [JsonPropertyName("contractMultiplier")] public decimal? ContractMultiplier { get; set; }
        [JsonPropertyName("optPayoutType")] public string? OptPayoutType { get; set; }
    }

    /// <summary>
    /// Accepts decimals expressed either as JSON numbers or as quoted
    /// strings (e.g. "0.01"). Strings are preferred for monetary values.
    /// </summary>
    private sealed class FlexibleDecimalConverter : JsonConverter<decimal?>
    {
        public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.Number:
                    return reader.GetDecimal();
                case JsonTokenType.String:
                    {
                        var s = reader.GetString();
                        if (string.IsNullOrWhiteSpace(s)) return null;
                        if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                            return d;
                        throw new JsonException($"Invalid decimal: '{s}'");
                    }
                default:
                    throw new JsonException($"Expected number or string for decimal, got {reader.TokenType}");
            }
        }

        public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }
}

public sealed class InstrumentConfigException : Exception
{
    public InstrumentConfigException(string message) : base(message) { }
    public InstrumentConfigException(string message, Exception inner) : base(message, inner) { }
}
