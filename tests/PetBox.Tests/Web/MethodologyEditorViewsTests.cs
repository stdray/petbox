using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Web;

// Covers the admin methodology-definition editor (Pages/Admin/ProjectMethodology): the
// presets-only banner + empty textarea + preset-template control on a definition-less
// project; loading the classic preset as a template; a rejected save (invalid JSON) →
// errors block with the user's JSON preserved; a valid save persisting through
// ITasksService and rendering the stored-definition state; the parse-only preview island.
// Own ModuleViewsFixture instance (xUnit class fixture = per-class host); the one test
// that WRITES a definition uses its own project key, so $system stays definition-less
// for the banner assertions regardless of order.
public sealed class MethodologyEditorViewsTests : IClassFixture<ModuleViewsFixture>
{
	readonly WebApplicationFactory<Program> _factory;
	readonly HttpClient _client;

	const string TestPassword = "test123";
	const string SystemUrl = "/ui/admin/ws/$system/projects/$system/methodology";

	// A small valid methodology document in the template/rules wire shape (camelCase).
	const string SmallDefinition =
		"""
		{"name":"custom","kinds":[{"kind":"job","quickAddAllowed":true,"workflows":[{
		"types":["task"],
		"statuses":[{"slug":"todo","name":"Todo","kind":"open"},
		            {"slug":"done","name":"Done","kind":"terminalok"},
		            {"slug":"dropped","name":"Dropped","kind":"terminalcancel"}],
		"transitions":[{"from":"todo","to":"done"},{"from":"todo","to":"dropped"}]}]}]}
		""";

	public MethodologyEditorViewsTests(ModuleViewsFixture fx)
	{
		_factory = fx.Factory;
		_client = fx.Client;
	}

	// Logs in (anti-forgery + cookie) and returns the authenticated response for url.
	// Mirrors ModuleViewsTests.GetAuthedAsync; the client's cookie container keeps the
	// auth + antiforgery cookies for the POST helpers below.
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
	// The token page is the EDITOR step — the only state guaranteed to render a form on
	// every project (the create CTA is a link-only screen).
	async Task<HttpResponseMessage> PostAuthedAsync(string url, string handler, Dictionary<string, string> fields)
	{
		using var page = await GetAuthedAsync($"{url}?step=edit");
		var html = await page.Content.ReadAsStringAsync();
		var form = new Dictionary<string, string>(fields)
		{
			["__RequestVerificationToken"] = ScrapeToken(html),
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
		const string marker = "data-testid=\"methodology-json\">";
		var start = html.IndexOf(marker, StringComparison.Ordinal);
		start.Should().BeGreaterThan(-1, "the definition textarea must render");
		var contentStart = start + marker.Length;
		var end = html.IndexOf("</textarea>", contentStart, StringComparison.Ordinal);
		return WebUtility.HtmlDecode(html[contentStart..end]);
	}

	// Entry for a definition-less project: the create CTA, not a bare textarea (wizard
	// commit); the honest presets banner stays.
	[Fact]
	public async Task Get_ProjectOnPresets_RendersBannerAndCreateCta()
	{
		using var resp = await GetAuthedAsync(SystemUrl);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"methodology-state-presets\"");
		html.Should().Contain("runs on the builtin presets");
		html.Should().Contain("data-testid=\"methodology-create-cta\"", "creation is an explicit action");
		html.Should().NotContain("data-testid=\"methodology-json\"", "the bare textarea no longer greets a definition-less project");
	}

