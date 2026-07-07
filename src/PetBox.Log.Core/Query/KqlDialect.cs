namespace PetBox.Log.Core.Query;

// Strategy for the BACKEND-SPECIFIC SQL a KQL pipeline compiles to. Today the only LIVE backend is
// SQLite (the per-project log store), and SqliteDialect designates the exact attribute-bound shims in
// KqlSqlExpressions as its translation set — ZERO behavior change. The abstraction is the SEAM a second
// backend (DuckDb, a later wave) plugs into: the pieces that genuinely differ between backends are
//   (a) the scalar-shim set (JSON bag extract, string/date functions, name-CASE maps), and
//   (b) the mv-expand array-explode table source — SQLite `json_each(...)` vs DuckDB
//       `unnest(from_json(...))`, a divergence the research probes (SqliteJsonEachProbeTests /
//       DuckDbLinq2DbProbeTests) pinned empirically.
//
// ArrayExplodeFrom is consumed by ComposeMvExpand (the mv-expand json_each splice); ScalarShims names the
// per-dialect [Sql.Expression] set. KqlTranslationOptions carries the dialect and the transformer threads
// it, with SqliteDialect the only LIVE backend (DuckDB is a scaffold, not wired as a live log store).
public abstract class KqlDialect
{
	// Stable identifier for the backend (diagnostics / future routing).
	public abstract string Name { get; }

	// The type carrying THIS dialect's [Sql.Expression] scalar shims. linq2db reads the
	// per-method SQL translation off these attribute-bound methods at query-build time, so the shim set
	// is inherently per-dialect. For SQLite this IS today's KqlSqlExpressions (unmoved — the attributes
	// stay where linq2db already resolves them; the dialect merely NAMES the set, which is all the seam
	// needs). A second dialect supplies its own shim type.
	public abstract Type ScalarShims { get; }

	// The mv-expand array-explode table source: given the SQL reference of a JSON-array column and a
	// table alias, the FROM fragment that explodes it one row per element. Consumed by ComposeMvExpand.
	public abstract string ArrayExplodeFrom(string jsonColumnRef, string tableAlias);

	// The active backend for the per-project log store. The behavior-preserving default everywhere.
	public static KqlDialect Sqlite { get; } = new SqliteDialect();
}

// The LIVE backend. Preserves today's SQLite translation exactly: the scalar shims remain the
// attribute-bound KqlSqlExpressions methods (all ServerSideOnly=true), and the array-explode form is
// SQLite's builtin `json_each`, whose `value` column carries each element (pinned by
// SqliteJsonEachProbeTests — the direct SelectMany/[Sql.TableExpression] lateral is rejected by the
// SQLite provider, so the ApplyMvExpand rewrite will emit this fragment via a raw table source).
public sealed class SqliteDialect : KqlDialect
{
	public override string Name => "sqlite";

	public override Type ScalarShims => typeof(KqlSqlExpressions);

	public override string ArrayExplodeFrom(string jsonColumnRef, string tableAlias) =>
		$"json_each({jsonColumnRef}) {tableAlias}";
}

// SCAFFOLD — a second-backend seam, deliberately NOT wired as a live log store. Its array-explode form is
// the `unnest(from_json(...))` shape the DuckDB research probe (DuckDbLinq2DbProbeTests.P4) proved linq2db
// emits; the scalar-shim set is not yet ported, so ScalarShims throws until a DuckDB wave implements it.
public sealed class DuckDbDialect : KqlDialect
{
	public override string Name => "duckdb";

	public override Type ScalarShims =>
		throw new NotSupportedException(
			"DuckDbDialect is a scaffold: DuckDB scalar shims are not implemented yet (SQLite is the live backend).");

	public override string ArrayExplodeFrom(string jsonColumnRef, string tableAlias) =>
		$"unnest(from_json({jsonColumnRef}, '[\"VARCHAR\"]')) {tableAlias}";
}
