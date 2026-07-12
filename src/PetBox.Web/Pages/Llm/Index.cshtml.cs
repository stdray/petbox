using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.LlmRouter.Contract;

namespace PetBox.Web.Pages.Llm;

// Admin page for the LLM router registry (spec llm-admin-ui + llm-routes-ui): provider endpoints
// (baseUrl, cert-pin, timeouts) + their api keys, and the routes (capability→endpoint→model chains).
//
// It edits through ILlmRegistryEditor — the LEVELLED registry in core.db, which is what the router
// actually resolves. It used to edit ILlmRegistryAdmin (the ConfigBindings store), which the router
// stopped reading at the flip: the page said "Saved." and the routing did not change. That is the
// bug this page's rewrite exists to close.
//
// Two other things changed with it:
//   * ROWS HAVE IDENTITY. A route is addressed by its row id, never by its index in the list. The
//     old `routes[i] = route` meant a concurrent save (or any re-ordering) landed the edit on
//     whichever route happened to sit at that position — a different route than the one on screen.
//   * INHERITED IS READ-ONLY. When a workspace declares no registry of its own it is served by the
//     level above, and those rows are shown with an `inherited` badge and no controls. Editing one
//     would create a workspace level holding just that row — and since a level resolves WHOLE, it
//     would instantly shadow the entire inherited registry (routes and api keys with it). The only
//     safe move is to copy the level whole ("override"), which is not built yet.
//
// Keys stay WRITE-ONLY: the view never returns them, so the form only offers set/replace (blank =
// keep). Depends only on PetBox.LlmRouter.Contract (LlmRouterBoundaryTests).
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class IndexModel : PageModel
{
	readonly ILlmRegistryEditor _registry;
	readonly FeatureFlags _features;
	readonly PetBoxDb _db;

	public IndexModel(ILlmRegistryEditor registry, FeatureFlags features, PetBoxDb db)
	{
		_registry = registry;
		_features = features;
		_db = db;
	}

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")] public string WorkspaceKey { get; set; } = string.Empty;
	[FromRoute(Name = "projectKey")] public string ProjectKey { get; set; } = string.Empty;

	public IReadOnlyList<LlmEndpoint> Endpoints { get; private set; } = [];
	public IReadOnlyList<IdentifiedRoute> Routes { get; private set; } = [];
	public string Level { get; private set; } = string.Empty;
	public bool Inherited { get; private set; }
	public string? InheritedFrom { get; private set; }
	public bool ProjectNotFound { get; private set; }
	public string? Error { get; private set; }
	public bool Saved { get; private set; }

	public async Task<IActionResult> OnGetAsync(bool saved = false, CancellationToken ct = default)
	{
		if (!_features.IsEnabled(Feature.LlmRouter)) return NotFound();
		if (!await ProjectExistsAsync(ct)) { ProjectNotFound = true; return Page(); }
		Saved = saved;
		await LoadAsync(ct);
		return Page();
	}

	// Add a new endpoint or update an existing one (matched by Name — the endpoint's PK). A non-empty
	// `newKey` sets/replaces that endpoint's api key; blank keeps the existing one. Routes unchanged.
	public async Task<IActionResult> OnPostSaveAsync(
		string name, [FromForm(Name = "baseUrl")] string baseAddress, string? certThumbprint,
		int connectTimeoutMs, int requestTimeoutMs, string? newKey, CancellationToken ct = default)
	{
		if (!_features.IsEnabled(Feature.LlmRouter)) return NotFound();
		if (!await ProjectExistsAsync(ct)) { ProjectNotFound = true; return Page(); }

		if (string.IsNullOrWhiteSpace(name))
			return await FailAsync("endpoint name is required", ct);

		var view = await _registry.ViewAsync(ProjectKey, ct);
		if (view.Inherited) return await InheritedRefusalAsync(view, ct);

		var endpoint = new LlmEndpoint(
			name.Trim(), (baseAddress ?? string.Empty).Trim(),
			string.IsNullOrWhiteSpace(certThumbprint) ? null : certThumbprint.Trim(),
			connectTimeoutMs <= 0 ? 2000 : connectTimeoutMs,
			requestTimeoutMs <= 0 ? 60000 : requestTimeoutMs);

		// Replace-or-append by name.
		var endpoints = view.Endpoints.Where(e => !string.Equals(e.Name, endpoint.Name, StringComparison.Ordinal)).ToList();
		endpoints.Add(endpoint);

		var apiKeys = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!string.IsNullOrWhiteSpace(newKey)) apiKeys[endpoint.Name] = newKey.Trim();

		return await SaveAsync(endpoints, view.Routes, apiKeys, ct);
	}

	// Remove an endpoint by name. Rejected by validation if a route still references it.
	public async Task<IActionResult> OnPostDeleteAsync(string name, CancellationToken ct = default)
	{
		if (!_features.IsEnabled(Feature.LlmRouter)) return NotFound();
		if (!await ProjectExistsAsync(ct)) { ProjectNotFound = true; return Page(); }

		var view = await _registry.ViewAsync(ProjectKey, ct);
		if (view.Inherited) return await InheritedRefusalAsync(view, ct);

		var endpoints = view.Endpoints.Where(e => !string.Equals(e.Name, name, StringComparison.Ordinal)).ToList();
		return await SaveAsync(endpoints, view.Routes, NoKeys, ct);
	}

	// Add a route, or — when `routeId` names an existing row — replace THAT row, wherever it now sits
	// in the list. A `routeId` that no longer exists is an error, not an append: the row was deleted
	// or replaced by someone else, and silently re-creating it is how a deleted route comes back.
	public async Task<IActionResult> OnPostSaveRouteAsync(
		LlmCapability capability, string endpoint, string model, int priority, string? tier,
		string? thinking, string? routeId, CancellationToken ct = default)
	{
		if (!_features.IsEnabled(Feature.LlmRouter)) return NotFound();
		if (!await ProjectExistsAsync(ct)) { ProjectNotFound = true; return Page(); }

		if (string.IsNullOrWhiteSpace(endpoint)) return await FailAsync("route endpoint is required", ct);
		if (string.IsNullOrWhiteSpace(model)) return await FailAsync("route model is required", ct);

		var view = await _registry.ViewAsync(ProjectKey, ct);
		if (view.Inherited) return await InheritedRefusalAsync(view, ct);

		var route = new LlmRoute(
			capability, endpoint.Trim(), model.Trim(),
			priority <= 0 ? 100 : priority,
			string.IsNullOrWhiteSpace(tier) ? null : tier.Trim(),
			Enum.TryParse<LlmThinking>(thinking, ignoreCase: true, out var th) ? th : null);

		var routes = view.Routes.ToList();
		if (string.IsNullOrWhiteSpace(routeId))
		{
			routes.Add(new IdentifiedRoute(string.Empty, route)); // append — the store mints the id
		}
		else
		{
			var at = routes.FindIndex(r => string.Equals(r.Id, routeId, StringComparison.Ordinal));
			if (at < 0) return await StaleRowAsync(ct);
			routes[at] = new IdentifiedRoute(routeId, route);
		}

		return await SaveAsync(view.Endpoints, routes, NoKeys, ct);
	}

	// Remove a route by its row id. A row id that is gone means somebody already removed it — say so
	// rather than quietly reporting success for a delete that deleted nothing.
	public async Task<IActionResult> OnPostDeleteRouteAsync(string routeId, CancellationToken ct = default)
	{
		if (!_features.IsEnabled(Feature.LlmRouter)) return NotFound();
		if (!await ProjectExistsAsync(ct)) { ProjectNotFound = true; return Page(); }

		var view = await _registry.ViewAsync(ProjectKey, ct);
		if (view.Inherited) return await InheritedRefusalAsync(view, ct);

		var routes = view.Routes.Where(r => !string.Equals(r.Id, routeId, StringComparison.Ordinal)).ToList();
		if (routes.Count == view.Routes.Count) return await StaleRowAsync(ct);

		return await SaveAsync(view.Endpoints, routes, NoKeys, ct);
	}

	// No key changes — endpoints absent from the map keep their existing secret (contract).
	static IReadOnlyDictionary<string, string> NoKeys => new Dictionary<string, string>(StringComparer.Ordinal);

	async Task<IActionResult> SaveAsync(
		IReadOnlyList<LlmEndpoint> endpoints, IReadOnlyList<IdentifiedRoute> routes,
		IReadOnlyDictionary<string, string> apiKeys, CancellationToken ct)
	{
		try
		{
			await _registry.SaveAsync(ProjectKey, endpoints, routes, apiKeys, ct);
		}
		catch (ValidationException ex)
		{
			return await FailAsync(string.Join("; ", ex.Errors.Select(e => e.ErrorMessage)), ct);
		}
		catch (InvalidOperationException ex)
		{
			return await FailAsync(ex.Message, ct);
		}
		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, saved = true });
	}

	// A POST that reached an inherited registry anyway (a stale form, or a hand-made request). The
	// page renders no controls in that state, so this is the belt: refuse, do not half-fork.
	Task<IActionResult> InheritedRefusalAsync(LlmRegistryView view, CancellationToken ct) =>
		FailAsync(
			$"this registry is inherited from {view.InheritedFrom} and cannot be edited here — "
			+ "a partial edit would replace the whole inherited registry with the one row you changed",
			ct);

	Task<IActionResult> StaleRowAsync(CancellationToken ct) =>
		FailAsync("that route no longer exists — it was changed or deleted since this page was loaded. Reload and try again.", ct);

	async Task<IActionResult> FailAsync(string error, CancellationToken ct)
	{
		Error = error;
		await LoadAsync(ct);
		return Page();
	}

	async Task LoadAsync(CancellationToken ct)
	{
		var view = await _registry.ViewAsync(ProjectKey, ct);
		Endpoints = view.Endpoints;
		Routes = view.Routes;
		Level = view.Level;
		Inherited = view.Inherited;
		InheritedFrom = view.InheritedFrom;
	}

	async Task<bool> ProjectExistsAsync(CancellationToken ct) =>
		await _db.Projects.AnyAsync(p => p.Key == ProjectKey, ct);
}
