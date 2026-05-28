using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Settings;
using PetBox.Web.Settings;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceAdmin")]
public sealed class WorkspaceDefaultsModel : PageModel
{
	readonly ISettingsResolver _resolver;

	public WorkspaceDefaultsModel(ISettingsResolver resolver) => _resolver = resolver;

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	// Records exposed on a workspace's Defaults page. Any record with at least
	// one property TopLevel >= Workspace appears (System-only props are hidden
	// by the form renderer at workspace scope).
	static readonly Type[] WorkspaceDefaultRecords =
	[
		typeof(LogSettings),
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
		var type = WorkspaceDefaultRecords.FirstOrDefault(t => t.Name == recordType);
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
		foreach (var type in WorkspaceDefaultRecords)
		{
			var hasWorkspaceProp = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Select(p => p.GetCustomAttribute<SettingAttribute>())
				.Any(a => a is not null && (int)a.TopLevel >= (int)Scope.Workspace);
			if (!hasWorkspaceProp) continue;

			var current = await ResolveAsync(type);
			sections.Add(new RecordSection(type, current));
		}
		return sections;
	}

	async Task<object> ResolveAsync(Type type)
	{
		var method = typeof(ISettingsResolver).GetMethod(nameof(ISettingsResolver.GetAsync))!
			.MakeGenericMethod(type);
		var task = (Task)method.Invoke(_resolver, [Scope.Workspace, WorkspaceKey, default(CancellationToken)])!;
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
		var task = (Task)method.Invoke(_resolver, [Scope.Workspace, WorkspaceKey, updated, current, userId, default(CancellationToken)])!;
		await task.ConfigureAwait(false);
	}
}
