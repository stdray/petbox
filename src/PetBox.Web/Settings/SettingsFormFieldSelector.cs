using System.Reflection;
using PetBox.Core.Settings;

namespace PetBox.Web.Settings;

// Shared by _SettingsForm.cshtml and _SettingsFormFields.cshtml: which [Setting] properties of a
// record are editable at a given page scope. All the actual policy (INTERIM decision B — see
// SettingsScopePolicy) lives in SettingsScopePolicy.IsEditableAt; this is just the reflection walk
// over the record's properties.
//
// A caller whose GetEditable(...) comes back empty must render an explanatory empty state, never a
// bare Save button (card project-log-settings-empty-form) — e.g. SessionFullScanSettings at
// Workspace scope: both its properties are HasMinScope-pinned to System/Project respectively, so
// neither is editable at Workspace. SettingsScopePageModel filters such records out of Sections
// before they'd reach an empty form (see SettingsScopePolicy.IsRecordVisibleAt).
public static class SettingsFormFieldSelector
{
	public static IReadOnlyList<(PropertyInfo Property, SettingAttribute Attribute)> GetEditable(Type recordType, Scope currentScope) =>
		recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(p => (Property: p, Attribute: p.GetCustomAttribute<SettingAttribute>()))
			.Where(x => x.Attribute is not null && SettingsScopePolicy.IsEditableAt(x.Attribute, currentScope))
			.Select(x => (x.Property, x.Attribute!))
			.ToList();
}
