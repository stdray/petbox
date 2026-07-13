using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Web.Navigation;
using PetBox.Web.Settings;

namespace PetBox.Tests.Settings;

// UiStateResolver combines the DB branch ([Setting], Scope.User) and the cookie branch
// ([BrowserState], the single `petbox.ui` cookie) into one typed record — see BrowserState.cs's
// doc comment for the storage-boundary rationale. Shares SettingsResolverFixture's
// WebApplicationFactory with SettingsResolverTests (same DB-backed ISettingsResolver; a fresh
// DI scope per test keeps the cases independent by using distinct scope keys).
public sealed class UiStateResolverTests : IClassFixture<SettingsResolverFixture>
{
	readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

	public UiStateResolverTests(SettingsResolverFixture fx) => _factory = fx.Factory;

	// Test-only combined record mixing BOTH branches on one type — exactly the shape a real
	// BrowserState.cs will take once a follow-up adds its first property.
	public sealed record TestUiState
	{
		[Setting(TopLevel = Scope.User, Key = "test.uistate.notifyByEmail")]
		public bool NotifyByEmail { get; init; } = true;

		[BrowserState(Key = "sidebarPinned")]
		public bool SidebarPinned { get; init; } = true;

		[BrowserState(Key = "kqlPanelPinned")]
		public bool KqlPanelPinned { get; init; }
	}

	sealed class FakeNav : INavigationContext
	{
		public required bool IsAuthenticated { get; init; }
		public string? Username => null;
		public string CurrentWorkspaceKey => "$system";
		public string? CurrentProjectKey => null;
		public IReadOnlyList<WorkspaceOption> AvailableWorkspaces => [];
		public IReadOnlyList<Project> ProjectsInCurrentWorkspace => [];
		public IReadOnlyDictionary<string, IReadOnlyList<Project>> ProjectsByWorkspace => new Dictionary<string, IReadOnlyList<Project>>();
		public bool DataEnabled => false;
		public bool TasksEnabled => false;
		public bool MemoryEnabled => false;
		public bool LlmRouterEnabled => false;
	}

	(ISettingsResolver Resolver, PetBoxDb Db) GetResolverAndDb()
	{
		var scope = _factory.Services.CreateScope();
		// Not `using`/disposed here — same rationale as SettingsResolverTests.GetResolverAsync: the
		// connection and the DI scope outlive this method and are used by the caller afterwards.
		var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		var resolver = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
		return (resolver, db);
	}

	// --- ResolveAsync (pure core: no HttpContext/INavigationContext) ---

	[Fact]
	public async Task ResolveAsync_NoUserId_NoCookie_ReturnsRecordDefaults()
	{
		var (resolver, _) = GetResolverAndDb();

		var result = await UiStateResolver.ResolveAsync<TestUiState>(resolver, userId: null, cookieValue: null);

		result.NotifyByEmail.Should().BeTrue();
		result.SidebarPinned.Should().BeTrue();
		result.KqlPanelPinned.Should().BeFalse();
	}

	[Fact]
	public async Task ResolveAsync_Anonymous_AppliesCookieBranch_SkipsDbBranch_NoException()
	{
		var (resolver, _) = GetResolverAndDb();
		var cookie = JsonSerializer.Serialize(new { sidebarPinned = false, kqlPanelPinned = true });

		var result = await UiStateResolver.ResolveAsync<TestUiState>(resolver, userId: null, cookieValue: cookie);

		result.SidebarPinned.Should().BeFalse();
		result.KqlPanelPinned.Should().BeTrue();
		// No user id → the DB branch is never attempted; the [Setting] property stays at its default.
		result.NotifyByEmail.Should().BeTrue();
	}

	[Fact]
	public async Task ResolveAsync_MalformedCookie_FallsBackToDefaults_NoException()
	{
		var (resolver, _) = GetResolverAndDb();

		var result = await UiStateResolver.ResolveAsync<TestUiState>(resolver, userId: null, cookieValue: "not json{{{");

		result.SidebarPinned.Should().BeTrue();
		result.KqlPanelPinned.Should().BeFalse();
	}

