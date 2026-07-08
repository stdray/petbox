using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PetBox.Core.Json;

// Strict MCP clients (ajv-based: Factory Droid, opencode — and any RFC-3339-strict validator)
// ENFORCE the output schema's format:"date-time". Timestamps in the temporal stores are written
// with DateTime.UtcNow but round-trip through SQLite as DateTimeKind.Unspecified, so the default
// System.Text.Json serializer emits them WITHOUT a zone (e.g. "2026-07-04T12:58:10.238") — which
// is NOT a valid RFC 3339 date-time. Every tool that returns a timestamp then fails the client's
// schema check with:
//   MCP error -32602: Structured content does not match the tool's output schema:
//   data/.../createdAt must match format "date-time"
// which breaks the whole tool call. This converter normalizes every DateTime to UTC (the stores
// always write UtcNow; the kind is merely lost in SQLite) and emits an RFC 3339 string WITH a 'Z',
// so the serialized value satisfies the declared format. Registered on the MCP serializer only
// (Program.cs), so it never changes REST/UI wire formats.
public sealed class McpUtcDateTimeConverter : JsonConverter<DateTime>
{
	public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		=> reader.GetDateTime();

	public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
	{
		var utc = value.Kind switch
		{
			DateTimeKind.Utc => value,
			DateTimeKind.Local => value.ToUniversalTime(),
			// Unspecified: the temporal stores write DateTime.UtcNow; SQLite just drops the kind.
			_ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
		};
		writer.WriteStringValue(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
	}
}
