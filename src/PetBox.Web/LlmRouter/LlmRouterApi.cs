using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.LlmRouter.Contract;

namespace PetBox.Web.LlmRouter;

// OpenAI-compatible chat completions endpoint for non-agent services (pets) that need to
// call the LLM router without MCP. One endpoint, project-scoped: the project is taken from
// the API key's project claim (no project in the URL, matching the OpenAI API surface).
//   POST /v1/chat/completions
// Auth: RequireAuthorization("LlmInvoke") — the named policy checks the ApiKey scheme AND
// the llm:invoke scope. TLS is provided by the transport (prod is HTTPS); see spec
// llm-endpoint-security for the security model.
// Streaming (stream:true) is not yet supported — returns 400 with a clear message.
// Per-project rate limits and usage policy are out of scope (later phase).
public static class LlmRouterApi
{
	public static void MapLlmRouterEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/v1/chat/completions", ChatCompletionsAsync)
			.RequireAuthorization("LlmInvoke");
	}

	// ---- DTOs (OpenAI chat.completions shape, camelCase JSON with snake_case overrides) ----

	sealed record ChatCompletionRequest
	{
		public string? Model { get; init; }
		public IReadOnlyList<ChatCompletionMessage>? Messages { get; init; }
		public double? Temperature { get; init; }

		[JsonPropertyName("max_tokens")]
		public int? MaxTokens { get; init; }

		public bool? Stream { get; init; }
	}

	sealed record ChatCompletionMessage
	{
		public string Role { get; init; } = "";
		public string Content { get; init; } = "";
	}

	sealed record ChatCompletionResponse
	{
		public string Id { get; init; } = "";
		public string Object { get; init; } = "chat.completion";
		public long Created { get; init; }
		public string Model { get; init; } = "";
		public IReadOnlyList<ChatCompletionChoice> Choices { get; init; } = [];
	}

	sealed record ChatCompletionChoice
	{
		public int Index { get; init; }
		public ChatCompletionResponseMessage Message { get; init; } = null!;

		[JsonPropertyName("finish_reason")]
		public string FinishReason { get; init; } = "stop";
	}

	sealed record ChatCompletionResponseMessage
	{
		public string Role { get; init; } = "assistant";
		public string Content { get; init; } = "";
	}

	// ---- handler ----

	static async Task<IResult> ChatCompletionsAsync(
		HttpContext ctx, ILlmClient client, CancellationToken ct)
	{
		// Auth: projectKey from the API key's project claim.
		// The LlmInvoke policy already verified ApiKey auth + llm:invoke scope — we only
		// extract the project claim here; if absent → Forbid (a cross-project '*' key
		// carries no project claim, and the OpenAI path has no project in the URL).
		var projectKey = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (string.IsNullOrEmpty(projectKey))
			return TypedResults.Forbid();

		// Parse the request body.
		ChatCompletionRequest req;
		try
		{
			req = (await ctx.Request.ReadFromJsonAsync<ChatCompletionRequest>(
				new JsonSerializerOptions(JsonSerializerDefaults.Web), ct))!;
			if (req is null)
				return TypedResults.BadRequest(new { error = "Invalid JSON body" });
		}
		catch (Exception)
		{
			return TypedResults.BadRequest(new { error = "Invalid JSON body" });
		}

		// Validate: messages must be present and non-empty.
		if (req.Messages is null || req.Messages.Count == 0)
			return TypedResults.BadRequest(new { error = "messages must be a non-empty array" });

		// Streaming is not yet supported.
		if (req.Stream == true)
			return TypedResults.BadRequest(new { error = "streaming not supported yet" });

		// Build the neutral contract and call the router. The user-facing "model" field
		// maps to ChatRequest.Tier — the router resolves it to a concrete endpoint/model
		// through the project's configured route chain.
		var msgs = req.Messages.Select(m => new ChatMessage(m.Role, m.Content)).ToList();
		var chatReq = new ChatRequest(msgs, req.Model /* tier */, req.Temperature, req.MaxTokens);
		var result = await client.ChatAsync(projectKey, chatReq, ct);

		// Map to OpenAI-compatible response shape.
		return TypedResults.Ok(new ChatCompletionResponse
		{
			Id = $"chatcmpl-{Guid.NewGuid():N}",
			Object = "chat.completion",
			Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
			Model = result.Model.Model,
			Choices =
			[
				new ChatCompletionChoice
				{
					Index = 0,
					Message = new ChatCompletionResponseMessage
					{
						Role = "assistant",
						Content = result.Text
					},
					FinishReason = "stop"
				}
			]
		});
	}
}
