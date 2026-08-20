using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using PetBox.Core.Models;

namespace PetBox.Core.Auth;

public sealed record ConfigApiKeyEntry
{
	public string Key { get; init; } = string.Empty;
	public string ProjectKey { get; init; } = string.Empty;
	public string Scopes { get; init; } = string.Empty;
	// Optional fallback project for a cross-project ("*") key — see ApiKey.DefaultProjectKey.
	public string? DefaultProjectKey { get; init; }
	// Grant, not credential (spec auth-key-expiry / work config-keys-lifecycle-gaps): the secret
	// still lives in appsettings/env and can only be rotated by editing the file and restarting the
	// process, but the LIFECYCLE around it — when it stops being valid, whether it is sandbox-only —
	// is now expressible without touching the credential itself. NULL = never expires (same default
	// as ApiKey.ExpiresAt), matching the letter of spec auth-key-expiry: "absence of an expiry means
	// an unbounded key".
	public DateTime? ExpiresAt { get; init; }
	// See ApiKey.SandboxOnly for the containment semantics; ConfigApiKeyLookup projects it
	// unchanged. Defaults false, so every existing Auth:ApiKeys[] entry keeps its current reach.
	public bool SandboxOnly { get; init; }
}

public sealed record ConfigApiKeyOptions
{
	public IList<ConfigApiKeyEntry> ApiKeys { get; init; } = [];
}

// Read-only ApiKey lookup backed by Auth:ApiKeys[] in appsettings. Useful for bootstrap /
// CI keys with fixed values — keys here cannot be revoked through the UI (lifecycle is
// owned by the config source). UI-minted keys live in PetBoxDb and are served by
// DbApiKeyLookup.
public sealed class ConfigApiKeyLookup : IApiKeyLookup
{
	readonly ImmutableDictionary<string, ApiKey> _byKey;

	public ConfigApiKeyLookup(IOptions<ConfigApiKeyOptions> options)
	{
		var builder = ImmutableDictionary.CreateBuilder<string, ApiKey>(StringComparer.Ordinal);
		var now = DateTime.UtcNow;
		foreach (var entry in options.Value.ApiKeys)
		{
			if (string.IsNullOrWhiteSpace(entry.Key)) continue;
			if (builder.ContainsKey(entry.Key)) continue;
			builder[entry.Key] = new ApiKey
			{
				Key = entry.Key,
				ProjectKey = entry.ProjectKey,
				Scopes = entry.Scopes,
				DefaultProjectKey = entry.DefaultProjectKey,
				ExpiresAt = entry.ExpiresAt,
				SandboxOnly = entry.SandboxOnly,
				CreatedAt = now,
			};
		}
		_byKey = builder.ToImmutable();
	}

	public ApiKey? FindByKey(string key) =>
		_byKey.TryGetValue(key, out var apiKey) ? apiKey : null;
}
