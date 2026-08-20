using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Config.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tests.Support;

namespace PetBox.Tests.Mcp;

// work `config-binding-mcp-declare-tenant`: the config_binding_* family stopped being
// [TenantExempt(Provisioning)] + admin:provision and became an ordinary tenant surface —
// [TenantFrom(Argument, "workspaceKey", TenantKind.Workspace)], gated on config:read / config:write,
// the same two things the REST twin (POST|DELETE /api/config/{workspaceKey}/bindings) has always used.
//
// WHY THIS FILE EXISTS ALONGSIDE AuthzCrossTenantTests. That sweep now records all four verbs as
// Denied, which is the headline result — but it is a SWEEP, and it aims at every surface with the same
// generic arguments. For config_binding_upsert those arguments are items:[], and an empty batch is a
// hard reject in the tool body, so the sweep could never distinguish "the tenant refused" from "the
// batch guard refused" on the one verb that WRITES. Its own KnownDeviations entry said so for a whole
// release. This file closes that: a NON-EMPTY payload, aimed at a foreign workspace, over the wire.
//
// It also measures the two halves a cross-tenant sweep structurally cannot:
//   * the OWN-workspace call still works (a denial-only test is satisfied by a broken caller);
//   * the deliberate compatibility BREAK — admin:provision alone no longer reaches these verbs. That
//     is the price the owner accepted on 2026-08-15 ("на регулярной основе на кросс-workspace
//     admin:provision никто не полагается"), and it is pinned here so it cannot be quietly undone.
public sealed class ConfigBindingTenantAuthzFixture : IAsyncLifetime
{
	public const string OwnWorkspace = "cfgauth-own-ws";
	public const string ForeignWorkspace = "cfgauth-foreign-ws";
	const string OwnProject = "cfgauth-own";
	const string ForeignProject = "cfgauth-foreign";

	// The key under test: ordinary config scopes, NO admin:provision. Before this work item such a key
	// could not even SEE these tools (McpToolScopeFilter filed config_* under admin:provision), let
	// alone call them.
	const string TenantApiKey = "yb_key_cfgauth_tenant";
	const string TenantScopes = "config:read,config:write";

	// The key that used to be the ONLY one admitted here, and now is not admitted at all.
	const string ProvApiKey = "yb_key_cfgauth_prov";
	const string ProvScopes = "admin:provision";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _tenantHttp = null!;
	HttpClient _provHttp = null!;
	McpClient _tenantMcp = null!;
	McpClient _provMcp = null!;

	public IReadOnlyDictionary<string, McpClientTool> TenantTools { get; private set; } = null!;
	public IReadOnlyDictionary<string, McpClientTool> ProvTools { get; private set; } = null!;

	public ConfigBindingTenantAuthzFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-cfgauth-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.UseSetting("Features:Config", "true");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Host:BackgroundServices"] = "false",
						["Features:Config"] = "true",
					});
				});
				b.ConfigureServices(svc =>
				{
					// Per-workspace config stores under this fixture's own temp dir, so the two
					// workspaces are real files and neither outlives the run.
					var configFactory = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<ConfigDb>));
					if (configFactory is not null) svc.Remove(configFactory);
					svc.AddSingleton<IScopedDbFactory<ConfigDb>>(_ => new ScopedDbFactory<ConfigDb>(
						Path.Combine(_baseDir, "config"), PetBox.Core.Settings.Scope.Workspace,
						cs => new ConfigDb(ConfigDb.CreateOptions(cs)), TestSchema.Config));
				});
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using (var scope = _factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			var now = DateTime.UtcNow;
			await db.InsertAsync(new Workspace { Key = OwnWorkspace, Name = "Own", CreatedAt = now });
			await db.InsertAsync(new Workspace { Key = ForeignWorkspace, Name = "Foreign", CreatedAt = now });
			await db.InsertAsync(new Project { Key = OwnProject, WorkspaceKey = OwnWorkspace, Name = "Own" });
			await db.InsertAsync(new Project { Key = ForeignProject, WorkspaceKey = ForeignWorkspace, Name = "Foreign" });
			await db.InsertAsync(new ApiKey { Key = TenantApiKey, ProjectKey = OwnProject, Scopes = TenantScopes, CreatedAt = now });
			await db.InsertAsync(new ApiKey { Key = ProvApiKey, ProjectKey = OwnProject, Scopes = ProvScopes, CreatedAt = now });
		}

		(_tenantHttp, _tenantMcp, TenantTools) = await ConnectAsync(TenantApiKey);
		(_provHttp, _provMcp, ProvTools) = await ConnectAsync(ProvApiKey);

		// The foreign workspace must genuinely HAVE a binding, or "denied" could just mean "there is
		// nothing there" — the same trap AuthzCrossTenantTests guards with its victim-exists anchor.
		// Seeded through the factory rather than through the tool, because no key in this fixture is
		// allowed to write that workspace, which is the whole point.
		using (var scope = _factory.Services.CreateScope())
		{
			var factory = scope.ServiceProvider.GetRequiredService<IConfigDbFactory>();
			using var cfg = factory.NewConfigDb(ForeignWorkspace);
			var now = DateTime.UtcNow;
			ForeignBindingId = await cfg.InsertWithInt64IdentityAsync(new ConfigBinding
			{
				Path = ForeignBindingPath,
				Tags = $"ws:{ForeignWorkspace}",
				Value = "do-not-read",
				Kind = BindingKind.Plain,
				Version = 1,
				CreatedAt = now,
				UpdatedAt = now,
			});
		}
	}

	public const string ForeignBindingPath = "secret/foreign";

	public long ForeignBindingId { get; private set; }

	// Reads the foreign workspace's store DIRECTLY, bypassing every gate — the only honest way to ask
	// "did the refused write land anyway?", since no key in this fixture may read that store.
	public IReadOnlyList<string> ForeignActivePaths()
	{
		using var scope = _factory.Services.CreateScope();
		var factory = scope.ServiceProvider.GetRequiredService<IConfigDbFactory>();
		using var cfg = factory.NewConfigDb(ForeignWorkspace);
		return cfg.Bindings.Where(b => !b.IsDeleted).Select(b => b.Path).ToList();
	}

	async Task<(HttpClient, McpClient, IReadOnlyDictionary<string, McpClientTool>)> ConnectAsync(string apiKey)
	{
		var http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey },
		}, http);
		var mcp = await McpTestClient.ConnectAsync(transport);
		return (http, mcp, (await mcp.ListToolsAsync()).ToDictionary(t => t.Name));
	}

	public async ValueTask DisposeAsync()
	{
		await _tenantMcp.DisposeAsync();
		await _provMcp.DisposeAsync();
		_tenantHttp.Dispose();
		_provHttp.Dispose();
		await _factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}

	internal static Dictionary<string, object?> ToArgs(object o) =>
		JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(o))!
			.ToDictionary(kv => kv.Key, kv => (object?)((JsonElement)kv.Value!));
}

public sealed class ConfigBindingTenantAuthzTests : IClassFixture<ConfigBindingTenantAuthzFixture>
{
	readonly ConfigBindingTenantAuthzFixture _fx;
	public ConfigBindingTenantAuthzTests(ConfigBindingTenantAuthzFixture fx) => _fx = fx;

	const string Own = ConfigBindingTenantAuthzFixture.OwnWorkspace;
	const string Foreign = ConfigBindingTenantAuthzFixture.ForeignWorkspace;

	async Task<CallToolResult> Tenant(string tool, object args) =>
		await _fx.TenantTools[tool].CallAsync(ConfigBindingTenantAuthzFixture.ToArgs(args));

