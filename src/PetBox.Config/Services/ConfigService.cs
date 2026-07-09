using LinqToDB;
using PetBox.Config.Contract;
using PetBox.Config.Data;
using PetBox.Core.Models;

namespace PetBox.Config.Services;

public sealed class ConfigService : IConfigService
{
	readonly IConfigDbFactory _configFactory;
	readonly ISecretEncryptor _encryptor;

	public ConfigService(IConfigDbFactory configFactory, ISecretEncryptor encryptor)
	{
		_configFactory = configFactory;
		_encryptor = encryptor;
	}

	// -- Bindings ---------------------------------------------------------------

	public Task<IReadOnlyList<ConfigBinding>> GetActiveBindingsAsync(
		string workspaceKey, CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);
		return Task.FromResult<IReadOnlyList<ConfigBinding>>(
			db.Bindings.Where(b => !b.IsDeleted).OrderBy(b => b.Path).ToList());
	}

	public Task<ConfigBinding?> GetBindingAsync(
		string workspaceKey, long id, CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);
		return Task.FromResult(
			db.Bindings.FirstOrDefault(b => b.Id == id && !b.IsDeleted));
	}

	public async Task<long> CreateBindingAsync(
		string workspaceKey, string path, string tags, string value,
		BindingKind kind, string actor, DateTime at, CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);

		string storedValue = value;
		string? cipher = null;
		string? iv = null;
		string? authTag = null;

		if (kind == BindingKind.Secret)
		{
			var bundle = _encryptor.Encrypt(value);
			cipher = bundle.Ciphertext;
			iv = bundle.Iv;
			authTag = bundle.AuthTag;
			storedValue = string.Empty;
		}

		var contentHash = BindingContentHash.Compute(path, tags, kind, storedValue, cipher);

		var binding = new ConfigBinding
		{
			Path = path,
			Tags = tags,
			Kind = kind,
			Value = storedValue,
			Ciphertext = cipher,
			Iv = iv,
			AuthTag = authTag,
			Version = 1,
			ContentHash = contentHash,
			CreatedAt = at,
			UpdatedAt = at,
		};

		var id = Convert.ToInt64(await db.InsertWithIdentityAsync(binding, token: ct).ConfigureAwait(false));

		await db.InsertAsync(new ConfigBindingHistoryEntry
		{
			BindingId = id,
			Action = "Create",
			Path = path,
			Tags = tags,
			Kind = kind,
			OldValue = null,
			NewValue = kind == BindingKind.Plain ? value : "(secret)",
			Actor = actor,
			At = at,
		}, token: ct).ConfigureAwait(false);

		return id;
	}

	public async Task<long> UpdateBindingAsync(
		string workspaceKey, long id, string path, string tags, string value,
		BindingKind kind, string actor, DateTime at, CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);

		var existing = db.Bindings.FirstOrDefault(b => b.Id == id);
		if (existing is null)
			throw new InvalidOperationException($"Binding #{id} not found.");

		string storedValue;
		string? cipher = null;
		string? iv = null;
		string? authTag = null;

		if (kind == BindingKind.Secret)
		{
			var bundle = _encryptor.Encrypt(value);
			cipher = bundle.Ciphertext;
			iv = bundle.Iv;
			authTag = bundle.AuthTag;
			storedValue = string.Empty;
		}
		else
		{
			storedValue = value;
		}

		var newHash = BindingContentHash.Compute(path, tags, kind, storedValue, cipher);

		var isNoOp = string.Equals(existing.ContentHash, newHash, StringComparison.Ordinal)
			&& !existing.IsDeleted;

		var updated = existing with
		{
			Path = path,
			Tags = tags,
			Kind = kind,
			Value = storedValue,
			Ciphertext = cipher,
			Iv = iv,
			AuthTag = authTag,
			Version = isNoOp ? existing.Version : existing.Version + 1,
			ContentHash = newHash,
			IsDeleted = false,
			DeletedAt = null,
			UpdatedAt = at,
		};

		await db.UpdateAsync(updated, token: ct).ConfigureAwait(false);

		if (!isNoOp)
		{
			await db.InsertAsync(new ConfigBindingHistoryEntry
			{
				BindingId = id,
				Action = existing.IsDeleted ? "Undelete" : "Update",
				Path = path,
				Tags = tags,
				Kind = kind,
				OldValue = existing.Kind == BindingKind.Plain ? existing.Value : "(secret)",
				NewValue = kind == BindingKind.Plain ? storedValue : "(secret)",
				Actor = actor,
				At = at,
			}, token: ct).ConfigureAwait(false);
		}

		return id;
	}

	public async Task<bool> DeleteBindingAsync(
		string workspaceKey, long id, string actor, DateTime at, CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);

		var existing = db.Bindings.FirstOrDefault(b => b.Id == id && !b.IsDeleted);
		if (existing is null)
			return false;

		await db.InsertAsync(new ConfigBindingHistoryEntry
		{
			BindingId = existing.Id,
			Action = "Delete",
			Path = existing.Path,
			Tags = existing.Tags,
			Kind = existing.Kind,
			OldValue = existing.Kind == BindingKind.Plain ? existing.Value : "(secret)",
			NewValue = null,
			Actor = actor,
			At = at,
		}, token: ct).ConfigureAwait(false);

		await db.Bindings
			.Where(b => b.Id == id)
			.Set(b => b.IsDeleted, true)
			.Set(b => b.DeletedAt, (DateTime?)at)
			.Set(b => b.UpdatedAt, at)
			.UpdateAsync(token: ct).ConfigureAwait(false);

		return true;
	}

	public Task<string?> RevealSecretAsync(
		string workspaceKey, long id, CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);
		var binding = db.Bindings.FirstOrDefault(b => b.Id == id);

		if (binding is null || binding.Kind != BindingKind.Secret)
			return Task.FromResult<string?>(null);

		if (!_encryptor.IsAvailable || binding.Ciphertext is null
			|| binding.Iv is null || binding.AuthTag is null)
			return Task.FromResult<string?>(null);

		try
		{
			var plaintext = _encryptor.Decrypt(binding.Ciphertext, binding.Iv, binding.AuthTag);
			return Task.FromResult<string?>(plaintext);
		}
		catch
		{
			return Task.FromResult<string?>(null);
		}
	}

	// -- Resolve ----------------------------------------------------------------

	public Task<IReadOnlyDictionary<string, string>> ResolveAsync(
		string workspaceKey, IReadOnlyList<string> requestTags, CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);
		var bindings = db.Bindings.ToList();

		var matches = ResolvePipeline.ResolveAll(requestTags, bindings);

		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var m in matches)
		{
			var val = m.Binding.Kind == BindingKind.Secret
				&& _encryptor.IsAvailable
				&& m.Binding.Ciphertext is not null
				&& m.Binding.Iv is not null
				&& m.Binding.AuthTag is not null
					? TryDecrypt(m.Binding.Ciphertext, m.Binding.Iv, m.Binding.AuthTag)
					: m.Binding.Value;
			result[m.Binding.Path] = val;
		}

		return Task.FromResult<IReadOnlyDictionary<string, string>>(result);
	}

	public Task<ResolveMatch?> ResolvePathAsync(
		string workspaceKey, string path, IReadOnlyList<string> requestTags,
		CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);
		var bindings = db.Bindings.ToList();

		var match = ResolvePipeline.ResolveDetailed(path, requestTags, bindings);
		return Task.FromResult(match);
	}

	// -- History ----------------------------------------------------------------

	public Task<IReadOnlyList<ConfigBindingHistoryEntry>> GetHistoryAsync(
		string workspaceKey, string? pathSubstring = null, CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);

		var query = db.History.AsQueryable();
		if (!string.IsNullOrWhiteSpace(pathSubstring))
			query = query.Where(h => h.Path.Contains(pathSubstring));

		return Task.FromResult<IReadOnlyList<ConfigBindingHistoryEntry>>(
			query.OrderByDescending(h => h.At).Take(500).ToList());
	}

	// -- Tags -------------------------------------------------------------------

	public Task<IReadOnlyList<TagVocabularyEntry>> GetTagsAsync(
		string workspaceKey, CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);
		return Task.FromResult<IReadOnlyList<TagVocabularyEntry>>(
			db.Tags.OrderBy(t => t.TagKey).ToList());
	}

	public async Task AddTagAsync(
		string workspaceKey, string tagKey, string? description, CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);
		var exists = db.Tags.Any(t => t.TagKey == tagKey);
		if (!exists)
		{
			await db.InsertAsync(new TagVocabularyEntry
			{
				TagKey = tagKey.Trim(),
				Description = description?.Trim(),
				CreatedAt = DateTime.UtcNow,
			}, token: ct).ConfigureAwait(false);
		}
	}

	public async Task RetireTagAsync(
		string workspaceKey, long id, CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);
		await db.Tags.Where(t => t.Id == id).DeleteAsync(token: ct).ConfigureAwait(false);
	}

	// -- Counts -----------------------------------------------------------------

	public Task<int> CountBindingsAsync(
		string workspaceKey, string? tagFilter = null, CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);
		if (string.IsNullOrWhiteSpace(tagFilter))
		{
			return Task.FromResult(db.Bindings.Count(b => !b.IsDeleted));
		}

		return Task.FromResult(
			db.Bindings.Where(b => !b.IsDeleted).AsEnumerable().Count(
				b => HasTag(b.Tags, tagFilter!)));
	}

	public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> AggregateUsedTagValuesAsync(
		string workspaceKey, CancellationToken ct = default)
	{
		using var db = _configFactory.NewConfigDb(workspaceKey);
		var bindings = db.Bindings.ToList();

		var used = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
		foreach (var b in bindings)
		{
			if (string.IsNullOrWhiteSpace(b.Tags)) continue;
			foreach (var part in b.Tags.Split(',',
				StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var sep = part.IndexOf(':');
				if (sep <= 0) continue;
				var key = part[..sep].Trim();
				var value = part[(sep + 1)..].Trim();
				if (!used.TryGetValue(key, out var set))
				{
					set = new SortedSet<string>(StringComparer.Ordinal);
					used[key] = set;
				}
				set.Add(value);
			}
		}

		return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(
			used.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)[.. kv.Value]));
	}

	// -- helpers ----------------------------------------------------------------

	static bool HasTag(string tags, string tag)
	{
		foreach (var t in tags.Split(',',
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	string TryDecrypt(string ciphertext, string iv, string authTag)
	{
		try { return _encryptor.Decrypt(ciphertext, iv, authTag); }
		catch { return string.Empty; }
	}
}
