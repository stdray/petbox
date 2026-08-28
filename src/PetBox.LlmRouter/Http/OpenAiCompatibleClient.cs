using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PetBox.LlmRouter.Contract;

namespace PetBox.LlmRouter.Http;

// Raw OpenAI-compatible HTTP client for embed/rerank/chat. No SDK dependency (Microsoft.
// Extensions.AI is not in the dependency set; all three upstreams speak the OpenAI dialect,
// so one raw client covers them and keeps the one-box lean). Stateless -> singleton.
public sealed partial class OpenAiCompatibleClient : IOpenAiCompatibleClient
{
	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	readonly ILogger<OpenAiCompatibleClient>? _log;

	// Logger is OPTIONAL (default null) so every existing `new OpenAiCompatibleClient()` call —
	// tests included — keeps compiling; DI (AddLlmRouter's TryAddSingleton) resolves the real
	// ILogger<OpenAiCompatibleClient> automatically, no registration change needed.
	public OpenAiCompatibleClient(ILogger<OpenAiCompatibleClient>? log = null)
	{
		_log = log;
	}

	public async Task<IReadOnlyList<float[]>> EmbedAsync(
		HttpClient http, string baseUrl, string? apiKey, string model,
		IReadOnlyList<string> inputs, CancellationToken ct)
	{
		using var doc = await PostAsync(http, Url(baseUrl, "/v1/embeddings"), apiKey,
			new { model, input = inputs }, ct);

		var data = doc.RootElement.GetProperty("data");
		var n = data.GetArrayLength();
		var vectors = new float[n][];
		var order = 0;
		foreach (var item in data.EnumerateArray())
		{
			var emb = item.GetProperty("embedding");
			var vec = new float[emb.GetArrayLength()];
			var k = 0;
			foreach (var f in emb.EnumerateArray()) vec[k++] = f.GetSingle();
			// Honor the upstream's `index` when it's in range; otherwise keep enumeration order.
			var idx = item.TryGetProperty("index", out var ie) && ie.TryGetInt32(out var i) && i >= 0 && i < n ? i : order;
			vectors[idx] = vec;
			order++;
		}
		for (var i = 0; i < n; i++) vectors[i] ??= [];
		return vectors;
	}

	public async Task<IReadOnlyList<RerankHit>> RerankAsync(
		HttpClient http, string baseUrl, string? apiKey, string model,
		string query, IReadOnlyList<string> documents, int? topN, CancellationToken ct)
	{
		object payload = topN is { } n
			? new { model, query, documents, top_n = n }
			: new { model, query, documents };
		using var doc = await PostAsync(http, Url(baseUrl, "/v1/rerank"), apiKey, payload, ct);

		if (!doc.RootElement.TryGetProperty("results", out var results))
			throw new LlmUpstreamException(false, "rerank response missing 'results'");

		var hits = new List<RerankHit>(results.GetArrayLength());
		foreach (var r in results.EnumerateArray())
		{
			var idx = r.TryGetProperty("index", out var ie) && ie.TryGetInt32(out var i) ? i : 0;
			double score = r.TryGetProperty("relevance_score", out var rs) ? rs.GetDouble()
				: r.TryGetProperty("score", out var s) ? s.GetDouble() : 0d;
			hits.Add(new RerankHit(idx, score));
		}
		return hits;
	}

