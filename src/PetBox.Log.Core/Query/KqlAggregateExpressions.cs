using LinqToDB;

namespace PetBox.Log.Core.Query;

// Custom-aggregate templates backing the KQL aggregates that have no direct linq2db/Enumerable
// counterpart (dcount's approximate mode, make_list, make_set). Each is a linq2db CUSTOM AGGREGATE:
// an [Sql.Extension(..., IsAggregate = true, ServerSideOnly = true)] method over `this IEnumerable<T>`
// whose element parameter is the ExprParameter — the `{source}` placeholder renders as the per-row
// aggregated expression, so the transformer composes them exactly like COUNT/SUM by projecting the
// group first: `g.Select(x => selector).<Method>()`. There are NO registered .NET UDFs here (hard
// principle) — the SQL lives in the attribute, and the C# body throws (SQL-only, like the regex shims
// in KqlSqlExpressions): the single-SQL-path engine never evaluates these in memory.
//
// Per-backend divergence rides the ProviderName arms:
//  - make_list / make_set: SQLite native `json_group_array(… ORDER BY …) FILTER (WHERE … IS NOT NULL)`;
//    DuckDB `to_json(list(… ORDER BY …) FILTER (…))` — DuckDB's `json_group_array` is a macro that
//    REJECTS DISTINCT/ORDER BY/FILTER, so DuckDB puts those modifiers on `list()` and JSON-encodes the
//    result. The two produce BYTE-IDENTICAL compact JSON for string/long/double elements.
//  - dcount(approx): DuckDB-only `approx_count_distinct` (there is no SQLite arm — the transformer only
//    emits this when the dialect is DuckDB; SQLite under Approx degrades to the exact COUNT(DISTINCT)).
public static class KqlAggregateExpressions
{
	// dcount(x) approximate — DuckDB HyperLogLog approx_count_distinct. NULLs are ignored natively
	// (matching exact COUNT(DISTINCT)). DuckDB-only: the transformer never routes SQLite here.
	[Sql.Extension(ProviderName.DuckDB, "approx_count_distinct({source})", IsAggregate = true, ServerSideOnly = true)]
	public static long ApproxCountDistinct<T>([ExprParameter] this IEnumerable<T> source) =>
		throw new NotSupportedException("KqlAggregateExpressions.ApproxCountDistinct is SQL-only (DuckDB approx_count_distinct)");

	// make_list(x) — a JSON-array TEXT of the non-null elements in value-ascending order. Kusto yields an
	// array skipping nulls; the FILTER drops nulls and the ORDER BY pins a deterministic (value-ascending)
	// order so the two backends agree byte-for-byte.
	[Sql.Extension(ProviderName.SQLite, "json_group_array({source} ORDER BY {source}) FILTER (WHERE {source} IS NOT NULL)", IsAggregate = true, ServerSideOnly = true)]
	[Sql.Extension(ProviderName.DuckDB, "to_json(list({source} ORDER BY {source}) FILTER (WHERE {source} IS NOT NULL))", IsAggregate = true, ServerSideOnly = true)]
	public static string MakeList<T>([ExprParameter] this IEnumerable<T> source) =>
		throw new NotSupportedException("KqlAggregateExpressions.MakeList is SQL-only (native per-dialect json array aggregate)");

	// make_set(x) — like make_list but DISTINCT (a set). Same per-dialect split and the same value-ascending
	// order, so both backends emit byte-identical compact JSON.
	[Sql.Extension(ProviderName.SQLite, "json_group_array(DISTINCT {source} ORDER BY {source}) FILTER (WHERE {source} IS NOT NULL)", IsAggregate = true, ServerSideOnly = true)]
	[Sql.Extension(ProviderName.DuckDB, "to_json(list(DISTINCT {source} ORDER BY {source}) FILTER (WHERE {source} IS NOT NULL))", IsAggregate = true, ServerSideOnly = true)]
	public static string MakeSet<T>([ExprParameter] this IEnumerable<T> source) =>
		throw new NotSupportedException("KqlAggregateExpressions.MakeSet is SQL-only (native per-dialect json array aggregate)");
}
