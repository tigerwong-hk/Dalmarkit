using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dalmarkit.Common.Converters;

public class DecimalJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            return reader.GetDecimal();
        }

        string? jsonToken = reader.GetString();
        return string.IsNullOrWhiteSpace(jsonToken)
            ? throw new JsonException("Unable to get string JSON token")
            : decimal.Parse(jsonToken, NumberStyles.Number, NumberFormatInfo.InvariantInfo);
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(NumberFormatInfo.InvariantInfo));
    }
}
