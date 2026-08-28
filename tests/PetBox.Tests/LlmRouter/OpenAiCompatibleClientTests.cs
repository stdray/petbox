using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Http;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace PetBox.Tests.LlmRouter;

// The response_format wire contract (bug facts-extraction-unparseable-batches / spec
// memory-autocapture): null must be byte-for-byte today's payload (no key at all — the
// distiller's transport disease predates this feature and must not regress for any caller that
// doesn't opt in), and the two OpenAI dialects the distiller uses must render exactly as the
// upstream expects. Also covers the one 400-retry safety net for an upstream that rejects the
// field outright (CapabilityRouter.RunChainAsync stops the whole fallback chain on a
// non-transient failure, so silently eating a response_format 400 here is the only fix that
// doesn't touch the chain-walk itself).
public sealed class OpenAiCompatibleClientTests
{
	static readonly IReadOnlyList<PetBox.LlmRouter.Contract.ChatMessage> Messages =
		[new PetBox.LlmRouter.Contract.ChatMessage("user", "hi")];

	static (OpenAiCompatibleClient Client, HttpClient Http, CapturingHandler Handler) Build(
		string body = """{"choices":[{"message":{"content":"ok"}}]}""", HttpStatusCode status = HttpStatusCode.OK)
	{
		var handler = new CapturingHandler(body, status);
		var http = new HttpClient(handler);
		return (new OpenAiCompatibleClient(), http, handler);
	}

	[Fact]
	public async Task NullResponseFormat_PayloadHasNoResponseFormatKey()
	{
		var (client, http, handler) = Build();

		await client.ChatAsync(http, "https://d", null, "m", Messages, null, null, null, null, CancellationToken.None);

		using var doc = JsonDocument.Parse(handler.LastBody!);
		doc.RootElement.TryGetProperty("response_format", out _).Should().BeFalse();
	}

	[Fact]
	public async Task JsonObjectFormat_RendersTypeJsonObject()
	{
		var (client, http, handler) = Build();

		await client.ChatAsync(http, "https://d", null, "m", Messages, null, null, null,
			LlmResponseFormat.JsonObject.Instance, CancellationToken.None);

		using var doc = JsonDocument.Parse(handler.LastBody!);
		var rf = doc.RootElement.GetProperty("response_format");
		rf.GetProperty("type").GetString().Should().Be("json_object");
	}

	[Fact]
	public async Task JsonSchemaFormat_RendersTypeJsonSchemaWithNameAndSchema()
	{
		var (client, http, handler) = Build();
		var schema = new { type = "object", properties = new { facts = new { type = "array" } } };

		await client.ChatAsync(http, "https://d", null, "m", Messages, null, null, null,
			new LlmResponseFormat.JsonSchema("facts_envelope", schema), CancellationToken.None);

		using var doc = JsonDocument.Parse(handler.LastBody!);
		var rf = doc.RootElement.GetProperty("response_format");
		rf.GetProperty("type").GetString().Should().Be("json_schema");
		var js = rf.GetProperty("json_schema");
		js.GetProperty("name").GetString().Should().Be("facts_envelope");
		js.GetProperty("schema").GetProperty("type").GetString().Should().Be("object");
	}

	[Fact]
	public async Task Upstream400MentioningResponseFormat_RetriesOnceWithoutTheField_Succeeds_AndLogsTheDegradation()
	{
		// The retry alone would repeat the exact silent-loss pattern the bug card is about (a
		// batch's fate is invisible from outside): the endpoint falling back to unconstrained
		// output MUST leave a Warning-level, queryable trace naming which endpoint/model degraded.
		var handler = new SequencedHandler(
			(HttpStatusCode.BadRequest, """{"error":"Unrecognized request argument supplied: response_format"}"""),
			(HttpStatusCode.OK, """{"choices":[{"message":{"content":"recovered"}}]}"""));
		var http = new HttpClient(handler);
		var log = new CapturingLogger<OpenAiCompatibleClient>();
		var client = new OpenAiCompatibleClient(log);

		var text = await client.ChatAsync(http, "https://deepseek.example", null, "deepseek-v4-pro", Messages, null, null, null,
			LlmResponseFormat.JsonObject.Instance, CancellationToken.None);

		text.Should().Be("recovered");
		handler.Bodies.Should().HaveCount(2);
		using (var first = JsonDocument.Parse(handler.Bodies[0]))
			first.RootElement.TryGetProperty("response_format", out _).Should().BeTrue("the first attempt still asks for structured output");
		using (var retry = JsonDocument.Parse(handler.Bodies[1]))
			retry.RootElement.TryGetProperty("response_format", out _).Should().BeFalse("the retry strips the rejected field");

		var entry = log.Entries.Should().ContainSingle(e => e.Level == MsLogLevel.Warning).Subject;
		entry.Message.Should().Contain("response_format REJECTED")
			.And.Contain("https://deepseek.example") // WHICH endpoint degraded
			.And.Contain("deepseek-v4-pro");          // WHICH model on that endpoint
	}

	[Fact]
	public async Task Upstream400MentioningResponseFormat_NoLoggerWired_StillRetries()
	{
		// The logger is optional (DI resolves the real one; a caller that doesn't wire one, or an
		// old test, must not crash) — the retry itself does not depend on logging succeeding.
		var handler = new SequencedHandler(
			(HttpStatusCode.BadRequest, """{"error":"Unrecognized request argument supplied: response_format"}"""),
			(HttpStatusCode.OK, """{"choices":[{"message":{"content":"recovered"}}]}"""));
		var http = new HttpClient(handler);
		var client = new OpenAiCompatibleClient(); // no logger

		var text = await client.ChatAsync(http, "https://d", null, "m", Messages, null, null, null,
			LlmResponseFormat.JsonObject.Instance, CancellationToken.None);

		text.Should().Be("recovered");
	}

