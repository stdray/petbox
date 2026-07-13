using System.ComponentModel;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// Typed config-binding tools (mcp-typing wave), on the uniform-entity-verbs matrix mirroring
// tasks/memory/comments: config_binding_upsert (batch write) · config_binding_search (list =
// search without q) · config_binding_delta (id-cursor catch-up) · config_binding_get (addressed
// single read) · config_binding_delete. Provisioning ops — admin:provision scope, no per-project
// claim. Secrets are stored encrypted, never returned as plaintext Value.
//
// DEVIATION from the temporal families: config bindings are NOT watermark-versioned. A binding is
// identified by (path, normalized tag SET) within a workspace; the row's `Version` is always 1 and
// its addressable identity is the auto-increment `Id`. A write is a PUT-by-(path,tagset) that
// supersedes (soft-closes) the active twin and inserts a NEW immutable row. So there is no CAS
// conflict (a PUT always applies or the batch throws) and the store-wide monotonic cursor is the
// MAX id, which config_binding_delta reads as `sinceVersion`. See the config_binding_upsert essay.
//
// Tools throw on a failed Assert*/validation; McpErrorEnvelopeFilter renders the {error} body.
[McpServerToolType]
public static class ConfigTools
{
	[McpServerTool(Name = "config_binding_upsert", Title = "Upsert config bindings", UseStructuredContent = true, OutputSchemaType = typeof(ConfigBindingsUpsertResult))]
	[Description("""
		Batch upsert config bindings in a workspace's config store — PUT by (path, tag SET). Each
		item {path, tags, value?, kind?} either creates a new (path, tagset) or SUPERSEDES the active
		twin with the same path + normalized tagset (the twin is soft-closed; its id lands in
		`superseded`). NO version watermark — bindings are immutable rows keyed by an auto-increment
		id (a change mints a new id), so there is no CAS/conflict: `applied` is true, or the whole
		ATOMIC batch throws (nothing written). `kind`: 'Plain' (default) or 'Secret' (value stored
		ENCRYPTED, never returned). Every item's tags must include 'ws:{workspaceKey}'. Requires
		admin:provision.
		[[full]]
		Batch upsert config bindings — the uniform write verb that replaced the single-binding
		config_binding_upsert. `items` is a JSON array; each item is a PUT keyed by (path, normalized
		tag SET) within `workspaceKey`:
		  • path — the dotted config path, e.g. 'app/connectionString'.
		  • tags — a comma-separated tag set (order/case/whitespace of the CSV don't matter for
		    identity); MUST include 'ws:{workspaceKey}'.
		  • value — the value (omit for empty). For kind=Secret it is stored ENCRYPTED (needs
		    PETBOX_MASTER_KEY); the plaintext never lands in the Value column and is never returned.
		  • kind — 'Plain' (default) or 'Secret'.
		PUT semantics: if an ACTIVE binding with the same path and the same normalized tag SET already
		exists it is SUPERSEDED (soft-closed in the same transaction; its id is reported in
		`superseded`) and a NEW row is inserted — no silent duplicates, so the resolve pipeline can
		never see the same (path, tagset) active twice. A DIFFERENT tagset at the same path is a normal
		specificity variant and coexists.
		This is NOT a temporal watermark upsert: config rows are immutable and keyed by an
		auto-increment id (`Version` is always 1), so there is no version baseline, no Stale/Future
		conflict, and no in-place edit — a change mints a new id. The ATOMIC batch applies every item
		in one transaction; a validation failure (missing path/tags, a tagset without ws:{workspaceKey},
		an unknown kind, a Secret without PETBOX_MASTER_KEY) throws and rolls the whole batch back.
		Returns { applied, currentVersion, added[], updated[], superseded[], conflicts[] }. `applied`
		is true on success (a failure throws instead). `added` = items that created a fresh (path,
		tagset); `updated` = items that superseded an active twin (a new immutable row replaced it);
		each row carries { id, path, tags, kind } — never a value. `superseded` = the soft-closed twin
		ids. `conflicts` is always empty (PUT-by-key has no CAS conflict; it exists for matrix shape
		parity). `currentVersion` is the store's MAX binding id — the monotonic cursor: pass it to
		config_binding_delta as `sinceVersion` for the catch-up. To delete a binding use
		config_binding_delete (delete is not folded into upsert). Requires admin:provision.
		""")]
	public static async Task<ConfigBindingsUpsertResult> BindingUpsertAsync(
		IHttpContextAccessor http, IConfigDbFactory configFactory, ISecretEncryptor secrets,
		[Description("Workspace key the bindings belong to.")] string workspaceKey,
		[Description("Array of binding items: { path (dotted config path), tags (CSV; must include 'ws:{workspaceKey}'), value? (encrypted when kind=Secret), kind? ('Plain'|'Secret') }.")] ConfigBindingItemInput[] items,
		[Description("Batch policy. TRUE (default) = ATOMIC: a bad item aborts the WHOLE call, nothing is written. FALSE = PARTIAL apply (explicit opt-in, same promise as the other batch verbs): valid bindings LAND, each refused one comes back in conflicts[] with its reason. Config rows are immutable and keyed by (path, tagset) with NO version watermark, so the mode DEGENERATES here — there is no stale conflict to have, and no intra-batch references, so every item is independent. That is the point: the flag means the same thing everywhere, you never have to remember which verb is the exception.")] bool atomic = true,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(workspaceKey)) throw new ArgumentException("workspaceKey is required");

