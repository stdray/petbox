using System.Collections.Immutable;
using System.Text.Json;

namespace PetBox.Log.Core.Contract;

// Ingest acknowledgement shared by CLEF and OTLP receivers: how many events
// landed and how many lines/records were rejected.
public sealed record IngestResponse(int Ingested, int Errors);

// One rejected ingest line, keyed by 1-based line number with the parser's reason.
public sealed record IngestLineError(int Line, string? Message);

// 400 when EVERY line of an ingest body failed validation (nothing was stored).
public sealed record IngestRejectedResponse(string Error, IReadOnlyList<IngestLineError> Details);

// 400 when the KQL query string fails to parse. Details carries per-diagnostic messages.
public sealed record KqlParseErrorResponse(string Error, IReadOnlyList<string> Details);

// One log event in the events-shaped query response. Timestamp is pre-formatted
// (yyyy-MM-ddTHH:mm:ss.fffZ); level is the enum name; properties are stringified.
public sealed record LogEventDto(
	long Id,
	string ServiceKey,
	string Timestamp,
	string Level,
	string Message,
	string MessageTemplate,
	string? Exception,
	Dictionary<string, string> Properties);

// Events-shaped query result: a count and the projected events.
public sealed record LogEventsResponse(int Count, IReadOnlyList<LogEventDto> Events);

// Table-shaped (KQL projection/summarize) query result: column names plus rows
// of JSON cells (null where the cell was null).
public sealed record KqlTableResponse(IReadOnlyList<string> Columns, IReadOnlyList<ImmutableArray<JsonElement?>> Rows);

// REST echo of a freshly created share link: its id and expiry.
public sealed record ShareCreatedResponse(string Id, DateTime ExpiresAt);
