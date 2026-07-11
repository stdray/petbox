using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// The LLM registry gets its OWN storage in core.db (llm-registry-own-store).
//
// WHY IT MOVES. Endpoints/routes/keys lived in the Config module (`llm/registry`,
// `llm/secret/{endpoint}` bindings). Config is BY INVARIANT the store for EXTERNAL pet projects'
// configuration (spec.md:305) and is partitioned per workspace (`config/{ws}.db`), so the only
// place bindings were ever entered was `$system` — every project in every other workspace resolved
// ZERO routes and semantic search was dead there ("no route configured for Embed"). PetBox's own
// router config is not a pet project's config; it belongs to PetBox, in core.db, with a scope
// cascade of its own. This migration only CREATES the store — nothing reads or writes it yet
// (the old ConfigBindings-backed store still serves production; data import and the DI flip are
// separate steps).
//
// THE SHAPE, AND THE THREE LOCKS THAT MAKE "AN ENDPOINT WITHOUT ITS KEY" UNREPRESENTABLE:
//
//   1. THE KEY LIVES IN THE ENDPOINT ROW (KeyCipher/KeyIv/KeyAuthTag), not in a separate
//      secret entity addressed by endpoint NAME. The old layout keyed secrets by name in a
//      DIFFERENT partition than the registry JSON, so "endpoint exists, key doesn't" was an
//      ordinary state — and the old store treated a key that failed to decrypt as ABSENT and
//      called the upstream UNAUTHENTICATED. Here an endpoint and its key are the same row: you
//      cannot inherit one without the other.
//
//   2. A COMPOSITE FOREIGN KEY (Scope, ScopeKey, Endpoint) -> llm_endpoints(Scope, ScopeKey, Name).
//      A route may only reference an endpoint declared AT ITS OWN LEVEL. A workspace route can
//      not point at a $system endpoint, so no cross-level franken-pair (route from one level, key
//      from another) can even be written. The FK is INLINE in CREATE TABLE: SQLite has no
//      `ALTER TABLE ADD CONSTRAINT`, so FluentMigrator's separate `Create.ForeignKey()` does not
//      arrive; and its typed column-level `.ForeignKey()` is single-column only. A COMPOSITE,
//      inline FK therefore has no typed form at all — hence SqliteDdl.Raw, named and guarded,
//      instead of an anonymous Execute.Sql. Enforcement needs PRAGMA foreign_keys=ON per
//      connection: PetBoxDb.CreateOptions appends `Foreign Keys=True` (see the note there).
//
//   3. (in the resolver, not the schema) LEVEL-ATOMIC resolution: the first level in
//      Project -> Workspace -> System that has ANY route wins WHOLE — endpoints, routes and keys
//      all come from that one level. No merging. The FK above is what makes that safe to rely on.
//
// SCOPE is a string — "System" / "Workspace" (PetBox.Core.Settings.Scope names). "Project" is
// RESERVED: the resolver walks it so the level exists the day we need it, but nothing writes it
// today (the admin rejects it).
//
// Forward-only, additive, no `IF NOT EXISTS` (a migration runs exactly once, gated by VersionInfo;
// a tolerant CREATE could only swallow schema drift).
[Migration(39, "llm_endpoints + llm_routes: the LLM registry moves into core.db, scoped and cascading")]
public sealed class M039_LlmRegistry : SqliteMigration
{
	public override void Up()
	{
		// The endpoint AND its api key, one row. Cipher columns are the AES-GCM triple produced by
		// ISecretEncryptor (base64 ciphertext / iv / tag). All three NULL = a deliberately keyless
		// endpoint (a local model with no auth); all three set = an authenticated endpoint. Anything
		// in between is a corrupt row and the resolver drops the endpoint rather than calling out
		// without credentials.
		Create.Table("llm_endpoints")
			.WithColumn("Scope").AsString().NotNullable().PrimaryKey()
			.WithColumn("ScopeKey").AsString().NotNullable().PrimaryKey()
			.WithColumn("Name").AsString().NotNullable().PrimaryKey()
			.WithColumn("BaseUrl").AsString().NotNullable()
			.WithColumn("CertThumbprint").AsString().Nullable()
			.WithColumn("ConnectTimeoutMs").AsInt32().NotNullable()
			.WithColumn("RequestTimeoutMs").AsInt32().NotNullable()
			.WithColumn("KeyCipher").AsString().Nullable()
			.WithColumn("KeyIv").AsString().Nullable()
			.WithColumn("KeyAuthTag").AsString().Nullable()
			.WithColumn("UpdatedAt").AsDateTime().NotNullable()
			.WithColumn("UpdatedBy").AsInt64().Nullable();

		// The routes. The composite FK is lock #2 above; ON DELETE CASCADE means retiring an
		// endpoint takes its routes with it instead of leaving them dangling at a name that
		// resolves to nothing.
		SqliteDdl.Raw(
			"a COMPOSITE foreign key must be declared INSIDE CREATE TABLE (SQLite has no ALTER TABLE " +
			"ADD CONSTRAINT, so FluentMigrator's separate Create.ForeignKey() never lands), and the " +
			"typed column-level .ForeignKey() is single-column only — a multi-column inline FK has no " +
			"typed FluentMigrator form",
			"""
			CREATE TABLE llm_routes (
				Id TEXT NOT NULL,
				Scope TEXT NOT NULL,
				ScopeKey TEXT NOT NULL,
				Capability TEXT NOT NULL,
				Endpoint TEXT NOT NULL,
				Model TEXT NOT NULL,
				Priority INTEGER NOT NULL,
				Tier TEXT NULL,
				Thinking TEXT NULL,
				UpdatedAt DATETIME NOT NULL,
				UpdatedBy INTEGER NULL,
				PRIMARY KEY (Id),
				FOREIGN KEY (Scope, ScopeKey, Endpoint)
					REFERENCES llm_endpoints (Scope, ScopeKey, Name)
					ON DELETE CASCADE ON UPDATE CASCADE
			);
			""");

		// One route per (level, capability, tier, endpoint, model) — the same model on the same
		// endpoint twice in one capability's chain is a config mistake, not a fallback.
		// Caveat, stated out loud: SQLite (like the SQL standard) treats NULLs as DISTINCT in a
		// UNIQUE index, so this does not deduplicate rows whose Tier is NULL. Those are the
		// "default tier" routes; a duplicate there is harmless (the chain would just try the same
		// provider twice) and de-duplicating it would need an expression index on COALESCE(Tier,'').
		Create.Index("ux_llm_routes_level_capability_tier_endpoint_model").OnTable("llm_routes")
			.OnColumn("Scope").Ascending()
			.OnColumn("ScopeKey").Ascending()
			.OnColumn("Capability").Ascending()
			.OnColumn("Tier").Ascending()
			.OnColumn("Endpoint").Ascending()
			.OnColumn("Model").Ascending()
			.WithOptions().Unique();

		// The resolver's only query shape: "give me this level's routes".
		Create.Index("ix_llm_routes_level").OnTable("llm_routes")
			.OnColumn("Scope").Ascending()
			.OnColumn("ScopeKey").Ascending();
	}

	public override void Down() { } // forward-only
}
