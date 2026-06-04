using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.LlmRouter.Contract;

namespace PetBox.Web.Pages.Llm;

// Workspace-scoped admin page for the LLM router registry (spec llm-admin-ui + llm-routes-ui):
// manage provider endpoints (baseUrl, cert-pin, timeouts) + their api keys AND the routes
// (capability→endpoint→model chains) over the neutral ILlmRegistryAdmin contract. Keys are
// WRITE-ONLY — GetAsync never returns them, so the form only offers set/replace (blank keeps the
// existing key). Routes have no id of their own, so edit/delete address a route by its row index
// in the stored list. Mirrors the Config admin page. Depends only on PetBox.LlmRouter.Contract.
[Authorize]
public sealed class IndexModel : PageModel
{
	readonly ILlmRegistryAdmin _registry;
	readonly FeatureFlags _features;
	readonly PetBoxDb _db;

	public IndexModel(ILlmRegistryAdmin registry, FeatureFlags features, PetBoxDb db)
	{
		_registry = registry;
		_features = features;
		_db = db;
	}

	[BindProperty(SupportsGet = true)] public string WorkspaceKey { get; set; } = string.Empty;
	[BindProperty(SupportsGet = true)] public string ProjectKey { get; set; } = string.Empty;

	public IReadOnlyList<LlmEndpoint> Endpoints { get; private set; } = [];
	public IReadOnlyList<LlmRoute> Routes { get; private set; } = [];
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

	// Add a new endpoint or update an existing one (matched by Name). A non-empty `newKey`
	// sets/replaces that endpoint's api key; blank keeps the existing one. Routes unchanged.
	public async Task<IActionResult> OnPostSaveAsync(
		string name, [FromForm(Name = "baseUrl")] string baseAddress, string? certThumbprint,
		int connectTimeoutMs, int requestTimeoutMs, string? newKey, CancellationToken ct = default)
	{
		if (!_features.IsEnabled(Feature.LlmRouter)) return NotFound();
		if (!await ProjectExistsAsync(ct)) { ProjectNotFound = true; return Page(); }

		if (string.IsNullOrWhiteSpace(name))
			return await FailAsync("endpoint name is required", ct);

		var existing = await _registry.GetAsync(ProjectKey, ct);
		var endpoint = new LlmEndpoint(
			name.Trim(), (baseAddress ?? string.Empty).Trim(),
			string.IsNullOrWhiteSpace(certThumbprint) ? null : certThumbprint.Trim(),
			connectTimeoutMs <= 0 ? 2000 : connectTimeoutMs,
			requestTimeoutMs <= 0 ? 60000 : requestTimeoutMs);

		// Replace-or-append by name.
		var endpoints = existing.Endpoints.Where(e => !string.Equals(e.Name, endpoint.Name, StringComparison.Ordinal)).ToList();
		endpoints.Add(endpoint);

		var apiKeys = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!string.IsNullOrWhiteSpace(newKey)) apiKeys[endpoint.Name] = newKey.Trim();

		return await SaveAsync(new LlmRegistry(endpoints, existing.Routes), apiKeys, ct);
	}

	// Remove an endpoint by name. Rejected by validation if a route still references it.
	public async Task<IActionResult> OnPostDeleteAsync(string name, CancellationToken ct = default)
	{
		if (!_features.IsEnabled(Feature.LlmRouter)) return NotFound();
		if (!await ProjectExistsAsync(ct)) { ProjectNotFound = true; return Page(); }

		var existing = await _registry.GetAsync(ProjectKey, ct);
		var endpoints = existing.Endpoints.Where(e => !string.Equals(e.Name, name, StringComparison.Ordinal)).ToList();
		return await SaveAsync(new LlmRegistry(endpoints, existing.Routes), NoKeys, ct);
	}

	// Add a route or, when `index` points at an existing row, replace it in place. The endpoint
	// must already exist — validation rejects a route to an unknown endpoint. Keys untouched
	// (empty apiKeys keeps every endpoint's existing secret).
	public async Task<IActionResult> OnPostSaveRouteAsync(
		LlmCapability capability, string endpoint, string model, int priority, string? tier,
		int? index, CancellationToken ct = default)
	{
		if (!_features.IsEnabled(Feature.LlmRouter)) return NotFound();
		if (!await ProjectExistsAsync(ct)) { ProjectNotFound = true; return Page(); }

		if (string.IsNullOrWhiteSpace(endpoint)) return await FailAsync("route endpoint is required", ct);
		if (string.IsNullOrWhiteSpace(model)) return await FailAsync("route model is required", ct);

		var existing = await _registry.GetAsync(ProjectKey, ct);
		var route = new LlmRoute(
			capability, endpoint.Trim(), model.Trim(),
			priority <= 0 ? 100 : priority,
			string.IsNullOrWhiteSpace(tier) ? null : tier.Trim());

		var routes = existing.Routes.ToList();
		if (index is { } i && i >= 0 && i < routes.Count) routes[i] = route; // edit in place
		else routes.Add(route);                                             // append

		return await SaveAsync(new LlmRegistry(existing.Endpoints, routes), NoKeys, ct);
	}

	// Remove a route by its row index in the stored list. Out-of-range index is a no-op save.
	public async Task<IActionResult> OnPostDeleteRouteAsync(int index, CancellationToken ct = default)
	{
		if (!_features.IsEnabled(Feature.LlmRouter)) return NotFound();
		if (!await ProjectExistsAsync(ct)) { ProjectNotFound = true; return Page(); }

		var existing = await _registry.GetAsync(ProjectKey, ct);
		var routes = existing.Routes.ToList();
		if (index >= 0 && index < routes.Count) routes.RemoveAt(index);
		return await SaveAsync(new LlmRegistry(existing.Endpoints, routes), NoKeys, ct);
	}

	// No key changes — endpoints absent from the map keep their existing secret (contract).
	static IReadOnlyDictionary<string, string> NoKeys => new Dictionary<string, string>(StringComparer.Ordinal);

	async Task<IActionResult> SaveAsync(LlmRegistry registry, IReadOnlyDictionary<string, string> apiKeys, CancellationToken ct)
	{
		try
		{
			await _registry.SetAsync(ProjectKey, registry, apiKeys, ct);
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

	async Task<IActionResult> FailAsync(string error, CancellationToken ct)
	{
		Error = error;
		await LoadAsync(ct);
		return Page();
	}

	async Task LoadAsync(CancellationToken ct)
	{
		var reg = await _registry.GetAsync(ProjectKey, ct);
		Endpoints = reg.Endpoints;
		Routes = reg.Routes;
	}

	async Task<bool> ProjectExistsAsync(CancellationToken ct) =>
		await _db.Projects.AnyAsync(p => p.Key == ProjectKey, ct);
}
