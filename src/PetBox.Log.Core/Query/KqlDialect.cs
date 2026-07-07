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

	// The per-element SCALAR expression over an explode alias — the value each exploded row yields.
	// SQLite's json_each exposes je.type/je.value (bool/null rendered EXACT, numbers canonicalized via
	// CAST(... AS TEXT)); DuckDB's unnest(from_json(...)) AS <alias>(value) already yields the element's
	// canonical text in <alias>.value. Consumed by ComposeMvExpand alongside ArrayExplodeFrom.
	public abstract string ElementSql(string tableAlias);

	// The active backend for the per-project log store. The behavior-preserving default everywhere.
	public static KqlDialect Sqlite { get; } = new SqliteDialect();

	// The DuckDb backend (a dedicated smoke test targets it directly; still OUT of KqlBackendConfig.Active
	// until the flip wave). Now REAL: scalar shims + mv-expand seam wired below.
	public static KqlDialect DuckDb { get; } = new DuckDbDialect();
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

	public override string ElementSql(string tableAlias) =>
		$"CASE {tableAlias}.type WHEN 'true' THEN 'true' WHEN 'false' THEN 'false' WHEN 'null' THEN NULL ELSE CAST({tableAlias}.value AS TEXT) END";
}

// The DuckDb backend. Its array-explode form is the `unnest(from_json(...))` shape the DuckDB research
// probe (DuckDbLinq2DbProbeTests.P4) proved linq2db emits, aliased AS <alias>(value) so the element ref
// is <alias>.value (a comma/lateral join, spike-confirmed). The scalar-shim set is the SHARED
// KqlSqlExpressions host — linq2db resolves the concrete SQL per provider off the [Sql.Expression]
// ProviderName.DuckDB arms there, so the same Type serves both dialects; the dialect merely NAMES the set.
public sealed class DuckDbDialect : KqlDialect
{
	public override string Name => "duckdb";

	// The shared shim host: linq2db resolves each method's DuckDB SQL from its [Sql.Expression(ProviderName.DuckDB, …)]
	// arm at query-build time, so this is the SAME Type SqliteDialect names — the per-dialect divergence lives in
	// the attributes, not in a separate host type.
	public override Type ScalarShims => typeof(KqlSqlExpressions);

	public override string ArrayExplodeFrom(string jsonColumnRef, string tableAlias) =>
		$"unnest(from_json({jsonColumnRef}, '[\"VARCHAR\"]')) AS {tableAlias}(value)";

	public override string ElementSql(string tableAlias) => $"{tableAlias}.value";
}
