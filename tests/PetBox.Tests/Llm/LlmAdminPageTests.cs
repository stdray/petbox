using FluentValidation;
using FluentValidation.Results;
using LinqToDB;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.LlmRouter.Contract;
using PetBox.Web.Pages.Llm;

namespace PetBox.Tests.Llm;

// The LLM-router admin page (spec llm-admin-ui + llm-routes-ui): add/update/delete provider
// endpoints AND routes over ILlmRegistryEditor — the LEVELLED registry the router actually
// resolves through. Keys are write-only. Routes are addressed by their ROW ID, never by position;
// an inherited registry is read-only.
public sealed class LlmAdminPageTests : IDisposable
{
	const string Proj = "p";
	readonly string _dir;
	readonly PetBoxDb _db;

	public LlmAdminPageTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-llmui-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	static FeatureFlags Features(bool llmRouter)
	{
		var cfg = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:LlmRouter"] = llmRouter ? "true" : "false" })
			.Build();
		return new FeatureFlags(cfg);
	}

	IndexModel Page(FakeEditor reg, bool feature = true) =>
		new(reg, Features(feature), _db.Factory()) { WorkspaceKey = "ws", ProjectKey = Proj };

	[Fact]
	public async Task Feature_disabled_returns_NotFound()
	{
		var page = Page(new FakeEditor(), feature: false);
		(await page.OnGetAsync()).Should().BeOfType<NotFoundResult>();
	}

	[Fact]
	public async Task Save_adds_endpoint_and_key_then_redirects()
	{
		var reg = new FakeEditor();
		var page = Page(reg);

		var result = await page.OnPostSaveAsync("home", "https://h:1234", null, 2000, 60000, "sk-secret");

		result.Should().BeOfType<RedirectToPageResult>();
		reg.Endpoints.Should().ContainSingle(e => e.Name == "home" && e.BaseUrl == "https://h:1234");
		reg.LastApiKeys.Should().ContainKey("home").WhoseValue.Should().Be("sk-secret");
	}

	[Fact]
	public async Task Save_updates_existing_endpoint_by_name_blank_key_keeps_secret()
	{
		var reg = new FakeEditor { Endpoints = [new LlmEndpoint("home", "https://old:1")] };
		var page = Page(reg);

		await page.OnPostSaveAsync("home", "https://new:2", "ABCD", 1500, 50000, newKey: "");

		reg.Endpoints.Should().ContainSingle();
		reg.Endpoints[0].BaseUrl.Should().Be("https://new:2");
		reg.Endpoints[0].CertThumbprint.Should().Be("ABCD");
		reg.LastApiKeys.Should().BeEmpty("blank key must not overwrite the existing secret");
	}

	[Fact]
	public async Task Save_blank_name_shows_error_no_write()
	{
		var reg = new FakeEditor();
		var page = Page(reg);

		var result = await page.OnPostSaveAsync("  ", "https://h", null, 2000, 60000, null);

		result.Should().BeOfType<PageResult>();
		page.Error.Should().NotBeNullOrEmpty();
		reg.SaveCalls.Should().Be(0);
	}

	[Fact]
	public async Task Save_surfaces_validation_error()
	{
		var reg = new FakeEditor { ThrowOnSave = new ValidationException([new ValidationFailure("BaseUrl", "bad url")]) };
		var page = Page(reg);

		var result = await page.OnPostSaveAsync("home", "not-a-url", null, 2000, 60000, null);

		result.Should().BeOfType<PageResult>();
		page.Error.Should().Contain("bad url");
	}

	[Fact]
	public async Task Delete_removes_endpoint()
	{
		var reg = new FakeEditor { Endpoints = [new LlmEndpoint("home", "https://h"), new LlmEndpoint("cloud", "https://c")] };
		var page = Page(reg);

		await page.OnPostDeleteAsync("home");

		reg.Endpoints.Should().ContainSingle(e => e.Name == "cloud");
	}

	[Fact]
	public async Task SaveRoute_without_id_appends_route_then_redirects()
	{
		var reg = new FakeEditor { Endpoints = [new LlmEndpoint("deepseek", "https://d")] };
		var page = Page(reg);

		var result = await page.OnPostSaveRouteAsync(LlmCapability.Chat, "deepseek", "deepseek-chat", 50, tier: null, thinking: null, routeId: null);

		result.Should().BeOfType<RedirectToPageResult>();
		reg.Routes.Should().ContainSingle(r =>
			r.Route.Capability == LlmCapability.Chat && r.Route.Endpoint == "deepseek"
			&& r.Route.Model == "deepseek-chat" && r.Route.Priority == 50);
	}

