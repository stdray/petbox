using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PetBox.Core.Auth;
using PetBox.Core.Models;

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

// work config-key-expiry-timezone-normalization / spec auth-key-expiry. The tests above (and the
// pre-fix version of this file) feed ConfigApiKeyLookup a ready-made `DateTime` via
// `DateTime.UtcNow.AddHours(1).ToString("O")` and only ever assert the endpoint accepted/rejected
// the key — never the resulting `.Kind`, and on a host whose local UTC offset happens to be zero
// the pre-fix defect (Binder's plain `DateTime.Parse`, no RoundtripKind/AdjustToUniversal, turning
// a zone-suffixed string into Kind=Local with SHIFTED digits) produces no observable shift at all.
// That is how the defect reached prod undetected: it needs a host at a non-zero offset to show up
// as a wrong accept/reject decision, and CI does not control that.
//
// These tests instead run the REAL string -> ConfigApiKeyEntry -> ApiKey path (an in-memory
// Microsoft.Extensions.Configuration source bound through the same ConfigurationBinder Program.cs
// uses for `Auth:ApiKeys`) and assert the resulting instant AND `.Kind` directly, so they fail on
// every host regardless of its timezone — see each test's comment for why the offset case in
// particular is host-independent by construction, not by luck.
public sealed class ConfigApiKeyExpiryTimezoneNormalizationTests
{
	static ApiKey BindSingleKey(string key, string? expiresAtRaw)
	{
		var values = new Dictionary<string, string?>
		{
			["ApiKeys:0:Key"] = key,
			["ApiKeys:0:ProjectKey"] = "proj",
			["ApiKeys:0:Scopes"] = "config:read",
		};
		if (expiresAtRaw is not null) values["ApiKeys:0:ExpiresAt"] = expiresAtRaw;

		// Root-bind mirrors Program.cs's `Configure<ConfigApiKeyOptions>(Configuration.GetSection("Auth"))`
		// exactly (same ConfigurationBinder machinery) — only the "Auth" section prefix is elided
		// since there is nothing else in this ad hoc configuration root to disambiguate from.
		var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
		var options = config.Get<ConfigApiKeyOptions>() ?? new ConfigApiKeyOptions();
		var lookup = new ConfigApiKeyLookup(Options.Create(options));
		return lookup.FindByKey(key) ?? throw new InvalidOperationException("key did not bind");
	}

	// "Z" is unambiguous UTC on the page, but the Binder's `DateTime.Parse(value, InvariantCulture)`
	// (DateTimeStyles.None, the default for that overload) converts a zone-suffixed string to
	// Kind=Local, shifting the digits to whatever the HOST's local offset is, before
	// ConfigApiKeyLookup ever sees the value. This test does not fight that conversion — it lets it
	// happen, then checks ConfigApiKeyLookup's normalization (ToUniversalTime on a Local Kind)
	// undoes it. That undo is exact on ANY host: Parse's shift-to-local and this test's (via the
	// fix) shift-back-to-UTC both go through the SAME host's TimeZoneInfo.Local, so they cancel
	// regardless of what that offset actually is.
	[Fact]
	public void ZSuffix_NormalizesToCorrectUtcInstantAndKind()
	{
		var key = BindSingleKey("tz-zsuffix", "2026-08-26T09:33:13Z");
		key.ExpiresAt.Should().NotBeNull();
		key.ExpiresAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
		key.ExpiresAt.Value.Should().Be(new DateTime(2026, 8, 26, 9, 33, 13, DateTimeKind.Utc));
	}

	// An explicit numeric offset is the strongest case: Parse computes the absolute instant from
	// the stated offset, converts it to host-local (Kind=Local, digits changed), and
	// ToUniversalTime() converts that back to UTC — the round trip through one host's own local
	// zone is exact by construction, so the asserted instant is correct on every machine this runs
	// on, not just the one it happened to be authored on.
	[Fact]
	public void ExplicitOffset_NormalizesToCorrectUtcInstantAndKind()
	{
		var key = BindSingleKey("tz-offset", "2026-08-26T14:33:13+05:00");
		key.ExpiresAt.Should().NotBeNull();
		key.ExpiresAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
		key.ExpiresAt.Value.Should().Be(new DateTime(2026, 8, 26, 9, 33, 13, DateTimeKind.Utc));
	}

	// No zone suffix at all: Parse yields Kind=Unspecified with the digits taken literally — this
	// path was never shifted by Parse on any host, so it was never the visible half of the defect.
	// What's under test is the DECIDED semantics: absence of a suffix means UTC, not host-local.
	[Fact]
	public void NoSuffix_IsTreatedAsUtc()
	{
		var key = BindSingleKey("tz-nosuffix", "2026-08-26T09:33:13");
		key.ExpiresAt.Should().NotBeNull();
		key.ExpiresAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
		key.ExpiresAt.Value.Should().Be(new DateTime(2026, 8, 26, 9, 33, 13, DateTimeKind.Utc));
	}

	// Letter-of-spec (auth-key-expiry): absence of the ExpiresAt key entirely still binds to an
	// unbounded key through this SAME real-binder path — the fix must not have started requiring one.
	[Fact]
	public void MissingExpiresAt_StaysUnbounded()
	{
		var key = BindSingleKey("tz-unbounded", null);
		key.ExpiresAt.Should().BeNull();
	}
}
