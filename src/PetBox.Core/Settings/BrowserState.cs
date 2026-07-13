namespace PetBox.Core.Settings;

// The single per-request UI-state record resolved before the layout renders (see
// PetBox.Web.Settings.UiStateResolver, and _Layout.cshtml where it's resolved next to
// ThemeHelper's data-theme). It mixes BOTH storage branches in one type:
//   - a property marked [Setting] (Scope.User) resolves from the DB — cross-device preferences.
//   - a property marked [BrowserState] resolves from the single `petbox.ui` JSON cookie —
//     window/device state, visible to the server before HTML is sent.
//
// Ships with ZERO properties: this work node (ui-state-framework) is the mechanism only. Its
// five blocked follow-ups — sidebar pin, board view mode, board filters, kql panel pin, the dead
// sidebar-tree cookie — each add THEIR property here (tagged [Setting] or [BrowserState] per the
// storage-boundary call in their own spec) instead of standing up a parallel resolver or an extra
// cookie. Do not add a demo/placeholder property to prove the mechanism works — UiStateResolverTests
// and UiStateTypeSyncTests exercise it against synthetic fixture types instead, the same way
// FluentMappingCompletenessTests and SettingsResolverTests prove their guards without requiring
// production data to already exhibit the scar.
public sealed record BrowserState;
