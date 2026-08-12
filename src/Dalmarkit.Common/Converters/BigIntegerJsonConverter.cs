using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dalmarkit.Common.Converters;

public class BigIntegerJsonConverter : JsonConverter<BigInteger>
{
    public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? jsonToken;
        if (reader.TokenType == JsonTokenType.Number)
        {
            ReadOnlySpan<byte> utf8 = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
            jsonToken = Encoding.UTF8.GetString(utf8);
        }
        else
        {
            jsonToken = reader.TokenType == JsonTokenType.String
                ? reader.GetString()
                : throw new JsonException($"Unable to handle {reader.TokenType}");
        }

        if (string.IsNullOrWhiteSpace(jsonToken))
        {
            throw new JsonException("Unable to get JSON token");
        }

        // TryParse, not Parse: a FormatException from a converter escapes model binding as a 500,
        // while a JsonException is what the input formatter turns into a 400.
        return BigInteger.TryParse(jsonToken, NumberStyles.Integer, NumberFormatInfo.InvariantInfo, out BigInteger result)
            ? result
            : throw new JsonException($"Unable to convert JSON token to {nameof(BigInteger)}");
    }

    public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(NumberFormatInfo.InvariantInfo));
    }
}
