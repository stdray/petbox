using Microsoft.AspNetCore.Http;
using PetBox.Core.Settings;
using PetBox.Web.Navigation;

namespace PetBox.Web.Settings;

// Typed, scoped view-facing accessor for the combined DB+cookie UI-state record — the replacement
// for the untyped `ViewData["UiState"]` bag every layout used to populate by hand. Concrete
// BrowserState, not a generic `GetAsync<T>()`: the design deliberately has ONE combined record
// behind ONE cookie (see BrowserState.cs), so a generic accessor here would be speculative —
// UiStateResolver stays the generic/testable core, this is thin, concrete wiring on top of it.
//
// Inject directly into a view: `@inject PetBox.Web.Settings.IUiState UiState`, then
// `(await UiState.GetAsync()).SidebarPinned` — no cast, no `??` fallback duplicating the record's
// own default, and no dependence on some OTHER view/layout having remembered to populate anything:
// whichever view asks first triggers the resolve.
public interface IUiState
{
	// Resolves (lazily, memoized once per request) and returns the combined BrowserState record.
	// The first call in a request pays UiStateResolver's DB round-trip (ISettingsResolver); every
	// later call in the SAME request — from the same layout, a partial it renders, or another
	// layout's shared partial — reuses the memoized result instead of re-resolving.
	Task<BrowserState> GetAsync(CancellationToken ct = default);
}

// Scoped: one instance per HTTP request (the DI container creates a fresh scope per request), so
// the memoized field below is exactly request-lifetime — never stale across requests, never shared
// across concurrent requests. Deliberately NOT resolved from middleware: that would pay the DB cost
// on every request, including API and static-file hits that never touch a view and so never ask.
public sealed class UiState(INavigationContext nav, ISettingsResolver settings, IHttpContextAccessor httpContextAccessor) : IUiState
{
	// Memoization is a cached Task, not a cached value: the first GetAsync call stores the
	// in-flight Task itself, so a second call arriving before the first completes (e.g. two
	// partials awaited concurrently via Task.WhenAll from the same view) awaits that SAME task
	// rather than firing a second resolve — no lock needed, because a Razor page's request
	// pipeline runs this scope's code on one logical thread at a time (a scoped service is never
	// meant to be called concurrently from multiple threads; `??=` on a plain field is safe under
	// that single-threaded-per-scope assumption).
	Task<BrowserState>? _resolved;

	public Task<BrowserState> GetAsync(CancellationToken ct = default) => _resolved ??= ResolveAsync(ct);

	async Task<BrowserState> ResolveAsync(CancellationToken ct)
	{
		var http = httpContextAccessor.HttpContext
			?? throw new InvalidOperationException("IUiState.GetAsync() was called outside an HTTP request.");
		return await UiStateResolver.ResolveForCurrentUserAsync<BrowserState>(nav, settings, http, ct);
	}
}
