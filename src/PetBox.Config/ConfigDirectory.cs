using LinqToDB;
using PetBox.Config.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Config;

// THE service layer for PetBox.Config — the door every presentation-layer caller goes through
// instead of opening core.db / ConfigDb itself (AGENTS.md § "Database connections — a hard
// invariant"). Modeled on ProjectDirectory (PetBox.Web/Auth/ProjectDirectory.cs) and
// IDataDbCatalog: interface + ctor-injected factory, one implementation, registered where its
// factories are.
//
// Two callers, two databases: Pages.Config.IndexModel (browse/manage bindings + saved filters)
// and PetBox.Config.ConfigApi (the REST surface — Conf/Create/Delete) both opened core.db AND
// ConfigDb inline before this existed. `PetBox.Config` only references `PetBox.Core` (not
// `PetBox.Web`), and ConfigApi lives IN this module — so this door lives here too, rather than in
// PetBox.Web where ProjectDirectory sits, to stay reachable from both callers without a circular
// project reference. It uses ICoreDbFactory directly for the trivial project-key -> workspace-key
// lookup (the one-liner ConfigApi already ran twice inline) rather than PetBox.Web.Auth's richer
// IProjectDirectory, for the same layering reason.
//
// The resolve pipeline itself is NOT reimplemented here — ResolveAllAsync is a thin wrapper over
// the existing ResolvePipeline.ResolveAll, so the precedence/ambiguity rule has exactly one body.
public interface IConfigDirectory
{
	// The workspace a project key belongs to, or null if the project does not exist. Both Conf's
	// project-not-found check and Create/Delete's cross-workspace authorization derive from this
	// one lookup.
	Task<string?> GetProjectWorkspaceAsync(string projectKey, CancellationToken ct = default);

	// Resolves every distinct path in the workspace's binding set (deleted rows included — the
	// pipeline itself filters IsDeleted) against `requestTags`. Throws AmbiguousConfigException
	// for the first path with competing equally-specific bindings (unchanged from ResolvePipeline).
	Task<IReadOnlyList<ResolveMatch>> ResolveAllAsync(
		string workspaceKey, IReadOnlyList<string> requestTags, CancellationToken ct = default);

	// The workspace's ACTIVE bindings, ordered by path — the config page's browse list.
	Task<IReadOnlyList<ConfigBinding>> ListActiveBindingsAsync(string workspaceKey, CancellationToken ct = default);

	// One binding by id, active or not (mirrors the page's Reveal lookup, which never filtered
	// IsDeleted — a revealed secret's history entry is written regardless).
	Task<ConfigBinding?> GetBindingAsync(string workspaceKey, long id, CancellationToken ct = default);

	// Plain create (REST POST /bindings) — NOT the MCP upsert's PUT-by-(path,tagset) supersede;
	// always inserts a fresh row. ContentHash is computed here so callers can't drift from the
	// hash the MCP tools already compute the same way.
	Task<ConfigBinding> CreateBindingAsync(
		string workspaceKey, string path, string value, string tags, BindingKind kind, CancellationToken ct = default);

	// Soft-delete by id, for the config page's Delete button. Writes a "Delete" history row (old
	// value redacted for Secret kind) BEFORE the soft-delete, exactly like the page did inline.
	// Returns false (no-op, no history written) if the binding is missing or already deleted.
	Task<bool> DeleteBindingByIdAsync(string workspaceKey, long id, string actor, CancellationToken ct = default);

	// Soft-delete by (path, tags), for the REST DELETE endpoint. Deliberately writes NO history
	// row — the REST surface never did either; that asymmetry with DeleteBindingByIdAsync is
	// preserved, not an oversight. Returns whether a row was actually deleted.
	Task<bool> DeleteBindingByPathTagsAsync(string workspaceKey, string path, string tags, CancellationToken ct = default);

