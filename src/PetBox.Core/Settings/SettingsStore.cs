using LinqToDB;
using PetBox.Core.Data;

namespace PetBox.Core.Settings;

// One stored setting, as the resolver sees it: the (Type, Value) pair still ENCODED. Decoding is
// the RESOLVER's job — it owns the CLR type and the encryptor. The store's job is core.db and
// nothing else, so it never learns what a "secret" or an "enum" is.
public sealed record StoredSetting(string Type, string Value);

// One property to persist, already ENCODED by the resolver. A whole save is a LIST of these:
// SetAsync is a single user edit, and applying half of it is never a state anyone asked for, so
// the list is written in ONE transaction (see SettingsStore.WriteAsync).
public sealed record SettingWrite(string Path, string Type, string Value);

// The DB side of one resolve: the scope chain from the requested scope up to System, plus EVERY
// settings row that lives anywhere on it.
//
// This is the shape that makes the extraction pay for itself. The resolver used to run, per
// [Setting] PROPERTY, one chain-building query plus up to one row query per link — an N+1 over the
// scope cascade. The chain does not depend on the property (only the TopLevel CAP does, and that is
// a pure filter over the chain), and the rows are all in one table, so BOTH collapse into a single
// pair of statements taken once per call. The resolver then answers every property from memory.
public sealed class SettingsSnapshot(
	IReadOnlyList<(Scope Scope, string ScopeKey)> chain,
	IReadOnlyDictionary<(string Scope, string ScopeKey, string Path), StoredSetting> rows)
{
	// Deepest scope FIRST, System last — the order the cascade is walked in, so "first row wins"
	// stays literally true for the caller.
	public IReadOnlyList<(Scope Scope, string ScopeKey)> Chain { get; } = chain;

	// The stored row at exactly this link of the chain, or null when this scope does not override
	// the property. Never a DB call — the snapshot is complete by construction.
	public StoredSetting? Find(Scope scope, string scopeKey, string path) =>
		rows.TryGetValue((scope.ToString(), scopeKey, path), out var row) ? row : null;
}

// THE door to the `Settings` table in core.db. Every read and every write of a setting goes through
// here — `SettingsResolver` (the caller) holds no factory and cannot open a connection, which is
// AGENTS.md's "the database is visible only in the service layer" applied to the one component that
// sits on the hottest path in the app: a cross-scope search drives the resolver on EVERY embed.
//
// Each method takes ONE call-owned connection from the factory. A linq2db DataConnection is not
// thread-safe and LoadChainAsync is reached from parallel branches of a single request scope
// (CapabilityRouter -> LlmRegistryLevelResolver -> ISettingsResolver.GetAsync), so a connection is
// never held across calls and never shared. No transaction is opened on the read path, and the
// write path opens exactly one — nothing here calls another core-db service, so the
// SQLITE_LOCKED-on-Cache=Shared trap in AGENTS.md stays unreachable.
public interface ISettingsStore
{
	// The scope chain for (deepestScope, deepestScopeKey) and every row on it, in one connection.
	// The chain is UNFILTERED by TopLevel: capping is the resolver's business (it is per-property),
	// and it is a filter over this list, not a different list.
	Task<SettingsSnapshot> LoadChainAsync(Scope deepestScope, string deepestScopeKey, CancellationToken ct = default);

	// Insert-or-update every write at (scope, scopeKey), ALL of them or NONE. The atomicity is the
	// point: the resolver's Encode() can throw midway (an unencryptable secret, a bad cast), and
	// before the transaction each property was its own autocommit — an admin form could silently
	// half-apply. Encoding happens BEFORE this call, so a throw never reaches an open transaction.
	Task WriteAsync(Scope scope, string scopeKey, IReadOnlyList<SettingWrite> writes, long? updatedBy, CancellationToken ct = default);

	// Drop the override row at exactly (scope, scopeKey, path); reads then fall back up the cascade.
	Task DeleteAsync(Scope scope, string scopeKey, string path, CancellationToken ct = default);
}

public sealed class SettingsStore(ICoreDbFactory factory) : ISettingsStore
{
	public async Task<SettingsSnapshot> LoadChainAsync(
		Scope deepestScope, string deepestScopeKey, CancellationToken ct = default)
	{
		using var db = factory.Open();

		var chain = await BuildChainAsync(db, deepestScope, deepestScopeKey, ct);
		var rows = await LoadRowsAsync(db, chain, ct);
		return new SettingsSnapshot(chain, rows);
	}

