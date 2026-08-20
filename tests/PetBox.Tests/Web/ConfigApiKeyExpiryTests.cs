using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PetBox.Core.Auth;

namespace PetBox.Tests.Web;

// work config-keys-lifecycle-gaps / spec auth-key-expiry — a config-sourced key (Auth:ApiKeys[])
// used to be STRUCTURALLY unable to carry ExpiresAt/SandboxOnly: ConfigApiKeyEntry had no such
// fields, so ConfigApiKeyLookup always materialized ApiKey.ExpiresAt as null regardless of operator
// intent, and ApiKeyAuthenticationHandler's expiry check (line ~102) never had anything to reject.
// This is the same invariant ApiKeyExpiryTests proves for DB-minted keys, proved here for the
// config source instead: the check itself lives in ONE place (the handler) and is generic over
// IApiKeyLookup, so the fix is entirely in what ConfigApiKeyLookup projects into ApiKey.
public sealed class ConfigApiKeyExpiryFixture : IAsyncLifetime
{
	public const string ExpiredKey = "yb_key_cfg_expired_test";
	public const string ValidKey = "yb_key_cfg_valid_test";       // future ExpiresAt
	public const string UnboundedKey = "yb_key_cfg_unbounded_test"; // no ExpiresAt at all

	const string Workspace = "cfgexpws";
	const string Project = "cfgexpproj";

	WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public ConfigApiKeyExpiryFixture()
	{
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Host:BackgroundServices"] = "false",
						["Features:Config"] = "true",

						["Auth:ApiKeys:0:Key"] = ExpiredKey,
						["Auth:ApiKeys:0:ProjectKey"] = Project,
						["Auth:ApiKeys:0:Scopes"] = "config:read",
						["Auth:ApiKeys:0:ExpiresAt"] = DateTime.UtcNow.AddDays(-1).ToString("O"),

						["Auth:ApiKeys:1:Key"] = ValidKey,
						["Auth:ApiKeys:1:ProjectKey"] = Project,
						["Auth:ApiKeys:1:Scopes"] = "config:read",
						["Auth:ApiKeys:1:ExpiresAt"] = DateTime.UtcNow.AddHours(1).ToString("O"),

						["Auth:ApiKeys:2:Key"] = UnboundedKey,
						["Auth:ApiKeys:2:ProjectKey"] = Project,
						["Auth:ApiKeys:2:Scopes"] = "config:read",
						// no ExpiresAt entry at all — must stay unbounded (letter of spec auth-key-expiry).
					});
				});
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<PetBox.Core.Data.ICoreDbFactory>().Open();
		await db.InsertAsync(new PetBox.Core.Models.Workspace { Key = Workspace, Name = "CfgExp", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new PetBox.Core.Models.Project { Key = Project, WorkspaceKey = Workspace, Name = "CfgExp" });
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class ConfigApiKeyExpiryTests : IClassFixture<ConfigApiKeyExpiryFixture>
{
	const string ExpiredKey = ConfigApiKeyExpiryFixture.ExpiredKey;
	const string ValidKey = ConfigApiKeyExpiryFixture.ValidKey;
	const string UnboundedKey = ConfigApiKeyExpiryFixture.UnboundedKey;

	readonly HttpClient _client;

	public ConfigApiKeyExpiryTests(ConfigApiKeyExpiryFixture fx)
	{
		_client = fx.Client;
	}

	[Fact]
	public async Task ExpiredConfigKey_Rejected()
	{
		var req = new HttpRequestMessage(HttpMethod.Get, "/v1/conf");
		req.Headers.Add("X-Api-Key", ExpiredKey);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task UnexpiredConfigKey_Accepted()
	{
		var req = new HttpRequestMessage(HttpMethod.Get, "/v1/conf");
		req.Headers.Add("X-Api-Key", ValidKey);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	// The letter of spec auth-key-expiry: "absence of an expiry means unbounded". A config key with
	// no ExpiresAt entry at all — the shape every key declared before this fix had — must keep working.
	[Fact]
	public async Task ConfigKeyWithNoExpiresAt_StaysUnbounded()
	{
		var req = new HttpRequestMessage(HttpMethod.Get, "/v1/conf");
		req.Headers.Add("X-Api-Key", UnboundedKey);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}
}

// Narrower unit-level proof that ConfigApiKeyLookup is what carries the field, independent of the
// full host: mirrors ApiKeyDefaultProjectClaimTests' ConfigApiKeyLookup_CarriesTheDefaultProject.
public sealed class ConfigApiKeyLookupExpiryTests
{
	[Fact]
	public void ConfigApiKeyLookup_CarriesExpiresAtAndSandboxOnly()
	{
		var expiry = DateTime.UtcNow.AddHours(1);
		var lookup = new ConfigApiKeyLookup(Options.Create(new ConfigApiKeyOptions
		{
			ApiKeys =
			[
				new ConfigApiKeyEntry
				{
					Key = "cfg-exp", ProjectKey = "proj", Scopes = "config:read",
					ExpiresAt = expiry, SandboxOnly = true,
				},
				new ConfigApiKeyEntry { Key = "cfg-plain", ProjectKey = "proj", Scopes = "config:read" },
			],
		}));

		var withExpiry = lookup.FindByKey("cfg-exp")!;
		withExpiry.ExpiresAt.Should().Be(expiry);
		withExpiry.SandboxOnly.Should().BeTrue();

		var plain = lookup.FindByKey("cfg-plain")!;
		plain.ExpiresAt.Should().BeNull("no ExpiresAt in config means an unbounded key");
		plain.SandboxOnly.Should().BeFalse();
	}
}
