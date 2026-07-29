using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using PetBox.Log.Core.SelfLogging;

namespace PetBox.Web.Mcp;

// Central error boundary for every MCP tool call. A tool body just THROWS on an
// auth/feature/project reject (the Assert* helpers) or any deeper failure — this filter
// catches it and returns a structured { error: { type, message, traceId | detail } } result
// instead of the framework's opaque "An error occurred invoking 'X'". The learning envelope
// stays, but the result IS flagged IsError=true: a tool that declares an outputSchema MUST, per
// the MCP spec, return structuredContent matching that schema on SUCCESS — an error is not a
// success, so it goes on the isError channel and carries NO structuredContent (the spec does
// not require schema conformance when isError=true). The {error} envelope rides the text
// content block, so agents still parse `.error`. This replaces the old per-tool GuardAsync
// wrapper — one place, every tool, concrete Task<T> return types kept.
//
// TRACEABILITY (work: mcp-error-envelope-leaks-stack-traces). The filter first WRITES the
// exception to the self-log (`exception: ex` → SystemLogger puts ex.ToString() in the
// `Exception` column, stamped with the ambient Activity's TraceId). Only because that carrier
// now exists may `detail` become conditional: when the event is retrievable BY trace id
// (a valid W3C Activity.Current AND a self-log that records this category), the envelope
// carries `traceId` and the raw stack stays out of the agent's context; otherwise there is
// nothing to look up, so the exception itself must ride the response — the answer to "either
// a trace id or the exception, never neither". Before this, the response was the ONLY place a
// tool-body stack ever materialized: nothing logged it, and nothing above sees it (the filter
// converts the throw into a result).
//
// Of the two conditions, `recorded` is the load-bearing one. MEASURED (see
// McpErrorEnvelopeTraceTests): ASP.NET Core's hosting layer starts a request Activity as soon as
// any logging provider is enabled — with OpenTelemetry OFF an MCP call still carries a valid
// 32-hex trace id. So the case to defend against is not "no Activity" but "a trace id that leads
// nowhere": hand one out while the self-log is off and the failure becomes invisible on BOTH
// ends. Hence the envelope asks whether the exception was actually STORED, not merely traced.
//
// `type`/`message` are unconditional and untouched: `type` is the CLR class name (a domain
// error code is deliberately deferred — see the card), and `message` carries contract data an
// agent acts on (the current version in a CAS conflict, "did you mean 'X'?"). traceId is a
// token for the OWNER, not the caller: the self-log lives in $system and log_query demands a
// matching project claim, so `message` must stay self-sufficient.
static partial class McpErrorEnvelopeFilter
{
	// Match the server's tool serializer (relaxed encoder so Cyrillic in a message stays
	// readable rather than \uXXXX-escaped).
	static readonly JsonSerializerOptions Json = new(ModelContextProtocol.McpJsonUtilities.DefaultOptions)
	{
		Encoder = PetBox.Core.Json.PetBoxJsonEncoder.Relaxed,
	};

	// Category MUST start with the SystemLogger prefix ("PetBox") so the self-log captures it —
	// see SystemLoggerOptions.CategoryPrefix. Its own category, not McpTracingFilter's
	// "PetBox.Mcp.ToolCalls": the failure stream is queried (and could be routed) on its own.
	const string ErrorLogCategory = "PetBox.Mcp.ToolErrors";

	// Deliberately NOT containing the literal "mcp tool" — that string is the KQL anchor every
	// economy query uses for the per-call metric event (McpTracingFilter, EventId 600). A
	// failure is a different stream and must not be swept into those counts. Anchor: "mcp error".
	const string ErrorTemplate = "mcp error {Tool} {ErrorType}";

	// A W3C trace id is 32 hex chars; an Activity with a non-W3C IdFormat (or no Activity at all)
	// yields the all-zeros id. Same guard SystemLogger.cs applies before stamping Properties.TraceId,
	// so "the envelope says traceId" and "the event carries that TraceId" cannot disagree.
	const string NoTraceId = "00000000000000000000000000000000";

