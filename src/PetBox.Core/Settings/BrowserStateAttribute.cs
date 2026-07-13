namespace PetBox.Core.Settings;

// [Setting]'s peer for the OTHER storage branch: window/device state that must be visible to the
// server BEFORE it emits HTML (so it can be applied without a post-load class correction, a
// redirect, or a reload — see ThemeHelper for the one prior mechanism built that way). Where
// [Setting] resolves from the DB via ISettingsResolver, [BrowserState] resolves from the single
// `petbox.ui` JSON cookie (UiStateResolver.CookieName) — never localStorage, which the server
// cannot read before first paint, and never a dedicated cookie per feature, which would grow
// every request's header with each new preference.
//
// Example:
//   public sealed record BrowserState
//   {
//       [BrowserState(Key = "sidebarPinned")]
//       public bool SidebarPinned { get; init; } = true;
//   }
[AttributeUsage(AttributeTargets.Property)]
public sealed class BrowserStateAttribute : Attribute
{
	// JSON property name inside the petbox.ui cookie object. Deliberately independent of the C#
	// property name (mirrors SettingAttribute.Key) so the wire shape doesn't have to track a
	// PascalCase rename, and so several unrelated properties can share one flat cookie namespace
	// without an accidental collision.
	public required string Key { get; init; }

	// Optional UI/diagnostic hint (no admin-form use today — [Setting] has that; this exists for
	// parity and for any future generic listing of registered browser-state keys).
	public string? Description { get; init; }
}
