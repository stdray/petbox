namespace PetBox.Core.Settings;

public enum Theme { Dark, Light, System }

// Per-user UI preferences. Cross-workspace (theme follows the user everywhere).
// Scope axis: User. No cascade above User — these don't fall back to System.
public sealed record UiSettings
{
	[Setting(TopLevel = Scope.User, Key = "ui.theme", Description = "Color theme for the UI.")]
	public Theme Theme { get; init; } = Theme.Dark;
}
