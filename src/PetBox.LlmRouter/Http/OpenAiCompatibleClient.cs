using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PetBox.LlmRouter.Contract;

namespace PetBox.LlmRouter.Http;

// Raw OpenAI-compatible HTTP client for embed/rerank/chat. No SDK dependency (Microsoft.
// Extensions.AI is not in the dependency set; all three upstreams speak the OpenAI dialect,
// so one raw client covers them and keeps the one-box lean). Stateless -> singleton.
public sealed class OpenAiCompatibleClient : IOpenAiCompatibleClient
{
	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
		LlmThinking? thinking, CancellationToken ct)
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

		using var doc = await PostAsync(http, Url(baseUrl, "/v1/chat/completions"), apiKey, payload, ct);
		var choices = doc.RootElement.GetProperty("choices");
		if (choices.GetArrayLength() == 0)
			throw new LlmUpstreamException(false, "chat response had no choices");
		return choices[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
	}

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
				var transient = rateLimited || code >= 500;
				throw new LlmUpstreamException(transient, $"HTTP {code}: {Truncate(body)}", rateLimited: rateLimited);
			}
			try { return JsonDocument.Parse(body); }
			catch (JsonException ex) { throw new LlmUpstreamException(false, $"invalid JSON from upstream: {ex.Message}"); }
		}
	}

	static string Url(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;

	static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