	public static void Register(IMcpRequestFilterBuilder filters) =>
		filters.AddCallToolFilter(next => async (request, ct) =>
		{
			try
			{
				return await next(request, ct);
			}
			catch (Exception ex)
			{
				// Mark the surrounding tool span (McpTracingFilter) as failed regardless of
				// filter ordering — we convert to a non-IsError result below, so the tracing
				// filter's own IsError check would otherwise miss it.
				Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);

				// Write the carrier FIRST, while Activity.Current still holds the trace id the
				// envelope is about to hand out — the event and the token must agree.
				var recorded = Record(request.Services, request.Params?.Name, ex);
				var envelope = Build(ex, Activity.Current?.TraceId.ToHexString(), recorded);
				return new CallToolResult
				{
					// No StructuredContent on an error: with a declared outputSchema, a success
					// result must conform — an error must not pretend to. The learning {error}
					// envelope rides the text content instead.
					Content = [new TextContentBlock { Text = JsonSerializer.Serialize(envelope, Json) }],
					IsError = true,
				};
			}
		});

	// The envelope decision, pure and total: `traceId` XOR `detail`, never both, never neither.
	// A trace id is only worth handing out when it can actually be looked up — hence `recorded`.
	internal static ErrorEnvelope Build(Exception ex, string? traceId, bool recorded)
	{
		var traceable = recorded
			&& traceId is { Length: 32 }
			&& !string.Equals(traceId, NoTraceId, StringComparison.Ordinal);
		return new ErrorEnvelope
		{
			Error = new ErrorBody
			{
				Type = ex.GetType().Name,
				Message = ex.Message,
				TraceId = traceable ? traceId : null,
				Detail = traceable ? null : ex.ToString(),
			},
		};
	}

	// Writes the exception to every logging provider and answers the only question the envelope
	// cares about: will the SELF-LOG hold it? (A console line is not retrievable by trace id.)
	// The provider is absent whenever the self-log is off — Program.cs registers it only under
	// Seq:SelfLog:Enabled + Feature.Logging — which is exactly the fallback case.
	static bool Record(IServiceProvider? services, string? tool, Exception ex)
	{
		if (services is null)
			return false;

		var factory = services.GetService<ILoggerFactory>();
		if (factory is not null)
			LogToolError(factory.CreateLogger(ErrorLogCategory), tool ?? "-", ex.GetType().Name, ex);

		return services.GetService<SystemLoggerProvider>()
			?.CreateLogger(ErrorLogCategory).IsEnabled(LogLevel.Error) == true;
	}

	// Tool/ErrorType become KQL-addressable Properties.<Name> (SystemLogger lifts every state
	// KVP), and `ex` lands in the `Exception` COLUMN — the stack's storage, and the whole point.
	[LoggerMessage(EventId = 601, Level = LogLevel.Error, Message = ErrorTemplate)]
	static partial void LogToolError(ILogger logger, string tool, string errorType, Exception exception);

	// The {error} body. traceId/detail are mutually exclusive by construction (Build) and omitted
	// when null, so the wire shape is byte-identical to the old anonymous type for every field an
	// agent already reads. Nested (not a Mcp/Contract type): this is the ERROR channel, which by
	// MCP-spec design carries no outputSchema and is not part of any tool's declared contract.
	internal sealed record ErrorBody
	{
		[JsonPropertyName("type")]
		public required string Type { get; init; }

		[JsonPropertyName("message")]
		public required string Message { get; init; }

		[JsonPropertyName("traceId")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? TraceId { get; init; }

		[JsonPropertyName("detail")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Detail { get; init; }
	}

	internal sealed record ErrorEnvelope
	{
		[JsonPropertyName("error")]
		public required ErrorBody Error { get; init; }
	}
}
