using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;

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

	// A small valid definition document in the def_upsert wire shape (camelCase).
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

	[Fact]
	public async Task Get_ProjectOnPresets_RendersBannerEmptyTextareaAndTemplateControl()
	{
		using var resp = await GetAuthedAsync(SystemUrl);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"methodology-state-presets\"");
		html.Should().Contain("runs on the builtin presets");
		html.Should().Contain("data-testid=\"methodology-json\"");
		Textarea(html).Trim().Should().BeEmpty("no stored definition → nothing to prefill");
		html.Should().Contain("data-testid=\"methodology-template-select\"");
		html.Should().Contain("value=\"quartet\"");
		html.Should().Contain("value=\"classic\"");
		html.Should().Contain("data-testid=\"methodology-save\"");
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
		(await tasks.GetMethodologyDefinitionAsync("$system")).Should().BeNull("nothing may be written on a rejected save");
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
			var stored = await tasks.GetMethodologyDefinitionAsync(project);
			stored.Should().NotBeNull("the save must go through the service door");
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
		Textarea(html).Should().Contain("\"job\"", "the stored definition prefills the textarea");
		html.Should().Contain("data-testid=\"methodology-preview-data\"");
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
		(await tasks.GetMethodologyDefinitionAsync("$system")).Should().BeNull("preview never writes");
	}
}