	[Fact]
	public async Task Upstream400NotMentioningResponseFormat_DoesNotRetry_ThrowsNonTransient_NoDegradationLogged()
	{
		var handler = new SequencedHandler(
			(HttpStatusCode.BadRequest, """{"error":"invalid model id"}"""));
		var http = new HttpClient(handler);
		var log = new CapturingLogger<OpenAiCompatibleClient>();
		var client = new OpenAiCompatibleClient(log);

		var act = async () => await client.ChatAsync(http, "https://d", null, "m", Messages, null, null, null,
			LlmResponseFormat.JsonObject.Instance, CancellationToken.None);

		var ex = (await act.Should().ThrowAsync<LlmUpstreamException>()).Which;
		ex.Transient.Should().BeFalse();
		handler.Bodies.Should().ContainSingle(); // no retry spent on an unrelated 400
		log.Entries.Should().BeEmpty(); // an unrelated 400 is not a response_format degradation
	}

	// ---- size-limit classification (bug rerank-oversize-falls-through-both-legs) ----
	//
	// A size-limit refusal is DETERMINISTIC by CONTENT, not by status code — the old `code >= 500 →
	// transient` rule sent every home-oversized rerank straight into a retry that classified correctly
	// only by accident. Every route's ceiling measured on this project is per query+document PAIR, never
	// per whole request (card comment a85af1e9d92e444d974e520c32b5f1ef: a 60-doc/~18k-token-total batch
	// passes fine everywhere; only one oversized PAIR fails) — but RerankInputTruncation (the primary fix
	// on this bug) already keeps a pair below every known route's ceiling in the normal case, so this
	// classification mainly stops a wasted attempt + breaker failure on an already-doomed retry rather
	// than routing around a genuinely bigger-ceilinged fallback.

	[Fact]
	public async Task Rerank500TooLargeToProcess_IsClassifiedNonTransient()
	{
		// The EXACT wording measured on the live local route (bug card, 2026-08-27): llama-server's
		// physical-batch refusal, HTTP 500 — the status code alone says "transient", the body says
		// otherwise.
		var (client, http, _) = Build(
			body: """{"error":"input (15936 tokens) is too large to process. increase the physical batch size (current batch size: 8192)"}""",
			status: HttpStatusCode.InternalServerError);

		var act = async () => await client.RerankAsync(http, "https://home", null, "m", "q", ["d"], null, CancellationToken.None);

		var ex = (await act.Should().ThrowAsync<LlmUpstreamException>()).Which;
		ex.Transient.Should().BeFalse("a size refusal will fail again on retry — 500 alone must not make it look transient");
		ex.RateLimited.Should().BeFalse();
	}

	[Fact]
	public async Task Rerank422ExceedsMaximumAllowed_IsClassifiedNonTransient()
	{
		// The cloud fallback's OWN wording (bug card): already non-transient under the old rule too
		// (422 < 500), but covered here so the SAME content check is proven to fire regardless of which
		// leg answers or what status code it happens to choose.
		var (client, http, _) = Build(
			body: """{"error":"Input length 14940 exceeds maximum allowed token size 10240"}""",
			status: HttpStatusCode.UnprocessableEntity);

		var act = async () => await client.RerankAsync(http, "https://cloud", null, "m", "q", ["d"], null, CancellationToken.None);

		var ex = (await act.Should().ThrowAsync<LlmUpstreamException>()).Which;
		ex.Transient.Should().BeFalse();
	}

	[Fact]
	public async Task Ordinary500_IsStillClassifiedTransient()
	{
		// The size-limit content check must be NARROW: an unrelated 500 (upstream crashed, out of
		// memory, whatever) must still fall through to the next route in the chain exactly as before —
		// this fix must not turn every 5xx into a dead end.
		var (client, http, _) = Build(body: """{"error":"internal server error"}""", status: HttpStatusCode.InternalServerError);

		var act = async () => await client.RerankAsync(http, "https://home", null, "m", "q", ["d"], null, CancellationToken.None);

		var ex = (await act.Should().ThrowAsync<LlmUpstreamException>()).Which;
		ex.Transient.Should().BeTrue("an ordinary 5xx with no size-limit wording must keep falling through the chain");
	}

	[Fact]
	public async Task Embed500TooLargeToProcess_IsClassifiedNonTransient()
	{
		// The same classification lives in the ONE shared PostAsync, so Embed (which can hit the same
		// physical-batch ceiling as Rerank) gets it too, not just the rerank call path.
		var (client, http, _) = Build(
			body: """{"error":"input (9800 tokens) is too large to process. increase the physical batch size (current batch size: 8192)"}""",
			status: HttpStatusCode.InternalServerError);

		var act = async () => await client.EmbedAsync(http, "https://home", null, "m", ["some text"], CancellationToken.None);

		var ex = (await act.Should().ThrowAsync<LlmUpstreamException>()).Which;
		ex.Transient.Should().BeFalse();
	}

	sealed record LogEntry(MsLogLevel Level, string Message);

	sealed class CapturingLogger<T> : ILogger<T>
	{
		public List<LogEntry> Entries { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(MsLogLevel logLevel) => true;
		public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
	}

	sealed class CapturingHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
	{
		public string? LastBody { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.Content is not null)
				LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
			return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
		}
	}

	// Answers each successive request from a scripted (status, body) queue and records every
	// request body seen — for the 400-then-retry-succeeds path.
	sealed class SequencedHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
	{
		readonly Queue<(HttpStatusCode Status, string Body)> _queue = new(responses);
		public List<string> Bodies { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.Content is not null)
				Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
			var (status, body) = _queue.Count > 0 ? _queue.Dequeue() : _queue.Last();
			return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
		}
	}
}
