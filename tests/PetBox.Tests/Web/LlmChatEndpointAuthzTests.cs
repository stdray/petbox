using System.Net;
using System.Text;
using System.Text.Json;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// AuthZ tests for the LlmRouter REST endpoint (POST /v1/chat/completions).
// The endpoint requires the "LlmInvoke" named policy (ApiKey auth + llm:invoke scope)
// and resolves the project from the key's project claim (no project in the URL).
// Reuses LogPipelineFixture (shared host); each test seeds a fresh project+key pair.
public sealed class LlmChatEndpointAuthzTests : IClassFixture<LogPipelineFixture>
{
	readonly LogPipelineFixture _fx;
	readonly HttpClient _client;
	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public LlmChatEndpointAuthzTests(LogPipelineFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	async Task<(string Key, string Project)> SeedProjectKeyAsync(string scopes)
	{
		var proj = $"llmchatauthz{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		using var scope = _fx.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.InsertAsync(new Project
		{
			Key = proj,
			WorkspaceKey = "$system",
			Name = proj
		});
		await db.InsertAsync(new ApiKey
		{
			Key = key,
			ProjectKey = proj,
			Scopes = scopes,
			Name = key,
			CreatedAt = DateTime.UtcNow
		});
		return (key, proj);
	}

	static HttpRequestMessage ChatReq(string apiKey, object body)
	{
		var json = JsonSerializer.Serialize(body, Json);
		var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
		{
			Content = new StringContent(json, Encoding.UTF8, "application/json")
		};
		req.Headers.Add("X-Api-Key", apiKey);
		return req;
	}

	[Fact]
	public async Task Chat_NoApiKey_Returns401()
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
		{
			Content = new StringContent("{}", Encoding.UTF8, "application/json")
		};
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
			"a request without an API key cannot authenticate against the ApiKey scheme — 401, not 403");
	}

	[Fact]
	public async Task Chat_NoLlmInvokeScope_Returns403()
	{
		var (key, _) = await SeedProjectKeyAsync("tasks:read");
		using var resp = await _client.SendAsync(ChatReq(key, new
		{
			messages = new[] { new { role = "user", content = "hello" } }
		}));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"a key without llm:invoke scope must be rejected by the LlmInvoke policy (403, not auth-checking scope ourselves)");
	}

	[Fact]
	public async Task Chat_ValidKey_EmptyMessages_Returns400()
	{
		var (key, _) = await SeedProjectKeyAsync("llm:invoke");
		using var resp = await _client.SendAsync(ChatReq(key, new
		{
			messages = Array.Empty<object>()
		}));
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
			"a valid key with llm:invoke must pass the auth gate and reach body validation (empty messages → 400, not 403)");
	}
}
