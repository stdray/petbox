using System.Text;
using System.Text.Json;

namespace PetBox.Log.Core.Ingestion;

// seq-logging (the JS client used by @datalust/winston-seq and friends) posts events in Seq's
// legacy "Raw events" shape: `{ Timestamp, Level, MessageTemplate, Exception, Properties: { … } }`.
// Our CLEF parser wants `{ @t, @l, @mt, @x, <flat extra keys> }`. This converter rewrites one shape
// to the other so the rest of the pipeline stays CLEF-only. Ported from yobalog (lost in the merge).
static class RawEventEnvelope
{
	public static string ToClefLine(JsonElement evt)
	{
		using var stream = new MemoryStream();
		using (var w = new Utf8JsonWriter(stream))
		{
			w.WriteStartObject();

			if (evt.TryGetProperty("Timestamp", out var ts) && ts.ValueKind == JsonValueKind.String)
				w.WriteString("@t", ts.GetString());

			if (evt.TryGetProperty("Level", out var lvl) && lvl.ValueKind == JsonValueKind.String)
				w.WriteString("@l", lvl.GetString());

			if (evt.TryGetProperty("MessageTemplate", out var mt) && mt.ValueKind == JsonValueKind.String)
				w.WriteString("@mt", mt.GetString());

			if (evt.TryGetProperty("Exception", out var ex) && ex.ValueKind == JsonValueKind.String)
				w.WriteString("@x", ex.GetString());

			if (evt.TryGetProperty("Properties", out var props) && props.ValueKind == JsonValueKind.Object)
			{
				foreach (var p in props.EnumerateObject())
				{
					w.WritePropertyName(p.Name);
					p.Value.WriteTo(w);
				}
			}

			w.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}
}
