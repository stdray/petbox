using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PetBox.Core.Auth;
using PetBox.Core.Models;

namespace PetBox.Tests.Auth;

// ApiKey.DefaultProjectKey reaches the request as the `project_default` claim — and ONLY when the
// key carries one (an absent claim is what keeps every existing key on the old behavior). Both
// lookups must carry the field through: the DB lookup maps the column, the config lookup projects
// the appsettings entry.
public sealed class ApiKeyDefaultProjectClaimTests
{
	[Fact]
	public async Task Handler_EmitsProjectDefaultClaim_WhenTheKeyHasOne()
	{
		var ticket = await AuthenticateAsync(new ApiKey
		{
			Key = "k",
			ProjectKey = ProjectScope.AllProjects,
			Scopes = "memory:read",
			DefaultProjectKey = "kpvotes",
		});

		Claim(ticket, "project").Should().Be(ProjectScope.AllProjects);
		Claim(ticket, ApiKeyAuthenticationHandler.DefaultProjectClaim).Should().Be("kpvotes");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public async Task Handler_OmitsProjectDefaultClaim_WhenTheKeyHasNone(string? stored)
	{
		var ticket = await AuthenticateAsync(new ApiKey
		{
			Key = "k",
			ProjectKey = ProjectScope.AllProjects,
			Scopes = "memory:read",
			DefaultProjectKey = stored,
		});

		Claim(ticket, ApiKeyAuthenticationHandler.DefaultProjectClaim).Should().BeNull();
	}

	[Fact]
	public void ConfigApiKeyLookup_CarriesTheDefaultProject()
	{
		var lookup = new ConfigApiKeyLookup(Options.Create(new ConfigApiKeyOptions
		{
			ApiKeys =
			[
				new ConfigApiKeyEntry
				{
					Key = "cfg", ProjectKey = ProjectScope.AllProjects,
					Scopes = "memory:read", DefaultProjectKey = "kpvotes",
				},
				new ConfigApiKeyEntry { Key = "plain", ProjectKey = "kpvotes", Scopes = "memory:read" },
			],
		}));

		lookup.FindByKey("cfg")!.DefaultProjectKey.Should().Be("kpvotes");
		lookup.FindByKey("plain")!.DefaultProjectKey.Should().BeNull();
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