		// Validate + prepare every item BEFORE touching the DB, so a bad item aborts the batch
		// without a partial write. Secret encryption happens here (needs PETBOX_MASTER_KEY).
		var prepared = new List<PreparedBinding>(items.Length);
		var conflicts = new List<ConfigBindingConflict>();
		foreach (var it in items)
		{
			try
			{
				var path = it.Path;
				var tags = it.Tags;
				if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("each binding item needs a path");
				if (string.IsNullOrWhiteSpace(tags)) throw new ArgumentException("each binding item needs tags");
				if (!tags.Contains($"ws:{workspaceKey}", StringComparison.OrdinalIgnoreCase))
					throw new ArgumentException($"Tags must include 'ws:{workspaceKey}' (path '{path}')");

				var kind = ParseKind(it.Kind);
				var plaintext = it.Value ?? string.Empty;
				var storedValue = plaintext;
				string? cipher = null, iv = null, authTag = null;
				if (kind == BindingKind.Secret)
				{
					if (!secrets.IsAvailable)
						throw new InvalidOperationException("Secret bindings require PETBOX_MASTER_KEY to be configured.");
					var bundle = secrets.Encrypt(plaintext);
					(cipher, iv, authTag) = (bundle.Ciphertext, bundle.Iv, bundle.AuthTag);
					storedValue = string.Empty;
				}
				prepared.Add(new PreparedBinding(path!, tags!, kind, storedValue, cipher, iv, authTag, Tagset(tags!)));
			}
			// PARTIAL: the refusal becomes a per-item conflict instead of failing the call. No
			// cascade step is needed — a config binding cannot reference another binding, so the
			// reference graph is empty and TemporalStore's cascade would reject nothing further.
			// The degeneracy is the CONTRACT, not a gap: `atomic:false` means the same thing here
			// as everywhere ("valid entries land, refused ones come back with a reason"), it just
			// has no watermark and no graph to work on.
			catch (Exception ex) when (!atomic && ex is ArgumentException or InvalidOperationException)
			{
				conflicts.Add(new(it.Path ?? "", it.Tags ?? "", nameof(TemporalConflictKind.Rejected), ex.Message));
			}
		}

		var now = DateTime.UtcNow;
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		var added = new List<ConfigBindingRow>();
		var updated = new List<ConfigBindingRow>();
		var supersededAll = new List<long>();

