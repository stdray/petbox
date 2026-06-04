using System.Text.Json;
using FluentValidation;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.LlmRouter.Contract;

namespace PetBox.LlmRouter.Registry;

// Stores the router registry in the Config module (llm-config-driven): the endpoints/routes
// as one Plain JSON binding, and each endpoint's api key as an encrypted Secret binding
// (llm-endpoint-security). Config is workspace-scoped, so the project's workspace is resolved
// via PetBoxDb. Reads/writes go straight at the bindings table by path (versioned, soft-
// delete on replace) — it does NOT go through the tag-resolve pipeline (a single known path,
// no ambiguity). Scoped: it depends on the scoped PetBoxDb.
public sealed class LlmRegistryStore : ILlmRegistryAdmin, ILlmRegistryResolver
{
	const string RegistryPath = "llm/registry";
	static string SecretPath(string endpoint) => $"llm/secret/{endpoint}";

	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	readonly IConfigDbFactory _configFactory;
	readonly ISecretEncryptor _secrets;
	readonly PetBoxDb _db;

	public LlmRegistryStore(IConfigDbFactory configFactory, ISecretEncryptor secrets, PetBoxDb db)
	{
		_configFactory = configFactory;
		_secrets = secrets;
		_db = db;
	}

	public Task<LlmRegistry> GetAsync(string projectKey, CancellationToken ct = default)
	{
		var ws = ResolveWorkspace(projectKey);
		var cfg = _configFactory.GetConfigDb(ws);
		return Task.FromResult(ReadRegistry(cfg));
	}

	public async Task<ResolvedRegistry> ResolveAsync(string projectKey, CancellationToken ct = default)
	{
		var ws = ResolveWorkspace(projectKey);
		var cfg = _configFactory.GetConfigDb(ws);
		var registry = ReadRegistry(cfg);

		var keys = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var ep in registry.Endpoints)
		{
			var row = ActiveByPath(cfg, SecretPath(ep.Name));
			if (row is { Kind: BindingKind.Secret, Ciphertext: not null, Iv: not null, AuthTag: not null }
				&& _secrets.IsAvailable)
			{
				try { keys[ep.Name] = _secrets.Decrypt(row.Ciphertext, row.Iv, row.AuthTag); }
				catch { /* a key that won't decrypt is treated as absent — calls go unauthenticated */ }
			}
		}
		await Task.CompletedTask;
		return new ResolvedRegistry(registry, keys);
	}

	public async Task SetAsync(string projectKey, LlmRegistry registry, IReadOnlyDictionary<string, string> apiKeys, CancellationToken ct = default)
	{
		var result = new LlmRegistryValidator().Validate(registry);
		if (!result.IsValid) throw new ValidationException(result.Errors);

		var ws = ResolveWorkspace(projectKey);
		var cfg = _configFactory.GetConfigDb(ws);

		await UpsertBindingAsync(cfg, ws, RegistryPath, JsonSerializer.Serialize(registry, Json), BindingKind.Plain, null, ct);

		foreach (var (endpoint, key) in apiKeys)
		{
			if (string.IsNullOrWhiteSpace(key)) continue;
			if (!_secrets.IsAvailable)
				throw new InvalidOperationException("storing an api key requires PETBOX_MASTER_KEY to be configured");
			var bundle = _secrets.Encrypt(key);
			await UpsertBindingAsync(cfg, ws, SecretPath(endpoint), string.Empty, BindingKind.Secret, bundle, ct);
		}
	}

	// ---- internals ----

	static LlmRegistry ReadRegistry(ConfigDb cfg)
	{
		var row = ActiveByPath(cfg, RegistryPath);
		if (row is null || string.IsNullOrWhiteSpace(row.Value)) return LlmRegistry.Empty;
		try { return JsonSerializer.Deserialize<LlmRegistry>(row.Value, Json) ?? LlmRegistry.Empty; }
		catch (JsonException) { return LlmRegistry.Empty; }
	}

	static ConfigBinding? ActiveByPath(ConfigDb cfg, string path) =>
		cfg.Bindings.Where(b => b.Path == path && !b.IsDeleted)
			.OrderByDescending(b => b.Version)
			.FirstOrDefault();

	static async Task UpsertBindingAsync(ConfigDb cfg, string ws, string path, string value, BindingKind kind, SecretBundle? secret, CancellationToken ct)
	{
		var now = DateTime.UtcNow;

		// Soft-close any active version at this path.
		await cfg.Bindings.Where(b => b.Path == path && !b.IsDeleted)
			.Set(b => b.IsDeleted, true)
			.Set(b => b.DeletedAt, (DateTime?)now)
			.Set(b => b.UpdatedAt, now)
			.UpdateAsync(ct);

		var maxVer = cfg.Bindings.Where(b => b.Path == path).Select(b => (int?)b.Version).Max() ?? 0;
		var tags = $"ws:{ws}";
		var binding = new ConfigBinding
		{
			Path = path,
			Value = value,
			Tags = tags,
			Kind = kind,
			Ciphertext = secret?.Ciphertext,
			Iv = secret?.Iv,
			AuthTag = secret?.AuthTag,
			Version = maxVer + 1,
			ContentHash = BindingContentHash.Compute(path, tags, kind, value, secret?.Ciphertext),
			CreatedAt = now,
			UpdatedAt = now,
		};
		await cfg.InsertAsync(binding, token: ct);
	}

	string ResolveWorkspace(string projectKey)
	{
		var project = _db.Projects.FirstOrDefault(p => p.Key == projectKey)
			?? throw new LlmRouterException(LlmCapability.Embed, false, $"unknown project '{projectKey}'");
		return project.WorkspaceKey;
	}
}