	// Records a "Reveal" history row for a secret binding's plaintext reveal. Encryption/caching
	// stay in the caller (ISecretEncryptor/IMemoryCache are not database concerns).
	Task RecordRevealAsync(string workspaceKey, ConfigBinding binding, string actor, CancellationToken ct = default);

	// EVERY binding in the workspace, deleted rows included — the input the resolve pipeline and
	// the tag-vocabulary aggregation both want (each does its own IsDeleted filtering, and the
	// preview page must be able to explain a path that resolves to nothing).
	Task<IReadOnlyList<ConfigBinding>> ListAllBindingsAsync(string workspaceKey, CancellationToken ct = default);

	// How many ACTIVE bindings the workspace has. `tag`, when given, counts only bindings carrying
	// that exact comma-separated token (e.g. "project:acme") — the project-home counter's filter.
	Task<int> CountActiveBindingsAsync(string workspaceKey, string? tag = null, CancellationToken ct = default);

	// The editor's save: insert-or-update a binding AND write its history row, in one context.
	//
	// This is deliberately one call rather than the page orchestrating get/update/insert-history,
	// because the three steps have to agree on `now`, on the Version bump and on the no-op rule —
	// which is precisely what drifted while this logic lived inline in the page.
	//
	// Semantics preserved verbatim from the page it replaces: a save whose content hash matches the
	// live row is a NO-OP (no Version bump, no history row); re-saving a soft-deleted binding
	// revives it and logs "Undelete"; a Secret binding's value is redacted to "(secret)" in history.
	Task<ConfigBindingSaveResult> SaveBindingAsync(
		string workspaceKey, ConfigBindingDraft draft, string actor, CancellationToken ct = default);

	// --- History ---------------------------------------------------------------

	// History rows for the workspace, newest first, capped at `limit`. `pathSubstring` filters on
	// a substring of Path when given.
	Task<IReadOnlyList<ConfigBindingHistoryEntry>> ListHistoryAsync(
		string workspaceKey, string? pathSubstring = null, int limit = 500, CancellationToken ct = default);

	// --- Tag vocabulary --------------------------------------------------------

	// The workspace's DECLARED tag keys, ordered by key. Distinct from the tag values actually in
	// use, which are aggregated from the binding set (ListAllBindingsAsync).
	Task<IReadOnlyList<TagVocabularyEntry>> ListTagsAsync(string workspaceKey, CancellationToken ct = default);

	// Declare a tag key. Idempotent — an already-declared key is silently left alone.
	Task DeclareTagAsync(string workspaceKey, string tagKey, string? description, CancellationToken ct = default);

	// Remove a declared tag key by row id. Does NOT touch bindings already carrying it.
	Task RetireTagAsync(string workspaceKey, long id, CancellationToken ct = default);

	// --- Saved config filters (core.db SavedConfigFilters — the config page's named tag-filter
	// chips; workspace-scoped, unrelated to ConfigDb). ---

	Task<IReadOnlyList<SavedConfigFilter>> ListSavedFiltersAsync(string workspaceKey, CancellationToken ct = default);

	// Upsert by (workspaceKey, name): updates FilterTags on an existing row, else inserts.
	Task SaveFilterAsync(string workspaceKey, string name, string filterTags, CancellationToken ct = default);

	Task DeleteFilterAsync(string workspaceKey, long id, CancellationToken ct = default);
}

// What the editor wants written. `Id` null (or <= 0) means "insert a new binding".
//
// Encryption happens in the CALLER (ISecretEncryptor is not a database concern, exactly as
// RecordRevealAsync already assumes), so a Secret binding arrives here with Value empty and the
// Ciphertext/Iv/AuthTag triple filled in.
public sealed record ConfigBindingDraft(
	long? Id,
	string Path,
	string Tags,
	BindingKind Kind,
	string Value,
	string? Ciphertext = null,
	string? Iv = null,
	string? AuthTag = null);

