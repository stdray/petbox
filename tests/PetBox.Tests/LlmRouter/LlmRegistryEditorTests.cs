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
		var first = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "old-model", 50)));

		await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "new-model", 50)),
			version: first.Version);

		var resolved = await _resolver.ResolveAsync(Proj);
		resolved.Registry.Routes.Should().ContainSingle().Which.Model.Should().Be("new-model");
	}

	// ---- write semantics: an omitted part is KEPT, not wiped (work llm-config-upsert-full-replace-no-cas) ----

	// THE CARD. An upsert that names only `routes` used to leave the level with ZERO endpoints — and
	// an endpoint row carries its api key, so the credentials went with it. The routes then pointed
	// at nothing and the project resolved no provider at all.
	[Fact]
	public async Task An_upsert_that_omits_endpoints_KEEPS_them_instead_of_wiping_the_level()
	{
		var created = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Json("""
				{ "endpoints": [ { "name": "home", "baseUrl": "https://home:1234" } ],
				  "routes":    [ { "capability": "chat", "endpoint": "home", "model": "old-model" } ],
				  "apiKeys":   { "home": "sk-secret" } }
				"""));

		// Only the routes are named. `endpoints` is ABSENT from the JSON — not empty, absent.
		await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Json("""{ "routes": [ { "capability": "chat", "endpoint": "home", "model": "new-model" } ] }"""),
			version: created.Version);

		var got = await LlmRouterTools.ConfigGetAsync(Http("llm:admin", Proj), Flags(), _editor, Proj);
		got.Endpoints.Should().ContainSingle(e => e.Name == "home" && e.BaseUrl == "https://home:1234");
		got.Routes.Should().ContainSingle().Which.Model.Should().Be("new-model");

		// And the runtime still has a usable provider — endpoint AND its key.
		var resolved = await _resolver.ResolveAsync(Proj);
		resolved.Registry.Endpoints.Should().ContainSingle(e => e.Name == "home");
		resolved.ApiKeys.Should().ContainKey("home").WhoseValue.Should().Be("sk-secret");
	}

	[Fact]
	public async Task An_upsert_that_omits_routes_KEEPS_them_and_their_row_ids()
	{
		var created = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Json("""
				{ "endpoints": [ { "name": "home", "baseUrl": "https://home:1234" } ],
				  "routes":    [ { "capability": "chat", "endpoint": "home", "model": "chat-model" } ] }
				"""));
		var idBefore = (await _editor.ViewAsync(Proj)).Routes.Single().Id;

		// Re-declare the endpoint only (a timeout tweak). The routes must survive it.
		await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Json("""{ "endpoints": [ { "name": "home", "baseUrl": "https://home:1234", "requestTimeoutMs": 90000 } ] }"""),
			version: created.Version);

		var view = await _editor.ViewAsync(Proj);
		view.Endpoints.Should().ContainSingle().Which.RequestTimeoutMs.Should().Be(90000);
		view.Routes.Should().ContainSingle().Which.Route.Model.Should().Be("chat-model");
		view.Routes.Single().Id.Should().Be(idBefore, "a route nobody touched keeps its handle");
	}

	// The other half of the contract: EXPLICITLY empty still clears, or there would be no way to
	// delete the last route.
	[Fact]
	public async Task An_explicitly_empty_routes_array_CLEARS_the_routes()
	{
		var created = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Json("""
				{ "endpoints": [ { "name": "home", "baseUrl": "https://home:1234" } ],
				  "routes":    [ { "capability": "chat", "endpoint": "home", "model": "chat-model" } ] }
				"""));

		await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Json("""{ "routes": [] }"""), version: created.Version);

		var got = await LlmRouterTools.ConfigGetAsync(Http("llm:admin", Proj), Flags(), _editor, Proj);
		got.Routes.Should().BeEmpty();
		got.Endpoints.Should().ContainSingle("clearing the routes must not take the endpoints with them");
	}

	// ---- CAS ----

	[Fact]
	public async Task A_concurrent_write_with_a_STALE_version_is_refused_and_nothing_is_written()
	{
		var created = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "model-a", 50)));

		// Both agents read the same baseline. The first one wins.
		var baseline = created.Version;
		await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "model-b", 50)),
			version: baseline);

		// The second arrives holding the version it read BEFORE that write.
		var act = async () => await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "model-c", 50)),
			version: baseline);

		(await act.Should().ThrowAsync<InvalidOperationException>())
			.Which.Message.Should().Contain("stale").And.Contain("llm_config_get");

		// The winner's write stands, untouched — a refused call writes NOTHING.
		var resolved = await _resolver.ResolveAsync(Proj);
		resolved.Registry.Routes.Should().ContainSingle().Which.Model.Should().Be("model-b");
	}

	// version 0 means "this level declares nothing yet". Against a level that already serves the
	// router it is not a create, it is a blind overwrite — which is exactly what the tool used to do
	// on EVERY call, because it had no version parameter at all.
	[Fact]
	public async Task Version_0_against_a_level_that_already_exists_is_refused()
	{
		await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "model-a", 50)));

		var act = async () => await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "clobbered", 50)));

		await act.Should().ThrowAsync<InvalidOperationException>();
		(await _resolver.ResolveAsync(Proj)).Registry.Routes.Should().ContainSingle().Which.Model.Should().Be("model-a");
	}

	[Fact]
	public async Task The_version_from_llm_config_get_is_the_baseline_a_correct_write_quotes()
	{
		// An unwritten level declares nothing: version 0, which is the baseline that CREATES it.
		var empty = await LlmRouterTools.ConfigGetAsync(Http("llm:admin", Proj), Flags(), _editor, Proj);
		empty.Version.Should().Be(0);

		var created = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "model-a", 50)),
			version: empty.Version);
		created.Version.Should().Be(1);

		// Read → write → read: the version the read hands out is the one the write accepts, and the
		// write's echo is the next read's baseline.
		var read = await LlmRouterTools.ConfigGetAsync(Http("llm:admin", Proj), Flags(), _editor, Proj);
		read.Version.Should().Be(created.Version);

		var second = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "model-b", 50)),
			version: read.Version);
		second.Version.Should().Be(2);
		(await _resolver.ResolveAsync(Proj)).Registry.Routes.Should().ContainSingle().Which.Model.Should().Be("model-b");
	}

	// A version must never go BACKWARDS, or a baseline read before an emptying would be accepted
	// after it — which is why the counter lives in its own row instead of on the endpoint/route rows
	// that a replace deletes.
	[Fact]
	public async Task Emptying_a_level_does_not_reset_its_version()
	{
		var created = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "model-a", 50)));

		var emptied = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Json("""{ "endpoints": [], "routes": [] }"""), version: created.Version);

		emptied.Version.Should().Be(2);
		(await LlmRouterTools.ConfigGetAsync(Http("llm:admin", Proj), Flags(), _editor, Proj)).Version.Should().Be(2);

		// The baseline from before the emptying is still refused.
		var act = async () => await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "resurrected", 50)),
			version: created.Version);
		await act.Should().ThrowAsync<InvalidOperationException>();
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

		// Now the user's form arrives, carrying the id it was rendered with (NOT an index) and the
		// CAS baseline for the version the concurrent save above just produced (work
		// llm-admin-page-no-cas — a page submit always quotes back the level's current version).
		var currentVersion = (await _editor.GetDeclaredAsync(Proj)).Version;
		var page = new IndexModel(_editor, Flags(), new ProjectDirectory(_db.Factory())) { WorkspaceKey = Ws, ProjectKey = Proj };
		await page.OnPostSaveRouteAsync(LlmCapability.Chat, "home", "chat-model-v2", 50, null, null, editedId, currentVersion);

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

		var currentVersion = (await _editor.GetDeclaredAsync(Proj)).Version;
		var page = new IndexModel(_editor, Flags(), new ProjectDirectory(_db.Factory())) { WorkspaceKey = Ws, ProjectKey = Proj };
		await page.OnPostDeleteRouteAsync(doomed, currentVersion);

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

	// ---- the level is NAMED in the answer (work llm-config-get-level-derivation-trap) ----

	// THE CARD, as a test. A smoke was planned against the sandbox project `smoke` on the reasoning
	// that a sandbox project has a level of its own. It does not — the level comes from the project's
	// WORKSPACE, and `smoke` then lived in `$system`, so its "own" level WAS the live `System:$`. The
	// surface described that as "this project's own level", which is what nearly turned a smoke into
	// a production edit. What makes it non-repeatable is that the answer now says where it came from.
	//
	// The project key here is deliberately NOT `smoke` any more: M048 moved the real `smoke` into its
	// own workspace (work `smoke-own-workspace-containment`), so naming it here would assert a
	// production fact that is no longer true. The HAZARD is not about that one project — it is that
	// ANY sandbox project parked in `$system` reads and writes the live level — so the test states the
	// shape and keeps proving it.
	[Fact]
	public async Task A_sandbox_project_in_the_system_workspace_reports_the_LIVE_System_level()
	{
		// A project whose KEY says "sandbox" and whose WORKSPACE says "$system" — the exact shape.
		_db.Insert(new Project { Key = "sbox-in-system", WorkspaceKey = WorkspaceMemory.SystemWorkspace, Name = "sbox", Description = "" });

		// $system declares the live registry.
		await _editor.SetAsync(SysProj,
			new LlmRegistry([new LlmEndpoint("home", "https://home:1234")], [new LlmRoute(LlmCapability.Chat, "home", "prod-model")]),
			NoKeys);

		var got = await LlmRouterTools.ConfigGetAsync(Http("llm:admin", "sbox-in-system"), Flags(), _editor, "sbox-in-system");

		got.Level.Should().Be("System:$",
			"the level is derived from the project's workspace — a sandbox project in `$system` reads and writes the live level");
		got.ServedBy.Should().BeNull("this level declares rows of its own; nothing is being inherited past it");
		got.Routes.Should().ContainSingle().Which.Model.Should().Be("prod-model",
			"byte for byte what `$system` returns — there is no per-project isolation here");
		got.Version.Should().BeGreaterThan(0, "and it is NOT the 'declares nothing yet' state that reads as free space");
	}

	// The other half of the trap: an empty level is not an empty registry. `version: 0` used to be
	// the only signal, and it reads as "free space" — but declaring one row here SHADOWS the whole
	// inherited level for every project of the workspace. servedBy says what is really serving it.
	[Fact]
	public async Task An_empty_level_reports_version_0_AND_the_level_that_actually_serves_the_project()
	{
		await _editor.SetAsync(SysProj,
			new LlmRegistry([new LlmEndpoint("home", "https://home:1234")], [new LlmRoute(LlmCapability.Chat, "home", "sys-model")]),
			NoKeys);

		var got = await LlmRouterTools.ConfigGetAsync(Http("llm:admin", Proj), Flags(), _editor, Proj);

		got.Version.Should().Be(0);
		got.Endpoints.Should().BeEmpty("the DECLARED registry at this level is genuinely empty");
		got.Level.Should().Be($"Workspace:{Ws}", "which is where a write with this projectKey would land");
		got.ServedBy.Should().Be("System:$", "and this is what a write here would shadow, whole");
	}

	// Nothing anywhere: empty level, and no inherited level either. servedBy must stay null rather
	// than inventing a source.
	[Fact]
	public async Task A_project_served_by_nothing_at_all_reports_no_servedBy()
	{
		var got = await LlmRouterTools.ConfigGetAsync(Http("llm:admin", Proj), Flags(), _editor, Proj);

		got.Version.Should().Be(0);
		got.Level.Should().Be($"Workspace:{Ws}");
		got.ServedBy.Should().BeNull();
	}

	// A write reports its own target. Without this the caller has to re-derive the level from the
	// projectKey — the exact derivation that was wrong in every head that got it wrong.
	[Fact]
	public async Task An_upsert_reports_the_level_it_actually_wrote()
	{
		var wsWrite = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "m", 50)));
		wsWrite.Level.Should().Be($"Workspace:{Ws}");

		var sysWrite = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", SysProj), Flags(), _editor, SysProj,
			Config(new LlmEndpoint("home", "https://home:1234"), new LlmRoute(LlmCapability.Chat, "home", "m", 50)));
		sysWrite.Level.Should().Be("System:$", "a project of the `$system` workspace writes the live system level");
	}

	// ---- read-modify-write round trip (work llm-config-capability-case-roundtrip) ----

	// THE CARD. Since an upsert REPLACES each part it is sent, read-modify-write is the only safe way
	// to edit a level — and nobody had ever run the loop end to end. llm_config_get emits enum values
	// Capitalized ("Embed" / "Disabled") while the upsert description documented them lowercase, so if
	// the parser were case-SENSITIVE this cycle would fail (or, worse, write something else) against
	// the live level. This test takes the get output VERBATIM — serialized exactly as the tool emits
	// it, no case fixing, no field pruning — feeds it straight back, and pins that the registry is
	// unchanged and the version moved by exactly one.
	[Fact]
	public async Task The_verbatim_output_of_llm_config_get_feeds_straight_back_into_llm_config_upsert()
	{
		// Every field whose casing or omission could break the loop: all three capabilities, an
		// explicit thinking mode, a tier, an embedSpaceId, and an api key that the get output does
		// NOT carry (secrets are stripped).
		var seeded = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Json("""
				{ "endpoints": [ { "name": "home", "baseUrl": "https://home:1234", "requestTimeoutMs": 90000 } ],
				  "routes": [
				    { "capability": "chat",   "endpoint": "home", "model": "chat-m",   "priority": 10, "thinking": "disabled" },
				    { "capability": "embed",  "endpoint": "home", "model": "embed-m",  "priority": 20, "embedSpaceId": "space-1" },
				    { "capability": "rerank", "endpoint": "home", "model": "rerank-m", "priority": 30, "tier": "fast" } ],
				  "apiKeys": { "home": "sk-secret" } }
				"""));

		var before = await LlmRouterTools.ConfigGetAsync(Http("llm:admin", Proj), Flags(), _editor, Proj);

		// What the caller literally sees on the wire. Pin the Capitalized emission the card reported —
		// if this ever flips to lowercase the round trip below stops proving anything about case.
		var wire = JsonSerializer.Serialize(before, Web);
		wire.Should().Contain("\"capability\":\"Chat\"").And.Contain("\"capability\":\"Embed\"")
			.And.Contain("\"capability\":\"Rerank\"").And.Contain("\"thinking\":\"Disabled\"");

		// VERBATIM: the whole get payload, `version`/`level`/`servedBy` and all, handed back as
		// `config`. No transformation whatsoever — this is what a read-modify-write with no edit is.
		var echoed = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Json(wire), version: before.Version);

		echoed.Ok.Should().BeTrue();
		echoed.Version.Should().Be(before.Version + 1, "one write, one version step — no retry, no double-apply");
		echoed.Version.Should().Be(seeded.Version + 1);
		echoed.Level.Should().Be(before.Level, "the write landed on the level the read came from");

		var after = await LlmRouterTools.ConfigGetAsync(Http("llm:admin", Proj), Flags(), _editor, Proj);
		after.Version.Should().Be(before.Version + 1);
		after.Level.Should().Be(before.Level);
		after.Endpoints.Should().BeEquivalentTo(before.Endpoints, "an echo must not alter the endpoints");
		after.Routes.Should().BeEquivalentTo(before.Routes, "…nor the routes: capability, thinking, tier and embedSpaceId all survive the loop");

		// The runtime agrees, and the api key — which the get output never carried — is still there.
		// A round trip that silently de-authenticated every endpoint would satisfy every assertion
		// above and break the router.
		var resolved = await _resolver.ResolveAsync(Proj);
		resolved.Registry.Routes.Should().HaveCount(3);
		resolved.ApiKeys.Should().ContainKey("home").WhoseValue.Should().Be("sk-secret");
	}

	// The same loop with the DOCUMENTED lowercase form, so both spellings are pinned as accepted and
	// neither the description nor the emitted output can drift into being the only one that works.
	[Fact]
	public async Task Capability_and_thinking_are_accepted_in_either_case()
	{
		var lower = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Json("""
				{ "endpoints": [ { "name": "home", "baseUrl": "https://home:1234" } ],
				  "routes": [ { "capability": "chat", "endpoint": "home", "model": "m", "thinking": "disabled" } ] }
				"""));

		var upper = await LlmRouterTools.ConfigUpsertAsync(Http("llm:admin", Proj), Flags(), _editor, Proj,
			Json("""
				{ "endpoints": [ { "name": "home", "baseUrl": "https://home:1234" } ],
				  "routes": [ { "capability": "Chat", "endpoint": "home", "model": "m", "thinking": "Disabled" } ] }
				"""),
			version: lower.Version);

		upper.Ok.Should().BeTrue();
		var got = await LlmRouterTools.ConfigGetAsync(Http("llm:admin", Proj), Flags(), _editor, Proj);
		got.Routes.Should().ContainSingle().Which.Capability.Should().Be(LlmCapability.Chat);
		got.Routes.Single().Thinking.Should().Be(LlmThinking.Disabled);
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

	// Raw JSON, so a test can leave a property genuinely ABSENT — which is the whole distinction the
	// write semantics turn on (absent = keep, [] = clear) and one a serialized record cannot express.
	static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

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
