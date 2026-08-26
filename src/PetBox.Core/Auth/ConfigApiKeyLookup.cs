using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
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

	// `logger` is OPTIONAL so the several direct `new ConfigApiKeyLookup(Options.Create(...))` call sites
	// in the tests keep compiling; under DI it is always supplied.
	public ConfigApiKeyLookup(IOptions<ConfigApiKeyOptions> options, ILogger<ConfigApiKeyLookup>? logger = null)
	{
		var builder = ImmutableDictionary.CreateBuilder<string, ApiKey>(StringComparer.Ordinal);
		var now = DateTime.UtcNow;
		foreach (var entry in options.Value.ApiKeys)
		{
			if (string.IsNullOrWhiteSpace(entry.Key)) continue;
			if (builder.ContainsKey(entry.Key)) continue;
			WarnOnUnknownScopes(entry, logger);
			builder[entry.Key] = new ApiKey
			{
				Key = entry.Key,
				ProjectKey = entry.ProjectKey,
				Scopes = entry.Scopes,
				DefaultProjectKey = entry.DefaultProjectKey,
				ExpiresAt = NormalizeToUtc(entry.ExpiresAt),
				SandboxOnly = entry.SandboxOnly,
				CreatedAt = now,
			};
		}
		_byKey = builder.ToImmutable();
	}

	// work config-key-expiry-timezone-normalization: Microsoft.Extensions.Configuration.Binder
	// materializes `Auth:ApiKeys[N]:ExpiresAt` via plain `DateTime.Parse` — no
	// `DateTimeStyles.RoundtripKind`/`AdjustToUniversal`. That means a value WITH an offset/`Z`
	// (e.g. "2026-08-26T09:33:13Z") comes back `Kind=Local` with the clock digits already
	// SHIFTED to the host's zone, and a value withOUT one comes back `Kind=Unspecified` with the
	// digits taken literally. `ApiKeyAuthenticationHandler`'s `expiresAt <= DateTime.UtcNow` check
	// then compares raw Ticks — DateTime's comparison operators ignore Kind — so on a host with a
	// non-zero UTC offset a key lives `offset` longer than its declared expiry, for BOTH forms.
	//
	// Decided semantics (owner-approved, not re-litigated here): a config moment with NO zone
	// suffix means UTC, not "host-local". So the fix is explicit at THIS materialization point,
	// not a host-timezone workaround:
	//   Unspecified -> the digits ARE the UTC instant (SpecifyKind, no shift)
	//   Local       -> the Binder already shifted the digits to host-local; undo that (ToUniversalTime)
	//   Utc         -> already correct (defensive; the Binder does not produce this Kind today)
	// The result is always Kind=Utc, matching DbApiKeyLookup's DB-sourced keys (see that type for
	// why a value read back from SQLite needs the analogous normalization even though it was never
	// mis-parsed — linq2db/Microsoft.Data.Sqlite drops Kind entirely on the way out).
	static DateTime? NormalizeToUtc(DateTime? value) => value switch
	{
		null => null,
		{ Kind: DateTimeKind.Utc } dt => dt,
		{ Kind: DateTimeKind.Local } dt => dt.ToUniversalTime(),
		{ } dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc), // Unspecified
	};

	// EVERY OTHER DOOR into ApiKeys.Scopes runs ApiKeyScopes.Validate first — mint (KeyIssuer), the
	// admin form (AgentKeyAdminService), the project-page re-scope (Pages/Admin/ProjectKeys), the MCP
	// patch (ApiKeyTools). This one copies `Auth:ApiKeys[]`.Scopes verbatim, so an operator typo
	// ("log:query", "Data:Read") reaches the gates as a token that matches NOTHING: since
	// `scope-claims-canonicalization` made the comparison Ordinal on every transport, a wrong CASE is
	// just as dead as a wrong WORD, and the failure looks like "the key works but is missing a
	// permission" at the far end of an agent run.
	//
	// IT WARNS, IT DOES NOT REFUSE — deliberately. This runs in a singleton constructor during host
	// build; throwing would turn one stale token in appsettings into a process that will not start, and
	// the config plane is exactly the plane you cannot fix through the UI (no `apikey_list`, no revoke —
	// only an edit-and-restart). A key with one dead token still authenticates and still carries its
	// live ones; the log line is what makes the dead one visible before someone spends an afternoon on
	// a 403.
	//
	// One line PER KEY, not per token: the tokens are joined into it, so a key with three typos does
	// not produce three lines that look like three keys. The key VALUE is a credential and is never
	// logged — the project claim and the bad tokens are what identify the entry to whoever edits the file.
	static void WarnOnUnknownScopes(ConfigApiKeyEntry entry, ILogger? logger)
	{
		if (logger is null) return;
		var (_, invalid) = ApiKeyScopes.Validate(entry.Scopes);
		if (invalid.Count == 0) return;
		logger.LogWarning(
			"Auth:ApiKeys entry for project '{ProjectKey}' declares {Count} scope token(s) that are not in "
			+ "the catalog and will authorize nothing: {UnknownScopes}. Scope matching is Ordinal, so case "
			+ "matters ('data:read', not 'Data:Read'). The key still works with its remaining scopes; fix "
			+ "the configuration and restart to grant the intended ones.",
			entry.ProjectKey,
			invalid.Count,
			string.Join(", ", invalid));
	}

	public ApiKey? FindByKey(string key) =>
		_byKey.TryGetValue(key, out var apiKey) ? apiKey : null;
}
