using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Auth;

// One row of the agent-keys admin views. `Key` is the raw key value — it is already stored in
// clear in ApiKeys and both views are admin-gated; it is what the revoke/edit forms post back.
// `LastUsedAt` is served FRESH (the stored column merged with the in-memory stamp — see ListAsync);
// null means the key has genuinely never authenticated, and the table says so in words.
public sealed record AgentKeyRow(
	string Key,
	string Name,
	string ProjectKey,
	string Scopes,
	DateTime CreatedAt,
	DateTime? ExpiresAt,
	bool Expired,
	string? DefaultProjectKey,
	DateTime? LastUsedAt);

// What an admin submitted from the edit form. It is a full REPLACE of the three editable fields,
// not a patch: the form always renders them pre-filled with the current values, so what comes back
// IS the intended end state. `DefaultProject` therefore carries the classic trap — an EMPTY string
// is a deliberate CLEAR (store NULL), not "leave it alone".
public sealed record AgentKeyEdit(
	string Key,
	string Name,
	IReadOnlyList<string> Scopes,
	string? DefaultProject);

// The outcome of an edit. Refused and NotFound are deliberately different: NotFound is the answer to
// a key that is not there OR not yours (a workspace admin must not be able to tell those apart), and
// it becomes a 404. Refused is a key the caller MAY address but an edit that cannot be honoured —
// it carries the reason to the user, because a refusal nobody can see is a silent failure.
public abstract record KeyUpdateResult
{
	KeyUpdateResult() { }

	public sealed record Updated : KeyUpdateResult;
	public sealed record NotFound : KeyUpdateResult;
	public sealed record Refused(string Reason) : KeyUpdateResult;
}

