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

	// --- Saved config filters (core.db SavedConfigFilters — the config page's named tag-filter
	// chips; workspace-scoped, unrelated to ConfigDb). ---

	Task<IReadOnlyList<SavedConfigFilter>> ListSavedFiltersAsync(string workspaceKey, CancellationToken ct = default);

	// Upsert by (workspaceKey, name): updates FilterTags on an existing row, else inserts.
	Task SaveFilterAsync(string workspaceKey, string name, string filterTags, CancellationToken ct = default);

	Task DeleteFilterAsync(string workspaceKey, long id, CancellationToken ct = default);
}

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
