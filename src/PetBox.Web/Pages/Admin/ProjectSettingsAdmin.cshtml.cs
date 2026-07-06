using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PetBox.Core.Settings;
using PetBox.Web.Settings;

namespace PetBox.Web.Pages.Admin;

// Generic Project-scope settings page (Scope.Project) — mirrors SysDefaultsModel /
// WorkspaceDefaultsModel, one scope deeper. See Routes.ProjectSettingsAdmin for how this differs
// from the bespoke ProjectDetail ("/info") page, which stays the owner of RepoSettings and the
// log-retention override control.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectSettingsAdminModel : SettingsScopePageModel
{
	public ProjectSettingsAdminModel(ISettingsResolver resolver) : base(resolver) { }

	// authz-bypass-project-create: bound ONLY from the route — never Form/Query — so a POST
	// body field named "workspaceKey"/"projectKey" cannot retarget the write after the
	// WorkspaceAdmin policy has already checked the ROUTE workspace. ASP.NET's default composite
	// provider order is Form -> Route -> Query, which is exactly the hole [FromRoute] closes.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	protected override Scope Scope => Scope.Project;
	protected override string ScopeKey => ProjectKey;

	// Deliberately NOT RepoSettings — CommitUrlTemplate has its own bespoke control on
	// ProjectDetail.cshtml (project Info page); duplicating it here would give it two disagreeing
	// edit surfaces.
	protected override IReadOnlyList<Type> Records => [typeof(SessionFullScanSettings)];
}