	[Fact]
	public async Task SaveRoute_with_id_replaces_that_row_in_place()
	{
		var reg = new FakeEditor
		{
			Endpoints = [new LlmEndpoint("home", "https://h"), new LlmEndpoint("cloud", "https://c")],
			Routes = [new IdentifiedRoute("r1", new LlmRoute(LlmCapability.Chat, "home", "old-model", 100))],
		};
		var page = Page(reg);

		await page.OnPostSaveRouteAsync(LlmCapability.Chat, "cloud", "new-model", 10, tier: "fast", thinking: "disabled", routeId: "r1");

		reg.Routes.Should().ContainSingle();
		reg.Routes[0].Id.Should().Be("r1", "an edit keeps the row's identity");
		reg.Routes[0].Route.Endpoint.Should().Be("cloud");
		reg.Routes[0].Route.Model.Should().Be("new-model");
		reg.Routes[0].Route.Priority.Should().Be(10);
		reg.Routes[0].Route.Tier.Should().Be("fast");
		reg.Routes[0].Route.Thinking.Should().Be(LlmThinking.Disabled);
	}

	// THE POSITION BUG, at page level. The row the user edited is `b`; between render and submit the
	// stored list was re-ordered, so `b` is no longer where it was. Index addressing would have
	// rewritten whatever now sits at that index (`a`). Id addressing follows the row.
	[Fact]
	public async Task SaveRoute_after_a_reorder_still_hits_the_same_row()
	{
		var reg = new FakeEditor
		{
			Endpoints = [new LlmEndpoint("home", "https://h")],
			Routes =
			[
				new IdentifiedRoute("id-b", new LlmRoute(LlmCapability.Embed, "home", "b-model", 200)),
				new IdentifiedRoute("id-a", new LlmRoute(LlmCapability.Chat, "home", "a-model", 100)),
			],
		};
		var page = Page(reg);

		// The form was rendered when "id-b" was at index 1; it is now at index 0.
		await page.OnPostSaveRouteAsync(LlmCapability.Embed, "home", "b-model-v2", 200, null, null, routeId: "id-b");

		reg.Routes.Single(r => r.Id == "id-b").Route.Model.Should().Be("b-model-v2");
		reg.Routes.Single(r => r.Id == "id-a").Route.Model.Should().Be("a-model", "the neighbouring row must be untouched");
	}

	[Fact]
	public async Task SaveRoute_with_an_unknown_id_is_an_error_not_an_append()
	{
		var reg = new FakeEditor { Endpoints = [new LlmEndpoint("home", "https://h")] };
		var page = Page(reg);

		var result = await page.OnPostSaveRouteAsync(LlmCapability.Chat, "home", "m", 100, null, null, routeId: "gone");

		result.Should().BeOfType<PageResult>();
		page.Error.Should().Contain("no longer exists");
		reg.SaveCalls.Should().Be(0);
	}

	[Fact]
	public async Task SaveRoute_blank_endpoint_shows_error_no_write()
	{
		var reg = new FakeEditor();
		var page = Page(reg);

		var result = await page.OnPostSaveRouteAsync(LlmCapability.Chat, "  ", "m", 100, null, null, null);

		result.Should().BeOfType<PageResult>();
		page.Error.Should().NotBeNullOrEmpty();
		reg.SaveCalls.Should().Be(0);
	}

	[Fact]
	public async Task SaveRoute_surfaces_validation_error_for_unknown_endpoint()
	{
		var reg = new FakeEditor { ThrowOnSave = new ValidationException([new ValidationFailure("Routes", "unknown endpoint 'ghost'")]) };
		var page = Page(reg);

		var result = await page.OnPostSaveRouteAsync(LlmCapability.Chat, "ghost", "m", 100, null, null, null);

		result.Should().BeOfType<PageResult>();
		page.Error.Should().Contain("unknown endpoint");
	}

	[Fact]
	public async Task DeleteRoute_removes_by_id()
	{
		var reg = new FakeEditor
		{
			Endpoints = [new LlmEndpoint("home", "https://h")],
			Routes =
			[
				new IdentifiedRoute("id-a", new LlmRoute(LlmCapability.Chat, "home", "a")),
				new IdentifiedRoute("id-b", new LlmRoute(LlmCapability.Embed, "home", "b")),
			],
		};
		var page = Page(reg);

		await page.OnPostDeleteRouteAsync("id-a");

		reg.Routes.Should().ContainSingle(r => r.Route.Model == "b");
	}

