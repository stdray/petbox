using LinqToDB;
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
		return db.ApiKeys.FirstOrDefault(k => k.Key == key);
	}
}
