using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Log.Core.Data;

// THE catalog of SavedQueries — project-scoped named KQL queries the Logs page lets a member save.
// Mirrors LogStore's shape for the same reason: a fresh, caller-owned core.db connection per method
// off the factory, never a request-shared one.
//
// projectKey is part of the ADDRESS everywhere, never a filter bolted on afterwards: a delete naming
// another project's saved-query id matches nothing (Pages/Logs/Index.cshtml.cs's own comment on
// OnPostDeleteAsync calls this out as defense in depth against a same-workspace member guessing/
// enumerating another project's id — this door keeps that guarantee in the one place that writes).
public interface ISavedQueryStore
{
	Task<IReadOnlyList<SavedQuery>> ListAsync(string projectKey, CancellationToken ct = default);

	Task<SavedQuery?> FindAsync(string projectKey, string name, CancellationToken ct = default);

	// Upsert by (ProjectKey, Name): creates on first save, updates Kql + UpdatedAt on every one after.
	Task<SavedQuery> SaveAsync(string projectKey, string name, string kql, CancellationToken ct = default);

	Task DeleteAsync(string projectKey, long id, CancellationToken ct = default);
}

public sealed class SavedQueryStore(ICoreDbFactory core) : ISavedQueryStore
{
	public async Task<IReadOnlyList<SavedQuery>> ListAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = core.Open();
		return await db.SavedQueries
			.Where(q => q.ProjectKey == projectKey)
			.OrderBy(q => q.Name)
			.ToListAsync(ct);
	}

	public async Task<SavedQuery?> FindAsync(string projectKey, string name, CancellationToken ct = default)
	{
		using var db = core.Open();
		return await db.SavedQueries
			.FirstOrDefaultAsync(q => q.ProjectKey == projectKey && q.Name == name, ct);
	}

	public async Task<SavedQuery> SaveAsync(string projectKey, string name, string kql, CancellationToken ct = default)
	{
		var trimmedName = name.Trim();
		using var db = core.Open();
		var existing = await db.SavedQueries
			.FirstOrDefaultAsync(q => q.ProjectKey == projectKey && q.Name == trimmedName, ct);

		if (existing is not null)
		{
			var updated = existing with { Kql = kql, UpdatedAt = DateTime.UtcNow };
			await db.UpdateAsync(updated, token: ct);
			return updated;
		}

		var now = DateTime.UtcNow;
		var created = new SavedQuery
		{
			Name = trimmedName,
			Kql = kql,
			ProjectKey = projectKey,
			CreatedAt = now,
			UpdatedAt = now,
		};
		var id = await db.InsertWithInt64IdentityAsync(created, token: ct);
		return created with { Id = id };
	}

	public async Task DeleteAsync(string projectKey, long id, CancellationToken ct = default)
	{
		using var db = core.Open();
		await db.SavedQueries
			.Where(q => q.Id == id && q.ProjectKey == projectKey)
			.DeleteAsync(ct);
	}
}