// `NotFound` = the draft named an Id that no longer exists (someone deleted it in another tab).
// `DuplicateOfId` = the save succeeded, but another binding now has the SAME (Path, Tags); the
// resolve pipeline breaks that tie by id (older wins), so this is a warning to surface, not a
// failure to roll back — same as the page behaved before.
public sealed record ConfigBindingSaveResult(long SavedId, bool NotFound, long? DuplicateOfId);

public sealed class ConfigDirectory(IConfigDbFactory configFactory, ICoreDbFactory coreFactory) : IConfigDirectory
{
	public async Task<string?> GetProjectWorkspaceAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = coreFactory.Open();
		var project = await db.Projects.FirstOrDefaultAsync(p => p.Key == projectKey, ct);
		return project?.WorkspaceKey;
	}

	public async Task<IReadOnlyList<ResolveMatch>> ResolveAllAsync(
		string workspaceKey, IReadOnlyList<string> requestTags, CancellationToken ct = default)
	{
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		var bindings = await configDb.Bindings.ToListAsync(ct);
		return ResolvePipeline.ResolveAll(requestTags, bindings);
	}

	public async Task<IReadOnlyList<ConfigBinding>> ListActiveBindingsAsync(string workspaceKey, CancellationToken ct = default)
	{
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		return await configDb.Bindings.Where(b => !b.IsDeleted).OrderBy(b => b.Path).ToListAsync(ct);
	}

	public async Task<ConfigBinding?> GetBindingAsync(string workspaceKey, long id, CancellationToken ct = default)
	{
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		return await configDb.Bindings.FirstOrDefaultAsync(b => b.Id == id, ct);
	}

	public async Task<ConfigBinding> CreateBindingAsync(
		string workspaceKey, string path, string value, string tags, BindingKind kind, CancellationToken ct = default)
	{
		var now = DateTime.UtcNow;
		var binding = new ConfigBinding
		{
			Path = path,
			Value = value,
			Tags = tags,
			Kind = kind,
			Version = 1,
			ContentHash = BindingContentHash.Compute(path, tags, kind, value, null),
			CreatedAt = now,
			UpdatedAt = now,
		};

		using var configDb = configFactory.NewConfigDb(workspaceKey);
#pragma warning disable CA2016
		var id = Convert.ToInt64(await configDb.InsertWithIdentityAsync(binding));
#pragma warning restore CA2016
		return binding with { Id = id };
	}

	public async Task<bool> DeleteBindingByIdAsync(string workspaceKey, long id, string actor, CancellationToken ct = default)
	{
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		var existing = await configDb.Bindings.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted, ct);
		if (existing is null) return false;

		var now = DateTime.UtcNow;
		await configDb.InsertAsync(new ConfigBindingHistoryEntry
		{
			BindingId = existing.Id,
			Action = "Delete",
			Path = existing.Path,
			Tags = existing.Tags,
			Kind = existing.Kind,
			OldValue = existing.Kind == BindingKind.Plain ? existing.Value : "(secret)",
			NewValue = null,
			Actor = actor,
			At = now,
		}, token: ct);
		await configDb.Bindings
			.Where(b => b.Id == id)
			.Set(b => b.IsDeleted, true)
			.Set(b => b.DeletedAt, (DateTime?)now)
			.Set(b => b.UpdatedAt, now)
			.UpdateAsync(ct);
		return true;
	}

	public async Task<bool> DeleteBindingByPathTagsAsync(string workspaceKey, string path, string tags, CancellationToken ct = default)
	{
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		var now = DateTime.UtcNow;
		var deleted = await configDb.Bindings
			.Where(b => b.Path == path && b.Tags == tags && !b.IsDeleted)
			.Set(b => b.IsDeleted, true)
			.Set(b => b.DeletedAt, (DateTime?)now)
			.Set(b => b.UpdatedAt, now)
			.UpdateAsync(ct);
		return deleted > 0;
	}

	public async Task RecordRevealAsync(string workspaceKey, ConfigBinding binding, string actor, CancellationToken ct = default)
	{
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		await configDb.InsertAsync(new ConfigBindingHistoryEntry
		{
			BindingId = binding.Id,
			Action = "Reveal",
			Path = binding.Path,
			Tags = binding.Tags,
			Kind = binding.Kind,
			OldValue = null,
			NewValue = null,
			Actor = actor,
			At = DateTime.UtcNow,
		}, token: ct);
	}

	public async Task<IReadOnlyList<ConfigBinding>> ListAllBindingsAsync(string workspaceKey, CancellationToken ct = default)
	{
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		return await configDb.Bindings.ToListAsync(ct);
	}

	public async Task<int> CountActiveBindingsAsync(
		string workspaceKey, string? tag = null, CancellationToken ct = default)
	{
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		var active = configDb.Bindings.Where(b => !b.IsDeleted);
		if (string.IsNullOrEmpty(tag))
			return await active.CountAsync(ct);

		// Tags is a comma-separated string, so the token test cannot be expressed in SQL without
		// a false positive on "project:acme-two" when filtering "project:acme". Materialize the
		// active set and match tokens in memory — the same trade the page made inline.
		var rows = await active.Select(b => b.Tags).ToListAsync(ct);
		return rows.Count(t => HasTag(t, tag));
	}

	static bool HasTag(string tags, string tag)
	{
		foreach (var t in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	public async Task<ConfigBindingSaveResult> SaveBindingAsync(
		string workspaceKey, ConfigBindingDraft draft, string actor, CancellationToken ct = default)
	{
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		var now = DateTime.UtcNow;
		var storedValue = draft.Kind == BindingKind.Secret ? string.Empty : draft.Value;
		var newHash = BindingContentHash.Compute(draft.Path, draft.Tags, draft.Kind, storedValue, draft.Ciphertext);

		long savedId;

		if (draft.Id is { } id and > 0)
		{
			var existing = await configDb.Bindings.FirstOrDefaultAsync(b => b.Id == id, ct);
			if (existing is null)
				return new ConfigBindingSaveResult(0, NotFound: true, DuplicateOfId: null);

			// Skip the Version bump on no-op edits (same content + same tags + same kind). A
			// soft-deleted row never counts as a no-op — reviving it IS a change.
			var isNoOp = string.Equals(existing.ContentHash, newHash, StringComparison.Ordinal)
				&& !existing.IsDeleted;

			await configDb.UpdateAsync(existing with
			{
				Path = draft.Path,
				Tags = draft.Tags,
				Kind = draft.Kind,
				Value = storedValue,
				Ciphertext = draft.Ciphertext,
				Iv = draft.Iv,
				AuthTag = draft.AuthTag,
				Version = isNoOp ? existing.Version : existing.Version + 1,
				ContentHash = newHash,
				IsDeleted = false,
				DeletedAt = null,
				UpdatedAt = now,
			}, token: ct);

			if (!isNoOp)
			{
				await configDb.InsertAsync(new ConfigBindingHistoryEntry
				{
					BindingId = id,
					Action = existing.IsDeleted ? "Undelete" : "Update",
					Path = draft.Path,
					Tags = draft.Tags,
					Kind = draft.Kind,
					OldValue = existing.Kind == BindingKind.Plain ? existing.Value : "(secret)",
					NewValue = draft.Kind == BindingKind.Plain ? storedValue : "(secret)",
					Actor = actor,
					At = now,
				}, token: ct);
			}

			savedId = id;
		}
		else
		{
			savedId = await configDb.InsertWithInt64IdentityAsync(new ConfigBinding
			{
				Path = draft.Path,
				Tags = draft.Tags,
				Kind = draft.Kind,
				Value = storedValue,
				Ciphertext = draft.Ciphertext,
				Iv = draft.Iv,
				AuthTag = draft.AuthTag,
				Version = 1,
				ContentHash = newHash,
				CreatedAt = now,
				UpdatedAt = now,
			}, token: ct);

			await configDb.InsertAsync(new ConfigBindingHistoryEntry
			{
				BindingId = savedId,
				Action = "Create",
				Path = draft.Path,
				Tags = draft.Tags,
				Kind = draft.Kind,
				OldValue = null,
				NewValue = draft.Kind == BindingKind.Plain ? storedValue : "(secret)",
				Actor = actor,
				At = now,
			}, token: ct);
		}

		// The duplicate probe uses the JUST-PERSISTED id as self — for a new binding the draft's
		// Id is still null, so without this the fresh row would match itself and report a spurious
		// duplicate.
		var duplicate = await configDb.Bindings
			.Where(b => b.Path == draft.Path && b.Tags == draft.Tags && b.Id != savedId)
			.Select(b => (long?)b.Id)
			.FirstOrDefaultAsync(ct);

		return new ConfigBindingSaveResult(savedId, NotFound: false, DuplicateOfId: duplicate);
	}

	public async Task<IReadOnlyList<ConfigBindingHistoryEntry>> ListHistoryAsync(
		string workspaceKey, string? pathSubstring = null, int limit = 500, CancellationToken ct = default)
	{
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		var query = configDb.History.AsQueryable();
		if (!string.IsNullOrWhiteSpace(pathSubstring))
		{
			var p = pathSubstring;
			query = query.Where(h => h.Path.Contains(p));
		}
		return await query.OrderByDescending(h => h.At).Take(limit).ToListAsync(ct);
	}

	public async Task<IReadOnlyList<TagVocabularyEntry>> ListTagsAsync(string workspaceKey, CancellationToken ct = default)
	{
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		return await configDb.Tags.OrderBy(t => t.TagKey).ToListAsync(ct);
	}

	public async Task DeclareTagAsync(
		string workspaceKey, string tagKey, string? description, CancellationToken ct = default)
	{
		var key = tagKey.Trim();
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		if (await configDb.Tags.AnyAsync(t => t.TagKey == key, ct))
			return;

		await configDb.InsertAsync(new TagVocabularyEntry
		{
			TagKey = key,
			Description = description?.Trim(),
			CreatedAt = DateTime.UtcNow,
		}, token: ct);
	}

	public async Task RetireTagAsync(string workspaceKey, long id, CancellationToken ct = default)
	{
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		await configDb.Tags.Where(t => t.Id == id).DeleteAsync(ct);
	}

	public async Task<IReadOnlyList<SavedConfigFilter>> ListSavedFiltersAsync(string workspaceKey, CancellationToken ct = default)
	{
		using var db = coreFactory.Open();
		return await db.SavedConfigFilters
			.Where(f => f.WorkspaceKey == workspaceKey)
			.OrderBy(f => f.Name)
			.ToListAsync(ct);
	}

	public async Task SaveFilterAsync(string workspaceKey, string name, string filterTags, CancellationToken ct = default)
	{
		using var db = coreFactory.Open();
		var existing = await db.SavedConfigFilters.FirstOrDefaultAsync(
			f => f.WorkspaceKey == workspaceKey && f.Name == name, ct);
		if (existing is null)
			await db.InsertAsync(new SavedConfigFilter
			{
				WorkspaceKey = workspaceKey,
				Name = name,
				FilterTags = filterTags,
				CreatedAt = DateTime.UtcNow,
			}, token: ct);
		else
			await db.SavedConfigFilters.Where(f => f.Id == existing.Id).Set(f => f.FilterTags, filterTags).UpdateAsync(ct);
	}

	public async Task DeleteFilterAsync(string workspaceKey, long id, CancellationToken ct = default)
	{
		using var db = coreFactory.Open();
		await db.SavedConfigFilters.Where(f => f.Id == id && f.WorkspaceKey == workspaceKey).DeleteAsync(ct);
	}
}