	public async Task WriteAsync(
		Scope scope, string scopeKey, IReadOnlyList<SettingWrite> writes, long? updatedBy, CancellationToken ct = default)
	{
		if (writes.Count == 0) return;

		var scopeName = scope.ToString();
		var now = DateTime.UtcNow;

		using var db = factory.Open();
		await using var tx = await db.BeginTransactionAsync(ct);

		foreach (var w in writes)
		{
			var updated = await db.Settings
				.Where(s => s.Scope == scopeName && s.ScopeKey == scopeKey && s.Path == w.Path)
				.Set(s => s.Type, w.Type)
				.Set(s => s.Value, w.Value)
				.Set(s => s.UpdatedAt, now)
				.Set(s => s.UpdatedBy, updatedBy)
				.UpdateAsync(ct);

			// UPDATE-then-INSERT rather than SELECT-then-branch: one statement fewer per property,
			// and the row cannot appear between the look and the leap (we are inside the tx).
			if (updated == 0)
			{
				await db.InsertAsync(new Setting
				{
					Scope = scopeName,
					ScopeKey = scopeKey,
					Path = w.Path,
					Type = w.Type,
					Value = w.Value,
					UpdatedAt = now,
					UpdatedBy = updatedBy,
				}, token: ct);
			}
		}

		await tx.CommitAsync(ct);
	}

	public async Task DeleteAsync(Scope scope, string scopeKey, string path, CancellationToken ct = default)
	{
		var scopeName = scope.ToString();

		// A single statement — atomic on its own, no explicit transaction needed.
		using var db = factory.Open();
		await db.Settings
			.Where(s => s.Scope == scopeName && s.ScopeKey == scopeKey && s.Path == path)
			.DeleteAsync(ct);
	}

	// The scope cascade, deepest first. The ONE query it needs is the project -> workspace edge,
	// and it runs on the CALLER'S connection (the one LoadChainAsync owns) — never a second one.
	static async Task<IReadOnlyList<(Scope Scope, string ScopeKey)>> BuildChainAsync(
		PetBoxDb db, Scope deepest, string deepestKey, CancellationToken ct)
	{
		var chain = new List<(Scope, string)> { (deepest, deepestKey) };

		switch (deepest)
		{
			case Scope.System:
				break;
			case Scope.Workspace:
				chain.Add((Scope.System, "$"));
				break;
			case Scope.Project:
				var ws = await WorkspaceOfAsync(db, deepestKey, ct);
				if (ws is not null)
					chain.Add((Scope.Workspace, ws));
				chain.Add((Scope.System, "$"));
				break;
			case Scope.Service:
				// ScopeKey format: "{projectKey}/{serviceKey}"
				var slash = deepestKey.IndexOf('/');
				if (slash > 0)
				{
					var projKey = deepestKey[..slash];
					chain.Add((Scope.Project, projKey));
					var svcWs = await WorkspaceOfAsync(db, projKey, ct);
					if (svcWs is not null)
						chain.Add((Scope.Workspace, svcWs));
				}
				chain.Add((Scope.System, "$"));
				break;
			case Scope.User:
				chain.Add((Scope.System, "$"));
				break;
			case Scope.Membership:
				// ScopeKey format: "{userId}:{workspaceKey}"
				var colon = deepestKey.IndexOf(':');
				if (colon > 0)
				{
					chain.Add((Scope.User, deepestKey[..colon]));
					chain.Add((Scope.Workspace, deepestKey[(colon + 1)..]));
				}
				chain.Add((Scope.System, "$"));
				break;
		}

		return chain;
	}

	static async Task<string?> WorkspaceOfAsync(PetBoxDb db, string projectKey, CancellationToken ct) =>
		await db.Projects
			.Where(p => p.Key == projectKey)
			.Select(p => p.WorkspaceKey)
			.FirstOrDefaultAsync(ct);

	// Every row on the chain in ONE statement. SQLite has no row-value IN, so the WHERE is the
	// CROSS product of the chain's scopes and its scope keys — a deliberate over-fetch of at most a
	// handful of rows, which the exact-pair filter below then discards. The alternative (one query
	// per link) is the N+1 this whole class exists to remove.
	static async Task<IReadOnlyDictionary<(string, string, string), StoredSetting>> LoadRowsAsync(
		PetBoxDb db, IReadOnlyList<(Scope Scope, string ScopeKey)> chain, CancellationToken ct)
	{
		var wanted = chain.Select(c => (c.Scope.ToString(), c.ScopeKey)).ToHashSet();
		var scopeNames = wanted.Select(w => w.Item1).Distinct().ToList();
		var scopeKeys = wanted.Select(w => w.Item2).Distinct().ToList();

		var rows = await db.Settings
			.Where(s => scopeNames.Contains(s.Scope) && scopeKeys.Contains(s.ScopeKey))
			.ToListAsync(ct);

		var map = new Dictionary<(string, string, string), StoredSetting>();
		foreach (var row in rows)
		{
			// Discards the cross-product's phantom pairs — e.g. a row at (Workspace, "$") when the
			// chain carries "$" only for System.
			if (!wanted.Contains((row.Scope, row.ScopeKey))) continue;
			map[(row.Scope, row.ScopeKey, row.Path)] = new StoredSetting(row.Type, row.Value);
		}
		return map;
	}
}
