using System.Text;
using System.Text.Json;

namespace PetBox.Log.Core;

public static class PropertiesJsonSerializer
{
	public static string Serialize(IReadOnlyDictionary<string, JsonElement> properties)
	{
		if (properties.Count == 0)
			return "{}";

		using var stream = new MemoryStream();
		using var writer = new Utf8JsonWriter(stream);
		writer.WriteStartObject();
		foreach (var (key, value) in properties)
		{
			writer.WritePropertyName(key);
			value.WriteTo(writer);
		}
		writer.WriteEndObject();
		writer.Flush();
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	public static string Serialize(IReadOnlyDictionary<string, object?> properties)
	{
		if (properties.Count == 0)
			return "{}";

		using var stream = new MemoryStream();
		using var writer = new Utf8JsonWriter(stream);
		writer.WriteStartObject();
		foreach (var (key, value) in properties)
		{
			writer.WritePropertyName(key);
			JsonSerializer.Serialize(writer, value, value?.GetType() ?? typeof(object));
		}
		writer.WriteEndObject();
		writer.Flush();
		return Encoding.UTF8.GetString(stream.ToArray());
	}
}
