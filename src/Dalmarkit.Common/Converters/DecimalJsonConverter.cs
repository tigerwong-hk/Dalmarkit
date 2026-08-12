using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dalmarkit.Common.Converters;

public class DecimalJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.TryGetDecimal(out decimal decimalResult)
                ? decimalResult
                : throw new JsonException($"Unable to get JSON token as {nameof(Decimal)}");
        }

        string? jsonToken = reader.TokenType == JsonTokenType.String
            ? reader.GetString()
            : throw new JsonException($"Unable to handle {reader.TokenType}");

#pragma warning disable IDE0046 // Convert to conditional expression
        if (string.IsNullOrWhiteSpace(jsonToken))
        {
            throw new JsonException("Unable to get string JSON token");
        }
#pragma warning restore IDE0046 // Convert to conditional expression

        return decimal.TryParse(jsonToken, NumberStyles.Number, NumberFormatInfo.InvariantInfo, out decimal result)
            ? result
            : throw new JsonException($"Unable to convert JSON token to {nameof(Decimal)}");
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(NumberFormatInfo.InvariantInfo));
    }
}
