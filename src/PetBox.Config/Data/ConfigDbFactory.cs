using PetBox.Core.Data;

namespace PetBox.Config.Data;

public interface IConfigDbFactory
{
	// Returns the CACHED connection (shared across all callers for one workspace).
	// Thread-UNSAFE — use only for bootstrap/ensure-schema; per-request reads and
	// writes get a fresh thread-safe connection via NewConfigDb.
	ConfigDb GetConfigDb(string workspaceKey);

	// Opens a FRESH, caller-owned connection to the config DB for `workspaceKey`.
	// Ensures the file + schema exist on first access (delegates to GetDb once),
	// then returns a NEW, non-cached connection the caller disposes (using var).
	// Thread-SAFE — for every per-request read/write.
	ConfigDb NewConfigDb(string workspaceKey);
}

// Thin typed facade over the generic ScopedDbFactory (Workspace-scoped, one DB
// per workspace at config/{workspaceKey}.db). It owns nothing — the underlying
// IScopedDbFactory<ConfigDb> is a DI singleton the container creates and disposes
// — so this facade is not itself disposable.
public sealed class ConfigDbFactory : IConfigDbFactory
{
	readonly IScopedDbFactory<ConfigDb> _inner;

	public ConfigDbFactory(IScopedDbFactory<ConfigDb> inner) => _inner = inner;

	public ConfigDb GetConfigDb(string workspaceKey) => _inner.GetDb(workspaceKey);

	public ConfigDb NewConfigDb(string workspaceKey) => _inner.NewEnsuredConnection(workspaceKey);
}
