using System.Net;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Services;

namespace PetBox.Tests.Web;

// Covers the admin agent-definition editor (Pages/Admin/ProjectAgentDefs): the empty state,
// the list of stored definitions, create-by-key, a valid save persisting through
// IAgentDefinitionService, and the three rejections that must surface verbatim WITHOUT losing
// the user's text — invalid JSON, a `model` property anywhere in the tree, a stale version —
// plus delete. Shares ModuleViewsFixture (per-class host, own temp DB); every write-y test uses
// its own project key so $system stays definition-less for the empty-state assertion.
public sealed class AgentDefsAdminPageTests : IClassFixture<ModuleViewsFixture>
{
	readonly WebApplicationFactory<Program> _factory;
	readonly HttpClient _client;

	const string TestPassword = "test123";
	const string SystemUrl = "/ui/admin/ws/$system/projects/$system/agent-defs";

	// A minimal valid portable definition (camelCase wire shape, no `model` anywhere).
	const string SmallDefinition =
		"""
		{"name":"core team","roles":[
		  {"slug":"lead","tier":"orchestrator","requiredCapabilities":["plan"],
		   "spawn":{"allowed":true,"allowedRoles":["worker"]},
		   "escalation":{"available":false}},
		  {"slug":"worker","tier":"worker","requiredCapabilities":[],
		   "spawn":{"allowed":false},
		   "escalation":{"available":true,"targets":["lead"]}}]}
		""";

	// The same document with a role-level `model` — the portable-definition reject.
	const string DefinitionWithModel =
		"""
		{"name":"core team","roles":[
		  {"slug":"lead","tier":"orchestrator","requiredCapabilities":[],"model":"opus"}]}
		""";

	public AgentDefsAdminPageTests(ModuleViewsFixture fx)
	{
		_factory = fx.Factory;
		_client = fx.Client;
	}

