using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Data;

// work/apikey-update-verb, spec apikey-mutable + apikey-update-config-key-refused.
//
// apikey_update PATCHes an issued key: name / scopes / expiry / defaultProject, each on its own and
// in combination. The load-bearing property is that an OMITTED field is not collateral damage — so
// every single-field test asserts the other three are byte-identical afterwards, read straight back
// out of core.db. Rides ProvisioningToolsFixture's host (one WebApplicationFactory for both classes).
[Collection("ProvisioningTools")]
public sealed class ApiKeyUpdateToolTests
{
	readonly WebApplicationFactory<Program> _factory;
	readonly McpClient _mcp;

	public ApiKeyUpdateToolTests(ProvisioningToolsFixture fx)
	{
		_factory = fx.Factory;
		_mcp = fx.Mcp;
	}

	static string Text(ModelContextProtocol.Protocol.CallToolResult r) =>
		r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;

	async Task<string> UpdateAsync(Dictionary<string, object?> args) =>
		Text(await _mcp.CallToolAsync("apikey_update", args));

	async Task<ApiKey> RowAsync(string key)
	{
		using var scope = _factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		return await db.ApiKeys.FirstAsync(k => k.Key == key);
	}

	// A DB-minted key with a known, complete starting state (all four editable fields set), so a
	// patch of one field has three others that could visibly regress.
	async Task<string> SeedAsync(string scopes = "data:read", bool wildcard = false, string? defaultProject = null)
	{
		var key = $"yb_key_{Guid.NewGuid():N}";
		using var scope = _factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		await db.InsertAsync(new ApiKey
		{
			Key = key,
			ProjectKey = wildcard ? "*" : "$system",
			Scopes = scopes,
			Name = "before",
			CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
			ExpiresAt = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
			DefaultProjectKey = defaultProject,
			SandboxOnly = false,
		});
		return key;
	}

	[Fact]
	public async Task Update_IsDiscoverable()
	{
		var names = (await _mcp.ListToolsAsync()).Select(t => t.Name).ToList();
		names.Should().Contain("apikey_update");
	}

	// ---- one field at a time; the other three survive bit-for-bit -------------------------------

	[Fact]
	public async Task PatchName_LeavesScopesExpiryDefaultProjectUntouched()
	{
		var key = await SeedAsync(scopes: "data:read,data:write", wildcard: true, defaultProject: "$system");
		var before = await RowAsync(key);

		(await UpdateAsync(new() { ["key"] = key, ["name"] = "  after  " })).Should().NotContain("\"error\"");

		var after = await RowAsync(key);
		after.Name.Should().Be("after");                                   // trimmed
		after.Scopes.Should().Be(before.Scopes);
		after.ExpiresAt.Should().Be(before.ExpiresAt);
		after.DefaultProjectKey.Should().Be(before.DefaultProjectKey);
		after.ProjectKey.Should().Be(before.ProjectKey);
		after.CreatedAt.Should().Be(before.CreatedAt);
		after.SandboxOnly.Should().Be(before.SandboxOnly);
	}

	[Fact]
	public async Task PatchScopes_ReplacesSet_LeavesNameExpiryDefaultProjectUntouched()
	{
		var key = await SeedAsync(scopes: "data:read", wildcard: true, defaultProject: "$system");
		var before = await RowAsync(key);

		(await UpdateAsync(new() { ["key"] = key, ["scopes"] = "tasks:read,tasks:write" })).Should().NotContain("\"error\"");

		var after = await RowAsync(key);
		after.Scopes.Should().Be("tasks:read,tasks:write");                // REPLACES, not adds
		after.Name.Should().Be(before.Name);
		after.ExpiresAt.Should().Be(before.ExpiresAt);
		after.DefaultProjectKey.Should().Be(before.DefaultProjectKey);
	}