	[Fact]
	public async Task ResolveAsync_AuthenticatedUser_CombinesDbAndCookieBranchesInOneRecord()
	{
		var (resolver, db) = GetResolverAndDb();
		await db.InsertAsync(new Setting
		{
			Scope = "User",
			ScopeKey = "user-uistate-combo",
			Path = "test.uistate.notifyByEmail",
			Type = "bool",
			Value = "false",
			UpdatedAt = DateTime.UtcNow,
		});
		var cookie = JsonSerializer.Serialize(new { sidebarPinned = false });

		var result = await UiStateResolver.ResolveAsync<TestUiState>(resolver, userId: "user-uistate-combo", cookieValue: cookie);

		result.NotifyByEmail.Should().BeFalse(); // DB branch
		result.SidebarPinned.Should().BeFalse(); // cookie branch
		result.KqlPanelPinned.Should().BeFalse(); // untouched default
	}

	// --- ResolveForCurrentUserAsync (ASP.NET wiring) ---

	[Fact]
	public async Task ResolveForCurrentUserAsync_AnonymousRequest_CookieBranchOnly_NoException()
	{
		var (resolver, _) = GetResolverAndDb();
		var http = new DefaultHttpContext();
		var cookieJson = JsonSerializer.Serialize(new { sidebarPinned = false });
		http.Request.Headers.Append("Cookie", $"{UiStateResolver.CookieName}={Uri.EscapeDataString(cookieJson)}");
		var nav = new FakeNav { IsAuthenticated = false };

		var result = await UiStateResolver.ResolveForCurrentUserAsync<TestUiState>(nav, resolver, http);

		result.SidebarPinned.Should().BeFalse();
		result.NotifyByEmail.Should().BeTrue(); // anonymous → DB branch skipped
	}

	// --- ApplyBrowserState (pure) ---

	[Fact]
	public void ApplyBrowserState_UnknownCookieKeys_AreIgnored()
	{
		var cookie = JsonSerializer.Serialize(new { somethingUnrelated = 1, sidebarPinned = false });

		var result = UiStateResolver.ApplyBrowserState(new TestUiState(), cookie);

		result.SidebarPinned.Should().BeFalse();
	}

	[Fact]
	public void ApplyBrowserState_WrongTypeForOneKey_SkipsOnlyThatProperty()
	{
		// sidebarPinned is bool; feed it a string. That key must fail to apply on its own, without
		// aborting the rest of the cookie.
		const string cookie = """{"sidebarPinned":"not-a-bool","kqlPanelPinned":true}""";

		var result = UiStateResolver.ApplyBrowserState(new TestUiState(), cookie);

		result.SidebarPinned.Should().BeTrue(); // default retained
		result.KqlPanelPinned.Should().BeTrue(); // still applied
	}

	// --- MergeCookieValue (pure) — proves "one cookie, not N" ---

	[Fact]
	public void MergeCookieValue_PreservesKeysOwnedByOtherRecordTypes()
	{
		// A key that belongs to some OTHER [BrowserState] record already in the cookie must survive
		// merging in TestUiState's own values.
		const string existing = """{"someOtherFeatureKey":"keep-me"}""";

		var merged = UiStateResolver.MergeCookieValue(existing, new TestUiState { SidebarPinned = false });

		var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(merged)!;
		obj["someOtherFeatureKey"].GetString().Should().Be("keep-me");
		obj["sidebarPinned"].GetBoolean().Should().BeFalse();
		obj["kqlPanelPinned"].GetBoolean().Should().BeFalse();
	}

	[Fact]
	public void MergeCookieValue_MalformedExistingCookie_TreatedAsEmpty()
	{
		var merged = UiStateResolver.MergeCookieValue("garbage{{{", new TestUiState());

		var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(merged)!;
		obj.Should().ContainKey("sidebarPinned");
	}

	[Fact]
	public void RoundTrip_MergeThenApply_IsStable()
	{
		var cookie = UiStateResolver.MergeCookieValue(null, new TestUiState { SidebarPinned = false, KqlPanelPinned = true });

		var resolved = UiStateResolver.ApplyBrowserState(new TestUiState(), cookie);

		resolved.SidebarPinned.Should().BeFalse();
		resolved.KqlPanelPinned.Should().BeTrue();
	}
}