	[Fact]
	public async Task DeleteRoute_with_an_unknown_id_is_an_error_not_a_silent_no_op()
	{
		var reg = new FakeEditor { Endpoints = [new LlmEndpoint("home", "https://h")] };
		var page = Page(reg);

		var result = await page.OnPostDeleteRouteAsync("gone");

		result.Should().BeOfType<PageResult>();
		page.Error.Should().Contain("no longer exists");
		reg.SaveCalls.Should().Be(0);
	}

	// An inherited registry is READ-ONLY: the page renders no controls, and a POST that arrives
	// anyway (stale form, hand-made request) is refused rather than half-forking the level.
	[Fact]
	public async Task Get_marks_an_inherited_registry_as_read_only()
	{
		var reg = new FakeEditor
		{
			Inherited = true,
			Endpoints = [new LlmEndpoint("home", "https://h")],
			Routes = [new IdentifiedRoute("id-a", new LlmRoute(LlmCapability.Chat, "home", "a"))],
		};
		var page = Page(reg);

		await page.OnGetAsync();

		page.Inherited.Should().BeTrue();
		page.InheritedFrom.Should().Be("System:$");
		page.Routes.Should().ContainSingle();
	}

	[Theory]
	[InlineData("endpoint")]
	[InlineData("route")]
	[InlineData("delete-route")]
	[InlineData("delete-endpoint")]
	public async Task Writes_are_refused_while_the_registry_is_inherited(string what)
	{
		var reg = new FakeEditor
		{
			Inherited = true,
			Endpoints = [new LlmEndpoint("home", "https://h")],
			Routes = [new IdentifiedRoute("id-a", new LlmRoute(LlmCapability.Chat, "home", "a"))],
		};
		var page = Page(reg);

		var result = what switch
		{
			"endpoint" => await page.OnPostSaveAsync("home", "https://evil", null, 2000, 60000, null),
			"route" => await page.OnPostSaveRouteAsync(LlmCapability.Chat, "home", "m", 100, null, null, "id-a"),
			"delete-route" => await page.OnPostDeleteRouteAsync("id-a"),
			_ => await page.OnPostDeleteAsync("home"),
		};

		result.Should().BeOfType<PageResult>();
		page.Error.Should().Contain("inherited");
		reg.SaveCalls.Should().Be(0, "a partial edit of an inherited level must never be written");
	}

	sealed class FakeEditor : ILlmRegistryEditor
	{
		public IReadOnlyList<LlmEndpoint> Endpoints { get; set; } = [];
		public IReadOnlyList<IdentifiedRoute> Routes { get; set; } = [];
		public bool Inherited { get; set; }
		public IReadOnlyDictionary<string, string> LastApiKeys { get; private set; } = new Dictionary<string, string>();
		public int SaveCalls { get; private set; }
		public Exception? ThrowOnSave { get; set; }

		public Task<LlmRegistry> GetAsync(string projectKey, CancellationToken ct = default) =>
			Task.FromResult(new LlmRegistry(Endpoints, Routes.Select(r => r.Route).ToList()));

		public Task<LlmRegistryView> ViewAsync(string projectKey, CancellationToken ct = default) =>
			Task.FromResult(new LlmRegistryView(
				"Workspace:ws", Inherited, Inherited ? "System:$" : null, Endpoints, Routes));

		public Task SetAsync(string projectKey, LlmRegistry registry, IReadOnlyDictionary<string, string> apiKeys, CancellationToken ct = default) =>
			SaveAsync(projectKey, registry.Endpoints,
				registry.Routes.Select(r => new IdentifiedRoute(string.Empty, r)).ToList(), apiKeys, ct);

		public Task SaveAsync(
			string projectKey, IReadOnlyList<LlmEndpoint> endpoints, IReadOnlyList<IdentifiedRoute> routes,
			IReadOnlyDictionary<string, string> apiKeys, CancellationToken ct = default)
		{
			SaveCalls++;
			if (ThrowOnSave is not null) throw ThrowOnSave;
			Endpoints = endpoints;
			Routes = routes;
			LastApiKeys = apiKeys;
			return Task.CompletedTask;
		}
	}
}
