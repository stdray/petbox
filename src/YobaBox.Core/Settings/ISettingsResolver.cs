namespace YobaBox.Core.Settings;

// Reads/writes settings records (typed groups marked with [Setting]) against the
// L2 Settings store. Implementation walks the scope chain for each property
// from `deepestScope` up to the property's `TopLevel` cap; first row wins,
// otherwise the record's default-init value stays.
//
// Implementations are in `YobaBox.Web.Settings.SettingsResolver` — they wire in
// `YobaBoxDb` for storage and `ISecretEncryptor` for `IsSecret` properties.
public interface ISettingsResolver
{
	// Resolves a record T at the requested scope. Every property marked with
	// [Setting] gets its value from the first matching row in the scope chain,
	// or keeps the record's default if no row exists at any reachable scope.
	Task<T> GetAsync<T>(Scope deepestScope, string deepestScopeKey, CancellationToken ct = default)
		where T : new();

	// Writes the diff between `newValues` and `oldValues` to the store at the
	// given (scope, scopeKey). Properties that match the old values are skipped
	// (no row created for unchanged values; previous overrides are not deleted).
	// To remove an override and fall back to the cascade, use ResetAsync.
	Task SetAsync<T>(Scope scope, string scopeKey, T newValues, T oldValues, long? updatedBy, CancellationToken ct = default)
		where T : notnull;

	// Removes the override row for a specific property at (scope, scopeKey).
	// Reads after this will fall back up the cascade.
	Task ResetAsync<T>(Scope scope, string scopeKey, string propertyName, CancellationToken ct = default)
		where T : notnull;
}
