using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PetBox.Core.Auth;
using PetBox.Core.Models;

namespace PetBox.Tests.Auth;

// ApiKey.SandboxOnly (spec work/smoke-writes-into-real-projects) reaches the request as the
// `sandbox_only` claim — and ONLY when the key carries it: an absent claim is what keeps every
// existing key on the old behavior (ProjectScope.AuthorizesAsync skips the containment check
// entirely when the claim is missing). Mirrors ApiKeyDefaultProjectClaimTests' shape.
public sealed class ApiKeySandboxOnlyClaimTests
{
	[Fact]
	public async Task Handler_EmitsSandboxOnlyClaim_WhenTheKeyIsSandboxOnly()
	{
		var ticket = await AuthenticateAsync(new ApiKey
		{
			Key = "k",
			ProjectKey = "kpvotes",
			Scopes = "memory:read",
			SandboxOnly = true,
		});

		Claim(ticket, ApiKeyAuthenticationHandler.SandboxOnlyClaim).Should().Be("true");
	}

	[Fact]
	public async Task Handler_OmitsSandboxOnlyClaim_WhenTheKeyIsNotSandboxOnly()
	{
		var ticket = await AuthenticateAsync(new ApiKey
		{
			Key = "k",
			ProjectKey = "kpvotes",
			Scopes = "memory:read",
			SandboxOnly = false,
		});

		Claim(ticket, ApiKeyAuthenticationHandler.SandboxOnlyClaim).Should().BeNull();
	}

	// ── plumbing ───────────────────────────────────────────────────────────────

	static string? Claim(AuthenticationTicket? ticket, string type) =>
		ticket?.Principal.Claims.FirstOrDefault(c => c.Type == type)?.Value;

	static async Task<AuthenticationTicket?> AuthenticateAsync(ApiKey key)
	{
		using var services = new ServiceCollection()
			.AddOptions()
			.AddLogging()
			.AddSingleton<IApiKeyLookup>(new StubLookup(key))
			.BuildServiceProvider();

		var ctx = new DefaultHttpContext { RequestServices = services };
		ctx.Request.Headers[ApiKeyAuthenticationHandler.ApiKeyHeader] = key.Key;

		var handler = new ApiKeyAuthenticationHandler(
			services.GetRequiredService<IOptionsMonitor<AuthenticationSchemeOptions>>(),
			services.GetRequiredService<ILoggerFactory>(),
			UrlEncoder.Default);
		await handler.InitializeAsync(
			new AuthenticationScheme(ApiKeyAuthenticationHandler.SchemeName, null, typeof(ApiKeyAuthenticationHandler)),
			ctx);

		var result = await handler.AuthenticateAsync();
		result.Succeeded.Should().BeTrue();
		return result.Ticket;
	}

	sealed class StubLookup(ApiKey key) : IApiKeyLookup
	{
		public ApiKey? FindByKey(string k) => k == key.Key ? key : null;
	}
}
