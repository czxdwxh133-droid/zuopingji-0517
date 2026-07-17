using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewsBriefingAssistant.Utilities;

public class NullableEnumConverter<T> : JsonConverter<T?> where T : struct, Enum
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var value = reader.GetString();
        return Enum.TryParse<T>(value, true, out var result) ? result : null;
    }

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value.ToString());
        else
            writer.WriteNullValue();
    }
}
