using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PetBox.Core.Settings;
using PetBox.Web.Settings;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceAdmin")]
public sealed class WorkspaceDefaultsModel : SettingsScopePageModel
{
	public WorkspaceDefaultsModel(ISettingsResolver resolver) : base(resolver) { }

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	protected override Scope Scope => Scope.Workspace;
	protected override string ScopeKey => WorkspaceKey;

	// Any record with at least one property TopLevel >= Workspace appears (System-only props are
	// hidden by the form renderer at workspace scope).
	protected override IReadOnlyList<Type> Records => [typeof(LogSettings)];
}
