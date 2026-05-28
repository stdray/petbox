using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using PetBox.Core.Models;

namespace PetBox.Core.Auth;

public sealed record ConfigApiKeyEntry
{
	public string Key { get; init; } = string.Empty;
	public string ProjectKey { get; init; } = string.Empty;
	public string Scopes { get; init; } = string.Empty;
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
				CreatedAt = now,
			};
		}
		_byKey = builder.ToImmutable();
	}

	public ApiKey? FindByKey(string key) =>
		_byKey.TryGetValue(key, out var apiKey) ? apiKey : null;
}
