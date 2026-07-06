using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Settings;

namespace PetBox.Web.Settings;

// Shared orchestration for the generic settings pages (System / Workspace / Project scope):
// SysDefaultsModel, WorkspaceDefaultsModel, ProjectSettingsAdminModel. Each concrete page differs
// only in three knobs — its Scope, its ScopeKey, and the record types it exposes — so everything
// else (resolving current values, rendering sections, binding + persisting a posted record) lives
// here once.
//
// The [Authorize] policy (SysAdmin vs WorkspaceAdmin) and any [FromRoute] key binding stay on the
// concrete subclass: those are per-page concerns the base can't express, and the [FromRoute]-only
// bind for workspace/project keys is a deliberate authz-bypass-project-create guard (see the
// subclasses).
//
// Section threshold: a record shows when it has at least one [Setting] whose TopLevel is at or
// above (numerically >=) this page's Scope — i.e. the record is configurable at this scope or a
// deeper one. The leaf form renderer (_SettingsFormFields via SettingsFormFieldSelector) then
// hides the individual properties that don't belong at this scope.
public abstract class SettingsScopePageModel : PageModel
{
	readonly ISettingsResolver _resolver;

	protected SettingsScopePageModel(ISettingsResolver resolver) => _resolver = resolver;

	// The scope this page reads/writes at.
	protected abstract Scope Scope { get; }

	// The scope-key for this page's scope (System → "$"; Workspace → route WorkspaceKey;
	// Project → route ProjectKey).
	protected abstract string ScopeKey { get; }

	// Records this page may expose, subject to the per-record TopLevel >= Scope threshold below.
	protected abstract IReadOnlyList<Type> Records { get; }

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
		var type = Records.FirstOrDefault(t => t.Name == recordType);
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
		foreach (var type in Records)
		{
			var configurableHere = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Select(p => p.GetCustomAttribute<SettingAttribute>())
				.Any(a => a is not null && (int)a.TopLevel >= (int)Scope);
			if (!configurableHere) continue;

			var current = await ResolveAsync(type);
			sections.Add(new RecordSection(type, current));
		}
		return sections;
	}

	async Task<object> ResolveAsync(Type type)
	{
		var method = typeof(ISettingsResolver).GetMethod(nameof(ISettingsResolver.GetAsync))!
			.MakeGenericMethod(type);
		var task = (Task)method.Invoke(_resolver, [Scope, ScopeKey, default(CancellationToken)])!;
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
		var task = (Task)method.Invoke(_resolver, [Scope, ScopeKey, updated, current, userId, default(CancellationToken)])!;
		await task.ConfigureAwait(false);
	}
}
