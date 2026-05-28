using LinqToDB.Mapping;

namespace PetBox.Log.Core.Tracing;

public enum SpanKind { Internal = 0, Server = 1, Client = 2, Producer = 3, Consumer = 4 }
public enum SpanStatusCode { Unset = 0, Ok = 1, Error = 2 }

[Table("Spans")]
public sealed record SpanRecord
{
	[Column, PrimaryKey, NotNull]
	public string SpanId { get; init; } = string.Empty;
	[Column, NotNull]
	public string TraceId { get; init; } = string.Empty;
	[Column]
	public string? ParentSpanId { get; init; }
	[Column, NotNull]
	public string Name { get; init; } = string.Empty;
	[Column]
	public int Kind { get; init; }
	[Column]
	public long StartUnixNs { get; init; }
	[Column]
	public long EndUnixNs { get; init; }
	[Column]
	public int StatusCode { get; init; }
	[Column]
	public string? StatusDescription { get; init; }
	[Column, NotNull]
	public string AttributesJson { get; init; } = "{}";
	[Column, NotNull]
	public string EventsJson { get; init; } = "[]";
	[Column, NotNull]
	public string LinksJson { get; init; } = "[]";

	public DateTime StartTime => new(StartUnixNs / 100, DateTimeKind.Utc);
	public TimeSpan Duration => TimeSpan.FromTicks((EndUnixNs - StartUnixNs) / 100);
}
