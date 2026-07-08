using System.Text.Json;
using System.Text.RegularExpressions;
using PetBox.Core.Json;

namespace PetBox.Tests.Mcp;

// Regression guard for the -32602 "must match format date-time" failure strict MCP clients
// (Factory Droid / opencode ajv) hit on any tool returning a timestamp: SQLite returns
// DateTimeKind.Unspecified, which STJ serialized zone-less ("2026-07-04T12:58:10.238"), violating
// the schema's format:"date-time". McpUtcDateTimeConverter normalizes to UTC + 'Z'.
public sealed class McpUtcDateTimeConverterTests
{
	static readonly JsonSerializerOptions Opts = Build();

	static JsonSerializerOptions Build()
	{
		var o = new JsonSerializerOptions();
		o.Converters.Add(new McpUtcDateTimeConverter());
		return o;
	}

	// RFC 3339 date-time as ajv's "date-time" format checker requires: date 'T' time + a zone.
	static readonly Regex Rfc3339 = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$");

	[Theory]
	[InlineData(DateTimeKind.Unspecified)]
	[InlineData(DateTimeKind.Utc)]
	[InlineData(DateTimeKind.Local)]
	public void Serializes_with_zone_matching_rfc3339(DateTimeKind kind)
	{
		var dt = DateTime.SpecifyKind(new DateTime(2026, 7, 4, 12, 58, 10, 238), kind);
		var s = JsonSerializer.Serialize(dt, Opts).Trim('"');
		Assert.EndsWith("Z", s);
		Assert.Matches(Rfc3339, s);
	}

	[Fact]
	public void Unspecified_stored_utc_serializes_verbatim_with_z()
	{
		var dt = DateTime.SpecifyKind(new DateTime(2026, 7, 4, 12, 58, 10, 238), DateTimeKind.Unspecified);
		Assert.Equal("\"2026-07-04T12:58:10.238Z\"", JsonSerializer.Serialize(dt, Opts));
	}

	[Fact]
	public void Nullable_datetime_roundtrips_through_converter()
	{
		DateTime? dt = DateTime.SpecifyKind(new DateTime(2026, 1, 2, 3, 4, 5, 6), DateTimeKind.Unspecified);
		var s = JsonSerializer.Serialize(dt, Opts).Trim('"');
		Assert.Matches(Rfc3339, s);
	}
}