		using (var tx = await configDb.BeginTransactionAsync(ct))
		{
			foreach (var p in prepared)
			{
				// PUT-by-(path,tagset): supersede active twins (same path ignore-case, same tag SET).
				// Queried inside the tx, so an earlier item's insert in THIS batch is visible — a
				// later same-(path,tagset) item supersedes it, keeping intra-batch idempotency.
				var superseded = (await configDb.Bindings
						.Where(b => !b.IsDeleted)
						.Select(b => new { b.Id, b.Path, b.Tags })
						.ToListAsync(ct))
					.Where(b => string.Equals(b.Path, p.Path, StringComparison.OrdinalIgnoreCase) && Tagset(b.Tags).SetEquals(p.Tagset))
					.Select(b => b.Id)
					.ToList();
				if (superseded.Count > 0)
					await configDb.Bindings
						.Where(b => superseded.Contains(b.Id) && !b.IsDeleted)
						.Set(b => b.IsDeleted, true)
						.Set(b => b.DeletedAt, (DateTime?)now)
						.Set(b => b.UpdatedAt, now)
						.UpdateAsync(ct);
#pragma warning disable CA2016
				var id = Convert.ToInt64(await configDb.InsertWithIdentityAsync(new ConfigBinding
				{
					Path = p.Path,
					Value = p.StoredValue,
					Tags = p.Tags,
					Kind = p.Kind,
					Ciphertext = p.Cipher,
					Iv = p.Iv,
					AuthTag = p.AuthTag,
					Version = 1,
					ContentHash = BindingContentHash.Compute(p.Path, p.Tags, p.Kind, p.StoredValue, p.Cipher),
					CreatedAt = now,
					UpdatedAt = now,
				}));
#pragma warning restore CA2016
				var row = new ConfigBindingRow(id, p.Path, p.Tags, p.Kind.ToString());
				if (superseded.Count > 0) { updated.Add(row); supersededAll.AddRange(superseded); }
				else added.Add(row);
			}
			await tx.CommitAsync(ct);
		}

