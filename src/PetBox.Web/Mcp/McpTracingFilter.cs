using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PetBox.Core.Observability;

namespace PetBox.Web.Mcp;

// Self-tracing: one span per MCP tool invocation, named by the tool, so a `POST /mcp/`
// request trace is attributable to the tool that ran (spec: trace-write-path-spans).
// The span nests under the AspNetCore request span via Activity.Current.
//
// Beyond the tool name + status, the span carries request/response SIZES and key call
// SHAPERS so the hot path can be audited (frequency × latency × volume × call character)
// without reading bodies. PRIVACY CONTRACT: we log only FORMS and SIZES — never parameter
// VALUES and never request/response bodies (spec: telemetry-no-payloads).
static partial class McpTracingFilter
{
	// Match the server's tool serializer (relaxed encoder, same as McpErrorEnvelopeFilter)
	// so a measured size tracks the wire form. Used only to MEASURE length — the serialized
	// string is never emitted onto the span.
	static readonly JsonSerializerOptions SizeJson = new(McpJsonUtilities.DefaultOptions)
	{
		Encoder = PetBox.Core.Json.PetBoxJsonEncoder.Relaxed,
	};

	// Category MUST start with the SystemLogger prefix ("PetBox") so the self-log captures it —
	// see SystemLoggerOptions.CategoryPrefix. This is the always-on economy metric: one
	// Information event per CallTool (spec: economy-measurable), independent of whether a trace
	// listener is active or the OTel self-export chain is up.
	const string ToolCallLogCategory = "PetBox.Mcp.ToolCalls";

	// The streamable-HTTP MCP session id can be null on a non-stateful transport; keep the
	// KQL column present with a stable, groupable placeholder rather than a missing property.
	const string NoSession = "-";

	public static void Register(IMcpRequestFilterBuilder filters) =>
		filters.AddCallToolFilter(next => async (request, ct) =>
		{
			// One logger + the request-side measurements resolved ONCE per call, used by BOTH
			// the (conditional) span tags and the (unconditional) self-log event. The request
			// size is serialized exactly once here and reused — no double-serialize.
			var logger = request.Services!.GetRequiredService<ILoggerFactory>()
				.CreateLogger(ToolCallLogCategory);
			var tool = request.Params?.Name;
			var session = request.Server.SessionId ?? NoSession;
			var reqChars = SerializedLength(request.Params?.Arguments);

			using var span = StartToolSpan(tool);
			if (span is not null)
			{
				var args = request.Params?.Arguments;
				span.SetTag("petbox.request_chars", reqChars);
				span.SetTag("petbox.session_id", request.Server.SessionId);
				foreach (var (key, value) in ExtractArgShapers(args))
					span.SetTag(key, value);
			}
			try
			{
				var result = await next(request, ct);
				// content + structuredContent = the payload a client receives (spec wording).
				var respChars = SerializedLength(new { result.Content, result.StructuredContent });
				span?.SetTag("petbox.response_chars", respChars);
				// The inner McpErrorEnvelopeFilter converts tool-body exceptions into an
				// IsError result BEFORE this (outer) filter sees them, so attribute the outcome
				// from the result, not only the catch below.
				var outcome = result.IsError == true ? "error" : "ok";
				if (result.IsError == true)
					span?.SetStatus(ActivityStatusCode.Error);
				LogToolCall(logger, tool, session, reqChars, respChars, outcome);
				return result;
			}
			catch (Exception ex)
			{
				span?.SetStatus(ActivityStatusCode.Error, ex.Message);
				// No result to measure on a genuine throw → RespChars 0, Outcome error, then
				// rethrow to preserve the error-status behavior for callers up the stack.
				LogToolCall(logger, tool, session, reqChars, 0, "error");
				throw;
			}
		});

	// Source-generated, allocation-free self-log write (CA1873-clean). The template placeholder
	// names (Tool/Session/ReqChars/RespChars/Outcome) become KQL-addressable Properties.<Name>
	// once SystemLogger lifts the named args. Message starts with "mcp tool " — the query anchor.
	[LoggerMessage(EventId = 600, Level = LogLevel.Information,
		Message = "mcp tool {Tool} session {Session} req {ReqChars} resp {RespChars} outcome {Outcome}")]
	static partial void LogToolCall(
		ILogger logger, string? tool, string session, int reqChars, int respChars, string outcome);

	internal static Activity? StartToolSpan(string? toolName)
	{
		var span = PetBoxActivitySources.Mcp.StartActivity($"mcp.tool {toolName}");
		span?.SetTag("petbox.tool", toolName);
		return span;
	}

