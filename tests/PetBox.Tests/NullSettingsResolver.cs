using PetBox.Core.Settings;

namespace PetBox.Tests;

// Minimal ISettingsResolver stand-in for page-model unit tests that construct PageModels
// directly (no DI container / Settings table). Every read resolves to the record's defaults;
// writes are no-ops. Use the real SettingsResolver (SettingsResolverTests) to test the cascade.
public sealed class NullSettingsResolver : ISettingsResolver
{
	public Task<T> GetAsync<T>(Scope deepestScope, string deepestScopeKey, CancellationToken ct = default)
		where T : new() => Task.FromResult(new T());

	public Task SetAsync<T>(Scope scope, string scopeKey, T newValues, T oldValues, long? updatedBy, CancellationToken ct = default)
		where T : notnull => Task.CompletedTask;

	public Task ResetAsync<T>(Scope scope, string scopeKey, string propertyName, CancellationToken ct = default)
		where T : notnull => Task.CompletedTask;
}
