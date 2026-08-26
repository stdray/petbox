using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Core.Auth;

// The auth hot path: one indexed read per request. It takes the FACTORY, not a context — a linq2db
// DataConnection is not thread-safe, and the scoped PetBoxDb this replaces was one connection shared
// by every thread a request fanned out onto. The connection opened here is caller-owned and disposed
// before the method returns, so it is reachable from exactly one thread by construction.
//
// Cost of the switch: opening a PetBoxDb is building a DataConnection over the SHARED MappingSchema
// (CoreDbFactory holds the DataOptions, never a connection) and Microsoft.Data.Sqlite hands back a
// POOLED underlying connection — so this is not a file open, and it does not rebuild the mapping.
public sealed class DbApiKeyLookup(ICoreDbFactory factory) : IApiKeyLookup
{
	public ApiKey? FindByKey(string key)
	{
		using var db = factory.Open();
		var found = db.ApiKeys.FirstOrDefault(k => k.Key == key);
		// work config-key-expiry-timezone-normalization: every writer of ApiKeys.ExpiresAt
		// (AgentKeyAdminService.MintAsync/PatchAsync) stores a value derived from
		// `DateTime.UtcNow` — Kind=Utc going IN. But linq2db over Microsoft.Data.Sqlite drops
		// Kind entirely on the way OUT (measured: a Utc write reads back Kind=Unspecified,
		// same Ticks); PetBox.Log.Core's KqlSqlExpressions and Mcp/HealthTools.cs already carry
		// the identical SpecifyKind workaround for the same SQLite behavior. The numeric value
		// was never wrong here (unlike ConfigApiKeyLookup's Binder defect), only the label — but
		// ApiKey.ExpiresAt must read Kind=Utc regardless of source, so a caller that reasonably
		// calls .ToUniversalTime() on it later cannot silently reinterpret it as host-local.
		return found is null || found.ExpiresAt is not { Kind: not DateTimeKind.Utc } expiresAt
			? found
			: found with { ExpiresAt = DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc) };
	}
}
