using Microsoft.AspNetCore.Http;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Web.Navigation;
using PetBox.Web.Settings;

namespace PetBox.Tests.Settings;

// IUiState is the typed, memoized-per-request replacement for the untyped ViewData["UiState"]
// bag — see UiState.cs's doc comment. These tests exercise the wiring in isolation (a counting
// fake ISettingsResolver, no real DB), proving the three properties the maintainer asked for:
// lazy (no resolve until the first GetAsync), memoized (a second call does not re-hit
// ISettingsResolver), and anonymous-safe (cookie-branch only, never throws).
public sealed class UiStateTests
{
	// Counts GetAsync<T> calls so tests can assert "resolved exactly once" without a real DB.
	sealed class CountingSettingsResolver : ISettingsResolver
	{
		public int GetAsyncCallCount;

		public Task<T> GetAsync<T>(Scope deepestScope, string deepestScopeKey, CancellationToken ct = default)
			where T : new()
		{
			Interlocked.Increment(ref GetAsyncCallCount);
			return Task.FromResult(new T());
		}

		public Task SetAsync<T>(Scope scope, string scopeKey, T newValues, T oldValues, long? updatedBy, CancellationToken ct = default)
			where T : notnull => throw new NotSupportedException();

		public Task ResetAsync<T>(Scope scope, string scopeKey, string propertyName, CancellationToken ct = default)
			where T : notnull => throw new NotSupportedException();
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

	static (UiState Sut, CountingSettingsResolver Resolver) Make(bool authenticated, string? cookieValue = null)
	{
		var resolver = new CountingSettingsResolver();
		var nav = new FakeNav { IsAuthenticated = authenticated };
		var http = new DefaultHttpContext();
		if (authenticated)
		{
			http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
				[new System.Security.Claims.Claim(PetBox.Core.Auth.PetBoxClaims.UserId, "user-uistate-test")],
				authenticationType: "Test"));
		}
		if (cookieValue is not null)
		{
			http.Request.Headers.Append("Cookie", $"{UiStateResolver.CookieName}={Uri.EscapeDataString(cookieValue)}");
		}
		var accessor = new HttpContextAccessor { HttpContext = http };
		var sut = new UiState(nav, resolver, accessor);
		return (sut, resolver);
	}

	[Fact]
	public void GetAsync_NotCalledYet_DoesNotResolve()
	{
		var (_, resolver) = Make(authenticated: true);

		// Constructing the service must not itself trigger a resolve — only GetAsync does.
		resolver.GetAsyncCallCount.Should().Be(0);
	}

	[Fact]
	public async Task GetAsync_FirstCall_ResolvesExactlyOnce()
	{
		var (sut, resolver) = Make(authenticated: true);

		await sut.GetAsync();

		resolver.GetAsyncCallCount.Should().Be(1);
	}

	[Fact]
	public async Task GetAsync_SecondCall_IsMemoized_DoesNotReResolve()
	{
		var (sut, resolver) = Make(authenticated: true);

		var first = await sut.GetAsync();
		var second = await sut.GetAsync();

		resolver.GetAsyncCallCount.Should().Be(1);
		second.Should().Be(first);
	}

	[Fact]
	public async Task GetAsync_ConcurrentFirstCalls_ResolveExactlyOnce()
	{
		// Two callers (e.g. a layout and a partial it renders) asking "concurrently" before the
		// first resolve completes must share the same in-flight Task, not fire two resolves.
		var (sut, resolver) = Make(authenticated: true);

		var t1 = sut.GetAsync();
		var t2 = sut.GetAsync();
		await Task.WhenAll(t1, t2);

		resolver.GetAsyncCallCount.Should().Be(1);
	}

	[Fact]
	public async Task GetAsync_Anonymous_CookieBranchOnly_NoDbCall_NoException()
	{
		var cookie = System.Text.Json.JsonSerializer.Serialize(new { sidebarPinned = false });
		var (sut, resolver) = Make(authenticated: false, cookieValue: cookie);

		var result = await sut.GetAsync();

		result.SidebarPinned.Should().BeFalse();
		// Anonymous → UiStateResolver.ResolveAsync skips the DB branch entirely.
		resolver.GetAsyncCallCount.Should().Be(0);
	}

	[Fact]
	public async Task GetAsync_OutsideHttpRequest_Throws()
	{
		var resolver = new CountingSettingsResolver();
		var nav = new FakeNav { IsAuthenticated = false };
		var accessor = new HttpContextAccessor { HttpContext = null };
		var sut = new UiState(nav, resolver, accessor);

		var act = () => sut.GetAsync();

		await act.Should().ThrowAsync<InvalidOperationException>();
	}
}
