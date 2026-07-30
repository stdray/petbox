using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Auth;

// One row of the agent-keys admin views. `Key` is the raw key value — it is already stored in
// clear in ApiKeys and both views are admin-gated; it is what the revoke/edit forms post back.
// `LastUsedAt` is served FRESH (the stored column merged with the in-memory stamp — see ListAsync);
// null means the key has genuinely never authenticated, and the table says so in words.
// `CreatedBy` is the issuing actor (spec access-attribution) — null on every key minted before M049,
// which the table renders as "unknown" in words rather than as an empty cell that reads like a bug.
public sealed record AgentKeyRow(
	string Key,
	string Name,
	string ProjectKey,
	string Scopes,
	DateTime? ExpiresAt,
	bool Expired,
	string? DefaultProjectKey,
	DateTime? LastUsedAt,
	string? CreatedBy);

// What an admin submitted from the edit form. It is a full REPLACE of the three editable fields,
// not a patch: the form always renders them pre-filled with the current values, so what comes back
// IS the intended end state. `DefaultProject` therefore carries the classic trap — an EMPTY string
// is a deliberate CLEAR (store NULL), not "leave it alone".
public sealed record AgentKeyEdit(
	string Key,
	string Name,
	IReadOnlyList<string> Scopes,
	string? DefaultProject);

// What the PROVISIONING surface (the apikey_* MCP tools) asks for when it mints a key. Scopes are
// already validated and canonicalized by the caller (ApiKeyScopes.Validate) — what is left here is
// everything that needs the DATABASE to decide: the project must exist, a sandbox-only key scoped to
// a specific project needs that project flagged `sandbox`, and a default project must name a real one.
public sealed record AgentKeyMint(
	string Name,
	IReadOnlyList<string> Scopes,
	string ProjectKey,
	DateTime? ExpiresAt = null,
	string? DefaultProjectKey = null,
	bool SandboxOnly = false);

public abstract record KeyMintResult
{
	KeyMintResult() { }

	// Carries the FULL row: the raw key value is readable exactly once, from here.
	public sealed record Minted(ApiKey Key) : KeyMintResult;

	// A project the mint names does not exist — distinct from Refused, because the callers map the
	// two onto different errors (a missing thing vs. an argument that cannot be honoured).
	public sealed record NotFound(string Reason) : KeyMintResult;
	public sealed record Refused(string Reason) : KeyMintResult;
}

// A PATCH of an already-issued key (the apikey_update MCP verb): a null field is "leave it alone",
// which is what makes this different from AgentKeyEdit (the admin FORM, a full replace). The two
// clearable fields carry an explicit sentinel: ExpiresInSeconds == 0 clears the expiry, DefaultProject
// == "" drops the default project.
public sealed record AgentKeyPatch(
	string Key,
	string? Name = null,
	string? Scopes = null,
	long? ExpiresInSeconds = null,
	string? DefaultProject = null);

public abstract record KeyPatchResult
{
	KeyPatchResult() { }

	public sealed record Patched(ApiKey Key, IReadOnlyList<string> Touched) : KeyPatchResult;

	// The KEY is not there.
	public sealed record NotFound : KeyPatchResult;

	// A THING THE PATCH NAMES is not there (today: the default project). Split from Refused because
	// the callers map "you named something that does not exist" and "that edit cannot be honoured"
	// onto different errors.
	public sealed record Missing(string Reason) : KeyPatchResult;