	public async Task<string> ChatAsync(
		HttpClient http, string baseUrl, string? apiKey, string model,
		IReadOnlyList<ChatMessage> messages, double? temperature, int? maxTokens,
		LlmThinking? thinking, LlmResponseFormat? responseFormat, CancellationToken ct)
	{
		var payload = new Dictionary<string, object>
		{
			["model"] = model,
			["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
		};
		if (temperature is { } t) payload["temperature"] = t;
		if (maxTokens is { } mt) payload["max_tokens"] = mt;
		// DeepSeek-dialect reasoning switch; absent = provider default (llm-route-reasoning-mode).
		if (thinking is { } th)
			payload["thinking"] = new { type = th == LlmThinking.Enabled ? "enabled" : "disabled" };
		if (responseFormat is not null) payload["response_format"] = ToWireFormat(responseFormat);

		var url = Url(baseUrl, "/v1/chat/completions");
		JsonDocument doc;
		try
		{
			doc = await PostAsync(http, url, apiKey, payload, ct);
		}
		catch (LlmUpstreamException ex) when (!ex.Transient && responseFormat is not null
			&& ex.Message.Contains("response_format", StringComparison.OrdinalIgnoreCase))
		{
			// CapabilityRouter.RunChainAsync treats a non-transient (4xx) failure as DEFINITIVE —
			// it rethrows immediately and never falls through to the next provider in the chain
			// (see the `catch (LlmUpstreamException ux) when (!ux.Transient)` there). So an
			// endpoint that rejects an unsupported response_format with a fatal 400 would turn
			// "sometimes lose a batch" into "Chat is dead on this route entirely". One bare retry
			// with the field stripped restores exactly today's unconstrained call for THIS
			// endpoint only — every other endpoint in the chain still gets the field.
			//
			// That retry must NOT be silent: an endpoint that rejects response_format and falls
			// back to unconstrained output is EXACTLY the transport disease the caller is trying
			// to route around (facts-extraction-unparseable-batches) — if this degrades quietly,
			// nobody can ever tell which endpoint stopped enforcing structure. Warning, not
			// Debug/Information: this is a standing config problem on THIS endpoint, not routine
			// traffic.
			if (_log is not null) LogResponseFormatRejected(_log, baseUrl, model, ex.Message);
			payload.Remove("response_format");
			doc = await PostAsync(http, url, apiKey, payload, ct);
		}
		using (doc)
		{
			var choices = doc.RootElement.GetProperty("choices");
			if (choices.GetArrayLength() == 0)
				throw new LlmUpstreamException(false, "chat response had no choices");
			return choices[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
		}
	}

	// The two OpenAI-dialect response_format shapes (llm-structured-output). The caller picks
	// which one to send; this only renders it onto the wire.
	static object ToWireFormat(LlmResponseFormat format) => format switch
	{
		LlmResponseFormat.JsonObject => new { type = "json_object" },
		LlmResponseFormat.JsonSchema js => new { type = "json_schema", json_schema = new { name = js.Name, schema = js.Schema } },
		_ => throw new NotSupportedException($"unknown LlmResponseFormat: {format.GetType()}"),
	};

	// POST JSON, mapping transport faults + HTTP status to transient/fatal LlmUpstreamException.
	static async Task<JsonDocument> PostAsync(HttpClient http, string url, string? apiKey, object payload, CancellationToken ct)
	{
		using var req = new HttpRequestMessage(HttpMethod.Post, url);
		if (!string.IsNullOrWhiteSpace(apiKey))
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
		req.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

		HttpResponseMessage resp;
		try
		{
			resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
		}
		catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
		{
			throw new LlmUpstreamException(true, "request timed out", ex);
		}
		catch (HttpRequestException ex)
		{
			throw new LlmUpstreamException(true, $"connection failed: {ex.Message}", ex);
		}

		using (resp)
		{
			var body = await resp.Content.ReadAsStringAsync(ct);
			if (!resp.IsSuccessStatusCode)
			{
				var code = (int)resp.StatusCode;
				var rateLimited = code == 429;
				// A size-limit refusal is a DETERMINISTIC failure by CONTENT, not by status code (bug
				// rerank-oversize-falls-through-both-legs): llama-server answers an oversized rerank/embed
				// input with HTTP 500 (`... is too large to process. increase the physical batch size ...`),
				// which the old `code >= 500` rule classified transient — so CapabilityRouter retried on
				// the NEXT route in the chain, which for this repo's rerank fallback has an even SMALLER
				// ceiling (10240 tokens per whole request vs. the local route's 8192 per pair) and refuses
				// with its own wording (`... exceeds maximum allowed token size ...`). Both refusals are
				// checked here so ANY leg's oversize wording is caught regardless of which one answers
				// first. The retry was guaranteed to fail either way — non-transient stops CapabilityRouter
				// from wasting it and, more importantly, from masking the real failure behind a generic
				// "all providers failed" after burning an attempt on a route that could never have served
				// this input.
				var oversize = IsInputTooLarge(body);
				var transient = !oversize && (rateLimited || code >= 500);
				throw new LlmUpstreamException(transient, $"HTTP {code}: {Truncate(body)}", rateLimited: rateLimited);
			}
			try { return JsonDocument.Parse(body); }
			catch (JsonException ex) { throw new LlmUpstreamException(false, $"invalid JSON from upstream: {ex.Message}"); }
		}
	}

	static string Url(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;

	static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

	// Recognizes the two size-limit wordings this repo has actually observed on a failing rerank/embed
	// call (bug rerank-oversize-falls-through-both-legs): llama-server's per-pair physical-batch refusal
	// ("... is too large to process. increase the physical batch size ...", HTTP 500) and the OpenAI-
	// dialect cloud fallback's whole-request token-count refusal ("... exceeds maximum allowed token
	// size ...", HTTP 422 today — already non-transient by the `code >= 500` rule, matched here too so
	// this is the ONE place that answers "was this a size refusal?" regardless of which leg answered
	// or what status code it chose). Matched against the FULL body (not the 300-char Truncate used for
	// the exception message) so the phrase is never missed to a truncation the caller never asked for.
	static bool IsInputTooLarge(string body) =>
		body.Contains("too large to process", StringComparison.OrdinalIgnoreCase)
		|| body.Contains("exceeds maximum allowed", StringComparison.OrdinalIgnoreCase);

	// The one queryable signal for a silently-degrading endpoint (facts-extraction-unparseable-
	// batches): fires exactly when a response_format-bearing chat call got a fatal 400 mentioning
	// response_format and was retried WITHOUT it. Distinct wording from every autocapture log line
	// ("facts extraction ...", "facts judge ...") — this is a transport/config fact about the
	// ENDPOINT, not a per-batch extraction outcome. `log_query` target for the post-deploy check:
	// `events | where Message contains "response_format REJECTED"`.
	[LoggerMessage(EventId = 307, Level = LogLevel.Warning,
		Message = "llm chat response_format REJECTED by endpoint '{BaseUrl}' model '{Model}': {Detail} — retried once WITHOUT response_format; this endpoint now silently degrades to UNCONSTRAINED output until its config is fixed")]
	static partial void LogResponseFormatRejected(ILogger logger, string baseUrl, string model, string detail);
}