// The list + revoke + edit logic behind BOTH agent-key admin pages: the system-wide one
// (/ui/admin/sys/agent-keys, SysAdmin, every key) and the workspace-scoped one
// (/ui/admin/ws/{workspaceKey}/agent-keys, WorkspaceAdmin, only the keys of THIS workspace's
// projects). `workspaceKey == null` IS the system scope — sysadmin sees, revokes and edits
// everything; a non-null one confines all three to the projects of that workspace.
//
// The scoping lives HERE, not in the page, because revoke and edit are addressed by the key VALUE:
// a page that merely filtered its rendered list would still mutate any key a forged POST named
// (workspace-access-isolation's IDOR, one layer down). RevokeAsync and UpdateAsync therefore re-prove
// ownership INSIDE the DELETE/UPDATE statement itself — the row is touched only if it belongs to a
// project of the given workspace, in ONE statement, so no TOCTOU window opens between check and write.
//
// A cross-project key (ProjectKey == "*") belongs to no single workspace, so it never appears in a
// workspace-scoped list and can never be revoked or edited from one: only a sysadmin may touch it.
// That falls out of `Owned` for free — no project row has Key == "*", so the ownership predicate
// simply never matches it.
public sealed class AgentKeyAdminService(
	ICoreDbFactory dbf,
	IKeyStatService stats,
	ConfigApiKeyLookup configKeys)
{
	// THE confinement predicate, used by every read and every write. Null workspace = the sysadmin's
	// deliberate free pass; otherwise the key must belong to a project of that workspace.
	static IQueryable<ApiKey> Owned(PetBoxDb db, IQueryable<ApiKey> q, string? workspaceKey) =>
		workspaceKey is null
			? q
			: q.Where(k => db.Projects.Any(p => p.Key == k.ProjectKey && p.WorkspaceKey == workspaceKey));

	public async Task<IReadOnlyList<AgentKeyRow>> ListAsync(string? workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();

		// All DB-minted keys (expiring and permanent). Config-declared keys (appsettings/env) are
		// not rows and don't appear here — which is also why they have no edit affordance at all.
		var rows = await Owned(db, db.ApiKeys, workspaceKey)
			.OrderByDescending(k => k.CreatedAt)
			.ToListAsync(ct);

		var now = DateTime.UtcNow;
		return [.. rows.Select(k => new AgentKeyRow(
			k.Key, k.Name, k.ProjectKey, k.Scopes, k.CreatedAt, k.ExpiresAt,
			k.ExpiresAt != null && k.ExpiresAt <= now,
			k.DefaultProjectKey,
			// The merge that keeps the column honest (spec apikey-last-used): KeyStatFlusher persists
			// the marks only every ~5 minutes, so the STORED value alone would show a key as never-used
			// for minutes after it was actually used. Take the later of stored and the live in-memory
			// stamp. This reads a singleton dictionary — no extra DB work, and no write on a render.
			Later(k.LastUsedAt, stats.LastUsed(k.Key))))];
	}

	static DateTime? Later(DateTime? stored, DateTime? inMemory) =>
		(stored, inMemory) switch
		{
			(null, var m) => m,
			(var s, null) => s,
			var (s, m) => s >= m ? s : m,
		};

	// Returns false when the key does not exist OR does not belong to `workspaceKey` — the caller
	// answers 404 either way, so a workspace admin cannot even probe for the existence of another
	// tenant's key.
	public async Task<bool> RevokeAsync(string key, string? workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await Owned(db, db.ApiKeys.Where(k => k.Key == key), workspaceKey).DeleteAsync(ct) > 0;
	}

	// Rename / re-scope / set + clear the default project of an ALREADY-ISSUED key (spec
	// apikey-mutable). The secret itself never changes — `key` is the address, not a field.
	// Mirrors the apikey_update MCP verb's semantics, with workspace confinement added on top.
	public async Task<KeyUpdateResult> UpdateAsync(AgentKeyEdit edit, string? workspaceKey, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(edit.Key))
			return new KeyUpdateResult.NotFound();

		// A config-declared key is refused BEFORE anything is written, and LOUDLY. CompositeApiKeyLookup
		// asks config FIRST and the DB only on a miss, so a stored row for a config key would never be
		// read back: the edit would look like it worked and change nothing. Such a key has no row in the
		// table (ListAsync reads the DB), so this path is only reachable by a hand-crafted POST — and it
		// still gets a reason rather than a shrug. Config keys are operator-declared and belong to no
		// workspace, so naming one leaks nothing to a ws admin: they had to already know the secret to
		// type it here.
		if (configKeys.FindByKey(edit.Key) is not null)
			return new KeyUpdateResult.Refused(
				"This key is declared in configuration (Auth:ApiKeys), not in the database. The config file "
				+ "owns its lifecycle and wins on every auth lookup, so a stored change would never take "
				+ "effect — edit appsettings instead, or mint a database key.");

		var name = edit.Name?.Trim() ?? string.Empty;
		if (name.Length == 0)
			return new KeyUpdateResult.Refused("A key needs a name — it is its only human-readable label in this list.");

		// The submitted set REPLACES the current one (it is not additive), and is validated against the
		// canonical catalog exactly like a mint is: an update must never be able to grant what a mint
		// could not.
		var (valid, invalid) = ApiKeyScopes.Validate(string.Join(',', edit.Scopes));
		if (invalid.Count > 0)
			return new KeyUpdateResult.Refused($"Unknown scopes: {string.Join(", ", invalid)}");
		if (valid.Count == 0)
			return new KeyUpdateResult.Refused("Select at least one scope — a key with no scopes can do nothing.");

		using var db = dbf.Open();

		// Ownership is proven TWICE. Here, so that a foreign key is indistinguishable from a missing one
		// (404, no existence oracle) and so the defaultProject rules below are decided against a row the
		// caller is actually entitled to see — and again inside the UPDATE, which is what closes the
		// TOCTOU window this read would otherwise open.
		var existing = await Owned(db, db.ApiKeys.Where(k => k.Key == edit.Key), workspaceKey)
			.FirstOrDefaultAsync(ct);
		if (existing is null)
			return new KeyUpdateResult.NotFound();

		string? defaultProject = null;
		var requested = edit.DefaultProject?.Trim() ?? string.Empty;
		if (requested.Length > 0)
		{
			// The create-time invariants, reused verbatim: a default project is meaningful only on a
			// cross-project key (a project-scoped one already defaults to its own claim), and it must
			// name a project that exists.
			if (existing.ProjectKey != ProjectScope.AllProjects)
				return new KeyUpdateResult.Refused(
					"A default project is only meaningful on a cross-project ('*') key — a project-scoped key "
					+ "already defaults to its own project.");
			if (!await db.Projects.AnyAsync((Project p) => p.Key == requested, ct))
				return new KeyUpdateResult.Refused($"Project '{requested}' not found.");
			defaultProject = requested;
		}
		// requested == "" leaves defaultProject NULL — the explicit CLEAR. Storing the empty string
		// instead would leave a key defaulting to a project named "", which resolves to nothing on the
		// next call: "" is not a value here, it is the absence of one.

		// The write re-proves ownership in the statement. A forged POST from another workspace's admin
		// matches zero rows and changes nothing — the rendered list was never the guard.
		var affected = await Owned(db, db.ApiKeys.Where(k => k.Key == edit.Key), workspaceKey)
			.Set(k => k.Name, name)
			.Set(k => k.Scopes, string.Join(',', valid))
			.Set(k => k.DefaultProjectKey, defaultProject)
			.UpdateAsync(ct);

		return affected > 0 ? new KeyUpdateResult.Updated() : new KeyUpdateResult.NotFound();
	}
}
