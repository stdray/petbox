using FluentValidation;
using FluentValidation.Results;
using LinqToDB;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.LlmRouter.Contract;
using PetBox.Web.Pages.Llm;

namespace PetBox.Tests.Llm;

// The LLM-router admin page (spec llm-admin-ui): add/update/delete provider endpoints over
// ILlmRegistryAdmin, surface validation errors, gate on Feature.LlmRouter. Keys are write-only.
[Collection("DataModule")]
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
		MigrationRunner.Run(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
	}

	public void Dispose()
	{
		_db.Dispose();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	static FeatureFlags Features(bool llmRouter)
	{
		var cfg = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:LlmRouter"] = llmRouter ? "true" : "false" })
			.Build();
		return new FeatureFlags(cfg);
	}

	IndexModel Page(FakeRegistry reg, bool feature = true) =>
		new(reg, Features(feature), _db) { WorkspaceKey = "ws", ProjectKey = Proj };

	[Fact]
	public async Task Feature_disabled_returns_NotFound()
	{
		var page = Page(new FakeRegistry(), feature: false);
		(await page.OnGetAsync()).Should().BeOfType<NotFoundResult>();
	}

	[Fact]
	public async Task Save_adds_endpoint_and_key_then_redirects()
	{
		var reg = new FakeRegistry();
		var page = Page(reg);

		var result = await page.OnPostSaveAsync("home", "https://h:1234", null, 2000, 60000, "sk-secret");

		result.Should().BeOfType<RedirectToPageResult>();
		reg.Current.Endpoints.Should().ContainSingle(e => e.Name == "home" && e.BaseUrl == "https://h:1234");
		reg.LastApiKeys.Should().ContainKey("home").WhoseValue.Should().Be("sk-secret");
	}

	[Fact]
	public async Task Save_updates_existing_endpoint_by_name_blank_key_keeps_secret()
	{
		var reg = new FakeRegistry { Current = new LlmRegistry([new LlmEndpoint("home", "https://old:1")], []) };
		var page = Page(reg);

		await page.OnPostSaveAsync("home", "https://new:2", "ABCD", 1500, 50000, newKey: "");

		reg.Current.Endpoints.Should().ContainSingle();
		reg.Current.Endpoints[0].BaseUrl.Should().Be("https://new:2");
		reg.Current.Endpoints[0].CertThumbprint.Should().Be("ABCD");
		reg.LastApiKeys.Should().BeEmpty("blank key must not overwrite the existing secret");
	}

	[Fact]
	public async Task Save_blank_name_shows_error_no_write()
	{
		var reg = new FakeRegistry();
		var page = Page(reg);

		var result = await page.OnPostSaveAsync("  ", "https://h", null, 2000, 60000, null);

		result.Should().BeOfType<PageResult>();
		page.Error.Should().NotBeNullOrEmpty();
		reg.SetCalls.Should().Be(0);
	}

	[Fact]
	public async Task Save_surfaces_validation_error()
	{
		var reg = new FakeRegistry { ThrowOnSet = new ValidationException([new ValidationFailure("BaseUrl", "bad url")]) };
		var page = Page(reg);

		var result = await page.OnPostSaveAsync("home", "not-a-url", null, 2000, 60000, null);

		result.Should().BeOfType<PageResult>();
		page.Error.Should().Contain("bad url");
	}

	[Fact]
	public async Task Delete_removes_endpoint()
	{
		var reg = new FakeRegistry
		{
			Current = new LlmRegistry([new LlmEndpoint("home", "https://h"), new LlmEndpoint("cloud", "https://c")], []),
		};
		var page = Page(reg);

		await page.OnPostDeleteAsync("home");

		reg.Current.Endpoints.Should().ContainSingle(e => e.Name == "cloud");
	}

	sealed class FakeRegistry : ILlmRegistryAdmin
	{
		public LlmRegistry Current { get; set; } = LlmRegistry.Empty;
		public IReadOnlyDictionary<string, string> LastApiKeys { get; private set; } = new Dictionary<string, string>();
		public int SetCalls { get; private set; }
		public Exception? ThrowOnSet { get; set; }

		public Task<LlmRegistry> GetAsync(string projectKey, CancellationToken ct = default) => Task.FromResult(Current);

		public Task SetAsync(string projectKey, LlmRegistry registry, IReadOnlyDictionary<string, string> apiKeys, CancellationToken ct = default)
		{
			SetCalls++;
			if (ThrowOnSet is not null) throw ThrowOnSet;
			Current = registry;
			LastApiKeys = apiKeys;
			return Task.CompletedTask;
		}
	}
}
