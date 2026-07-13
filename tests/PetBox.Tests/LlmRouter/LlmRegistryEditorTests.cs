using System.Security.Claims;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PetBox.Config;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Registry;
using PetBox.Web.Auth;
using PetBox.Web.Mcp;
using PetBox.Web.Pages.Llm;
using PetBox.Web.Settings;

namespace PetBox.Tests.LlmRouter;

// llm-l5: THE ADMIN SURFACE WRITES WHERE THE RUNTIME READS.
//
// After the runtime flip, the router resolved the levelled registry in core.db while the admin page
// and llm_config_* still wrote the old ConfigBindings store — so a save reported success and changed
// nothing that could ever be routed. These tests pin the whole path end to end, against a real
// database: write through the admin surface (page + MCP), then READ BACK THROUGH THE RESOLVER — the
// same interface CapabilityRouter uses to pick an endpoint. If the write ever lands somewhere the
// resolver does not look, the resolver assertion is what fails.
//
// They also pin the two things that made the surface unsafe even once it pointed at the right store:
// rows addressed by POSITION (an edit landing on the wrong route), and an INHERITED registry offered
// as editable (a one-row workspace level silently shadowing the whole inherited one).
public sealed class LlmRegistryEditorTests : IDisposable
{
	const string Ws = "ws-a";
	const string Proj = "proj-a";
	const string SysProj = "sys-proj";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly LlmRegistryLevelAdmin _levels;
	readonly LlmRegistryLevelResolver _resolver;
	readonly LlmRegistryEditor _editor;

