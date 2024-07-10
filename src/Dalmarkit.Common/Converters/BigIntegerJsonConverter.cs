using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dalmarkit.Common.Converters;

public class BigIntegerJsonConverter : JsonConverter<BigInteger>
{
    public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? jsonToken = reader.GetString();
#pragma warning disable IDE0046 // Convert to conditional expression
        if (string.IsNullOrWhiteSpace(jsonToken))
        {
            throw new JsonException("Unable to get JSON token");
        }
#pragma warning restore IDE0046 // Convert to conditional expression

        return BigInteger.Parse(jsonToken, NumberStyles.Integer, NumberFormatInfo.InvariantInfo);
    }

    public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options)
    {
        writer.WriteRawValue(value.ToString(NumberFormatInfo.InvariantInfo));
    }
}
