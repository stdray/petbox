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

	// percentile(x, P) — DuckDB's exact discrete quantile: quantile_disc(x, p) returns the element at the
	// 1-based nearest rank MAX(1, ceil(n·p)) of the group's non-null values sorted ascending, EXACTLY the
	// engine's nearest-rank contract (spec kql-percentile: exact nearest-rank, NOT KustoLoco's type-5
	// interpolation — spike-verified equal to the MAX(1, ceil(n·P/100)) formula on every group). NULLs are
	// ignored natively; an empty/all-null group yields NULL. DuckDB-only: SQLite has no quantile primitive,
	// so the transformer routes SQLite percentile through a ROW_NUMBER/COUNT window pre-stage instead.
	// InlineParameters renders the fraction as a LITERAL (the quantile must be a constant); the CAST AS
	// DOUBLE is ESSENTIAL: linq2db prints the double G17 (e.g. 0.90000000000000002), which DuckDB would
	// otherwise type DECIMAL(18,17) and quantile over the EXACT decimal — one rank above the intended
	// fraction (probe-pinned: quantile_disc(v, 0.90000000000000002) → rank ceil(9.000…2)=10, while
	// CAST(… AS DOUBLE) restores the original double 0.9 → rank 9, the nearest-rank contract).
	[Sql.Extension(ProviderName.DuckDB, "quantile_disc({source}, CAST({p} AS DOUBLE))", IsAggregate = true, ServerSideOnly = true, InlineParameters = true)]
	public static T QuantileDisc<T>([ExprParameter] this IEnumerable<T> source, [ExprParameter] double p) =>
		throw new NotSupportedException("KqlAggregateExpressions.QuantileDisc is SQL-only (DuckDB quantile_disc)");
}
