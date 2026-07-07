using System.Runtime.CompilerServices;
using System.Text.Json;
using Kusto.Language;
using Kusto.Language.Syntax;
using LinqToDB;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Metrics;
using PetBox.Log.Core.Models;
using PetBox.Log.Core.Tracing;
using Expr = System.Linq.Expressions.Expression;
using ParamExpr = System.Linq.Expressions.ParameterExpression;

namespace PetBox.Log.Core.Query;

public static partial class KqlTransformer
{
	public const string EventsTable = "events";
	public const string SpansTable = "spans";
	public const string MetricsTable = "metrics";

	public static readonly IReadOnlyList<KqlColumn> EventRecordColumns =
	[
		new("Id", typeof(long)),
		new("ServiceKey", typeof(string)),
		new("Timestamp", typeof(DateTime)),
		new("Level", typeof(int)),
		new("LevelName", typeof(string)),
		new("Message", typeof(string)),
		new("MessageTemplate", typeof(string)),
		new("Exception", typeof(string)),
		new("PropertiesJson", typeof(string)),
	];

	// The streamed `spans` row shape. Start/End are epoch-ms-derived instants (like events' Timestamp),
	// Duration a TimeSpan, Kind/Status carry a name form. The final column is named "PropertiesJson"
	// (carrying the span's AttributesJson) so the shared post-split machinery — RowScalarContext's
	// bare-name/Properties fallback, summarize/distinct/join/mv-expand — locates the JSON bag unchanged.
	public static readonly IReadOnlyList<KqlColumn> SpanRecordColumns =
	[
		new("SpanId", typeof(string)),
		new("TraceId", typeof(string)),
		new("ParentSpanId", typeof(string)),
		new("Name", typeof(string)),
		new("Kind", typeof(int)),
		new("KindName", typeof(string)),
		new("Start", typeof(DateTime)),
		new("End", typeof(DateTime)),
		new("Duration", typeof(TimeSpan)),
		new("Status", typeof(int)),
		new("StatusName", typeof(string)),
		new("StatusDescription", typeof(string)),
		new("PropertiesJson", typeof(string)),
	];

	// The streamed `metrics` row shape. Time/StartTime are epoch-ms-derived instants (like events'
	// Timestamp and spans' Start), MetricType carries a name form (TypeName — the metric analog of
	// Level/LevelName and Kind/KindName), and Value is the unified COALESCE(ValueDouble, ValueLong).
	// The wide optional scalars stay nullable (an unset arm is null, not 0). The final column is named
	// "PropertiesJson" (carrying the point's AttributesJson) so the shared post-split machinery —
	// RowScalarContext's bare-name/Properties fallback, summarize/distinct/join/mv-expand — locates the
	// JSON bag unchanged, exactly as the spans root does. The histogram/summary array-shaped tails
	// (ExplicitBoundsJson/BucketCountsJson/…) stay JSON-addressable via the bag, not first-class columns.
	public static readonly IReadOnlyList<KqlColumn> MetricPointRecordColumns =
	[
		new("MetricName", typeof(string)),
		new("MetricType", typeof(int)),
		new("TypeName", typeof(string)),
		new("Unit", typeof(string)),
		new("Description", typeof(string)),
		new("Time", typeof(DateTime)),
		new("StartTime", typeof(DateTime?)),
		new("Value", typeof(double?)),
		new("ValueDouble", typeof(double?)),
		new("ValueLong", typeof(long?)),
		new("Count", typeof(long?)),
		new("Sum", typeof(double?)),
		new("Min", typeof(double?)),
		new("Max", typeof(double?)),
		new("Temporality", typeof(int?)),
		new("IsMonotonic", typeof(bool?)),
		new("Scale", typeof(int?)),
		new("ZeroCount", typeof(long?)),
		new("Flags", typeof(int?)),
		new("PropertiesJson", typeof(string)),
	];

	// A table root: its name, streamed column shape, the SQL/record ScalarContext factory for the
	// pre-split `where`/`order`/`top` path, and how one record materializes into a streamed row. This is
	// the ONLY thing that varies between the `events` and `spans` roots; the whole pipeline (pre-split SQL,
	// the shape-changing suffix, correlation subqueries) is otherwise identical and shared generically.
	sealed record RootSpec<T>(
		string TableName,
		IReadOnlyList<KqlColumn> Columns,
		Func<ParamExpr, ScalarContext> MakeContext,
		Func<T, object?[]> ToRow);

