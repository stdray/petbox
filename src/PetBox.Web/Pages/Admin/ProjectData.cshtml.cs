using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Data.Contract;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Admin;

// Two-level navigation: this page lists DataDbs for a project. Detail page
// for an individual DataDb (table introspection + paste-migration) lives in
// ProjectDataDetail.cshtml.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectDataModel : PageModel
{
	readonly IProjectDirectory _projects;
	readonly FeatureFlags _features;
	readonly IDataDbCatalog _catalog;

	public ProjectDataModel(IProjectDirectory projects, FeatureFlags features, IDataDbCatalog catalog)
	{
		_projects = projects;
		_features = features;
		_catalog = catalog;
	}

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public List<DataDbInfo> DataDbs { get; private set; } = [];
	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync()
	{
		if (!_features.IsEnabled(Feature.Data))
			return NotFound();

		var project = await _projects.GetAsync(ProjectKey);
		if (project is null) { ProjectNotFound = true; return Page(); }

		DataDbs = [.. await _catalog.ListAsync(ProjectKey)];
		return Page();
	}

	public async Task<IActionResult> OnPostCreateAsync(string name, string? description, long? maxPageCount)
	{
		if (!_features.IsEnabled(Feature.Data)) return NotFound();
		if (string.IsNullOrWhiteSpace(name))
		{
			ErrorMessage = "Name is required.";
			await OnGetAsync();
			return Page();
		}

		DataDbChangeResult result;
		try
		{
			// The file first, then the row — the catalog's CreateAsync order (see DataDbCatalog); a
			// throw from the file create (e.g. disk/permission failure) is reported the same way this
			// page always reported it, rather than 500ing.
			result = await _catalog.CreateAsync(ProjectKey, name, description, maxPageCount);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			ErrorMessage = "Failed to create DataDb file: " + ex.Message;
			await OnGetAsync();
			return Page();
		}

		switch (result)
		{
			case DataDbChangeResult.Created:
				return RedirectToPage();
			case DataDbChangeResult.Conflict conflict:
				ErrorMessage = conflict.Reason;
				break;
			case DataDbChangeResult.Refused refused:
				ErrorMessage = refused.Reason;
				break;
			default:
				ErrorMessage = "Failed to create DataDb.";
				break;
		}

		await OnGetAsync();
		return Page();
	}

	public async Task<IActionResult> OnPostDeleteAsync(string name)
	{
		if (!_features.IsEnabled(Feature.Data)) return NotFound();

		await _catalog.DeleteAsync(ProjectKey, name);
		this.NotifySuccess($"DataDb '{name}' deleted.");
		return RedirectToPage();
	}
}