	[Fact]
	public async Task PatchExpiry_LeavesNameScopesDefaultProjectUntouched()
	{
		var key = await SeedAsync(scopes: "data:read", wildcard: true, defaultProject: "$system");
		var before = await RowAsync(key);

		(await UpdateAsync(new() { ["key"] = key, ["expiresInSeconds"] = 3600 })).Should().NotContain("\"error\"");

		var after = await RowAsync(key);
		after.ExpiresAt.Should().NotBe(before.ExpiresAt);
		after.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(1), TimeSpan.FromMinutes(1));
		after.Name.Should().Be(before.Name);
		after.Scopes.Should().Be(before.Scopes);
		after.DefaultProjectKey.Should().Be(before.DefaultProjectKey);
	}

	[Fact]
	public async Task PatchDefaultProject_LeavesNameScopesExpiryUntouched()
	{
		var key = await SeedAsync(scopes: "data:read", wildcard: true, defaultProject: null);
		var before = await RowAsync(key);

		(await UpdateAsync(new() { ["key"] = key, ["defaultProject"] = "$system" })).Should().NotContain("\"error\"");

		var after = await RowAsync(key);
		after.DefaultProjectKey.Should().Be("$system");
		after.Name.Should().Be(before.Name);
		after.Scopes.Should().Be(before.Scopes);
		after.ExpiresAt.Should().Be(before.ExpiresAt);
	}

	// ---- combination + explicit clears (distinct from "omitted") --------------------------------

	[Fact]
	public async Task PatchAllFourAtOnce_AppliesEveryChange()
	{
		var key = await SeedAsync(scopes: "data:read", wildcard: true, defaultProject: null);

		var result = await UpdateAsync(new()
		{
			["key"] = key,
			["name"] = "combined",
			["scopes"] = "memory:read,memory:write",
			["expiresInSeconds"] = 7200,
			["defaultProject"] = "$system",
		});
		result.Should().NotContain("\"error\"");
		// The result names exactly the fields the call touched.
		result.Should().Contain("name").And.Contain("scopes").And.Contain("expiry").And.Contain("defaultProject");

		var after = await RowAsync(key);
		after.Name.Should().Be("combined");
		after.Scopes.Should().Be("memory:read,memory:write");
		after.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(2), TimeSpan.FromMinutes(1));
		after.DefaultProjectKey.Should().Be("$system");
		after.Key.Should().Be(key);                                       // the secret itself is never rotated
	}

	[Fact]
	public async Task ClearExpiry_ZeroSeconds_MakesKeyNonExpiring()
	{
		var key = await SeedAsync();
		(await UpdateAsync(new() { ["key"] = key, ["expiresInSeconds"] = 0 })).Should().NotContain("\"error\"");

		var after = await RowAsync(key);
		after.ExpiresAt.Should().BeNull();                                 // explicit clear…
		after.Name.Should().Be("before");                                  // …and nothing else moved
		after.Scopes.Should().Be("data:read");
	}

	[Fact]
	public async Task ClearDefaultProject_EmptyString_DropsIt()
	{
		var key = await SeedAsync(wildcard: true, defaultProject: "$system");
		(await UpdateAsync(new() { ["key"] = key, ["defaultProject"] = "" })).Should().NotContain("\"error\"");

		var after = await RowAsync(key);
		after.DefaultProjectKey.Should().BeNull();
		after.ExpiresAt.Should().NotBeNull();                              // the OTHER clearable field is untouched
	}

	[Fact]
	public async Task OmittedFields_AreNotClears()
	{
		// The mirror image of the two tests above: a patch that names only `name` must NOT be read as
		// "clear the expiry / clear the default project" — omission and clear are different requests.
		var key = await SeedAsync(wildcard: true, defaultProject: "$system");
		(await UpdateAsync(new() { ["key"] = key, ["name"] = "renamed" })).Should().NotContain("\"error\"");

		var after = await RowAsync(key);
		after.ExpiresAt.Should().Be(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
		after.DefaultProjectKey.Should().Be("$system");
	}

	// ---- refusals -------------------------------------------------------------------------------

	[Fact]
	public async Task ConfigDeclaredKey_Refused_WithReason()
	{
		var result = await UpdateAsync(new() { ["key"] = ProvisioningToolsFixture.ConfigDeclaredKey, ["name"] = "hijack" });

		// Not a silent no-op and not a bare 500 — the reason names the config source.
		result.Should().Contain("configuration");
		result.Should().Contain("Auth:ApiKeys");

		// …and nothing was written: the config key still has no DB row to shadow it.
		using var scope = _factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		(await db.ApiKeys.AnyAsync(k => k.Key == ProvisioningToolsFixture.ConfigDeclaredKey)).Should().BeFalse();
	}

	[Fact]
	public async Task UnknownKey_Refused()
	{
		(await UpdateAsync(new() { ["key"] = "yb_key_nonexistent", ["name"] = "x" }))
			.Should().Contain("ApiKey not found");
	}

	[Fact]
	public async Task UnknownScope_Refused_NothingWritten()
	{
		var key = await SeedAsync();
		(await UpdateAsync(new() { ["key"] = key, ["name"] = "newname", ["scopes"] = "data:read,bogus:scope" }))
			.Should().Contain("Unknown scopes");

		// The whole patch is rejected — the name change that rode along with it did NOT land.
		(await RowAsync(key)).Name.Should().Be("before");
	}

	[Fact]
	public async Task BlankName_Refused()
	{
		var key = await SeedAsync();
		(await UpdateAsync(new() { ["key"] = key, ["name"] = "   " })).Should().Contain("name cannot be blank");
		(await RowAsync(key)).Name.Should().Be("before");
	}

	[Fact]
	public async Task NothingToUpdate_Refused()
	{
		var key = await SeedAsync();
		(await UpdateAsync(new() { ["key"] = key })).Should().Contain("Nothing to update");
	}

	[Fact]
	public async Task DefaultProject_OnProjectScopedKey_Refused()
	{
		// The create-time invariant, reused: a project-scoped key already defaults to its own claim.
		var key = await SeedAsync(wildcard: false);
		(await UpdateAsync(new() { ["key"] = key, ["defaultProject"] = "$system" }))
			.Should().Contain("only valid on a cross-project");
		(await RowAsync(key)).DefaultProjectKey.Should().BeNull();
	}

	[Fact]
	public async Task DefaultProject_UnknownProject_Refused()
	{
		var key = await SeedAsync(wildcard: true);
		(await UpdateAsync(new() { ["key"] = key, ["defaultProject"] = "nosuchproject" }))
			.Should().Contain("not found");
		(await RowAsync(key)).DefaultProjectKey.Should().BeNull();
	}

	// ---- privilege: update needs exactly what mint needs -----------------------------------------

	[Fact]
	public async Task WithoutProvisionScope_CannotEscalate()
	{
		// A key that cannot MINT (no admin:provision) must not be able to grant itself scopes through
		// the patch verb either — otherwise apikey_update is a privilege-escalation ladder.
		var victim = await SeedAsync(scopes: "config:read");

		var http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		http.DefaultRequestHeaders.Add("X-Api-Key", ProvisioningToolsFixture.NoScopeKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = ProvisioningToolsFixture.NoScopeKey },
		}, http);
		var mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		try
		{
			var result = Text(await mcp.CallToolAsync("apikey_update", new Dictionary<string, object?>
			{
				["key"] = victim,
				["scopes"] = "admin:provision",
			}));
			result.Should().Contain("admin:provision");   // the message names the scope it lacks
			result.Should().Contain("lacks required scope");
			(await RowAsync(victim)).Scopes.Should().Be("config:read");   // unchanged
		}
		finally
		{
			await mcp.DisposeAsync();
			http.Dispose();
		}
	}

	// ---- immediacy: a scope change binds on the very next call, both directions --------------------

	[Fact]
	public async Task ScopeChange_TakesEffect_Immediately_BothDirections()
	{
		// spec apikey-mutable: "изменённые атрибуты вступают в силу немедленно — со следующего вызова,
		// без переподключения клиента". The victim key keeps ONE MCP connection open for the whole test:
		// grant → the very next call passes; revoke → the very next call fails. Nothing about a key is
		// cached per connection (auth re-reads the row on every request), and this is what proves it.
		var key = await SeedAsync(scopes: "config:read");   // no tasks:read

		var http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		http.DefaultRequestHeaders.Add("X-Api-Key", key);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = key },
		}, http);
		var victimMcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		try
		{
			var call = () => victimMcp.CallToolAsync("tasks_search",
				new Dictionary<string, object?> { ["projectKey"] = "$system", ["bodyLen"] = 0 });

			// (1) before: refused for want of tasks:read
			// (the apostrophes in the server message arrive JSON-escaped as ' — assert on the parts)
			Text(await call()).Should().Contain("lacks required scope").And.Contain("tasks:read");

			// (2) grant it — through the tool under test, on the admin connection
			(await UpdateAsync(new() { ["key"] = key, ["scopes"] = "config:read,tasks:read" }))
				.Should().NotContain("\"error\"");

			// (3) the VERY NEXT call on the SAME, un-reconnected client goes through
			Text(await call()).Should().NotContain("lacks required scope");

			// (4) revoke it — the direction that matters for security
			(await UpdateAsync(new() { ["key"] = key, ["scopes"] = "config:read" }))
				.Should().NotContain("\"error\"");

			// (5) …and the very next call is refused again, with no reconnect in between
			Text(await call()).Should().Contain("lacks required scope").And.Contain("tasks:read");
		}
		finally
		{
			await victimMcp.DisposeAsync();
			http.Dispose();
		}
	}
}