	static RootSpec<LogEntryRecord> EventsSpec(DateTime now) => new(
		EventsTable,
		EventRecordColumns,
		row => new RecordScalarContext(row) { UtcNow = now },
		EventToRow);

	static RootSpec<SpanRecord> SpansSpec(DateTime now) => new(
		SpansTable,
		SpanRecordColumns,
		row => new SpanRecordScalarContext(row) { UtcNow = now },
		SpanToRow);

	static RootSpec<MetricPointRecord> MetricsSpec(DateTime now) => new(
		MetricsTable,
		MetricPointRecordColumns,
		row => new MetricRecordScalarContext(row) { UtcNow = now },
		MetricToRow);

	static object?[] EventToRow(LogEntryRecord r) =>
	[
		r.Id,
		r.ServiceKey,
		DateTimeOffset.FromUnixTimeMilliseconds(r.TimestampMs).UtcDateTime,
		r.Level,
		LogLevelNames.ToName(r.Level),
		r.Message,
		r.MessageTemplate,
		r.Exception,
		r.PropertiesJson,
	];

	static object?[] SpanToRow(SpanRecord s) =>
	[
		s.SpanId,
		s.TraceId,
		s.ParentSpanId,
		s.Name,
		s.Kind,
		SpanKindNames.ToName(s.Kind),
		DateTimeOffset.FromUnixTimeMilliseconds(s.StartUnixNs / 1_000_000).UtcDateTime,
		DateTimeOffset.FromUnixTimeMilliseconds(s.EndUnixNs / 1_000_000).UtcDateTime,
		s.Duration,
		s.StatusCode,
		SpanStatusNames.ToName(s.StatusCode),
		s.StatusDescription,
		s.AttributesJson,
	];

	static object?[] MetricToRow(MetricPointRecord m) =>
	[
		m.MetricName,
		m.MetricType,
		MetricPointTypeNames.ToName(m.MetricType),
		m.Unit,
		m.Description,
		DateTimeOffset.FromUnixTimeMilliseconds(m.TimeUnixNs / 1_000_000).UtcDateTime,
		m.StartUnixNs is { } startNs
			? DateTimeOffset.FromUnixTimeMilliseconds(startNs / 1_000_000).UtcDateTime
			: (DateTime?)null,
		// Unified value: the double arm, else the int64 arm widened to double, else null (histogram/summary).
		m.ValueDouble ?? (m.ValueLong is { } vl ? vl : (double?)null),
		m.ValueDouble,
		m.ValueLong,
		m.Count,
		m.Sum,
		m.Min,
		m.Max,
		m.AggregationTemporality,
		m.IsMonotonic,
		m.Scale,
		m.ZeroCount,
		m.Flags,
		m.AttributesJson,
	];

	// The teaching error for an unknown table root. Only surfaces that actually route BOTH roots
	// (LogQueryService for MCP/REST, the Logs UI page) may emit it; events-only surfaces (Apply — the
	// share pages) claim only 'events' instead, so the message is true wherever it appears.
	public static string UnknownTableMessage(string got) =>
		$"unknown table '{got}'; supported tables: {EventsTable}, {SpansTable}, {MetricsTable}";

	// The leading table reference of a query ('events' / 'spans' / other), or null when there is none.
	// LogQueryService and the Logs UI page route on this to pick the record type and to reject unknown
	// roots with the full supported-table list.
	public static string? GetRootTableName(KustoCode code)
	{
		ArgumentNullException.ThrowIfNull(code);
		var ops = FlattenPipeline(code.Syntax).ToList();
		return ops.Count > 0 && ops[0] is NameReference n ? n.SimpleName : null;
	}

	public static KqlResult Execute(IQueryable<LogEntryRecord> source, KustoCode code, TimeProvider? clock = null, KqlTranslationOptions? options = null)
	{
		var now = ParseAndClock(code, clock);
		return ExecutePipeline(source, code.Syntax, now, EventsSpec(now), options ?? KqlTranslationOptions.Default);
	}