	async Task<CallToolResult> Prov(string tool, object args) =>
		await _fx.ProvTools[tool].CallAsync(ConfigBindingTenantAuthzFixture.ToArgs(args));

	static string Text(CallToolResult r) => string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text));

	static (string Type, string Message) Error(CallToolResult r)
	{
		using var doc = JsonDocument.Parse(Text(r));
		var err = doc.RootElement.GetProperty("error");
		return (err.GetProperty("type").GetString() ?? "", err.GetProperty("message").GetString() ?? "");
	}

	// ── THE GUARD ON THE GUARD: the caller works in its OWN workspace ────────────────────────────
	//
	// Every refusal below is satisfied by a caller that cannot do anything at all, so this comes first.

	[Fact]
	public async Task OwnWorkspace_WithConfigScopes_IsServedOnEveryVerb()
	{
		var upsert = await Tenant("config_binding_upsert", new
		{
			workspaceKey = Own,
			items = new[] { new { path = "svc/url", tags = $"ws:{Own}", value = "https://own" } },
		});
		upsert.IsError.Should().NotBe(true, "config:write on the key's OWN workspace is the ordinary case: " + Text(upsert));

		var search = await Tenant("config_binding_search", new { workspaceKey = Own });
		search.IsError.Should().NotBe(true);
		Text(search).Should().Contain("svc/url");

		using var doc = JsonDocument.Parse(Text(upsert));
		var id = doc.RootElement.GetProperty("added")[0].GetProperty("id").GetInt64();

		var get = await Tenant("config_binding_get", new { workspaceKey = Own, id });
		get.IsError.Should().NotBe(true);

		var del = await Tenant("config_binding_delete", new { workspaceKey = Own, id });
		del.IsError.Should().NotBe(true);
	}

	// ── THE FIX ITSELF: a foreign workspace is refused on all four verbs ─────────────────────────

	[Fact]
	public async Task ForeignWorkspace_Upsert_IsRefused_WithANonEmptyBatch()
	{
		// The case AuthzCrossTenantTests structurally cannot reach: its generic items:[] hits the
		// empty-batch guard first. A real payload aimed at someone else's workspace must not land.
		var r = await Tenant("config_binding_upsert", new
		{
			workspaceKey = Foreign,
			items = new[] { new { path = "pwned", tags = $"ws:{Foreign}", value = "x" } },
		});

		r.IsError.Should().BeTrue();
		var (type, message) = Error(r);
		type.Should().Be(nameof(UnauthorizedAccessException), "the refusal is on the AUTHORIZATION axis, not the batch guard");
		message.Should().Contain($"Not authorized for workspace:{Foreign}");
		message.Should().NotContain("empty batch",
			"a non-empty payload was sent precisely so the tenant decision is what answers");

		// And nothing was written. Read out of the foreign store directly — a refusal that still lands
		// the row would satisfy every assertion above and be the exact bug this work item closes.
		_fx.ForeignActivePaths().Should().NotContain("pwned")
			.And.BeEquivalentTo([ConfigBindingTenantAuthzFixture.ForeignBindingPath],
				"the victim workspace holds exactly what it was seeded with");
	}

	[Fact]
	public async Task ForeignWorkspace_Search_IsRefused()
	{
		var r = await Tenant("config_binding_search", new { workspaceKey = Foreign });
		r.IsError.Should().BeTrue();
		var (type, message) = Error(r);
		type.Should().Be(nameof(UnauthorizedAccessException));
		message.Should().Contain($"Not authorized for workspace:{Foreign}");
		message.Should().NotContain("secret/foreign", "the refusal must not carry the rows it refused");
	}

	[Fact]
	public async Task ForeignWorkspace_Get_IsRefused_AndIsNoLongerAnExistenceOracle()
	{
		// This verb's old KnownDeviations entry: InvalidOperationException "config binding '0' not found
		// in workspace 'victimws'" — an existence answer to an outsider, naming the workspace back at
		// them. The PEP now decides before the id is ever looked up, so BOTH halves of that are gone:
		// the id that really exists and an id that does not must be indistinguishable.
		var real = await Tenant("config_binding_get", new { workspaceKey = Foreign, id = _fx.ForeignBindingId });
		var absent = await Tenant("config_binding_get", new { workspaceKey = Foreign, id = 999_999_999L });

		real.IsError.Should().BeTrue();
		absent.IsError.Should().BeTrue();
		Error(real).Should().Be(Error(absent),
			"an outsider must not be able to tell a binding that EXISTS from one that does not — identical "
			+ "refusals are what makes this not an existence oracle");
		Error(real).Type.Should().Be(nameof(UnauthorizedAccessException));
		Error(real).Message.Should().NotContain("not found", "\"not found\" is the answer that leaked existence");
	}

	[Fact]
	public async Task ForeignWorkspace_Delete_IsRefused_AndTheBindingSurvives()
	{
		var r = await Tenant("config_binding_delete", new { workspaceKey = Foreign, id = _fx.ForeignBindingId });

		r.IsError.Should().BeTrue();
		var (type, message) = Error(r);
		type.Should().Be(nameof(UnauthorizedAccessException));
		message.Should().Contain($"Not authorized for workspace:{Foreign}");
		message.Should().NotContain("Binding not found", "the old answer told an outsider what was there");

		// The row is still active — the refusal happened BEFORE the soft-delete, not after it.
		_fx.ForeignActivePaths().Should().Contain(ConfigBindingTenantAuthzFixture.ForeignBindingPath);
	}

	// ── THE ACCEPTED BREAK: admin:provision alone no longer reaches config bindings ──────────────

	[Fact]
	public async Task AdminProvisionAlone_NoLongerReachesConfigBindings_EvenInItsOwnWorkspace()
	{
		// The compatibility cost the owner signed off on. Note the workspace is the key's OWN, so the
		// tenant axis PASSES and what refuses is the scope axis — this pins the gate, not the tenant.
		var r = await Prov("config_binding_search", new { workspaceKey = Own });

		r.IsError.Should().BeTrue();
		var (type, message) = Error(r);
		type.Should().Be(nameof(UnauthorizedAccessException));
		message.Should().Contain($"lacks required scope '{PetBox.Core.Auth.ApiKeyScopes.ConfigRead}'",
			"admin:provision is no longer a substitute for config:read — deliberately, and the same way "
			+ "POST /api/config/{{workspaceKey}}/bindings has always behaved");

		var w = await Prov("config_binding_upsert", new
		{
			workspaceKey = Own,
			items = new[] { new { path = "p", tags = $"ws:{Own}", value = "v" } },
		});
		w.IsError.Should().BeTrue();
		Error(w).Message.Should().Contain($"lacks required scope '{PetBox.Core.Auth.ApiKeyScopes.ConfigWrite}'");
	}

	// ── THE VISIBILITY HALF: a config:* key can SEE the verbs it may call ────────────────────────

	[Fact]
	public void ToolsList_AConfigScopedKey_SeesTheConfigBindingVerbs()
	{
		// McpToolScopeFilter used to file config_* under the literal admin:provision, so a key holding
		// config:read/config:write — the only key that may now call them — got a tools/list with no
		// config verbs in it at all. A gate nobody can discover is a gate nobody can use.
		_fx.TenantTools.Keys.Should().Contain(
			["config_binding_upsert", "config_binding_search", "config_binding_get", "config_binding_delete"],
			"a key with config:* scopes must see the config module in tools/list");

		_fx.TenantTools.Should().HaveCountLessThan(_fx.ProvTools.Count,
			"the trim is still doing real work — this key holds no admin:provision, so it must see FEWER "
			+ "tools than the key that does. Without this, 'the config verbs are listed' would also be "
			+ "true of a filter that had simply stopped filtering");
	}
}
