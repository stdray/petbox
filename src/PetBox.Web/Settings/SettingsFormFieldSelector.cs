using System.Reflection;
using PetBox.Core.Settings;

namespace PetBox.Web.Settings;

// Shared by _SettingsForm.cshtml and _SettingsFormFields.cshtml: which [Setting] properties of a
// record are editable at a given page scope.
//
// A field passes when `currentScope` is no finer (numerically no greater) than the field's
// TopLevel. TopLevel is documented on SettingAttribute as the CASCADE-READ ceiling, not a
// write-depth cap — reusing it here is only safe because every page that currently renders these
// partials asks for a scope at or above (numerically <=) every one of its record's TopLevel
// values.
//
// Do NOT route a field through this generic mechanism at a scope DEEPER than its TopLevel (e.g. a
// per-project override whose TopLevel caps out at Workspace/System for read-cascade purposes, the
// way LogSettings.RetentionDays used to be rendered on the project-scope /log page) — every
// property would filter out and the page would render with zero fields. Give that field a bespoke
// control instead (see ProjectDetail.cshtml's retention-override control), and — as a backstop —
// a caller whose GetEditable(...) comes back empty must render an explanatory empty state, never a
// bare Save button (card project-log-settings-empty-form; the /log page itself was already fixed
// this way in commit 9b0d4cd by dropping the generic form entirely for project-scope retention).
public static class SettingsFormFieldSelector
{
	public static IReadOnlyList<(PropertyInfo Property, SettingAttribute Attribute)> GetEditable(Type recordType, Scope currentScope) =>
		recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(p => (Property: p, Attribute: p.GetCustomAttribute<SettingAttribute>()))
			.Where(x => x.Attribute is not null && (int)currentScope <= (int)x.Attribute.TopLevel)
			.Select(x => (x.Property, x.Attribute!))
			.ToList();
}
