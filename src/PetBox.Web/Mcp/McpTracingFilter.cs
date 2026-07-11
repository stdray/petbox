using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PetBox.Core.Observability;

namespace PetBox.Web.Mcp;

// Self-tracing: one span per MCP tool invocation, named by the tool, so a `POST /mcp/`
// request trace is attributable to the tool that ran (spec: trace-write-path-spans).
// The span nests under the AspNetCore request span via Activity.Current.
//
// Beyond the tool name + status, the span AND the ToolCalls self-log carry request/response
// SIZES and key call SHAPERS so the hot path can be audited (frequency × latency × volume ×
// call character) without reading bodies. The shapers land in the LOG too because that — not
// the span — is what the economy measurements are queried from (KQL over the self-log).
// PRIVACY CONTRACT (spec: trace-mcp-call-shape): no CONTENT ever — no free text (the `q`
// string included), no request/response bodies, no secrets. Only sizes, forms, and the values
// of knobs a tool signature explicitly marked [LogArg]; unmarked is unobservable.
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
			// The [LogArg]-marked args, extracted ONCE (one registry lookup by tool name) and fed
			// to BOTH sinks: the span tags below and the self-log event's dynamic properties.
			var args = request.Params?.Arguments;
			var marked = ExtractMarkedArgs(tool, args);

			using var span = StartToolSpan(tool);
			if (span is not null)
			{
				span.SetTag("petbox.request_chars", reqChars);
				span.SetTag("petbox.session_id", request.Server.SessionId);
				foreach (var (key, value) in ExtractArgShapers(marked, args))
					span.SetTag(key, value);
			}
			try
			{
				var result = await next(request, ct);
				var respChars = ResponseChars(result);
				span?.SetTag("petbox.response_chars", respChars);
				// The inner McpErrorEnvelopeFilter converts tool-body exceptions into an
				// IsError result BEFORE this (outer) filter sees them, so attribute the outcome
				// from the result, not only the catch below.
				var outcome = result.IsError == true ? "error" : "ok";
				if (result.IsError == true)
					span?.SetStatus(ActivityStatusCode.Error);
				LogToolCall(logger, tool, session, reqChars, respChars, outcome, marked);
				return result;
			}
			catch (Exception ex)
			{
				span?.SetStatus(ActivityStatusCode.Error, ex.Message);
				// No result to measure on a genuine throw → RespChars 0, Outcome error, then
				// rethrow to preserve the error-status behavior for callers up the stack.
				LogToolCall(logger, tool, session, reqChars, 0, "error", marked);
				throw;
			}
		});

	// The self-log event's template. Its placeholder names (Tool/Session/ReqChars/RespChars/
	// Outcome) are the STABLE KQL columns (Properties.<Name>) every existing query anchors on;
	// the message starts with "mcp tool " — the query anchor for MessageTemplate.
	const string ToolCallTemplate =
		"mcp tool {Tool} session {Session} req {ReqChars} resp {RespChars} outcome {Outcome}";

	// Written by hand rather than by [LoggerMessage] because the property set is VARIABLE: a
	// source-generated template has a fixed placeholder list, but each tool contributes its own
	// [LogArg] knobs (Arg_bodyLen, Arg_limit, Arg_q, …). SystemLogger lifts EVERY KVP of the MEL
	// state (bar {OriginalFormat}, skipping nulls) into Properties.<Name>, so passing the state
	// as an IReadOnlyList<KVP> makes each marked arg a top-level, KQL-addressable column — while
	// {OriginalFormat} keeps MessageTemplate byte-identical to the old source-generated one.
	static void LogToolCall(
		ILogger logger, string? tool, string session, int reqChars, int respChars, string outcome,
		List<McpLoggedArgs.MarkedArg> marked)
	{
		if (!logger.IsEnabled(LogLevel.Information)) return;

		var state = new List<KeyValuePair<string, object?>>(6 + marked.Count)
		{
			new("Tool", tool),
			new("Session", session),
			new("ReqChars", reqChars),
			new("RespChars", respChars),
			new("Outcome", outcome),
		};
		foreach (var arg in marked)
			state.Add(new(arg.LogProperty, arg.Value));
		state.Add(new("{OriginalFormat}", ToolCallTemplate));

		var message =
			$"mcp tool {tool} session {session} req {reqChars} resp {respChars} outcome {outcome}";
		logger.Log(LogLevel.Information, new EventId(600, nameof(LogToolCall)),
			(IReadOnlyList<KeyValuePair<string, object?>>)state, null, (_, _) => message);
	}

	internal static Activity? StartToolSpan(string? toolName)
	{
		var span = PetBoxActivitySources.Mcp.StartActivity($"mcp.tool {toolName}");
		span?.SetTag("petbox.tool", toolName);
		return span;
	}

	// RespChars: the payload ONE client actually puts in context, counted ONCE. The SDK emits
	// every result TWICE on the wire — as structuredContent AND as an escaped text mirror of
	// the same JSON — but no client reads both (Claude Code reads structuredContent; droid and
	// opencode read the text copy), so the wire is ~2x what any single agent sees. Measure the
	// logical payload: structuredContent when present, else the content blocks — the error
	// envelope (McpErrorEnvelopeFilter) is text-only by design and must still be measured.
	internal static int ResponseChars(CallToolResult result) =>
		result.StructuredContent is not null
			? SerializedLength(result.StructuredContent)
			: result.Content is { Count: > 0 } content ? SerializedLength(content) : 0;

	// Length of the serialized value (0 for null). Cheap enough for the hot path — one
	// serialize pass, no second copy.
	static int SerializedLength(object? value) =>
		value is null ? 0 : JsonSerializer.Serialize(value, SizeJson).Length;

	// Pure, testable extraction of the [LogArg]-marked args of ONE call. The registry (built once
	// from the parameter markup) says WHICH params of this tool may be shaped and how; here we
	// pick the ones actually present. An unknown/unmarked tool yields NOTHING — the safe default:
	// no markup, no arg telemetry, so a param can never leak by being merely named `q` or `limit`.
	// An absent/null/empty arg produces NO entry either (present-only semantics).
	internal static List<McpLoggedArgs.MarkedArg> ExtractMarkedArgs(
		string? tool, IDictionary<string, JsonElement>? args)
	{
		var marked = new List<McpLoggedArgs.MarkedArg>();
		if (args is null || args.Count == 0)
			return marked;

		foreach (var def in McpLoggedArgs.For(tool))
		{
			if (!args.TryGetValue(def.Name, out var el) || !IsNonEmpty(el))
				continue;

			// Presence: the VALUE never leaves the process — only "it was passed, non-empty".
			// This is the only mode a free-text param (q) may ever carry.
			var value = def.Mode == LogArgMode.Presence ? true : Scalar(el);
			if (value is null)
				continue; // a Value-mode arg of a shape we refuse to render (object/array) — drop it.

			marked.Add(new(def.SpanTag, def.LogProperty, value));
		}

		return marked;
	}

	// The privacy-safe rendering of a Value-mode arg: numbers, bools and closed enum-like strings
	// only (marking a param is the assertion that its value is such a knob — see LogArgAttribute).
	// Objects/arrays have no scalar shape and are refused.
	static object? Scalar(JsonElement el) => el.ValueKind switch
	{
		JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDouble(),
		JsonValueKind.True => true,
		JsonValueKind.False => false,
		JsonValueKind.String => el.GetString(),
		_ => null,
	};

	// The span tags of one call: the marked args (already extracted) under their petbox.arg.*
	// names, plus the DERIVED `fields` shape — which is not a parameter at all but a shape read
	// off the node payload, hence computed here rather than declared in a signature.
	internal static IEnumerable<KeyValuePair<string, object?>> ExtractArgShapers(
		IReadOnlyList<McpLoggedArgs.MarkedArg> marked, IDictionary<string, JsonElement>? args)
	{
		var tags = new List<KeyValuePair<string, object?>>(marked.Count + 1);
		foreach (var arg in marked)
			tags.Add(new(arg.SpanTag, arg.Value));

		// fields — for upsert-style tools, the sorted set of NON-EMPTY payload field NAMES on
		// the node payload(s). Names only, never values: lets a status-only transition be told
		// apart from a body/title edit.
		if (args is { Count: > 0 } && ExtractFieldShape(args) is { } fields)
			tags.Add(new("petbox.arg.fields", fields));

		return tags;
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