	public LlmRegistryEditorTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-llmeditor-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);

		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = Ws, Name = "A", Description = "" });
		_db.Insert(new Project { Key = SysProj, WorkspaceKey = WorkspaceMemory.SystemWorkspace, Name = "S", Description = "" });

		var secrets = new AesGcmSecretEncryptor(Options.Create(new SecretEncryptorOptions { MasterKey = "test-master-key" }));
		var settings = new SettingsResolver(new SettingsStore(_db.Factory()), secrets);
		_levels = new LlmRegistryLevelAdmin(_db.Factory(), secrets);
		_resolver = new LlmRegistryLevelResolver(_db.Factory(), secrets, settings, NullLogger<LlmRegistryLevelResolver>.Instance);
		_editor = new LlmRegistryEditor(_db.Factory(), _levels, _resolver);
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	// ---- the MCP path: llm_config_upsert -> llm_config_get AND the runtime resolver ----

	[Fact]
	public async Task McpUpsert_is_visible_to_llm_config_get_AND_to_the_runtime_resolver()
	{
		var config = Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "qwen3", 50));

		var set = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj, config);
		set.Ok.Should().BeTrue();

		// The tool's own read-back (shape unchanged: a plain LlmRegistry, no secrets).
		var got = await LlmRouterTools.ConfigGetAsync(Http("llm:admin", Proj), Flags(), _editor, Proj);
		got.Endpoints.Should().ContainSingle(e => e.Name == "home" && e.BaseUrl == "https://home:1234");
		got.Routes.Should().ContainSingle(r => r.Model == "qwen3");

		// THE point of the card: the RUNTIME sees it. Same resolver CapabilityRouter routes through.
		var resolved = await _resolver.ResolveAsync(Proj);
		resolved.Level.Should().Be(RegistryLevel.Workspace(Ws), "a workspace writes its own level");
		resolved.Registry.Routes.Should().ContainSingle(r => r.Capability == LlmCapability.Chat && r.Model == "qwen3");

		// And it is in the NEW tables, not the old ConfigBindings store.
		_db.LlmRoutes.Count(r => r.Scope == nameof(Scope.Workspace) && r.ScopeKey == Ws).Should().Be(1);
	}

	[Fact]
	public async Task McpUpsert_replaces_the_level_whole_so_an_edited_model_actually_changes_the_route()
	{
		await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "old-model", 50)));

		await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "new-model", 50)));

		var resolved = await _resolver.ResolveAsync(Proj);
		resolved.Registry.Routes.Should().ContainSingle().Which.Model.Should().Be("new-model");
	}

	// The reserved built-in workspace IS the system level — the one every inheriting workspace is
	// served from. If the owner's page wrote Workspace:$system instead, it would create a shadow
	// level: the edit would take effect for $system's own projects and NOT for anyone inheriting.
	[Fact]
	public async Task The_system_workspace_writes_the_SYSTEM_level()
	{
		await _editor.SetAsync(SysProj,
			new LlmRegistry([new LlmEndpoint("home", "https://home:1234")], [new LlmRoute(LlmCapability.Embed, "home", "qwen3-embed")]),
			NoKeys);

		_db.LlmRoutes.Count(r => r.Scope == nameof(Scope.System) && r.ScopeKey == RegistryLevel.SystemScopeKey).Should().Be(1);
		_db.LlmRoutes.Count(r => r.Scope == nameof(Scope.Workspace)).Should().Be(0);

		(await _resolver.ResolveAsync(SysProj)).Level.Should().Be(RegistryLevel.System);
		// …and a workspace that declares nothing inherits exactly that.
		(await _resolver.ResolveAsync(Proj)).Level.Should().Be(RegistryLevel.System);
	}

	// ---- row identity ----

	[Fact]
	public async Task A_routes_id_survives_a_save_of_the_level()
	{
		await Seed();
		var before = (await _editor.ViewAsync(Proj)).Routes.Select(r => r.Id).ToList();

		// Any unrelated save replaces the level's rows — the ids must come back the same.
		var view = await _editor.ViewAsync(Proj);
		await _editor.SaveAsync(Proj, view.Endpoints, view.Routes, NoKeys);

		(await _editor.ViewAsync(Proj)).Routes.Select(r => r.Id).Should().Equal(before);
	}

	// THE BUG, against a real database, through the real page. The user opens the page, someone else
	// re-orders the stored rows, and only THEN does the user submit the row they were editing. With
	// `routes[i] = route` the edit landed on whatever now occupied that index — the neighbouring
	// route. Addressed by id, it follows the row it was rendered from.
	[Fact]
	public async Task An_edit_submitted_after_a_reorder_hits_the_same_row_not_its_neighbour()
	{
		await Seed();

		// What the page rendered: the row the user is about to edit (at index 0) and its neighbour.
		var rendered = await _editor.ViewAsync(Proj);
		rendered.Routes[0].Route.Model.Should().Be("chat-model");
		var editedId = rendered.Routes[0].Id;
		var neighbourId = rendered.Routes.Single(r => r.Route.Model == "embed-model").Id;

		// Meanwhile: a concurrent save re-orders the level (same rows, other order).
		await _editor.SaveAsync(Proj, rendered.Endpoints, rendered.Routes.Reverse().ToList(), NoKeys);

		// The trap this test exists for: index 0 — the position the user's form was rendered at — is
		// now the OTHER route. `routes[0] = route` would rewrite the neighbour.
		var reordered = await _editor.ViewAsync(Proj);
		reordered.Routes[0].Id.Should().Be(neighbourId);
		reordered.Routes[1].Id.Should().Be(editedId);

		// Now the user's form arrives, carrying the id it was rendered with (NOT an index).
		var page = new IndexModel(_editor, Flags(), new ProjectDirectory(_db.Factory())) { WorkspaceKey = Ws, ProjectKey = Proj };
		await page.OnPostSaveRouteAsync(LlmCapability.Chat, "home", "chat-model-v2", 50, null, null, editedId);

		var after = await _editor.ViewAsync(Proj);
		after.Routes.Single(r => r.Id == editedId).Route.Model.Should().Be("chat-model-v2");
		after.Routes.Single(r => r.Id == neighbourId).Route.Model.Should().Be("embed-model", "the neighbour must be untouched");

		// And the runtime agrees.
		var resolved = await _resolver.ResolveAsync(Proj);
		resolved.Registry.Routes.Should().Contain(r => r.Model == "chat-model-v2");
		resolved.Registry.Routes.Should().Contain(r => r.Model == "embed-model");
	}

	[Fact]
	public async Task Deleting_by_id_removes_that_row_and_the_runtime_stops_serving_it()
	{
		await Seed();
		var view = await _editor.ViewAsync(Proj);
		var doomed = view.Routes.Single(r => r.Route.Model == "embed-model").Id;

		var page = new IndexModel(_editor, Flags(), new ProjectDirectory(_db.Factory())) { WorkspaceKey = Ws, ProjectKey = Proj };
		await page.OnPostDeleteRouteAsync(doomed);

		var resolved = await _resolver.ResolveAsync(Proj);
		resolved.Registry.Routes.Should().ContainSingle().Which.Model.Should().Be("chat-model");
	}

	// ---- inherited = read-only ----

	[Fact]
	public async Task A_workspace_with_no_level_of_its_own_shows_the_inherited_one_read_only()
	{
		// $system declares the registry; ws-a declares nothing.
		await _editor.SetAsync(SysProj,
			new LlmRegistry([new LlmEndpoint("home", "https://home:1234")], [new LlmRoute(LlmCapability.Chat, "home", "sys-model")]),
			NoKeys);

		var view = await _editor.ViewAsync(Proj);

		view.Inherited.Should().BeTrue();
		view.InheritedFrom.Should().Be("System:$");
		view.Level.Should().Be("Workspace:ws-a");
		view.Routes.Should().ContainSingle().Which.Route.Model.Should().Be("sys-model");
	}

	[Fact]
	public async Task An_inherited_registry_refuses_a_partial_edit_and_leaves_the_level_empty()
	{
		await _editor.SetAsync(SysProj,
			new LlmRegistry([new LlmEndpoint("home", "https://home:1234")], [new LlmRoute(LlmCapability.Chat, "home", "sys-model")]),
			NoKeys);

		var view = await _editor.ViewAsync(Proj);
		var page = new IndexModel(_editor, Flags(), new ProjectDirectory(_db.Factory())) { WorkspaceKey = Ws, ProjectKey = Proj };

		await page.OnPostSaveRouteAsync(LlmCapability.Chat, "home", "hijacked", 1, null, null, view.Routes[0].Id);

		page.Error.Should().Contain("inherited");
		_db.LlmRoutes.Count(r => r.Scope == nameof(Scope.Workspace) && r.ScopeKey == Ws)
			.Should().Be(0, "a partial fork of the inherited level must not be created");
		// The workspace is still served by $system's registry, whole.
		var resolved = await _resolver.ResolveAsync(Proj);
		resolved.Level.Should().Be(RegistryLevel.System);
		resolved.Registry.Routes.Should().ContainSingle().Which.Model.Should().Be("sys-model");
	}

	// ---- helpers ----

	Task Seed() => _editor.SetAsync(Proj, new LlmRegistry(
		[new LlmEndpoint("home", "https://home:1234")],
		[
			new LlmRoute(LlmCapability.Chat, "home", "chat-model", 50),
			new LlmRoute(LlmCapability.Embed, "home", "embed-model", 50),
		]), NoKeys);

	static readonly IReadOnlyDictionary<string, string> NoKeys = new Dictionary<string, string>(StringComparer.Ordinal);

	static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

	static JsonElement Config(LlmEndpoint endpoint, LlmRoute route) =>
		JsonSerializer.SerializeToElement(
			new LlmRouterTools.ConfigSetInput([endpoint], [route]), Web);

	static IHttpContextAccessor Http(string scopes, string project) =>
		new HttpContextAccessor
		{
			HttpContext = new DefaultHttpContext
			{
				User = new ClaimsPrincipal(new ClaimsIdentity(
					[new Claim("project", project), new Claim("scopes", scopes)], "test")),
				RequestServices = TestProjectCatalog.Services,
			},
		};

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:LlmRouter"] = "true" }).Build());
}
