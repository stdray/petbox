using PetBox.Core.Data;

namespace PetBox.Config.Data;

public interface IConfigDbFactory
{
	ConfigDb GetConfigDb(string workspaceKey);
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
}