	// The `spans` root: the SAME KQL subset over a named log's Spans table. Unlike events, a plain
	// (non-shape-changing) spans query has no LogEntry-shaped result, so this ALWAYS yields the streamed
	// span column shape (a Table); LogQueryService routes to it on the `spans` root.
	public static KqlResult ExecuteSpans(IQueryable<SpanRecord> source, KustoCode code, TimeProvider? clock = null, KqlTranslationOptions? options = null)
	{
		var now = ParseAndClock(code, clock);
		return ExecutePipeline(source, code.Syntax, now, SpansSpec(now), options ?? KqlTranslationOptions.Default);
	}

	// The `metrics` root: the SAME KQL subset over a named log's MetricPoints table. Like spans, a plain
	// metrics query has no LogEntry-shaped result, so this ALWAYS yields the streamed metric column shape
	// (a Table); LogQueryService routes to it on the `metrics` root.
	public static KqlResult ExecuteMetrics(IQueryable<MetricPointRecord> source, KustoCode code, TimeProvider? clock = null, KqlTranslationOptions? options = null)
	{
		var now = ParseAndClock(code, clock);
		return ExecutePipeline(source, code.Syntax, now, MetricsSpec(now), options ?? KqlTranslationOptions.Default);
	}

	// Guards parse diagnostics and resolves the single now()/ago() instant for the whole query.
	static DateTime ParseAndClock(KustoCode code, TimeProvider? clock)
	{
		var parseErrors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
		if (parseErrors.Count > 0)
			throw new UnsupportedKqlException("KQL parse error: " + string.Join("; ", parseErrors.Select(d => d.Message)));
		return (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
	}

	// Compiles and runs one rooted pipeline against `source` as a SINGLE composed linq2db query: the
	// pre-split prefix (where/order/take/top) applies directly, then the shape-changing suffix composes op
	// by op through ComposeLoop (a join/lookup right side composes as a SQL subquery, recursing over the
	// SAME log source). There is no in-memory execution path. Generic over the record type: the `spec`
	// supplies the only root-specific pieces (name, column shape, SQL context, row projector).
	static KqlResult ExecutePipeline<T>(IQueryable<T> source, SyntaxNode root, DateTime now, RootSpec<T> spec, KqlTranslationOptions options)
	{
		var operators = FlattenPipeline(root).ToList();
		if (operators.Count == 0)
			throw new UnsupportedKqlException("empty query");

		if (operators[0] is not NameReference tableName)
			throw new UnsupportedKqlException($"expected table reference, got {operators[0].GetType().Name}");
		if (!string.Equals(tableName.SimpleName, spec.TableName, StringComparison.Ordinal))
			throw new UnsupportedKqlException(UnknownTableMessage(tableName.SimpleName));

		var pipeline = operators.Skip(1).ToList();
		var splitAt = pipeline.FindIndex(IsShapeChangingOp);

		var (preOps, postOps) = splitAt < 0
			? (pipeline, new List<SyntaxNode>())
			: (pipeline.Take(splitAt).ToList(), pipeline.Skip(splitAt).ToList());

		var preResult = ApplyPipeline(source, preOps, spec.MakeContext);

		if (postOps.Count == 0)
			return new KqlResult(spec.Columns, StreamRecordRows(preResult, spec.ToRow));

		// SINGLE SQL PATH: the compose loop is the ONLY execution route. Every supported shape-changing op
		// composes to ONE linq2db query via the storage→logical mapping layer (a join/lookup right side
		// composes as a SQL subquery through sqlRunSub, self-referential so a nested right side reuses it).
		// There is no in-memory fallback: an op that does not compose is a genuinely unsupported construct,
		// surfaced as a precise error (the scalar compile throws it) — nothing is silently buffered in memory.
		SqlSubqueryRunner sqlRunSub = null!;
		sqlRunSub = (rightExpr, opName) => TryComposeSubqueryStage(rightExpr, source, now, opName, spec, options, sqlRunSub);

		var (stage, composed, counted) = ComposeLoop(RecordStage(preResult, spec), postOps, now, sqlRunSub, options);

		if (composed < postOps.Count)
			throw new UnsupportedKqlException(
				$"operator '{postOps[composed].Kind}' is not supported in this position");
		return counted ?? Materialize(stage);
	}

	// An operator whose output is no longer the event shape. `distinct` reduces to its chosen
	// columns; the others compute/aggregate. `order by` / `take` / `top` are NOT here — they preserve the
	// current shape and compose in SQL both before the split (pre-split ApplyPipeline) and after it
	// (ComposeOrder/ComposeTake/ComposeTop), so a query that only sorts/limits stays one linq2db query.
	static bool IsShapeChangingOp(SyntaxNode op) =>
		op is ProjectOperator or CountOperator or SummarizeOperator or ExtendOperator or DistinctOperator
			// correlation ops (wave 5): all post-materialization in-memory stages. join/lookup carry a
			// right-hand subquery that runs independently; mv-expand/parse reshape each streamed row.
			or JoinOperator or LookupOperator or MvExpandOperator or ParseOperator;

	public static bool HasShapeChangingOps(KustoCode code)
	{
		ArgumentNullException.ThrowIfNull(code);
		return FlattenPipeline(code.Syntax).Any(IsShapeChangingOp);
	}

	// Whether the TOP-LEVEL pipeline carries an explicit row bound (take/limit/top). Used by the
	// response capping (KqlLimits): only a top-level limit bounds the final row count — a take inside
	// a join/lookup subquery bounds the build side, not the output, and deliberately does not count.
	public static bool HasExplicitRowLimit(KustoCode code)
	{
		ArgumentNullException.ThrowIfNull(code);
		return FlattenPipeline(code.Syntax).Any(op => op is TakeOperator or TopOperator);
	}

	public static IQueryable<LogEntryRecord> Apply(IQueryable<LogEntryRecord> source, KustoCode code, TimeProvider? clock = null)
	{
		var parseErrors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
		if (parseErrors.Count > 0)
			throw new UnsupportedKqlException("KQL parse error: " + string.Join("; ", parseErrors.Select(d => d.Message)));

		var operators = FlattenPipeline(code.Syntax).ToList();
		if (operators.Count == 0)
			throw new UnsupportedKqlException("empty query");

		if (operators[0] is not NameReference tableName)
			throw new UnsupportedKqlException($"expected table reference, got {operators[0].GetType().Name}");
		// Apply is the events-only surface (the share pages stream LogEntryRecords); surfaces that route
		// both roots (LogQueryService, the Logs UI) validate the root BEFORE reaching here, so this
		// message may truthfully claim events only.
		if (!string.Equals(tableName.SimpleName, EventsTable, StringComparison.Ordinal))
			throw new UnsupportedKqlException(
				$"unknown table '{tableName.SimpleName}'; only '{EventsTable}' is supported here");

		var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
		return ApplyPipeline(source, operators.Skip(1).ToList(), EventsSpec(now).MakeContext);
	}

	// Kusto names an un-aliased `by bin(Col, …)` after Col; fall back to the function name.
	static string DefaultKeyName(FunctionCallExpression f) =>
		f.ArgumentList.Expressions.Count > 0 && f.ArgumentList.Expressions[0].Element is NameReference n
			? n.SimpleName
			: f.Name.SimpleName;

	// The nullable form of a value type (int → int?, DateTime → DateTime?), passing reference types and
	// already-nullable types through — so an aggregate over any column can declare a null-capable result.
	static Type Nullable(Type t) =>
		t.IsValueType && !KqlScalar.IsNullable(t) ? typeof(Nullable<>).MakeGenericType(t) : t;

	// --- post-shape (in-memory) order by / take / top / distinct ---
	// These run after a shape change, over the streamed object?[] rows. order/top buffer the
	// whole stream (a sort must); take just truncates. Sort keys compile through the wave-1
	// scalar engine over the current row shape, so they may reference computed columns.

	// Compares boxed scalar values (long/double/string/DateTime/…) for post-shape sorting.
	// Comparer<object>.Default routes to the boxed value's IComparable and treats null as the
	// smallest value, matching Kusto's nulls-first ascending order. STRINGS compare ORDINAL: the
	// pre-split ORDER BY runs under SQLite's BINARY collation, and String.CompareTo (culture-
	// sensitive) would order the same clause differently by pipeline position ("apple" before
	// "Banana" post-split, after it pre-split).
	sealed class BoxedComparer : IComparer<object?>
	{
		public static readonly BoxedComparer Instance = new();

		public int Compare(object? x, object? y) =>
			x is string sx && y is string sy
				? StringComparer.Ordinal.Compare(sx, sy)
				: Comparer<object>.Default.Compare(x!, y!);
	}

	// --- join kind. Cross-log/table addressing is out of scope: a join/lookup right side must be a
	// subquery over the SAME log (`events`/`spans`), composed by the same transformer against the same
	// IQueryable source (TryComposeSubqueryStage). ---

	enum JoinKind { InnerUnique, Inner, LeftOuter }

	static JoinKind ParseJoinKind(SyntaxList<NamedParameter> parameters, string opName, JoinKind defaultKind)
	{
		var kind = defaultKind;
		for (var i = 0; i < parameters.Count; i++)
		{
			var p = parameters[i];
			var name = p.Name?.SimpleName;
			if (!string.Equals(name, "kind", StringComparison.Ordinal))
				throw new UnsupportedKqlException($"{opName} parameter '{name}' not supported (only 'kind' is supported)");
			var value = (p.Expression as LiteralExpression)?.LiteralValue?.ToString();
			kind = value switch
			{
				"innerunique" => JoinKind.InnerUnique,
				"inner" => JoinKind.Inner,
				"leftouter" => JoinKind.LeftOuter,
				_ => throw new UnsupportedKqlException(
					$"{opName} kind '{value}' not supported (supported: innerunique, inner, leftouter)"),
			};
		}
		return kind;
	}

	static (string Column, bool IsLeft)? DollarRef(Expression e)
	{
		if (e is PathExpression { Expression: NameReference side, Selector: NameReference col })
		{
			if (side.SimpleName == "$left") return (col.SimpleName, true);
			if (side.SimpleName == "$right") return (col.SimpleName, false);
		}
		return null;
	}

	// Turns the parse pattern into an ordered literal/capture segment list, rejecting the unsupported
	// flavors (typed captures, `*` wildcards, two captures with no separating literal).
	static IReadOnlyList<(bool IsCapture, string Value)> ParsePatternSegments(SyntaxList<SyntaxNode> patterns)
	{
		var segments = new List<(bool IsCapture, string Value)>();
		for (var i = 0; i < patterns.Count; i++)
		{
			switch (patterns[i])
			{
				case LiteralExpression { LiteralValue: string s }:
					segments.Add((false, s));
					break;
				case NameDeclaration nd:
					if (segments.Count > 0 && segments[^1].IsCapture)
						throw new UnsupportedKqlException(
							"parse: two adjacent capture columns need a separating literal");
					segments.Add((true, nd.SimpleName));
					break;
				case NameAndTypeDeclaration:
					throw new UnsupportedKqlException("parse: typed captures (Col:type) not supported (captures are string-typed)");
				default:
					throw new UnsupportedKqlException(
						$"parse: unsupported pattern element '{patterns[i].Kind}' (only string literals and star-free captures are supported)");
			}
		}
		if (segments.Count == 0 || !segments.Any(s => s.IsCapture))
			throw new UnsupportedKqlException("parse: the pattern must declare at least one capture column");
		return segments;
	}

	static int FindColumnIndex(IReadOnlyList<KqlColumn> columns, string name)
	{
		for (var i = 0; i < columns.Count; i++)
			if (string.Equals(columns[i].Name, name, StringComparison.Ordinal))
				return i;
		return -1;
	}

	// Resolves a bare user-typed column reference to a row-shape column: exact (Ordinal) match wins;
	// else a UNIQUE case-insensitive match (so prod field-casing like `level` / `message` binds to the
	// real column instead of silently falling to the Properties bag). Returns -1 when nothing matches
	// or the case-insensitive match is ambiguous → the caller's Properties.<name> fallback then applies.
	// Distinct from FindColumnIndex, which stays exact for internal lookups (PropertiesJson, replace-in-
	// place aliases) that must NOT case-fold.
	internal static int ResolveColumnIndexCI(IReadOnlyList<KqlColumn> columns, string name)
	{
		var exact = FindColumnIndex(columns, name);
		if (exact >= 0)
			return exact;
		var ci = -1;
		for (var i = 0; i < columns.Count; i++)
			if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
			{
				if (ci >= 0)
					return -1; // ambiguous → fall through to Properties
				ci = i;
			}
		return ci;
	}

	static IQueryable<T> ApplyPipeline<T>(IQueryable<T> source, IReadOnlyList<SyntaxNode> operators, Func<ParamExpr, ScalarContext> makeCtx)
	{
		var q = source;
		foreach (var op in operators)
		{
			q = op switch
			{
				FilterOperator f => ApplyWhere(q, f, makeCtx),
				TakeOperator t => ApplyTake(q, t),
				SortOperator s => ApplySort(q, s, makeCtx),
				TopOperator t => ApplyTop(q, t, makeCtx),
				_ => throw new UnsupportedKqlException($"operator '{op.Kind}' not supported"),
			};
		}
		return q;
	}

	// Streams the pre-split SQL query into the root's object?[] row shape. linq2db's AsAsyncEnumerable
	// drives real SQLite; over an in-memory IQueryable (tests) it falls back to synchronous enumeration.
	static async IAsyncEnumerable<object?[]> StreamRecordRows<T>(
		IQueryable<T> query,
		Func<T, object?[]> toRow,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var r in query.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
			yield return toRow(r);
	}

	static IEnumerable<SyntaxNode> FlattenPipeline(SyntaxNode root)
	{
		// A bare table reference (e.g. the `(events)` right side of a join, after the parens are
		// unwrapped by the caller, or a top-level query that is just a table name) has no pipe.
		if (root is NameReference nameRoot)
		{
			yield return nameRoot;
			yield break;
		}

		var pipes = root.GetDescendants<PipeExpression>();
		if (pipes.Count == 0)
		{
			var names = root.GetDescendants<NameReference>();
			if (names.Count > 0)
				yield return names[0];
			yield break;
		}

		PipeExpression? outermost = null;
		for (var i = 0; i < pipes.Count; i++)
		{
			if (pipes[i].Parent is not PipeExpression)
			{
				outermost = pipes[i];
				break;
			}
		}
		if (outermost is null)
			yield break;

		var stack = new Stack<SyntaxNode>();
		SyntaxNode? current = outermost;
		while (current is PipeExpression pe)
		{
			stack.Push(pe.Operator);
			current = pe.Expression;
		}
		if (current is not null)
			yield return current;
		while (stack.Count > 0)
			yield return stack.Pop();
	}

	static IQueryable<T> ApplyWhere<T>(IQueryable<T> source, FilterOperator filter, Func<ParamExpr, ScalarContext> makeCtx)
	{
		var row = Expr.Parameter(typeof(T), "e");
		var body = KqlScalar.Compile(filter.Condition, makeCtx(row));
		if (body.Type != typeof(bool))
			throw new UnsupportedKqlException($"where condition must be boolean, got {body.Type.Name}");
		var predicate = Expr.Lambda<Func<T, bool>>(body, row);
		return source.Where(predicate);
	}

	static IQueryable<T> ApplyTake<T>(IQueryable<T> source, TakeOperator take)
	{
		if (take.Expression is not LiteralExpression { LiteralValue: long n })
			throw new UnsupportedKqlException("take requires an integer literal");
		return source.Take(checked((int)n));
	}

	static IQueryable<T> ApplySort<T>(IQueryable<T> source, SortOperator sort, Func<ParamExpr, ScalarContext> makeCtx)
	{
		IOrderedQueryable<T>? ordered = null;
		foreach (var element in sort.Expressions)
		{
			var (keyExpr, descending) = element.Element switch
			{
				OrderedExpression { Expression: var e, Ordering: var o } => (e, !IsAscending(o)),
				var e => ((Expression)e, true),
			};

			ordered = ApplyOrder(source, ordered, keyExpr, descending, makeCtx, "order-by expression");
		}
		return ordered ?? source;
	}

	// `top N by <column> [asc|desc]` on the SQL path = ORDER BY + LIMIT. The by-expression is a bare
	// column ref or a Properties access here (same reach as ApplySort); a computed top-by key composes
	// in ComposeTop. Default ordering is descending, matching Kusto.
	static IQueryable<T> ApplyTop<T>(IQueryable<T> source, TopOperator top, Func<ParamExpr, ScalarContext> makeCtx)
	{
		if (top.Expression is not LiteralExpression { LiteralValue: long n })
			throw new UnsupportedKqlException("top requires an integer literal count");
		var (keyExpr, descending) = top.ByExpression switch
		{
			OrderedExpression { Expression: var e, Ordering: var o } => (e, !IsAscending(o)),
			var e => ((Expression)e, true),
		};
		return ApplyOrder(source, null, keyExpr, descending, makeCtx, "top by-expression").Take(checked((int)n));
	}

	static bool IsAscending(OrderingClause? o) =>
		string.Equals(o?.AscOrDescKeyword?.Text, "asc", StringComparison.Ordinal);

	static IOrderedQueryable<T> ApplyOrder<T>(
		IQueryable<T> source,
		IOrderedQueryable<T>? prior,
		Expression keyExpr,
		bool descending,
		Func<ParamExpr, ScalarContext> makeCtx,
		string what)
	{
		var row = Expr.Parameter(typeof(T), "e");
		var ctx = makeCtx(row);
		// A bare column ref, or a Properties access (path / bracket-index form) — all SQL-translatable
		// key shapes. A computed sort/top key composes in ComposeOrder/ComposeTop.
		var access = keyExpr switch
		{
			NameReference n => ctx.ResolveColumn(n.SimpleName),
			PathExpression p when IsPropertiesPath(p, out var pathKey) => ctx.ResolveProperties(pathKey),
			ElementExpression el when IsPropertiesIndex(el, out var idxKey) => ctx.ResolveProperties(idxKey),
			_ => throw new UnsupportedKqlException(
				$"{what} '{keyExpr.Kind}' not supported (use a column ref, Properties.<key>, or Properties[\"key\"])"),
		};
		var keyType = access.Type;
		var keyLambda = Expr.Lambda(access, row);

		var methodName = (prior is null, descending) switch
		{
			(true, true) => nameof(Queryable.OrderByDescending),
			(true, false) => nameof(Queryable.OrderBy),
			(false, true) => nameof(Queryable.ThenByDescending),
			(false, false) => nameof(Queryable.ThenBy),
		};

		var queryableMethod = typeof(Queryable).GetMethods()
			.Single(m => m.Name == methodName
				&& m.GetParameters().Length == 2
				&& m.GetGenericArguments().Length == 2)
			.MakeGenericMethod(typeof(T), keyType);

		var callExpr = Expr.Call(queryableMethod, (prior ?? source).Expression, Expr.Quote(keyLambda));
		return (IOrderedQueryable<T>)source.Provider.CreateQuery<T>(callExpr);
	}

	// Recognizes a `Properties.<key>` path, flattening a dotted chain into ONE flat key:
	// Properties.petbox.tool → "petbox.tool". The bag is FLAT (OTLP attribute names are dotted), so the
	// whole remainder after Properties is a single key, never nested segments. Shared by the scalar
	// engine (KqlScalar), summarize's by-clause, distinct and mv-expand. The bracket form
	// Properties["petbox.tool"] (IsPropertiesIndex) is the canonical contract; this dotted form is the
	// ergonomic alias for keys that are valid identifier chains.
	internal static bool IsPropertiesPath(PathExpression path, out string key)
	{
		var parts = new Stack<string>();
		Expression current = path;
		while (current is PathExpression { Selector: NameReference sel } p)
		{
			parts.Push(sel.SimpleName);
			current = p.Expression;
		}
		if (current is NameReference { SimpleName: "Properties" } && parts.Count > 0)
		{
			// The key keeps the user's spelling here — it doubles as the display/column name. Search-
			// boundary normalization happens at the single seam the key flows into (ScalarContext.
			// ResolveProperties, see KqlPropertyKeys).
			key = string.Join(".", parts);
			return true;
		}
		key = "";
		return false;
	}

	// Recognizes `Properties["any.key"]` — bracket indexing with a STRING-LITERAL selector, the
	// canonical way to address a flat bag key (dotted OTLP names, keys with spaces/quotes/…). A
	// Properties[...] whose selector is NOT a string literal is a precise structural error rather than
	// a generic unsupported-expression one; non-Properties targets return false and keep the generic
	// error path.
	internal static bool IsPropertiesIndex(ElementExpression element, out string key)
	{
		key = "";
		if (element.Expression is not NameReference { SimpleName: "Properties" })
			return false;
		if (element.Selector is BracketedExpression { Expression: LiteralExpression { LiteralValue: string s } })
		{
			// The key keeps the user's spelling (display/column name); normalization happens at the
			// single search-boundary seam it flows into (see IsPropertiesPath's note).
			key = s;
			return true;
		}
		throw new UnsupportedKqlException("Properties[...] indexing requires a string literal key, e.g. Properties[\"petbox.request_chars\"]");
	}
}
