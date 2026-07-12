using LinqToDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using Microsoft.Extensions.Options;
using PetBox.Config;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Registry;
using PetBox.Web.Settings;

namespace PetBox.Tests.LlmRouter;

// The levelled LLM registry in core.db (llm-registry-own-store): the cascade, the two inheritance
// switches, and the three locks that together make "an endpoint without its api key" impossible —
//
//   1. the key is a COLUMN of the endpoint row (not a separate secret keyed by name),
//   2. a composite FK (Scope, ScopeKey, Endpoint) -> llm_endpoints(Scope, ScopeKey, Name) forbids a
//      route from naming an endpoint at another level,
//   3. resolution is LEVEL-ATOMIC: the first level with any route wins WHOLE; levels never merge.
//
// The old ConfigBindings-backed store had none of these and answered an undecryptable key by
// calling the upstream ANONYMOUSLY. The last test here pins the opposite behaviour.
public sealed class LlmRegistryLevelsTests : IDisposable
{
	const string Ws = "ws-a";
	const string OtherWs = "ws-b";
	const string Proj = "proj-a";
	const string OtherProj = "proj-b";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ISecretEncryptor _secrets;
	readonly SettingsResolver _settings;
	readonly CapturingLogger<LlmRegistryLevelResolver> _log = new();
	readonly LlmRegistryLevelResolver _resolver;
	readonly LlmRegistryLevelAdmin _admin;

