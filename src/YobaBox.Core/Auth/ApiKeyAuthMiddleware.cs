using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using YobaBox.Core.Data;

namespace YobaBox.Core.Auth;

public sealed class ApiKeyAuthMiddleware
{
	readonly RequestDelegate _next;

	public ApiKeyAuthMiddleware(RequestDelegate next) => _next = next;

	public async Task InvokeAsync(HttpContext context)
	{
		var apiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();
		if (string.IsNullOrEmpty(apiKey))
		{
			context.Response.StatusCode = 401;
			return;
		}

		var db = context.RequestServices.GetRequiredService<YobaBoxDb>();
		var key = await db.ApiKeys
			.FirstOrDefaultAsync(k => k.Key == apiKey, CancellationToken.None);

		if (key is null)
		{
			context.Response.StatusCode = 401;
			return;
		}

		context.Items["ProjectKey"] = key.ProjectKey;
		context.Items["Scopes"] = key.Scopes;

		if (context.Request.Path.StartsWithSegments("/api/config"))
		{
			var requiredScope = context.Request.Method == "GET" ? "config:read" : "config:write";
			if (!HasScope(key.Scopes, requiredScope))
			{
				context.Response.StatusCode = 403;
				return;
			}
		}

		await _next(context);
	}

	static bool HasScope(string scopes, string required)
	{
		if (string.IsNullOrWhiteSpace(scopes))
			return false;
		return scopes
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Any(s => string.Equals(s, required, StringComparison.OrdinalIgnoreCase));
	}
}
