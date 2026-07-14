using System.Text;
using System.Text.Json;
using PetBox.Core.Json;
using PetBox.Log.Core.Query;

namespace PetBox.Log.Core;

// The WRITE boundary for the events Properties bag (CLEF / seq / raw-envelope ingest and the
// self-log all serialize through here): keys are normalized ONCE, before storage, with the same
// KqlPropertyKeys rule the KQL search boundary applies — so a stored key and a queried key always
// meet in the same form, and every stored key is safe inside a quoted SQLite JSON-path label.
// Name assignment goes through KqlPropertyKeys.NameAllocator, so two DISTINCT originals colliding on
// one normalized name get deterministic '_2', '_3' suffixes instead of duplicate-key JSON.
public static class PropertiesJsonSerializer
{
	// json-encoder-shared-globally: a bare `new Utf8JsonWriter(stream)` defaults to the same
	// HTML-safe encoder as a bare JsonSerializer.Serialize(x) — it escapes every Cyrillic char to
	// \uXXXX here, at the WRITE boundary, before the value ever reaches LogApi's read-side
	// PropertyJson (which only fixes up values re-serialized from an already-parsed
	// Dictionary<string, JsonElement>, not raw already-escaped text sitting in this stored blob).
	static readonly JsonWriterOptions WriterOptions = new() { Encoder = PetBoxJsonEncoder.Relaxed };

	public static string Serialize(IReadOnlyDictionary<string, JsonElement> properties)
	{
		if (properties.Count == 0)
			return "{}";

		using var stream = new MemoryStream();
		using var writer = new Utf8JsonWriter(stream, WriterOptions);
		var names = new KqlPropertyKeys.NameAllocator();
		writer.WriteStartObject();
		foreach (var (key, value) in properties)
		{
			writer.WritePropertyName(names.Assign(key));
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
		using var writer = new Utf8JsonWriter(stream, WriterOptions);
		var names = new KqlPropertyKeys.NameAllocator();
		writer.WriteStartObject();
		foreach (var (key, value) in properties)
		{
			writer.WritePropertyName(names.Assign(key));
			JsonSerializer.Serialize(writer, value, value?.GetType() ?? typeof(object));
		}
		writer.WriteEndObject();
		writer.Flush();
		return Encoding.UTF8.GetString(stream.ToArray());
	}
}
