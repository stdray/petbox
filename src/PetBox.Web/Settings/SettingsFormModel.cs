using PetBox.Core.Settings;

namespace PetBox.Web.Settings;

// View model for Pages/Shared/_SettingsForm.cshtml — full form (form tag +
// fields + submit). The partial reflects over `RecordType`'s
// [Setting]-annotated properties and renders an input per property,
// prefilled from `Current`. The owning PageModel handles POST in
// `Handler` and reconstructs the record from form fields.
public sealed class SettingsFormModel
{
	public required Type RecordType { get; init; }
	public required object Current { get; init; }
	public required Scope CurrentScope { get; init; }
	public required string ScopeKey { get; init; }
	public string Handler { get; init; } = "Save";
	public string? FormTestId { get; init; }
	public bool ShowReset { get; init; } = true;
}

// Inner partial: just the field inputs (no form tag, no submit button).
// Used by pages with multiple records on one page (Defaults pages) that
// need their own form-per-record wrapping with extra hidden fields.
public sealed class SettingsFormFieldsModel
{
	public required Type RecordType { get; init; }
	public required object Current { get; init; }
	public required Scope CurrentScope { get; init; }
}