	// The key exists but is CONFIG-DECLARED: the file owns its lifecycle, so no stored change could
	// ever take effect. A state refusal, not a bad argument.
	public sealed record Immutable(string Reason) : KeyPatchResult;
	public sealed record Refused(string Reason) : KeyPatchResult;
}

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

	// ── THE GRANT GATE (work workspaceadmin-self-issue-admin-provision-root) ──────────────────
	//
	// The second question the mint path never asked. `ApiKeyScopes.Validate` answers "is this a scope
	// that EXISTS?"; this answers "may THIS issuer hand it out?". Without it a WorkspaceAdmin — which
	// every self-service tenant becomes on creating a workspace — ticked `admin:provision` on their
	// own project's key and walked out with cross-tenant root, because tenancy was standing in for
	// capability.
	//
	// IT LIVES HERE, IN THE SERVICE, ON PURPOSE, and the same sentence is already written three times
	// in this file about ownership: the rendered checkbox list was never the guard. `scopes` arrives
	// as a string[] straight off a form, so a hand-crafted POST carries whatever it likes no matter
	// what the markup chose to render. Every door onto ApiKeys' scope column goes through this
	// function — mint, the admin form's replace, the project page's re-scope, the MCP patch — so a
	// future fifth caller cannot re-derive the rule or forget it, exactly as the ownership predicate
	// cannot be forgotten.
	//
	// THE CARRY-OVER, which is the subtle half. On a RE-SCOPE the submitted set REPLACES the current
	// one, and the UI now hides privileged checkboxes from an issuer who may not grant them — so a
	// workspace admin merely renaming a key would submit a set with the key's existing
	// `deploy:write` missing, and a naive gate would read that as a request and silently STRIP it.
	// Silently downgrading a working key is its own outage. So the privileged scopes a key ALREADY
	// has are preserved untouched, and only an attempt to ADD one the key does not have is refused —
	// loudly, naming the scope. Escalation is blocked; nothing legitimate breaks.
	//
	// Returns the effective scope set, or a refusal reason (never both).
	static (List<string>? Scopes, string? Refusal) GateGrant(
		IReadOnlyList<string> requested, string? existingScopes, KeyIssuer issuer)
	{
		if (issuer.MayGrantPrivileged)
			return ([.. requested.Distinct(StringComparer.Ordinal)], null);

		// The privileged scopes the key ALREADY carries — the set this issuer may keep but not change.
		// Empty for a mint (there is no existing key), which is what makes a fresh privileged scope a
		// refusal there and never a carry-over.
		var held = ApiKeyScopes.PrivilegedIn(
			(existingScopes ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

		var adding = ApiKeyScopes.PrivilegedIn(requested).Except(held, StringComparer.Ordinal).ToList();
		if (adding.Count > 0)
			return (null,
				$"Not allowed to grant {(adding.Count == 1 ? "scope" : "scopes")} {string.Join(", ", adding)}: "
				+ "these reach beyond a single tenant (cross-tenant provisioning or the fleet-wide deploy "
				+ "control-plane), so only a sysadmin — or a key that already holds admin:provision — may "
				+ "issue them. Administering a workspace does not carry that authority. Ask an operator to "
				+ "mint this key, or pick scopes confined to this project.");

		// Kept, not stripped: whatever privileged scopes the key already had survive the edit.
		var effective = requested.Distinct(StringComparer.Ordinal).ToList();
		effective.AddRange(held.Where(h => !effective.Contains(h, StringComparer.Ordinal)));
		return (effective, null);
	}

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
			k.Key, k.Name, k.ProjectKey, k.Scopes, k.ExpiresAt,
			k.ExpiresAt != null && k.ExpiresAt <= now,
			k.DefaultProjectKey,
			// The merge that keeps the column honest (spec apikey-last-used): KeyStatFlusher persists
			// the marks only every ~5 minutes, so the STORED value alone would show a key as never-used
			// for minutes after it was actually used. Take the later of stored and the live in-memory
			// stamp. This reads a singleton dictionary — no extra DB work, and no write on a render.
			Later(k.LastUsedAt, stats.LastUsed(k.Key)),
			k.CreatedBy))];
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
	public async Task<KeyUpdateResult> UpdateAsync(
		AgentKeyEdit edit, string? workspaceKey, KeyIssuer issuer, CancellationToken ct = default)
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

		// May this issuer hand out what the submission asks for? Decided AFTER ownership so a caller
		// cannot use the privilege refusal as an existence oracle for another tenant's key: a key that
		// is not theirs is 404 first, whatever scopes the POST named.
		var (granted, refusal) = GateGrant(valid, existing.Scopes, issuer);
		if (refusal is not null)
			return new KeyUpdateResult.Refused(refusal);
		valid = granted!;

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

	// ── the PROJECT-confined pair (the per-project settings page) ─────────────────────────────
	//
	// The project settings page (Pages/Admin/ProjectDetail) lists exactly ONE project's keys and
	// revokes / re-scopes them BY KEY VALUE. Routing those two writes through the workspace-confined
	// pair above would be a real WIDENING, not a simplification: that page's admin typically holds
	// WorkspaceAdmin over a dozen projects, so `Owned(..., workspaceKey)` would happily honour a
	// forged POST naming a SIBLING project's key — a key the page never rendered and the admin was
	// not looking at. The predicate this surface actually needs is the PROJECT, so it is welded into
	// the statement here: the same shape as `Owned`, one level finer. The page filtered its rendered
	// list; the rendered list was never the guard.
	//
	// The scope rules are welded in too (an edit must never grant what a mint could not), so the page
	// cannot re-derive — or forget — them.

	public async Task<bool> RevokeForProjectAsync(string key, string projectKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.ApiKeys
			.Where(k => k.Key == key && k.ProjectKey == projectKey)
			.DeleteAsync(ct) > 0;
	}

	// Replace the scopes of one of `projectKey`'s keys. NotFound when the key does not exist OR is not
	// this project's — indistinguishable on purpose, so the page cannot be used to probe for the
	// existence of a sibling project's key.
	public async Task<KeyUpdateResult> SetScopesForProjectAsync(
		string key, string projectKey, IReadOnlyList<string> scopes, KeyIssuer issuer, CancellationToken ct = default)
	{
		var (valid, invalid) = ApiKeyScopes.Validate(string.Join(',', scopes));
		if (invalid.Count > 0)
			return new KeyUpdateResult.Refused("Unknown scope(s): " + string.Join(", ", invalid));
		if (valid.Count == 0)
			return new KeyUpdateResult.Refused("At least one scope is required.");

		using var db = dbf.Open();

		// The key's CURRENT scopes, read project-confined — the grant gate needs them to tell an
		// escalation ("add admin:provision") from a benign edit of a key that already holds a
		// privileged scope. This read is project-confined exactly like the UPDATE below, so it is not
		// a new way to observe a sibling project's key; the UPDATE still re-proves ownership in the
		// statement, which is what keeps the TOCTOU window closed.
		var existing = await db.ApiKeys
			.Where(k => k.Key == key && k.ProjectKey == projectKey)
			.FirstOrDefaultAsync(ct);
		if (existing is null)
			return new KeyUpdateResult.NotFound();

		var (granted, refusal) = GateGrant(valid, existing.Scopes, issuer);
		if (refusal is not null)
			return new KeyUpdateResult.Refused(refusal);

		var affected = await db.ApiKeys
			.Where(k => k.Key == key && k.ProjectKey == projectKey)
			.Set(k => k.Scopes, string.Join(',', granted!))
			.UpdateAsync(ct);

		return affected > 0 ? new KeyUpdateResult.Updated() : new KeyUpdateResult.NotFound();
	}

	// ── the PROVISIONING surface (apikey_* MCP tools, admin:provision) ────────────────────────
	//
	// These are NOT workspace-confined: admin:provision is the fleet-wide onboarding scope (it is
	// what mints a cross-project key in the first place), so there is no workspace to confine to.
	// The confinement of the ADMIN PAGES above is unchanged — the two surfaces have different
	// subjects (a cookie user in a workspace vs. a provisioning key), and only the pages carry the
	// `workspaceKey` predicate. What they share is this file: the DB is opened here, once, and no
	// caller re-derives the key rules.

	// A project's keys, newest-usage merged (the same freshness contract as ListAsync). Addressed by
	// project, which is how the MCP surface addresses keys — including the literal "*" project, the
	// cross-project keys' claim.
	public async Task<IReadOnlyList<ApiKey>> ListByProjectAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		var rows = await db.ApiKeys
			.Where(k => k.ProjectKey == projectKey)
			.OrderBy(k => k.CreatedAt)
			.ToListAsync(ct);

		// Same merge as ListAsync: the stored column is persisted in ~5-minute batches, so it alone
		// would report a key used seconds ago as idle.
		return [.. rows.Select(k => k with { LastUsedAt = Later(k.LastUsedAt, stats.LastUsed(k.Key)) })];
	}

	// Mint a key. Everything that needs the DATABASE to decide lives here — the project must exist, a
	// sandbox-only key scoped to a SPECIFIC project needs that project flagged `sandbox` (otherwise it
	// could never write anything: ProjectScope.AuthorizesAsync would refuse every call), and a default
	// project must name a real one. The cross-project claim ("*") is not a project row, so it is never
	// looked up. The raw secret is generated here and returned exactly once, in the Minted row.
	public async Task<KeyMintResult> MintAsync(AgentKeyMint mint, KeyIssuer issuer, CancellationToken ct = default)
	{
		using var db = dbf.Open();

		// FIRST, before the database is even consulted: may this issuer grant these scopes? A mint has
		// no existing key, so there is nothing to carry over — any privileged scope here is a fresh
		// grant and is refused unless the issuer holds that authority. This is the exact check whose
		// absence let a WorkspaceAdmin mint themselves cross-tenant root.
		var (granted, refusal) = GateGrant(mint.Scopes, existingScopes: null, issuer);
		if (refusal is not null)
			return new KeyMintResult.Refused(refusal);

		if (mint.ProjectKey != ProjectScope.AllProjects)
		{
			var project = await db.Projects.FirstOrDefaultAsync((Project p) => p.Key == mint.ProjectKey, ct);
			if (project is null)
				return new KeyMintResult.NotFound($"Project '{mint.ProjectKey}' not found");
			if (mint.SandboxOnly && !project.Sandbox)
				return new KeyMintResult.Refused(
					$"sandboxOnly:true requires projectKey '{mint.ProjectKey}' to be a sandbox project (project_create sandbox:true) — "
					+ "a sandbox-only key scoped to a non-sandbox project could never write anything");
		}

		if (mint.DefaultProjectKey is { } dflt
			&& !await db.Projects.AnyAsync((Project p) => p.Key == dflt, ct))
			return new KeyMintResult.NotFound($"Project '{dflt}' not found");

		var row = new ApiKey
		{
			Key = $"yb_key_{Guid.NewGuid():N}",
			ProjectKey = mint.ProjectKey,
			Scopes = string.Join(',', granted!),
			Name = mint.Name.Trim(),
			CreatedAt = DateTime.UtcNow,
			ExpiresAt = mint.ExpiresAt,
			DefaultProjectKey = mint.DefaultProjectKey,
			SandboxOnly = mint.SandboxOnly,
			// spec access-attribution: the issuer, by label — `user:<name>` from an admin page,
			// `key:<name>` from apikey_create. Recorded at the one door every mint goes through, so no
			// caller can produce an unattributed key.
			CreatedBy = issuer.Actor,
		};
		await db.InsertAsync(row, token: ct);
		return new KeyMintResult.Minted(row);
	}

	// PATCH an already-issued key: a field left null keeps its stored value bit-for-bit. Same rules a
	// mint obeys (an update must never grant what a mint could not) — and the same refusal of a
	// config-declared key, which has no row here and whose lifecycle belongs to appsettings.
	public async Task<KeyPatchResult> PatchAsync(AgentKeyPatch patch, KeyIssuer issuer, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(patch.Key))
			return new KeyPatchResult.Refused("key is required");

		// Refused BEFORE anything is written: CompositeApiKeyLookup asks config FIRST and the DB only
		// on a miss, so a stored row for a config key would never be read back — the update would look
		// like it worked and change nothing.
		if (configKeys.FindByKey(patch.Key) is not null)
			return new KeyPatchResult.Immutable(
				"This key is declared in configuration (Auth:ApiKeys) — its lifecycle belongs to the config file, "
				+ "not the database. A stored change would never take effect (config wins on every auth lookup), "
				+ "so it is refused: edit appsettings (or mint a DB key with apikey_create) instead.");

		using var db = dbf.Open();

		var existing = await db.ApiKeys.FirstOrDefaultAsync((ApiKey k) => k.Key == patch.Key, ct);
		if (existing is null) return new KeyPatchResult.NotFound();

		// Patch onto the LOADED row: the UPDATE below writes every mapped column, and the ones the
		// caller did not name carry `existing`'s data.
		var updated = existing;
		var touched = new List<string>();

		if (patch.Name is not null)
		{
			if (string.IsNullOrWhiteSpace(patch.Name))
				return new KeyPatchResult.Refused("name cannot be blank (omit it to leave the name unchanged)");
			updated = updated with { Name = patch.Name.Trim() };
			touched.Add("name");
		}

		if (patch.Scopes is not null)
		{
			var (valid, invalid) = ApiKeyScopes.Validate(patch.Scopes);
			if (invalid.Count > 0) return new KeyPatchResult.Refused($"Unknown scopes: {string.Join(", ", invalid)}");
			if (valid.Count == 0) return new KeyPatchResult.Refused("At least one valid scope is required");
			// The same grant gate the mint and the admin forms pass through — an update must never be
			// able to grant what a mint could not, which this file already asserts twice about scope
			// VALIDITY and now also holds for scope AUTHORITY. In practice today's only caller is
			// apikey_update (admin:provision, so it passes), but the rule belongs to the door, not to
			// the caller that happens to be standing in it.
			var (granted, refusal) = GateGrant(valid, existing.Scopes, issuer);
			if (refusal is not null) return new KeyPatchResult.Refused(refusal);
			updated = updated with { Scopes = string.Join(',', granted!) };
			touched.Add("scopes");
		}

		if (patch.ExpiresInSeconds is { } secs)
		{
			if (secs < 0)
				return new KeyPatchResult.Refused("expiresInSeconds must be >= 0 (0 clears the expiry — the key stops expiring)");
			updated = updated with { ExpiresAt = secs == 0 ? null : DateTime.UtcNow.AddSeconds(secs) };
			touched.Add("expiry");
		}

		if (patch.DefaultProject is not null)
		{
			var dflt = patch.DefaultProject.Trim();
			if (dflt.Length == 0)
			{
				// The explicit clear — distinct from omitting the argument, which changes nothing.
				updated = updated with { DefaultProjectKey = null };
			}
			else
			{
				if (existing.ProjectKey != ProjectScope.AllProjects)
					return new KeyPatchResult.Refused(
						"defaultProject is only valid on a cross-project ('*') key (a project-scoped key already defaults to its own project)");
				if (!await db.Projects.AnyAsync((Project p) => p.Key == dflt, ct))
					return new KeyPatchResult.Missing($"Project '{dflt}' not found");
				updated = updated with { DefaultProjectKey = dflt };
			}
			touched.Add("defaultProject");
		}

		if (touched.Count == 0)
			return new KeyPatchResult.Refused(
				"Nothing to update — pass at least one of name / scopes / expiresInSeconds / defaultProject");

		await db.UpdateAsync(updated, token: ct);
		return new KeyPatchResult.Patched(updated, touched);
	}

	// Revoke by key value alone, no workspace confinement — the provisioning surface's delete.
	public Task<bool> DeleteAsync(string key, CancellationToken ct = default) =>
		RevokeAsync(key, workspaceKey: null, ct);

	// The node-agent key of the deploy control-plane: ONE live key per node, addressed by its
	// `node:<id>` name. Re-minting ROTATES it — the previous one is dropped in the same call, so a
	// node can never end up with two live keys. Its "project" is the node id (the deploy plane is
	// fleet-wide, not project-scoped); the scopes are fixed by the caller (poll + heartbeat + logs).
	//
	// DELIBERATELY NOT GATED, and it is not an escape hatch. `agent:poll`/`agent:heartbeat` are
	// classified Privileged, but this path takes no caller-chosen scope set — the constant is baked
	// into DeployTools/DeployAgentService, and reaching it at all already requires `deploy:write`,
	// itself privileged and therefore already gated at the mint that issued the calling key. Routing
	// enrollment through the grant gate would check an authority the caller has necessarily proven
	// one step earlier. The key is attributed to the system, because that is literally who made it.
	public async Task<string> MintNodeKeyAsync(string keyRef, string nodeId, string scopes, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		await db.ApiKeys.Where(k => k.Name == keyRef).DeleteAsync(ct);
		var key = $"yb_key_node_{Guid.NewGuid():N}";
		await db.InsertAsync(new ApiKey
		{
			Key = key,
			ProjectKey = nodeId,
			Scopes = scopes,
			Name = keyRef,
			CreatedAt = DateTime.UtcNow,
			CreatedBy = KeyIssuer.System.Actor,
		}, token: ct);
		return key;
	}
}