		var currentVersion = await MaxIdAsync(configDb, ct);
		// `applied` keeps its meaning: something landed. An atomic call that got here always did
		// (a refusal threw); a partial call where every item was rejected wrote nothing.
		return new ConfigBindingsUpsertResult(prepared.Count > 0 || conflicts.Count == 0, currentVersion, added, updated, supersededAll, conflicts);
	}

	[McpServerTool(Name = "config_binding_search", Title = "Read config bindings (list + search)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ConfigBindingsSearchResult))]
	[Description("THE config-binding read verb — one tool for LISTING (no `q`) and SEARCH (`q`). Without `q`: a deterministic list of a workspace's ACTIVE bindings (id, path, tags, kind), ordered by path, optionally narrowed by `pathPrefix`. With `q`: a case-insensitive SUBSTRING match over path/tags/plaintext-value (config has no FTS/vector index, so a query degrades to this lexical floor — `retrievers` reports semantic:false). Secret values are NEVER returned (rows carry no value), so there is no bodyLen knob; the ~30k-char output budget still applies (overflow → truncated/omitted/hint). Requires admin:provision.")]
	public static async Task<ConfigBindingsSearchResult> BindingSearchAsync(
		IHttpContextAccessor http, IConfigDbFactory configFactory,
		[Description("Workspace key to read bindings for.")] string workspaceKey,
		[Description("Search query: a case-insensitive substring matched over path/tags/plaintext-value. Omit for a deterministic listing (list = search without q).")] string? q = null,
		[Description("Keep only bindings whose path starts with this prefix (case-insensitive). Applies in both modes.")] string? pathPrefix = null,
		[Description("Max rows returned (0 = no cap — the output budget still applies).")] int? limit = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(workspaceKey)) throw new ArgumentException("workspaceKey is required");
		using var configDb = configFactory.NewConfigDb(workspaceKey);

		// Project the enum raw (linq2db can't translate Enum.ToString()); the substring filters run
		// in memory (config stores are small provisioning stores). `Value` is read ONLY to match a
		// plaintext query — it is never placed on the wire (secret-safety invariant).
		var raw = await configDb.Bindings
			.Where(b => !b.IsDeleted)
			.OrderBy(b => b.Path)
			.Select(b => new { b.Id, b.Path, b.Tags, b.Kind, b.Value })
			.ToListAsync(ct);

		IEnumerable<Binding> rows = raw.Select(b => new Binding(b.Id, b.Path, b.Tags, b.Kind, b.Value));
		if (!string.IsNullOrWhiteSpace(pathPrefix))
			rows = rows.Where(b => b.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase));

		var hasQuery = !string.IsNullOrWhiteSpace(q);
		if (hasQuery)
		{
			var needle = q!.Trim();
			rows = rows.Where(b =>
				b.Path.Contains(needle, StringComparison.OrdinalIgnoreCase)
				|| b.Tags.Contains(needle, StringComparison.OrdinalIgnoreCase)
				|| (b.Kind == BindingKind.Plain && b.Value.Contains(needle, StringComparison.OrdinalIgnoreCase)));
		}

		var wire = rows.Select(b => new ConfigBindingRow(b.Id, b.Path, b.Tags, b.Kind.ToString()));
		if (limit is > 0) wire = wire.Take(limit.Value);
		var list = wire.ToList();

		var (kept, omitted) = new ResponseBudget().Take(list);
		var retrievers = hasQuery ? new RetrieverInfo(Lexical: true, Semantic: false, Degraded: false) : null;
		return omitted == 0
			? new ConfigBindingsSearchResult(kept, retrievers)
			: new ConfigBindingsSearchResult(kept, retrievers, Truncated: true, Omitted: omitted, Hint: SearchBudgetHint);
	}

	[McpServerTool(Name = "config_binding_delta", Title = "Config bindings delta since cursor", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ConfigBindingsUpsertResult))]
	[Description("Return config bindings created/replaced since `sinceVersion` (no writes) — the catch-up surface (a config_binding_upsert ack echoes only its own call; pass its `currentVersion` here). The cursor is the store's auto-increment binding id: this returns the ACTIVE bindings with id > sinceVersion (in `added`; config rows are immutable so a change is a new id, hence everything is an add — `updated` is empty). NOTE (documented limitation): a pure delete of a pre-cursor binding is NOT surfaced here — the id cursor can't see a soft-delete of a low-id row; use config_binding_search for the current active set. Requires admin:provision.")]
	public static async Task<ConfigBindingsUpsertResult> BindingDeltaAsync(
		IHttpContextAccessor http, IConfigDbFactory configFactory,
		[Description("Workspace key the bindings belong to.")] string workspaceKey,
		[Description("The binding-id cursor from a prior config_binding_upsert/_delta `currentVersion`.")] long sinceVersion,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(workspaceKey)) throw new ArgumentException("workspaceKey is required");
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		var current = await MaxIdAsync(configDb, ct);
		var rows = await configDb.Bindings
			.Where(b => !b.IsDeleted && b.Id > sinceVersion)
			.OrderBy(b => b.Id)
			.Select(b => new { b.Id, b.Path, b.Tags, b.Kind })
			.ToListAsync(ct);
		var added = rows.Select(b => new ConfigBindingRow(b.Id, b.Path, b.Tags, b.Kind.ToString())).ToList();
		return new ConfigBindingsUpsertResult(Applied: true, current, added, [], [], []);
	}

	[McpServerTool(Name = "config_binding_get", Title = "Get one config binding", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ConfigBindingRow))]
	[Description("Return ONE active config binding by its id (the addressed single read; mirrors memory_get/comments_get). Carries id/path/tags/kind — never the value (secret-safety). A missing/deleted id is a not-found ERROR (never a bare null — a declared outputSchema demands structured content, so the error rides the isError channel). Requires admin:provision.")]
	public static async Task<ConfigBindingRow> BindingGetAsync(
		IHttpContextAccessor http, IConfigDbFactory configFactory,
		[Description("Workspace key the binding belongs to.")] string workspaceKey,
		[Description("Binding id (from config_binding_search/_upsert).")] long id,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(workspaceKey)) throw new ArgumentException("workspaceKey is required");
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		var b = await configDb.Bindings
			.Where(x => x.Id == id && !x.IsDeleted)
			.Select(x => new { x.Id, x.Path, x.Tags, x.Kind })
			.FirstOrDefaultAsync(ct)
			?? throw new InvalidOperationException($"config binding '{id}' not found in workspace '{workspaceKey}'");
		return new ConfigBindingRow(b.Id, b.Path, b.Tags, b.Kind.ToString());
	}

	[McpServerTool(Name = "config_binding_delete", Title = "Delete a config binding", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(ConfigBindingDeletedResult))]
	[Description("Soft-deletes a config binding by id (the row is kept, marked deleted). Requires admin:provision.")]
	public static async Task<ConfigBindingDeletedResult> BindingDeleteAsync(
		IHttpContextAccessor http, IConfigDbFactory configFactory,
		[Description("Workspace key the binding belongs to.")] string workspaceKey,
		[Description("Binding id (from config_binding_search).")] long id,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(workspaceKey)) throw new ArgumentException("workspaceKey is required");
		using var configDb = configFactory.NewConfigDb(workspaceKey);
		var now = DateTime.UtcNow;
		var updated = await configDb.Bindings
			.Where(b => b.Id == id && !b.IsDeleted)
			.Set(b => b.IsDeleted, true)
			.Set(b => b.DeletedAt, (DateTime?)now)
			.Set(b => b.UpdatedAt, now)
			.UpdateAsync(ct);
		if (updated == 0) throw new InvalidOperationException("Binding not found");
		return new ConfigBindingDeletedResult(true, id);
	}

	// The store-wide monotonic cursor: the max binding id across ALL rows (active or soft-closed),
	// since identity increments on every insert. 0 for an empty store.
	static async Task<long> MaxIdAsync(ConfigDb configDb, CancellationToken ct) =>
		await configDb.Bindings.Select(b => (long?)b.Id).MaxAsync(ct) ?? 0;

	// The binding-identity tag SET: CSV split, trimmed, blanks dropped, ignore-case — the same
	// equality the resolve pipeline applies when it declares two bindings ambiguous.
	static HashSet<string> Tagset(string raw) =>
		new(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
			StringComparer.OrdinalIgnoreCase);

	static BindingKind ParseKind(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return BindingKind.Plain;
		if (Enum.TryParse<BindingKind>(raw, ignoreCase: true, out var k)) return k;
		throw new ArgumentException($"Unknown kind '{raw}'. Known: {string.Join(", ", Enum.GetNames<BindingKind>())}");
	}

	// Surfaced on ConfigBindingsSearchResult.Hint when the rows were cut by the response budget.
	const string SearchBudgetHint =
		"Output budget exceeded: config binding rows were truncated (see truncated/omitted). Narrow " +
		"the read: `pathPrefix` (a path subtree), `q` (a substring), or a smaller `limit`.";

	// A validated, encryption-ready binding to insert (secret ciphertext computed up front).
	readonly record struct PreparedBinding(
		string Path, string Tags, BindingKind Kind, string StoredValue, string? Cipher, string? Iv, string? AuthTag, HashSet<string> Tagset);

	// An in-memory binding row for the search filters (Value read only to match a plaintext query).
	readonly record struct Binding(long Id, string Path, string Tags, BindingKind Kind, string Value);
}