	// Logs in (anti-forgery + cookie) and returns the authenticated response for url.
	async Task<HttpResponseMessage> GetAuthedAsync(string url)
	{
		var resp = await _client.GetAsync(url);
		if (resp.StatusCode != HttpStatusCode.Found) return resp;

		var loginPage = await _client.GetAsync("/Login");
		var loginHtml = await loginPage.Content.ReadAsStringAsync();
		var token = ScrapeToken(loginHtml);
		var cookies = loginPage.Headers.GetValues("Set-Cookie").ToList();

		var loginReq = new HttpRequestMessage(HttpMethod.Post, "/Login?returnUrl=" + Uri.EscapeDataString(url));
		loginReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = "admin",
			["password"] = TestPassword,
			["returnUrl"] = url,
			["__RequestVerificationToken"] = token,
		});
		foreach (var c in cookies) loginReq.Headers.Add("Cookie", c.Split(';')[0]);

		var loginResp = await _client.SendAsync(loginReq);
		var authCookie = loginResp.Headers.GetValues("Set-Cookie").First();
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", authCookie.Split(';')[0]);
		return await _client.SendAsync(req);
	}

	// GET the page (login if needed), scrape its antiforgery token, POST the handler form.
	// The create form renders on every state, so the bare page always carries a token.
	async Task<HttpResponseMessage> PostAuthedAsync(string url, string handler, Dictionary<string, string> fields)
	{
		using var page = await GetAuthedAsync(url);
		var html = await page.Content.ReadAsStringAsync();
		var form = new Dictionary<string, string>(fields)
		{
			["__RequestVerificationToken"] = ScrapeToken(html),
		};
		return await _client.PostAsync($"{url}?handler={handler}", new FormUrlEncodedContent(form));
	}

	// Same as PostAuthedAsync, but for a handler with REPEATED keys (checkbox groups like
	// `capabilities` / `spawnAllowedRoles` / `escalationTargets`) — a Dictionary can't carry
	// duplicate keys, so this takes the pairs directly.
	async Task<HttpResponseMessage> PostAuthedFieldsAsync(string url, string handler, List<KeyValuePair<string, string>> fields)
	{
		using var page = await GetAuthedAsync(url);
		var html = await page.Content.ReadAsStringAsync();
		var form = new List<KeyValuePair<string, string>>(fields)
		{
			new("__RequestVerificationToken", ScrapeToken(html)),
		};
		return await _client.PostAsync($"{url}?handler={handler}", new FormUrlEncodedContent(form));
	}

	static string ScrapeToken(string html)
	{
		var tokenStart = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = html.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = html.IndexOf('"', valueStart);
		return html[valueStart..valueEnd];
	}

	// The decoded contents of the definition textarea (data-testid is its LAST attribute,
	// so the marker ends the opening tag).
	static string Textarea(string html)
	{
		const string marker = "data-testid=\"agent-defs-json\">";
		var start = html.IndexOf(marker, StringComparison.Ordinal);
		start.Should().BeGreaterThan(-1, "the definition textarea must render");
		var contentStart = start + marker.Length;
		var end = html.IndexOf("</textarea>", contentStart, StringComparison.Ordinal);
		return WebUtility.HtmlDecode(html[contentStart..end]);
	}

	async Task EnsureProjectAsync(string project)
	{
		using var scope = _factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		if (!db.Projects.Any(p => p.Key == project))
			await db.InsertAsync(new Project { Key = project, WorkspaceKey = "$system", Name = $"Agent-def test target {project}" });
	}

	static string Url(string project) => $"/ui/admin/ws/$system/projects/{project}/agent-defs";

	[Fact]
	public async Task Get_NoDefinitions_RendersEmptyState()
	{
		using var resp = await GetAuthedAsync(SystemUrl);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"agent-defs-list\"");
		html.Should().Contain("data-testid=\"agent-defs-empty\"");
		html.Should().Contain("data-testid=\"agent-defs-create-form\"");
		html.Should().Contain("data-testid=\"agent-defs-reference\"");
		html.Should().NotContain("data-testid=\"agent-defs-json\"", "no key selected → no editor");
	}

	[Fact]
	public async Task Get_StoredDefinition_RendersRow_AndOpensInEditor()
	{
		const string project = "agdlist";
		await EnsureProjectAsync(project);
		using (var scope = _factory.Services.CreateScope())
		{
			var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
			await defs.UpsertJsonAsync(project, "squad", SmallDefinition, 0);
		}

		using var resp = await GetAuthedAsync(Url(project));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"agent-defs-row\"");
		html.Should().Contain("data-def-key=\"squad\"");
		html.Should().Contain("data-testid=\"agent-defs-row-name\"");
		html.Should().Contain("data-testid=\"agent-defs-row-version\"");
		html.Should().Contain("data-testid=\"agent-defs-open\"");
		html.Should().NotContain("data-testid=\"agent-defs-empty\"");

		using var editor = await GetAuthedAsync($"{Url(project)}?key=squad");
		var editorHtml = await editor.Content.ReadAsStringAsync();
		editorHtml.Should().Contain("data-testid=\"agent-defs-editor\"");
		editorHtml.Should().Contain("data-testid=\"agent-defs-version\"");
		editorHtml.Should().Contain("data-testid=\"agent-defs-schema-note\"");
		var doc = Textarea(editorHtml);
		doc.Should().Contain("\"name\": \"core team\"", "the stored document prefills the editor");
		doc.Should().Contain("\"slug\": \"lead\"");
	}

	[Fact]
	public async Task PostCreate_NewKey_StoresStarterDefinition_AndOpensEditor()
	{
		const string project = "agdcreate";
		await EnsureProjectAsync(project);

		using var resp = await PostAuthedAsync(Url(project), "Create", new() { ["key"] = "newteam" });
		resp.StatusCode.Should().Be(HttpStatusCode.Found, "a successful create redirects");
		resp.Headers.Location!.ToString().Should().Contain("newteam");

		using (var scope = _factory.Services.CreateScope())
		{
			var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
			var stored = await defs.GetAsync(project, "newteam");
			stored.Should().NotBeNull();
			stored!.Definition.Roles.Should().NotBeEmpty("the starter document carries at least one role");
		}

		using var after = await GetAuthedAsync(resp.Headers.Location!.ToString());
		after.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await after.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"agent-defs-editor\"");
		html.Should().Contain("data-testid=\"agent-defs-row\"");
		Textarea(html).Should().Contain("\"name\": \"newteam\"");
	}

	[Fact]
	public async Task PostCreate_InvalidKey_SurfacesError_AndWritesNothing()
	{
		const string project = "agdbadkey";
		await EnsureProjectAsync(project);

		using var resp = await PostAuthedAsync(Url(project), "Create", new() { ["key"] = "9bad key" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "a rejected create rerenders in place");
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"agent-defs-errors\"");

		using var scope = _factory.Services.CreateScope();
		var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
		(await defs.ListAsync(project)).Should().BeEmpty("nothing may be written on a rejected create");
	}

	[Fact]
	public async Task PostSave_ValidDocument_Persists_AndRedirects()
	{
		const string project = "agdsave";
		await EnsureProjectAsync(project);
		using (var scope = _factory.Services.CreateScope())
		{
			var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
			await defs.UpsertJsonAsync(project, "squad", ProjectAgentDefsStarter("squad"), 0);
		}

		using var resp = await PostAuthedAsync(Url(project), "Save",
			new() { ["key"] = "squad", ["definitionJson"] = SmallDefinition, ["version"] = "1" });
		resp.StatusCode.Should().Be(HttpStatusCode.Found, "a successful save redirects");

		using (var scope = _factory.Services.CreateScope())
		{
			var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
			var stored = await defs.GetAsync(project, "squad");
			stored.Should().NotBeNull();
			stored!.Definition.Name.Should().Be("core team");
			stored.Definition.Roles.Should().HaveCount(2);
			stored.Version.Should().Be(2, "the save bumped the watermark");
		}
	}

	// Fields outside the schema (the TS client's DEFAULT_AGENT_DEFINITION carries `notes` on every
	// role) must survive a save through the page: the raw document is what gets stored, and the
	// editor prefills from that raw document — not from the typed projection, which would strip it.
	[Fact]
	public async Task PostSave_UnknownFields_ArePreserved_AndRenderBackInEditor()
	{
		const string project = "agdnotes";
		await EnsureProjectAsync(project);
		const string withNotes =
			"""
			{"name":"core team","notes":"roster prose","roles":[
			  {"slug":"lead","tier":"orchestrator","requiredCapabilities":[],"notes":"runs the show"}]}
			""";

		using var resp = await PostAuthedAsync(Url(project), "Save",
			new() { ["key"] = "squad", ["definitionJson"] = withNotes, ["version"] = "0" });
		resp.StatusCode.Should().Be(HttpStatusCode.Found, "a successful save redirects");

		using (var scope = _factory.Services.CreateScope())
		{
			var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
			var raw = await defs.GetJsonAsync(project, "squad");
			raw.Should().NotBeNull();
			var root = JsonDocument.Parse(raw!).RootElement;
			root.GetProperty("notes").GetString().Should().Be("roster prose");
			root.GetProperty("roles")[0].GetProperty("notes").GetString().Should().Be("runs the show");
		}

		using var editor = await GetAuthedAsync($"{Url(project)}?key=squad");
		var doc = Textarea(await editor.Content.ReadAsStringAsync());
		doc.Should().Contain("\"notes\": \"roster prose\"", "the editor prefills from the RAW stored document");
		doc.Should().Contain("\"notes\": \"runs the show\"");
	}

	[Fact]
	public async Task PostSave_InvalidJson_RendersErrors_PreservesInput_AndWritesNothing()
	{
		const string project = "agdbadjson";
		await EnsureProjectAsync(project);
		const string bad = "{ this is not json";

		using var resp = await PostAuthedAsync(Url(project), "Save",
			new() { ["key"] = "squad", ["definitionJson"] = bad, ["version"] = "0" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"agent-defs-errors\"");
		Textarea(html).Should().Contain(bad, "a rejected save must echo the user's JSON back");

		using var scope = _factory.Services.CreateScope();
		var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
		(await defs.ListAsync(project)).Should().BeEmpty("nothing may be written on a rejected save");
	}

	[Fact]
	public async Task PostSave_DocumentWithModelField_SurfacesRejectMessage_AndPreservesInput()
	{
		const string project = "agdmodel";
		await EnsureProjectAsync(project);

		using var resp = await PostAuthedAsync(Url(project), "Save",
			new() { ["key"] = "squad", ["definitionJson"] = DefinitionWithModel, ["version"] = "0" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"agent-defs-errors\"");
		html.Should().Contain("is not allowed on portable agent definitions", "the service's reject reaches the user verbatim");
		html.Should().Contain("roles[0].model", "the message names the offending JSON path");
		Textarea(html).Should().Contain("\"model\"", "the user's document is preserved so they can fix it");

		using var scope = _factory.Services.CreateScope();
		var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
		(await defs.ListAsync(project)).Should().BeEmpty("a model-carrying document is never stored");
	}

	[Fact]
	public async Task PostSave_StaleVersion_SurfacesConflict_AndPreservesInput()
	{
		const string project = "agdconflict";
		await EnsureProjectAsync(project);
		using (var scope = _factory.Services.CreateScope())
		{
			var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
			await defs.UpsertJsonAsync(project, "squad", ProjectAgentDefsStarter("squad"), 0);
			await defs.UpsertJsonAsync(project, "squad", SmallDefinition, 1); // now at version 2
		}

		// A DIFFERENT document (an identical resubmit would dedupe to a no-op) against the
		// now-stale baseline 1 — the current version is 2.
		var edited = SmallDefinition.Replace("core team", "renamed team", StringComparison.Ordinal);
		using var resp = await PostAuthedAsync(Url(project), "Save",
			new() { ["key"] = "squad", ["definitionJson"] = edited, ["version"] = "1" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "a conflict rerenders in place");
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"agent-defs-errors\"");
		html.Should().Contain("conflict", "the CAS rejection reaches the user");
		Textarea(html).Should().Contain("renamed team", "the user's JSON survives a conflict — they resolve it");
		html.Should().Contain("name=\"version\" value=\"1\"", "the posted watermark is echoed back, not silently advanced");
	}

	[Fact]
	public async Task PostDelete_RemovesDefinition()
	{
		const string project = "agddelete";
		await EnsureProjectAsync(project);
		using (var scope = _factory.Services.CreateScope())
		{
			var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
			await defs.UpsertJsonAsync(project, "squad", SmallDefinition, 0);
		}

		using var list = await GetAuthedAsync(Url(project));
		var listHtml = await list.Content.ReadAsStringAsync();
		listHtml.Should().Contain("data-testid=\"agent-defs-delete-form\"");
		listHtml.Should().Contain("data-testid=\"agent-defs-delete\"");
		listHtml.Should().Contain("data-confirm=", "destructive posts confirm through ts/confirm.ts, not inline JS");

		using var resp = await PostAuthedAsync(Url(project), "Delete",
			new() { ["key"] = "squad", ["version"] = "1" });
		resp.StatusCode.Should().Be(HttpStatusCode.Found);

		using var scope2 = _factory.Services.CreateScope();
		var defs2 = scope2.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
		(await defs2.GetAsync(project, "squad")).Should().BeNull("the definition is gone");
	}

	// A document with fields OUTSIDE the schema (a top-level property and a per-role property),
	// same spirit as SmallDefinition but with the extras the round-trip test needs, and
	// capabilities already in the catalog's own order (harness-capabilities.ts) so a checkbox
	// rebuild that only re-selects the same set reproduces the same array.
	const string DefinitionWithUnknownFields =
		"""
		{"name":"core team","ownerNote":"do not delete","roles":[
		  {"slug":"lead","tier":"orchestrator","requiredCapabilities":["mcp_main_session","spawn_subagents"],
		   "spawn":{"allowed":true,"allowedRoles":["worker"]},
		   "escalation":{"available":false},"notes":"runs the show","favoriteColor":"teal"},
		  {"slug":"worker","tier":"worker","requiredCapabilities":[],
		   "escalation":{"available":true,"targets":["lead"]}}]}
		""";

	[Fact]
	public async Task Get_Editor_RendersCapabilityCheckboxes_FromTheKnownCatalog()
	{
		const string project = "agdcaps";
		await EnsureProjectAsync(project);
		using (var scope = _factory.Services.CreateScope())
		{
			var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
			await defs.UpsertJsonAsync(project, "squad", SmallDefinition, 0);
		}

		using var resp = await GetAuthedAsync($"{Url(project)}?key=squad");
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"agent-defs-role-card\"");
		foreach (var cap in PetBox.Core.Contract.AgentDefinitionCapabilities.All)
			html.Should().Contain($"data-testid=\"agent-defs-role-capability-{cap}\"", "the checkbox catalog is AgentDefinitionCapabilities.All");
		// The seeded role's own non-catalog capability ("plan") must still be visible, not silently dropped.
		html.Should().Contain("data-testid=\"agent-defs-role-extra-capabilities\"");
		html.Should().Contain(">plan<");
	}

	[Fact]
	public async Task PostSaveRole_NoOpSave_IsByteForByteIdentical()
	{
		const string project = "agdroleroundtrip";
		await EnsureProjectAsync(project);
		using (var scope = _factory.Services.CreateScope())
		{
			var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
			await defs.UpsertJsonAsync(project, "squad", DefinitionWithUnknownFields, 0);
		}

		// Post role 0 ("lead") back with EXACTLY the values it already has.
		var fields = new List<KeyValuePair<string, string>>
		{
			new("key", "squad"), new("version", "1"), new("roleIndex", "0"),
			new("slug", "lead"), new("tier", "orchestrator"),
			new("capabilities", "mcp_main_session"), new("capabilities", "spawn_subagents"),
			new("spawnAllowed", "true"), new("spawnAllowed", "false"), // checkbox-then-hidden: FIRST posted value wins
			new("spawnAllowedRoles", "worker"),
			new("escalationAvailable", "false"),
			new("notes", "runs the show"),
		};
		using var resp = await PostAuthedFieldsAsync(Url(project), "SaveRole", fields);
		resp.StatusCode.Should().Be(HttpStatusCode.Found, "a no-op role save still succeeds");

		using var scope2 = _factory.Services.CreateScope();
		var defs2 = scope2.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
		var raw = await defs2.GetJsonAsync(project, "squad");
		var canonicalOriginal = PetBox.Core.Contract.AgentDefinitionJson.CanonicalizeRaw(DefinitionWithUnknownFields, "squad");
		raw.Should().Be(canonicalOriginal,
			"editing role 0 with its own unchanged values must not rewrite a single byte — not role 1, " +
			"not the top-level ownerNote, not role 0's own favoriteColor");
	}

	[Fact]
	public async Task PostSaveRole_ChangesTier_PersistsAndPreservesTheOtherRoleAndUnknownFields()
	{
		const string project = "agdroleedit";
		await EnsureProjectAsync(project);
		using (var scope = _factory.Services.CreateScope())
		{
			var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
			await defs.UpsertJsonAsync(project, "squad", DefinitionWithUnknownFields, 0);
		}

		var fields = new List<KeyValuePair<string, string>>
		{
			new("key", "squad"), new("version", "1"), new("roleIndex", "0"),
			new("slug", "lead"), new("tier", "principal"), // <-- changed
			new("capabilities", "mcp_main_session"), new("capabilities", "spawn_subagents"),
			new("spawnAllowed", "true"), new("spawnAllowed", "false"),
			new("spawnAllowedRoles", "worker"),
			new("escalationAvailable", "false"),
			new("notes", "runs the show"),
		};
		using var resp = await PostAuthedFieldsAsync(Url(project), "SaveRole", fields);
		resp.StatusCode.Should().Be(HttpStatusCode.Found);

		using var scope2 = _factory.Services.CreateScope();
		var defs2 = scope2.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
		var stored = await defs2.GetAsync(project, "squad");
		stored!.Definition.Roles[0].Tier.Should().Be("principal");
		var raw = await defs2.GetJsonAsync(project, "squad");
		using var doc = JsonDocument.Parse(raw!);
		doc.RootElement.GetProperty("ownerNote").GetString().Should().Be("do not delete", "untouched top-level field survives");
		doc.RootElement.GetProperty("roles")[0].GetProperty("favoriteColor").GetString().Should().Be("teal", "untouched per-role field survives");
		doc.RootElement.GetProperty("roles")[1].GetProperty("slug").GetString().Should().Be("worker", "the other role is untouched");
	}

	[Fact]
	public async Task PostAddRole_AppendsAStarterRole()
	{
		const string project = "agdroleadd";
		await EnsureProjectAsync(project);
		using (var scope = _factory.Services.CreateScope())
		{
			var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
			await defs.UpsertJsonAsync(project, "squad", SmallDefinition, 0);
		}

		using var resp = await PostAuthedAsync(Url(project), "AddRole",
			new() { ["key"] = "squad", ["version"] = "1", ["newRoleSlug"] = "researcher" });
		resp.StatusCode.Should().Be(HttpStatusCode.Found);

		using var scope2 = _factory.Services.CreateScope();
		var defs2 = scope2.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
		var stored = await defs2.GetAsync(project, "squad");
		stored!.Definition.Roles.Should().Contain(r => r.Slug == "researcher");
		stored.Definition.Roles.Should().HaveCount(3, "the two seeded roles survive the append");
	}

	[Fact]
	public async Task PostDeleteRole_RemovesTheNamedRole()
	{
		const string project = "agdroledelete";
		await EnsureProjectAsync(project);
		using (var scope = _factory.Services.CreateScope())
		{
			var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
			await defs.UpsertJsonAsync(project, "squad", SmallDefinition, 0);
		}

		using var resp = await PostAuthedAsync(Url(project), "DeleteRole",
			new() { ["key"] = "squad", ["version"] = "1", ["roleIndex"] = "1" });
		resp.StatusCode.Should().Be(HttpStatusCode.Found);

		using var scope2 = _factory.Services.CreateScope();
		var defs2 = scope2.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
		var stored = await defs2.GetAsync(project, "squad");
		stored!.Definition.Roles.Should().ContainSingle(r => r.Slug == "lead");
	}

	[Fact]
	public async Task PostDeleteRole_RefusesToRemoveTheLastRole()
	{
		const string project = "agdrolelastguard";
		await EnsureProjectAsync(project);
		const string oneRole = """{"name":"solo","roles":[{"slug":"only","tier":"worker","requiredCapabilities":[]}]}""";
		using (var scope = _factory.Services.CreateScope())
		{
			var defs = scope.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
			await defs.UpsertJsonAsync(project, "squad", oneRole, 0);
		}

		using var resp = await PostAuthedAsync(Url(project), "DeleteRole",
			new() { ["key"] = "squad", ["version"] = "1", ["roleIndex"] = "0" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "a refused delete rerenders in place, it does not redirect");
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"agent-defs-errors\"");
		html.Should().Contain("at least one role");

		using var scope2 = _factory.Services.CreateScope();
		var defs2 = scope2.ServiceProvider.GetRequiredService<IAgentDefinitionService>();
		(await defs2.GetAsync(project, "squad"))!.Definition.Roles.Should().ContainSingle();
	}

	// The page's own starter document (what Create stores) — kept here so the fixtures above
	// seed exactly what the UI would.
	static string ProjectAgentDefsStarter(string key) =>
		PetBox.Web.Pages.Admin.ProjectAgentDefsModel.StarterJson(key);
}
