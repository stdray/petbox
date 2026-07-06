namespace PetBox.Core.Settings;

// Marks a record property as a Settings entry. The property's default-init value
// is the absolute fallback when no row exists at any reachable scope.
//
// Example:
//   public sealed record LogSettings
//   {
//       [Setting(TopLevel = Scope.Workspace, Key = "log.retention.days")]
//       public int RetentionDays { get; init; } = 20;
//   }
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute : Attribute
{
	// Topmost scope where this setting may be set. Cascade walks deepestScope → ... → TopLevel
	// and stops there. Settings whose TopLevel is finer than the consumer's scope can't be
	// configured at that consumer (e.g. ui.theme has TopLevel=User, project page can't override).
	public Scope TopLevel { get; init; } = Scope.Workspace;

	// Optional floor beneath TopLevel: the ONLY scope this field renders/edits at, for settings
	// that don't share a single "natural" deepest scope with the rest of their record (e.g.
	// SessionFullScanSettings: SystemEnabled lives at System, ProjectEnabled lives at Project — two
	// independent, non-cascading switches in one record, not a Project→Workspace→System cascade).
	// Left unset (the default, and every pre-existing [Setting] usage), a field renders on any page
	// whose CurrentScope <= TopLevel — unchanged behavior. Set (HasMinScope = true), a field renders
	// ONLY when CurrentScope == MinScope: without this, SettingsFormFieldSelector.GetEditable's
	// one-sided `currentScope <= TopLevel` check would also show (and let an admin silently write a
	// dead, never-read-back value for) a field on every broader page up to TopLevel, not just its
	// own home scope — verified by probe: adding SessionFullScanSettings to SysDefaults rendered
	// BOTH SystemEnabled and ProjectEnabled before this fix.
	// (`Scope? MinScope` isn't usable directly — Nullable<T> isn't a valid attribute argument type
	// (CS0655) — hence the companion HasMinScope flag.)
	public bool HasMinScope { get; init; }
	public Scope MinScope { get; init; }

	// Path stored in the Settings table. Convention: `{group}.{name}.{subname}` in lowerCamel.
	public required string Key { get; init; }

	// Optional UI hint (label fallback if not set is the property name).
	public string? Description { get; init; }

	// When true, the property type must be string and its value is stored as
	// base64(cipher+iv+tag) via ISecretEncryptor. UI renders as password input + reveal.
	public bool IsSecret { get; init; }
}
