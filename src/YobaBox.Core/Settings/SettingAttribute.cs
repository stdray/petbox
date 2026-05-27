namespace YobaBox.Core.Settings;

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

	// Path stored in the Settings table. Convention: `{group}.{name}.{subname}` in lowerCamel.
	public required string Key { get; init; }

	// Optional UI hint (label fallback if not set is the property name).
	public string? Description { get; init; }

	// When true, the property type must be string and its value is stored as
	// base64(cipher+iv+tag) via ISecretEncryptor. UI renders as password input + reveal.
	public bool IsSecret { get; init; }
}
