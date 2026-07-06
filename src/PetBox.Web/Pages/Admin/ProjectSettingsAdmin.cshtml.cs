using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Settings;
using PetBox.Web.Settings;

namespace PetBox.Web.Pages.Admin;

// Generic Project-scope settings page (Scope.Project) — mirrors SysDefaultsModel /
// WorkspaceDefaultsModel, one scope deeper. See Routes.ProjectSettingsAdmin for how this differs
// from the bespoke ProjectDetail ("/info") page, which stays the owner of RepoSettings and the
// log-retention override control.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectSettingsAdminModel : PageModel
{
	readonly ISettingsResolver _resolver;

	public ProjectSettingsAdminModel(ISettingsResolver resolver) => _resolver = resolver;

	// authz-bypass-project-create: bound ONLY from the route — never Form/Query — so a POST
	// body field named "workspaceKey"/"projectKey" cannot retarget the write after the
	// WorkspaceAdmin policy has already checked the ROUTE workspace. ASP.NET's default composite
	// provider order is Form -> Route -> Query, which is exactly the hole [FromRoute] closes.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	// Records exposed on a project's generic Settings page. Deliberately NOT RepoSettings —
	// CommitUrlTemplate has its own bespoke control on ProjectDetail.cshtml (project Info page);
	// duplicating it here would give it two disagreeing edit surfaces.
	static readonly Type[] ProjectSettingRecords =
	[
		typeof(SessionFullScanSettings),
	];

	public IReadOnlyList<RecordSection> Sections { get; private set; } = [];
	public string? SuccessMessage { get; set; }
	public string? ErrorMessage { get; set; }

	public sealed record RecordSection(Type RecordType, object Current);

	public async Task OnGetAsync()
	{
		Sections = await LoadSectionsAsync();
	}

	public async Task<IActionResult> OnPostSaveAsync(string recordType)
	{
		var type = ProjectSettingRecords.FirstOrDefault(t => t.Name == recordType);
		if (type is null)
		{
			ErrorMessage = $"Unknown settings record: {recordType}.";
			Sections = await LoadSectionsAsync();
			return Page();
		}

		var current = await ResolveAsync(type);
		var updated = InvokeBindAndSet(type, current);

		var userIdRaw = User.FindFirst(PetBoxClaims.UserId)?.Value;
		long? userId = long.TryParse(userIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;

		await SaveDynamicallyAsync(type, current, updated, userId);

		SuccessMessage = $"Saved {type.Name}.";
		Sections = await LoadSectionsAsync();
		return Page();
	}

	async Task<IReadOnlyList<RecordSection>> LoadSectionsAsync()
	{
		var sections = new List<RecordSection>();
		foreach (var type in ProjectSettingRecords)
		{
			var hasProjectProp = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Select(p => p.GetCustomAttribute<SettingAttribute>())
				.Any(a => a is not null && (int)a.TopLevel >= (int)Scope.Project);
			if (!hasProjectProp) continue;

			var current = await ResolveAsync(type);
			sections.Add(new RecordSection(type, current));
		}
		return sections;
	}

	async Task<object> ResolveAsync(Type type)
	{
		var method = typeof(ISettingsResolver).GetMethod(nameof(ISettingsResolver.GetAsync))!
			.MakeGenericMethod(type);
		var task = (Task)method.Invoke(_resolver, [Scope.Project, ProjectKey, default(CancellationToken)])!;
		await task.ConfigureAwait(false);
		return task.GetType().GetProperty("Result")!.GetValue(task)!;
	}

	object InvokeBindAndSet(Type type, object current)
	{
		var method = typeof(SettingsFormBinder).GetMethod(nameof(SettingsFormBinder.BuildFrom))!
			.MakeGenericMethod(type);
		return method.Invoke(null, [Request.Form, current])!;
	}

	async Task SaveDynamicallyAsync(Type type, object current, object updated, long? userId)
	{
		var method = typeof(ISettingsResolver).GetMethod(nameof(ISettingsResolver.SetAsync))!
			.MakeGenericMethod(type);
		var task = (Task)method.Invoke(_resolver, [Scope.Project, ProjectKey, updated, current, userId, default(CancellationToken)])!;
		await task.ConfigureAwait(false);
	}
}
