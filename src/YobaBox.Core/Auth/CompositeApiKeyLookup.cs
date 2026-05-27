using YobaBox.Core.Models;

namespace YobaBox.Core.Auth;

// Tries config-defined keys first (immutable, fast in-memory dict), then DB-defined keys
// (UI-minted, can be revoked). Order matters: config wins on collision so a CI key
// declared in appsettings can never be silently overridden by a clashing UI-minted key.
public sealed class CompositeApiKeyLookup(ConfigApiKeyLookup config, DbApiKeyLookup db) : IApiKeyLookup
{
	public ApiKey? FindByKey(string key) =>
		config.FindByKey(key) ?? db.FindByKey(key);
}