	// The editor step still offers the empty textarea + template control (the raw path).
	[Fact]
	public async Task Get_StepEdit_ProjectOnPresets_RendersEmptyEditorAndTemplateControl()
	{
		using var resp = await GetAuthedAsync($"{SystemUrl}?step=edit");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"methodology-json\"");
		Textarea(html).Trim().Should().BeEmpty("no stored definition → nothing to prefill");
		html.Should().Contain("data-testid=\"methodology-template-select\"");
		html.Should().Contain("value=\"quartet\"");
		html.Should().Contain("value=\"classic\"");
		html.Should().Contain("data-testid=\"methodology-confirm-btn\"", "the editor leads to the confirm step");
	}

	[Fact]
	public async Task PostLoadPreset_Classic_FillsTextareaWithClassicDefinitionDocument()
	{
		using var resp = await PostAuthedAsync(SystemUrl, "LoadPreset", new() { ["preset"] = "classic" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		var doc = Textarea(html);
		doc.Should().Contain("\"name\": \"classic\"", "the preset renders as a definition document named after its slug");
		doc.Should().Contain("terminalok");
		doc.Should().Contain("\"kinds\"");
		// the template is immediately previewed
		html.Should().Contain("data-testid=\"methodology-preview-data\"");
	}

	// Regression: the display formatter (MethodologyJsonFormat) once serialized leaf values
	// with the default HTML-safe encoder, so an apostrophe in a status name surfaced as the
	// literal escape `Won't fix` in the editor textarea. Leaves now use relaxed escaping.
	[Fact]
	public async Task PostLoadPreset_Quartet_RendersHumanReadableApostrophe_NotUnicodeEscape()
	{
		using var resp = await PostAuthedAsync(SystemUrl, "LoadPreset", new() { ["preset"] = "quartet" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var doc = Textarea(await resp.Content.ReadAsStringAsync());

		doc.Should().Contain("\"name\": \"Won't fix\"", "human text renders with a real apostrophe");
		doc.Should().NotContain("\\u0027", "no leftover HTML-escape dirt in the display JSON");
	}

	[Fact]
	public async Task PostSave_InvalidJson_RendersErrorsBlock_AndPreservesTextarea()
	{
		const string bad = "{ this is not json";
		using var resp = await PostAuthedAsync(SystemUrl, "Save",
			new() { ["definitionJson"] = bad, ["version"] = "0" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"methodology-errors\"");
		html.Should().Contain("invalid JSON");
		Textarea(html).Should().Contain(bad, "a rejected save must echo the user's JSON back");

		using var scope = _factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		(await tasks.ListMethodologyInstancesAsync("$system")).Should().BeEmpty("nothing may be written on a rejected save");
	}

	[Fact]
	public async Task PostSave_ValidDefinition_Persists_AndRendersStoredState()
	{
		const string project = "meditsave";
		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			if (!db.Projects.Any(p => p.Key == project))
				await db.InsertAsync(new Project { Key = project, WorkspaceKey = "$system", Name = "Methodology editor target" });
		}

		var url = $"/ui/admin/ws/$system/projects/{project}/methodology";
		using var resp = await PostAuthedAsync(url, "Save",
			new() { ["definitionJson"] = SmallDefinition, ["version"] = "0" });
		resp.StatusCode.Should().Be(HttpStatusCode.Found, "a successful save redirects");
		resp.Headers.Location!.ToString().Should().Contain("aved", "the redirect carries the saved flag");

		using (var scope = _factory.Services.CreateScope())
		{
			var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
			var stored = await tasks.GetMethodologyInstanceRulesAsync(project, "custom");
			stored.Should().NotBeNull("the save must create an open instance with these rules");
			stored!.Definition.Name.Should().Be("custom");
			stored.Definition.Kinds.Should().ContainSingle(k => k.Kind == "job");
		}

		using var after = await GetAuthedAsync(resp.Headers.Location!.ToString());
		after.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await after.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"methodology-saved\"");
		html.Should().Contain("data-testid=\"methodology-state-name\"");
		html.Should().Contain("custom");
		html.Should().Contain("data-testid=\"methodology-state-kind\"");
		html.Should().Contain("data-testid=\"methodology-preview-data\"");

		using var edit = await GetAuthedAsync($"{url}?step=edit&instance=custom");
		Textarea(await edit.Content.ReadAsStringAsync()).Should().Contain("\"job\"", "the stored rules prefill the editor");
	}

	// Creates the project row when missing (each write-y test uses its own key so $system
	// stays definition-less for the banner tests).
	async Task EnsureProjectAsync(string project)
	{
		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		if (!db.Projects.Any(p => p.Key == project))
			await db.InsertAsync(new Project { Key = project, WorkspaceKey = "$system", Name = $"Methodology test target {project}" });
	}

	// The owner-reported lifecycle repro: a methodology instance created FROM A PRESET
	// through the service door, then the page is rendered — the stored state must surface
	// in VIEW mode (summary + preview + explicit Edit / close guidance), and ?step=edit
	// prefills the editor with the document.
	[Fact]
	public async Task Get_AfterQuartetPresetDefinitionSaved_RendersViewMode_AndEditPrefills()
	{
		const string project = "medlifecycle";
		await EnsureProjectAsync(project);
		using (var scope = _factory.Services.CreateScope())
		{
			var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
			if (await tasks.GetMethodologyInstanceAsync(project, "quartet") is null)
				await tasks.CreateMethodologyInstanceAsync(project, "quartet", "builtin", "quartet");
		}

		var url = $"/ui/admin/ws/$system/projects/{project}/methodology";
		using var resp = await GetAuthedAsync(url);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"methodology-state-name\"", "the open instance rules must surface");
		html.Should().Contain("quartet");
		html.Should().Contain("data-testid=\"methodology-view\"", "an open instance opens in view mode");
		html.Should().Contain("data-testid=\"methodology-edit-link\"", "editing is an explicit action");
		html.Should().Contain("data-testid=\"methodology-delete\"", "close guidance is offered");
		html.Should().Contain("data-testid=\"methodology-preview-data\"", "view mode previews the workflows");
		html.Should().NotContain("data-testid=\"methodology-json\"", "view mode shows a summary, not the raw editor");

		using var edit = await GetAuthedAsync($"{url}?step=edit&instance=quartet");
		var editHtml = await edit.Content.ReadAsStringAsync();
		Textarea(editHtml).Should().Contain("\"work\"", "the instance rules prefill the editor");
	}

	// Finding 1: statuses[]/transitions[] elements render on ONE line each; the layout is
	// display-only — parsing the displayed document and re-projecting it reproduces the
	// exact same text (a lossless round-trip).
	[Fact]
	public void ToJson_InlinesStatusAndTransitionObjects_AndRoundTrips()
	{
		var def = MethodologyPresets.RenderPresetDefinition("quartet");
		var json = MethodologyWire.ToJson(MethodologyWire.ProjectDefinition(def, version: 0, created: null, updated: null));

		json.Should().Contain("{ \"slug\": \"reported\", \"name\": \"Reported\", \"kind\": \"open\" }",
			"a status object renders on one line");
		json.Should().Contain(
			"{ \"from\": \"reported\", \"to\": \"triage\", \"requiresApproval\": false, \"requiresReason\": false, \"enforceApproval\": false }",
			"a transition object renders on one line");
		json.Should().Contain("\"kinds\": [", "the envelope stays multi-line");
		json.Should().NotContain("\"slug\":\n", "no status field is broken across lines");

		var parsed = MethodologyWire.ParseDocument(json);
		var again = MethodologyWire.ToJson(MethodologyWire.ProjectDefinition(parsed, version: 0, created: null, updated: null));
		again.Should().Be(json, "display layout must be lossless (parse → project → format reproduces the text)");
	}

	// Finding 3: after "Load preset as template" the select keeps the loaded preset
	// instead of snapping back to the first option.
	[Fact]
	public async Task PostLoadPreset_Classic_KeepsSelectSelection()
	{
		using var resp = await PostAuthedAsync(SystemUrl, "LoadPreset", new() { ["preset"] = "classic" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().MatchRegex("value=\"classic\"[^>]*selected", "the loaded preset stays selected");
		html.Should().NotMatchRegex("value=\"quartet\"[^>]*selected", "the first option must not steal the selection");
	}

	// Finding 4: rules are not deleted independently — delete rejects with a close-instance CTA.
	[Fact]
	public async Task DeleteDefinition_RejectsWithCloseInstanceGuidance()
	{
		const string project = "meddelete";
		await EnsureProjectAsync(project);

		var url = $"/ui/admin/ws/$system/projects/{project}/methodology";
		using var saved = await PostAuthedAsync(url, "Save",
			new() { ["definitionJson"] = SmallDefinition, ["version"] = "0" });
		saved.StatusCode.Should().Be(HttpStatusCode.Found);

		using var view = await GetAuthedAsync(url);
		var viewHtml = await view.Content.ReadAsStringAsync();
		viewHtml.Should().Contain("data-testid=\"methodology-delete-form\"");

		using var deleted = await PostAuthedAsync(url, "Delete", new() { ["version"] = "1", ["instance"] = "custom" });
		deleted.StatusCode.Should().Be(HttpStatusCode.OK, "delete of rules is rejected in place");
		var html = await deleted.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"methodology-errors\"");
		html.Should().Contain("Close the methodology instance");
		html.Should().Contain("data-testid=\"methodology-state-name\"", "instance rules survive the rejected delete");

		using var scope = _factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		(await tasks.GetMethodologyInstanceRulesAsync(project, "custom")).Should().NotBeNull(
			"rules stay until the instance is closed via tasks_methodology_close");
	}

	// Finding 4 (guard): delete always rejects with close guidance (no silent def wipe).
	[Fact]
	public async Task DeleteDefinition_AlwaysRejects_EvenWithLiveNodes()
	{
		const string project = "meddelreject";
		await EnsureProjectAsync(project);
		using (var scope = _factory.Services.CreateScope())
		{
			var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
			var def = new MethodologyDefinition("flowdef",
			[
				new MethodologyKindDef("flow", QuickAddAllowed: true,
				[
					new MethodologyWorkflowDef(["case"],
						[new("verifying", "Verifying", StatusKind.Open), new("closed", "Closed", StatusKind.TerminalOk)],
						[new("verifying", "closed")]),
				]),
			]);
			await tasks.UpsertMethodologyTemplateAsync(project, "flow-tmpl", def, 0);
			await tasks.CreateMethodologyInstanceAsync(project, "flowdef", "template", "flow-tmpl");
			var board = (await tasks.ListBoardsAsync(project)).Single(b => b.Kind == "flow").Name;
			await tasks.QuickAddAsync(project, board, "case one", null, 0);
		}

		var url = $"/ui/admin/ws/$system/projects/{project}/methodology";
		using var rejected = await PostAuthedAsync(url, "Delete", new() { ["version"] = "1", ["instance"] = "flowdef" });
		rejected.StatusCode.Should().Be(HttpStatusCode.OK, "delete of rules is rejected with close guidance");
		var html = await rejected.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"methodology-errors\"");
		html.Should().Contain("Close the methodology instance");
		html.Should().Contain("data-testid=\"methodology-state-name\"", "instance rules survive");

		using (var scope = _factory.Services.CreateScope())
		{
			var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
			(await tasks.GetMethodologyInstanceRulesAsync(project, "flowdef")).Should().NotBeNull();
		}
	}

	// Finding 5a+5c: the legend renders under the preview, and the quartet work kind's
	// cross-board effects surface in the preview island as pre-phrased sentences.
	[Fact]
	public async Task PostLoadPreset_Quartet_RendersLegend_AndEffectNotes()
	{
		using var resp = await PostAuthedAsync(SystemUrl, "LoadPreset", new() { ["preset"] = "quartet" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"methodology-preview-legend\"");
		html.Should().Contain("checklist", "the legend explains the checklist marker");
		html.Should().Contain("INITIAL status", "the legend explains the initial-status marker");
		// The preview island carries the work kind's effects as guide-phrased sentences.
		html.Should().Contain("On entering Done, incoming issue_task nodes are set to done.");
		html.Should().Contain("On entering Done, outgoing blocks nodes currently in Blocked are set to InProgress.");
	}

	// Finding 6: the collapsible definition reference renders, generated off the wire DTOs
	// — key entities/fields present, nothing left undocumented.
	[Fact]
	public async Task Get_RendersDefinitionReference()
	{
		using var resp = await GetAuthedAsync($"{SystemUrl}?step=edit");
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"methodology-reference\"");
		foreach (var field in new[] { "linkConstraints", "enforceApproval", "targetStatuses", "preconditionArtifact", "tagAxes", "onlyFrom" })
			html.Should().Contain(field, $"the reference must document `{field}`");
		html.Should().Contain("terminalok", "the status-kind vocabulary is stated");
		html.Should().NotContain("(undocumented", "every reflected field carries a description");
	}

	// REPRO 2: the exact owner flow through the PAGE — load a preset as template, save the
	// templated document verbatim, follow the redirect: the stored state must surface.
	[Theory]
	[InlineData("quartet", "medpageflow")]
	[InlineData("classic", "medpageflowc")]
	public async Task PageFlow_LoadPresetThenSave_RendersStoredState(string preset, string project)
	{
		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			if (!db.Projects.Any(p => p.Key == project))
				await db.InsertAsync(new Project { Key = project, WorkspaceKey = "$system", Name = "Methodology page-flow target" });
		}

		var url = $"/ui/admin/ws/$system/projects/{project}/methodology";
		using var loaded = await PostAuthedAsync(url, "LoadPreset", new() { ["preset"] = preset });
		loaded.StatusCode.Should().Be(HttpStatusCode.OK);
		var templated = Textarea(await loaded.Content.ReadAsStringAsync());
		templated.Should().Contain($"\"name\": \"{preset}\"");

		using var saved = await PostAuthedAsync(url, "Save",
			new() { ["definitionJson"] = templated, ["version"] = "0" });
		var savedHtml = await saved.Content.ReadAsStringAsync();
		saved.StatusCode.Should().Be(HttpStatusCode.Found,
			$"the templated preset document must save; page said: {Between(savedHtml, "methodology-errors\">", "</div>")}");

		using var after = await GetAuthedAsync(saved.Headers.Location!.ToString());
		after.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await after.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"methodology-state-name\"", "the stored definition must surface");
		html.Should().Contain("data-testid=\"methodology-view\"", "the saved definition opens in view mode");
		html.Should().Contain(preset);
	}

	static string Between(string html, string start, string end)
	{
		var s = html.IndexOf(start, StringComparison.Ordinal);
		if (s < 0) return "(no errors block)";
		s += start.Length;
		var e = html.IndexOf(end, s, StringComparison.Ordinal);
		return e < 0 ? html[s..] : html[s..e];
	}

	// ── creation wizard (commit 2) ─────────────────────────────────────────────

	// Step 1: the base picker lists the builtin provisioning presets AND open instance
	// rules from other projects, each wired to a per-card SVG preview via the bases island.
	[Fact]
	public async Task Get_StepBase_ListsPresetAndUserDefinitionBases()
	{
		const string source = "medbasesrc";
		await EnsureProjectAsync(source);
		using (var scope = _factory.Services.CreateScope())
		{
			var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
			if (await tasks.GetMethodologyInstanceAsync(source, "classic") is null)
				await tasks.CreateMethodologyInstanceAsync(source, "classic", "builtin", "classic");
		}

		using var resp = await GetAuthedAsync($"{SystemUrl}?step=base");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"methodology-base-picker\"");
		html.Should().Contain("value=\"preset:quartet\"");
		html.Should().Contain("value=\"preset:classic\"");
		html.Should().Contain($"value=\"instance:{source}:classic\"", "another project's open instance is offered as a base");
		html.Should().Contain("data-testid=\"methodology-base-previews-data\"", "each base carries its SVG preview docs");
		html.Should().Contain($"data-base-preview=\"instance:{source}:classic\"");
	}

	// Step 1 → 2: choosing a base opens the editor prefilled with it (preset and
	// user-definition variants).
	[Fact]
	public async Task PostStartEdit_PresetBase_PrefillsEditor()
	{
		using var resp = await PostAuthedAsync(SystemUrl, "StartEdit", new() { ["baseRef"] = "preset:classic" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		var doc = Textarea(html);
		doc.Should().Contain("\"name\": \"classic\"");
		html.Should().Contain("data-testid=\"methodology-preview-data\"", "the chosen base previews immediately");
	}

	[Fact]
	public async Task PostStartEdit_UserDefinitionBase_PrefillsEditor()
	{
		const string source = "medbasesrc2";
		await EnsureProjectAsync(source);
		using (var scope = _factory.Services.CreateScope())
		{
			var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
			if (await tasks.GetMethodologyInstanceAsync(source, "quartet") is null)
				await tasks.CreateMethodologyInstanceAsync(source, "quartet", "builtin", "quartet");
		}

		using var resp = await PostAuthedAsync(SystemUrl, "StartEdit", new() { ["baseRef"] = $"instance:{source}:quartet" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		Textarea(await resp.Content.ReadAsStringAsync()).Should().Contain("\"name\": \"quartet\"",
			"the other project's open instance rules are copied into the editor");
	}

	// Step 2 → 3: the confirm summary digests the parsed document (counts + gates) and
	// carries the JSON in hidden fields for the final Save; bad JSON falls back to the
	// editor with the message.
	[Fact]
	public async Task PostConfirm_RendersSummary_WithHiddenDocument()
	{
		using var resp = await PostAuthedAsync(SystemUrl, "Confirm",
			new() { ["definitionJson"] = SmallDefinition, ["version"] = "0" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"methodology-confirm\"");
		html.Should().Contain("data-testid=\"methodology-confirm-kind\"");
		html.Should().Contain("job");
		html.Should().Contain("3 status(es)");
		html.Should().Contain("2 transition(s)");
		html.Should().Contain("name=\"definitionJson\"", "the confirm form carries the document to Save");
		html.Should().Contain("data-testid=\"methodology-save\"");
		html.Should().Contain("data-testid=\"methodology-confirm-back\"");

		using var scope = _factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		(await tasks.ListMethodologyInstancesAsync("$system")).Should().BeEmpty("confirm never writes");
	}

	[Fact]
	public async Task PostConfirm_InvalidJson_FallsBackToEditorWithError()
	{
		using var resp = await PostAuthedAsync(SystemUrl, "Confirm",
			new() { ["definitionJson"] = "{ nope", ["version"] = "0" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"methodology-errors\"");
		html.Should().Contain("invalid JSON");
		Textarea(html).Should().Contain("{ nope", "the editor echoes the user's JSON back");
	}

	[Fact]
	public async Task PostPreview_ValidDefinition_RendersPreviewIsland_WithoutSaving()
	{
		using var resp = await PostAuthedAsync(SystemUrl, "Preview",
			new() { ["definitionJson"] = SmallDefinition, ["version"] = "0" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"methodology-preview-data\"");
		html.Should().Contain("data-testid=\"methodology-preview\"");
		// the island carries the graph docs (WorkflowGraphJson camelCase, StatusKind names)
		html.Should().Contain("\"kind\":\"job\"");
		html.Should().Contain("\"slug\":\"todo\"");
		html.Should().Contain("TerminalOk");

		using var scope = _factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		(await tasks.ListMethodologyInstancesAsync("$system")).Should().BeEmpty("preview never writes");
	}
}