	public LlmRegistryLevelsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-llmreg-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);

		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = Ws, Name = "A", Description = "" });
		_db.Insert(new Project { Key = OtherProj, WorkspaceKey = OtherWs, Name = "B", Description = "" });

		_secrets = new AesGcmSecretEncryptor(Options.Create(new SecretEncryptorOptions { MasterKey = "test-master-key" }));
		_settings = new SettingsResolver(_db.Factory(), _secrets);
		_resolver = new LlmRegistryLevelResolver(_db.Factory(), _secrets, _settings, _log);
		_admin = new LlmRegistryLevelAdmin(_db, _secrets);
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	static LlmRegistry Registry(string endpoint, string model, string url = "https://up.example") => new(
		[new LlmEndpoint(endpoint, url)],
		[new LlmRoute(LlmCapability.Embed, endpoint, model)]);

	Task SeedSystemAsync(string key = "sys-key") =>
		_admin.SetAsync(Scope.System, "$", Registry("sys-ep", "sys-model"),
			new Dictionary<string, string> { ["sys-ep"] = key });

	Task SeedWorkspaceAsync(string workspace = Ws, string key = "ws-key") =>
		_admin.SetAsync(Scope.Workspace, workspace, Registry("ws-ep", "ws-model", "https://ws.example"),
			new Dictionary<string, string> { ["ws-ep"] = key });

	// (a) Nothing at the project, nothing at the workspace -> the System level is inherited WHOLE:
	// its endpoint, its route AND its key, all from the one level.
	[Fact]
	public async Task Empty_workspace_inherits_the_system_level_whole()
	{
		await SeedSystemAsync();

		var resolved = await _resolver.ResolveAsync(Proj);

		resolved.Level.Should().Be(RegistryLevel.System);
		resolved.Registry.Endpoints.Should().ContainSingle().Which.Name.Should().Be("sys-ep");
		resolved.Registry.Routes.Should().ContainSingle().Which.Model.Should().Be("sys-model");
		resolved.ApiKeys.Should().ContainKey("sys-ep").WhoseValue.Should().Be("sys-key");
		resolved.InheritanceBlocked.Should().BeFalse();
	}

	// (b) LEVEL-ATOMIC, the crux: a workspace with routes of its own does NOT see $system at all —
	// not its routes, not its endpoints, not its keys. No merge, so no franken-pair of "system route
	// + my key" (or worse, "system route + NO key").
	[Fact]
	public async Task Workspace_with_own_routes_does_not_inherit_anything_from_system()
	{
		await SeedSystemAsync();
		await SeedWorkspaceAsync();

		var resolved = await _resolver.ResolveAsync(Proj);

		resolved.Level.Should().Be(RegistryLevel.Workspace(Ws));
		resolved.Registry.Endpoints.Select(e => e.Name).Should().Equal("ws-ep");
		resolved.Registry.Routes.Select(r => r.Model).Should().Equal("ws-model");
		resolved.ApiKeys.Should().ContainKey("ws-ep").WhoseValue.Should().Be("ws-key");
		resolved.ApiKeys.Should().NotContainKey("sys-ep");
	}

	// (c) The owner's tap. System switch off -> NOBODY inherits, whatever a workspace asked for
	// ("wider scope = stricter"). And the failure is honest, not a quiet fallback.
	[Fact]
	public async Task System_tap_off_stops_inheritance_for_everyone()
	{
		await SeedSystemAsync();
		await _settings.SetAsync(Scope.System, "$",
			new LlmRegistryInheritanceSettings { SystemShared = false },
			new LlmRegistryInheritanceSettings(), updatedBy: null);

		var a = await _resolver.ResolveAsync(Proj);
		var b = await _resolver.ResolveAsync(OtherProj);

		foreach (var resolved in new[] { a, b })
		{
			resolved.Level.Should().BeNull();
			resolved.Registry.Routes.Should().BeEmpty();
			resolved.InheritanceBlocked.Should().BeTrue();
			resolved.NoRouteMessage(LlmCapability.Embed).Should().Contain("not inherited");
		}
	}

	// (d) The workspace's own opt-out is independent: it silences THIS workspace, and only it.
	[Fact]
	public async Task Workspace_optout_stops_only_that_workspace()
	{
		await SeedSystemAsync();
		await _settings.SetAsync(Scope.Workspace, Ws,
			new LlmRegistryInheritanceSettings { WorkspaceInherits = false },
			new LlmRegistryInheritanceSettings(), updatedBy: null);

		var opted_out = await _resolver.ResolveAsync(Proj);
		var still_inheriting = await _resolver.ResolveAsync(OtherProj);

		opted_out.Level.Should().BeNull();
		opted_out.InheritanceBlocked.Should().BeTrue();
		opted_out.NoRouteMessage(LlmCapability.Embed).Should().Contain(Ws);

		still_inheriting.Level.Should().Be(RegistryLevel.System);
		still_inheriting.ApiKeys.Should().ContainKey("sys-ep");
	}

	// (e) The write side cannot cascade and cannot be aimed by accident: the target level is an
	// explicit argument (there is no projectKey overload at all), Scope.Project is reserved, and
	// writing one workspace leaves every other level untouched.
	[Fact]
	public async Task Admin_writes_exactly_the_level_it_is_given()
	{
		await SeedSystemAsync();
		await SeedWorkspaceAsync(Ws);

		(await _admin.GetAsync(Scope.Workspace, Ws)).Endpoints.Select(e => e.Name).Should().Equal("ws-ep");
		(await _admin.GetAsync(Scope.System, "$")).Endpoints.Select(e => e.Name).Should().Equal("sys-ep");
		// The neighbouring workspace was never named, so it has nothing — no inheritance on the write
		// side, ever.
		(await _admin.GetAsync(Scope.Workspace, OtherWs)).Endpoints.Should().BeEmpty();
		(await _admin.GetAsync(Scope.Workspace, OtherWs)).Routes.Should().BeEmpty();
	}

	[Fact]
	public async Task Admin_refuses_reserved_and_unknown_levels()
	{
		var reg = Registry("ep", "m");
		var keys = new Dictionary<string, string>();

		await new Func<Task>(() => _admin.SetAsync(Scope.Project, Proj, reg, keys))
			.Should().ThrowAsync<ArgumentException>();
		await new Func<Task>(() => _admin.SetAsync(Scope.User, "1", reg, keys))
			.Should().ThrowAsync<ArgumentOutOfRangeException>();
		// The System level has exactly one key.
		await new Func<Task>(() => _admin.SetAsync(Scope.System, Ws, reg, keys))
			.Should().ThrowAsync<ArgumentException>();
	}

	// (f) LOCK #2, enforced by SQLite itself: a route may not reference an endpoint declared at
	// another level. Bypassing the admin entirely and inserting the row by hand still fails —
	// PetBoxDb turns PRAGMA foreign_keys ON, so the composite FK is real, not decorative.
	[Fact]
	public async Task Foreign_key_rejects_a_route_pointing_at_another_levels_endpoint()
	{
		await SeedWorkspaceAsync(Ws); // "ws-ep" exists ONLY at (Workspace, ws-a)

		var cross = new LlmRouteRow
		{
			Id = Guid.NewGuid().ToString("N"),
			Scope = nameof(Scope.System),
			ScopeKey = "$",
			Capability = nameof(LlmCapability.Embed),
			Endpoint = "ws-ep",
			Model = "stolen",
			Priority = 1,
			UpdatedAt = DateTime.UtcNow,
		};

		var act = async () => await _db.InsertAsync(cross);

		(await act.Should().ThrowAsync<SqliteException>())
			.Which.Message.Should().Contain("FOREIGN KEY");
	}

	// (g) LOCK #1's payoff. A key that will not decrypt is a HARD failure of that endpoint: the
	// endpoint is excluded, its routes are dropped, a Warning names it — and the call never goes out
	// unauthenticated, which is exactly what the old store did (`catch { /* treated as absent */ }`).
	[Fact]
	public async Task Undecryptable_key_excludes_the_endpoint_and_warns()
	{
		await SeedSystemAsync();

		// Corrupt the stored ciphertext in place: valid base64, wrong bytes -> AES-GCM tag mismatch.
		await _db.LlmEndpoints
			.Where(e => e.Name == "sys-ep")
			.Set(e => e.KeyCipher, Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]))
			.UpdateAsync();

		var resolved = await _resolver.ResolveAsync(Proj);

		resolved.Level.Should().Be(RegistryLevel.System);      // the level still owns the routes...
		resolved.Registry.Endpoints.Should().BeEmpty();        // ...but the endpoint is gone,
		resolved.Registry.Routes.Should().BeEmpty();           // ...and so is the route that named it.
		resolved.ApiKeys.Should().BeEmpty();                   // no key -> no anonymous call is possible.

		_log.Entries.Should().Contain(e =>
			e.Level == MsLogLevel.Warning && e.Message.Contains("sys-ep") && e.Message.Contains("EXCLUDED"));
	}

	// A HALF-written key (cipher present, iv/tag missing) is a corrupt row, not "keyless".
	[Fact]
	public async Task Half_written_key_excludes_the_endpoint()
	{
		await SeedSystemAsync();
		await _db.LlmEndpoints.Where(e => e.Name == "sys-ep").Set(e => e.KeyIv, (string?)null).UpdateAsync();

		var resolved = await _resolver.ResolveAsync(Proj);

		resolved.Registry.Endpoints.Should().BeEmpty();
		resolved.ApiKeys.Should().BeEmpty();
		_log.Entries.Should().Contain(e => e.Level == MsLogLevel.Warning && e.Message.Contains("incomplete"));
	}

	// A DELIBERATELY keyless endpoint (all three cipher columns null — a local model with no auth)
	// stays legal: the invariant is "no endpoint whose key is missing/unreadable", not "every
	// endpoint must have a key".
	[Fact]
	public async Task Endpoint_declared_without_a_key_stays_usable()
	{
		await _admin.SetAsync(Scope.System, "$", Registry("local", "nomic", "http://127.0.0.1:11434"),
			new Dictionary<string, string>());

		var resolved = await _resolver.ResolveAsync(Proj);

		resolved.Registry.Endpoints.Select(e => e.Name).Should().Equal("local");
		resolved.Registry.Routes.Should().ContainSingle();
		resolved.ApiKeys.Should().BeEmpty();
	}

	// An endpoint absent from the apiKeys map keeps the key it already had at THIS level — a route
	// edit must not silently disarm an endpoint's credentials.
	[Fact]
	public async Task Rewriting_a_level_without_the_key_map_keeps_the_stored_key()
	{
		await SeedSystemAsync("sys-key");

		await _admin.SetAsync(Scope.System, "$",
			new LlmRegistry(
				[new LlmEndpoint("sys-ep", "https://up.example")],
				[new LlmRoute(LlmCapability.Embed, "sys-ep", "sys-model-v2")]),
			new Dictionary<string, string>());

		var resolved = await _resolver.ResolveAsync(Proj);

		resolved.Registry.Routes.Should().ContainSingle().Which.Model.Should().Be("sys-model-v2");
		resolved.ApiKeys["sys-ep"].Should().Be("sys-key");
	}

	sealed class CapturingLogger<T> : ILogger<T>
	{
		public List<(MsLogLevel Level, string Message)> Entries { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(MsLogLevel logLevel) => true;

		public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			Entries.Add((logLevel, formatter(state, exception)));
	}
}