	// Length of the serialized value (0 for null). Cheap enough for the hot path — one
	// serialize pass, no second copy.
	static int SerializedLength(object? value) =>
		value is null ? 0 : JsonSerializer.Serialize(value, SizeJson).Length;

	// Pure, testable shaper extraction. Given the raw tool arguments, yields the privacy-safe
	// span tags — FORMS and SIZES only, never values. A shaper whose arg is absent/empty
	// produces NO pair, so its span tag stays unset (present-only semantics).
	internal static IEnumerable<KeyValuePair<string, object?>> ExtractArgShapers(
		IDictionary<string, JsonElement>? args)
	{
		var tags = new List<KeyValuePair<string, object?>>();
		if (args is null || args.Count == 0)
			return tags;

		// q — whether a non-empty search query was passed (bool; the value stays private).
		if (args.TryGetValue("q", out var q)
			&& q.ValueKind == JsonValueKind.String
			&& !string.IsNullOrEmpty(q.GetString()))
			tags.Add(new("petbox.arg.q", true));

		// bodyLen / limit — numeric knobs; the number itself carries no user content.
		if (TryGetInt64(args, "bodyLen", out var bodyLen))
			tags.Add(new("petbox.arg.body_len", bodyLen));
		if (TryGetInt64(args, "limit", out var limit))
			tags.Add(new("petbox.arg.limit", limit));

		// fields — for upsert-style tools, the sorted set of NON-EMPTY payload field NAMES on
		// the node payload(s). Names only, never values: lets a status-only transition be told
		// apart from a body/title edit.
		var fields = ExtractFieldShape(args);
		if (fields is not null)
			tags.Add(new("petbox.arg.fields", fields));

		return tags;
	}

	static bool TryGetInt64(IDictionary<string, JsonElement> args, string name, out long value)
	{
		value = 0;
		return args.TryGetValue(name, out var el)
			&& el.ValueKind == JsonValueKind.Number
			&& el.TryGetInt64(out value);
	}

	// `version` is pure CAS bookkeeping — always present on an upsert node and semantically
	// empty, so it drowns the signal. Everything else (title/body/status/type/tags/priority/
	// key/…) is kept: the KEY distinction we want is body/title-present vs status-only.
	static readonly HashSet<string> NodeFieldExclusions =
		new(StringComparer.Ordinal) { "version" };

	// The semantically meaningful node-payload fields. Used two ways: (1) to detect a single
	// top-level node payload (the non-`nodes` variant), and (2) to filter it — at top level the
	// node fields sit next to routing args (projectKey/board/…), so only these known node
	// fields can be safely attributed to the node. Inside an explicit `nodes` object every
	// property IS a node field, so that branch takes them all (minus `version`).
	static readonly HashSet<string> NodeSignalFields =
		new(StringComparer.Ordinal)
		{ "title", "body", "status", "type", "priority", "tags", "delivery" };

	// Derives the field-name shape from an upsert-style args object. Handles a `nodes` array
	// (union the non-empty names across all node objects) or a single top-level node payload.
	// Returns null when the args are not an upsert shape or carry no non-empty fields.
	static string? ExtractFieldShape(IDictionary<string, JsonElement> args)
	{
		var names = new SortedSet<string>(StringComparer.Ordinal);
		var isNodeShape = false;

		if (args.TryGetValue("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
		{
			isNodeShape = true;
			foreach (var node in nodes.EnumerateArray())
				if (node.ValueKind == JsonValueKind.Object)
					foreach (var prop in node.EnumerateObject())
						AddIfMeaningful(names, prop.Name, prop.Value);
		}
		else
		{
			// Top-level single-node payload: only the known node fields (routing args like
			// projectKey/board sit alongside and must not be attributed to the node).
			foreach (var kv in args)
				if (NodeSignalFields.Contains(kv.Key) && IsNonEmpty(kv.Value))
				{
					isNodeShape = true;
					names.Add(kv.Key);
				}
		}

		if (!isNodeShape || names.Count == 0)
			return null;
		return string.Join(",", names);
	}

	static void AddIfMeaningful(SortedSet<string> names, string name, JsonElement value)
	{
		if (!NodeFieldExclusions.Contains(name) && IsNonEmpty(value))
			names.Add(name);
	}

	// A field "counts" (is non-empty) when it carries a real value: a non-empty string/array/
	// object, or any number/bool. null/undefined and empty string/array/object do not.
	static bool IsNonEmpty(JsonElement v) => v.ValueKind switch
	{
		JsonValueKind.String => v.GetString() is { Length: > 0 },
		JsonValueKind.Array => v.GetArrayLength() > 0,
		JsonValueKind.Object => v.EnumerateObject().Any(),
		JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
		_ => false, // Null / Undefined
	};
}
