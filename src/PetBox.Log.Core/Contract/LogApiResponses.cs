using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

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

// 500 when a syntactically valid query fails during EXECUTION (linq2db SQL translation,
// SQLite, row streaming). Always JSON — never the HTML error page. Type is the
// originating exception type name so callers can branch on the failure class.
public sealed record KqlExecutionErrorResponse(string Error, string Type);

// One log event in the events-shaped query response. Timestamp is pre-formatted
// (yyyy-MM-ddTHH:mm:ss.fffZ); level is the enum name; properties are stringified.
//
// Field names are pinned to the PascalCase KQL schema (KqlSchema.Events / the KQL
// identifiers a query references: Timestamp, Level, ServiceKey…). The minimal-API JSON
// default is camelCase, but the table-shaped result (KqlTableResponse.Columns) carries
// the schema names verbatim as data, so without these pins the two query shapes disagree
// on casing (event.timestamp vs column "Timestamp") and an agent parser written for one
// shape silently breaks on the other. Kept identical to the KQL schema so both shapes,
// and the query language itself, use one casing.
public sealed record LogEventDto(
	[property: JsonPropertyName("Id")] long Id,
	[property: JsonPropertyName("ServiceKey")] string ServiceKey,
	[property: JsonPropertyName("Timestamp")] string Timestamp,
	[property: JsonPropertyName("Level")] string Level,
	[property: JsonPropertyName("Message")] string Message,
	[property: JsonPropertyName("MessageTemplate")] string MessageTemplate,
	[property: JsonPropertyName("Exception")] string? Exception,
	[property: JsonPropertyName("Properties")] Dictionary<string, string> Properties);

// Events-shaped query result: a count and the projected events.
public sealed record LogEventsResponse(int Count, IReadOnlyList<LogEventDto> Events);

// Table-shaped (KQL projection/summarize) query result: column names plus rows
// of JSON cells (null where the cell was null).
public sealed record KqlTableResponse(IReadOnlyList<string> Columns, IReadOnlyList<ImmutableArray<JsonElement?>> Rows);

// REST echo of a freshly created share link: its id and expiry.
public sealed record ShareCreatedResponse(string Id, DateTime ExpiresAt);
