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

	// The right-hand subquery of a correlation op (join/lookup), pre-validated to be over the same root and
	// executed against the same typed source. Closes over the root's source + spec so ApplyShapeChanges
	// stays record-type-agnostic.
	delegate KqlResult SubqueryRunner(Expression rightExpr, string opName);

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

	// Compiles and runs one rooted pipeline (the top-level query, or a correlation operator's right-hand
	// subquery — see RunCorrelationSubquery) against `source`. The pre-split prefix runs as linq2db SQL;
	// the shape-changing suffix runs in-memory. Recursion (a join/lookup whose right side is itself a
	// pipeline) reuses the SAME compiler against the SAME log source. Generic over the record type: the
	// `spec` supplies the only root-specific pieces (name, column shape, SQL context, row projector).
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

		// The in-memory correlation-subquery runner (join/lookup right side → materialized KqlResult), used
		// when a join falls back to the in-memory hash join.
		SubqueryRunner runSub = (rightExpr, opName) => RunCorrelationSubquery(rightExpr, source, now, opName, spec, options);
		// The SQL correlation-subquery runner: compiles a join/lookup right side to a composable SqlStage
		// (null when the right side is not fully SQL-composable → the whole join falls back). Self-referential
		// so a nested join's right side reuses the same runner.
		SqlSubqueryRunner sqlRunSub = null!;
		sqlRunSub = (rightExpr, opName) => TryComposeSubqueryStage(rightExpr, source, now, opName, spec, options, sqlRunSub);

		// HYBRID single-SQL migration: compose a LEADING run of migrated ops directly on the pre-split
		// linq2db IQueryable as ONE chained SQL query via the storage→logical mapping layer. At the first op
		// NOT yet migrated, or a transient fallback, the stage materializes (logical-typed) and the remainder
		// runs on the existing in-memory Stream* path.
		var (stage, composed, counted) = ComposeLoop(RecordStage(preResult, spec), postOps, now, sqlRunSub);

		if (composed == 0 && counted is null)
		{
			// The leading op is not migrated (or fell back) — exact pre-migration behavior.
			var recordShape = new KqlResult(spec.Columns, StreamRecordRows(preResult, spec.ToRow));
			return ApplyShapeChanges(recordShape, postOps, now, runSub);
		}

		var head = counted ?? Materialize(stage);
		return composed >= postOps.Count
			? head
			: ApplyShapeChanges(head, postOps.Skip(composed).ToList(), now, runSub);
	}

	// An operator whose output is no longer the event shape. `distinct` reduces to its chosen
	// columns; the others compute/aggregate. `order by` / `take` / `top` are NOT here — they
	// preserve the current shape and are handled both pre-split (SQL) and post-split (in-memory),
	// so a query that only sorts/limits stays entirely in the linq2db path.
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

	static KqlResult ApplyShapeChanges(KqlResult input, IReadOnlyList<SyntaxNode> ops, DateTime now, SubqueryRunner runSub)
	{
		var current = input;
		foreach (var op in ops)
		{
			current = op switch
			{
				ExtendOperator e => ApplyExtend(current, e, now),
				ProjectOperator p => ApplyProject(current, p, now),
				CountOperator => ApplyCount(current),
				SummarizeOperator s => ApplySummarize(current, s, now),
				DistinctOperator d => ApplyDistinct(current, d),
				// correlation ops. join/lookup compile their right subquery against the same root source.
				JoinOperator j => ApplyJoin(current, j, now, runSub),
				LookupOperator l => ApplyLookup(current, l, now, runSub),
				MvExpandOperator m => ApplyMvExpand(current, m, now),
				ParseOperator p => ApplyParse(current, p, now),
				// where / order by / take / top run in-memory once the pipeline has changed shape;
				// their predicates and sort keys may reference computed (post-split) columns.
				FilterOperator f => ApplyPostWhere(current, f, now),
				SortOperator s => ApplyPostSort(current, s, now),
				TakeOperator t => ApplyPostTake(current, t),
				TopOperator t => ApplyPostTop(current, t, now),
				_ => throw new UnsupportedKqlException($"operator '{op.Kind}' not supported in shape-changing pipeline"),
			};
		}
		return current;
	}

	// project selects/renames/computes columns. A bare column ref or `alias = column` is a
	// pass-through (keeps the source type); `alias = <expression>` compiles a scalar over the
	// current row shape and evaluates it in-memory (post-split). An un-aliased computed
	// expression is rejected — KQL auto-names it, which we don't reproduce.
	static KqlResult ApplyProject(KqlResult input, ProjectOperator project, DateTime now)
	{
		// project is compiled SEQUENTIALLY like extend: each projected column is appended to a working
		// row (input columns first, projected columns after) and the shared context reads from it, so a
		// later projection can reference an earlier one in the SAME project. Bare column refs, `alias =
		// column` renames and computed expressions all route through the scalar engine (which supplies
		// the case-insensitive column resolution and the Properties.<name> bare-name fallback, and keeps
		// the source column's type). An un-aliased computed expression is rejected — KQL auto-names it,
		// which we don't reproduce. The output is the projected columns (the appended slots), in order.
		var working = input.Columns.ToList();
		var rowParam = Expr.Parameter(typeof(object?[]), "row");
		var ctx = new RowScalarContext(rowParam, working) { UtcNow = now };
		var outColumns = new List<KqlColumn>();
		var producers = new List<Func<object?[], object?>>();

		foreach (var element in project.Expressions)
		{
			var (name, expr) = element.Element switch
			{
				NameReference n => (n.SimpleName, (Expression)n),
				SimpleNamedExpression { Name: NameDeclaration alias, Expression: var e } => (alias.Name.SimpleName, e),
				_ => throw new UnsupportedKqlException(
					$"project expression '{element.Element.Kind}' not supported (use a column ref or 'name = expression')"),
			};
			var (type, fn) = CompileCell(expr, ctx, rowParam);
			working.Add(new KqlColumn(name, type));
			outColumns.Add(new KqlColumn(name, type));
			producers.Add(fn);
		}

		return new KqlResult(outColumns,
			StreamProject(input.Rows, input.Columns.Count, working.Count, producers.ToArray()));
	}

	// extend appends computed columns (evaluated in-memory over the current row). Re-using an
	// existing name replaces that column in place (KQL extend semantics). Expressions are compiled
	// SEQUENTIALLY: `columns` grows as each element is compiled and the shared context reads from it,
	// so a later expression sees the columns introduced by earlier ones in the SAME extend — Kusto
	// allows this, e.g. `extend A = tostring(Level), B = strcat(A, '-x')`. Writes then execute in that
	// same order over the output row so an earlier computed cell is populated before a later one reads it.
	static KqlResult ApplyExtend(KqlResult input, ExtendOperator extend, DateTime now)
	{
		var columns = input.Columns.ToList();
		var rowParam = Expr.Parameter(typeof(object?[]), "row");
		var ctx = new RowScalarContext(rowParam, columns) { UtcNow = now };
		var writes = new List<(int TargetIndex, Func<object?[], object?> Fn)>();

		foreach (var element in extend.Expressions)
		{
			if (element.Element is not SimpleNamedExpression { Name: NameDeclaration alias, Expression: var expr })
				throw new UnsupportedKqlException(
					$"extend expression '{element.Element.Kind}' not supported (use 'name = expression')");

			var (type, fn) = CompileCell(expr, ctx, rowParam);
			var name = alias.Name.SimpleName;
			var existing = FindColumnIndex(columns, name);
			int target;
			if (existing >= 0)
			{
				columns[existing] = new KqlColumn(name, type);
				target = existing;
			}
			else
			{
				columns.Add(new KqlColumn(name, type));
				target = columns.Count - 1;
			}
			writes.Add((target, fn));
		}

		return new KqlResult(columns, StreamExtend(input.Rows, input.Columns.Count, columns.Count, writes.ToArray()));
	}

	// Compile a scalar KQL expression to a delegate over a materialized object?[] row.
	static (Type Type, Func<object?[], object?> Fn) CompileCell(Expression expr, RowScalarContext ctx, ParamExpr rowParam)
	{
		var body = KqlScalar.Compile(expr, ctx);
		var boxed = Expr.Convert(body, typeof(object));
		var fn = Expr.Lambda<Func<object?[], object?>>(boxed, rowParam).Compile();
		return (body.Type, fn);
	}

	static KqlResult ApplyCount(KqlResult input) =>
		new([new KqlColumn("Count", typeof(long))], StreamCount(input.Rows));

	// summarize <aggregates> [by <keys>]. Aggregate arguments and countif predicates are
	// arbitrary scalar expressions compiled over the current (post-split) row via the wave-1
	// engine; by-keys are column refs, Properties.<key>, or computed expressions (incl.
	// bin(...) for time/numeric bucketing). Both aggregate results and by-keys accept an
	// `alias = …` name. Grouping and folding are in-memory (the pipeline is already split).
	static KqlResult ApplySummarize(KqlResult input, SummarizeOperator op, DateTime now)
	{
		var rowParam = Expr.Parameter(typeof(object?[]), "row");
		var ctx = new RowScalarContext(rowParam, input.Columns) { UtcNow = now };

		var outputColumns = new List<KqlColumn>();
		var keyExtractors = new List<Func<object?[], object?>>();

		if (op.ByClause is not null)
		{
			foreach (var element in op.ByClause.Expressions)
			{
				var (name, type, fn) = CompileByKey(element.Element, input, ctx, rowParam);
				outputColumns.Add(new KqlColumn(name, type));
				keyExtractors.Add(fn);
			}
		}

		var aggSpecs = new List<AggSpec>();
		foreach (var element in op.Aggregates)
		{
			var (name, call) = element.Element switch
			{
				FunctionCallExpression f => ($"{f.Name.SimpleName}_", f),
				SimpleNamedExpression { Name: NameDeclaration alias, Expression: FunctionCallExpression f }
					=> (alias.Name.SimpleName, f),
				_ => throw new UnsupportedKqlException($"summarize aggregate '{element.Element.Kind}' not supported"),
			};
			var spec = BuildAggregate(name, call, ctx, rowParam);
			aggSpecs.Add(spec);
			outputColumns.Add(new KqlColumn(spec.OutputName, spec.ResultType));
		}

		return new KqlResult(outputColumns, StreamSummarize(input.Rows, keyExtractors.ToArray(), aggSpecs.ToArray()));
	}

	// The single owner of bag-key access over a MATERIALIZED row shape — every summarize-by /
	// distinct / mv-expand / join-key Properties lookup funnels through here. It NORMALIZES the
	// requested key (the search-boundary rule; ScalarContext.ResolveProperties is the equivalent seam
	// for the expression paths — see KqlPropertyKeys) and returns the flat lookup over the row's
	// PropertiesJson cell. Throws `missingBagError` when the current shape no longer carries the bag.
	static Func<object?[], string?> BagValueExtractor(
		IReadOnlyList<KqlColumn> columns, string rawKey, string missingBagError)
	{
		var propIdx = FindColumnIndex(columns, nameof(LogEntryRecord.PropertiesJson));
		if (propIdx < 0)
			throw new UnsupportedKqlException(missingBagError);
		var key = KqlPropertyKeys.Normalize(rawKey);
		return row => KqlSqlExpressions.JsonGet(row[propIdx] as string, key);
	}

	// A by-key: (output name, CLR type, value extractor). Bare column refs stay a cheap index
	// pass-through; Properties access (bare fallback / path / bracket-index) is the shared
	// BagValueExtractor; anything else (incl. bin) compiles as a scalar. An un-aliased function call
	// (e.g. `by bin(Timestamp, 1h)`) is named after its first column argument, matching Kusto's
	// default column naming.
	static (string Name, Type Type, Func<object?[], object?> Fn) CompileByKey(
		Expression element, KqlResult input, RowScalarContext ctx, ParamExpr rowParam)
	{
		switch (element)
		{
			case NameReference n:
				{
					var idx = ResolveColumnIndexCI(input.Columns, n.SimpleName);
					if (idx >= 0)
						return (n.SimpleName, input.Columns[idx].ClrType, row => row[idx]);
					// Bare-name fallback: group by Properties.<name> when the shape still has PropertiesJson.
					return (n.SimpleName, typeof(string),
						BagValueExtractor(input.Columns, n.SimpleName, $"summarize by: unknown column '{n.SimpleName}'"));
				}
			case PathExpression p when IsPropertiesPath(p, out var propKey):
				return ("Properties." + propKey, typeof(string),
					BagValueExtractor(input.Columns, propKey, "summarize by Properties.<key>: input has no PropertiesJson column"));
			case ElementExpression el when IsPropertiesIndex(el, out var idxKey):
				return ("Properties." + idxKey, typeof(string),
					BagValueExtractor(input.Columns, idxKey, "summarize by Properties[\"key\"]: input has no PropertiesJson column"));
			case SimpleNamedExpression { Name: NameDeclaration alias, Expression: var expr }:
				{
					var (type, fn) = CompileCell(expr, ctx, rowParam);
					return (alias.Name.SimpleName, type, fn);
				}
			case FunctionCallExpression f:
				{
					var (type, fn) = CompileCell(f, ctx, rowParam);
					return (DefaultKeyName(f), type, fn);
				}
			default:
				throw new UnsupportedKqlException(
					$"summarize by '{element.Kind}' not supported (column ref, Properties.<key>, Properties[\"key\"], or 'name = expression')");
		}
	}

	// Kusto names an un-aliased `by bin(Col, …)` after Col; fall back to the function name.
	static string DefaultKeyName(FunctionCallExpression f) =>
		f.ArgumentList.Expressions.Count > 0 && f.ArgumentList.Expressions[0].Element is NameReference n
			? n.SimpleName
			: f.Name.SimpleName;

	// Builds one aggregate: the output name, its result CLR type, and a factory for a fresh
	// per-group accumulator. Aggregate arguments (and countif's predicate) are scalar
	// expressions compiled over the row. New aggregates slot in here.
	static AggSpec BuildAggregate(string name, FunctionCallExpression call, RowScalarContext ctx, ParamExpr rowParam)
	{
		var fn = call.Name.SimpleName;
		var argElements = call.ArgumentList.Expressions;

		(Type Type, Func<object?[], object?> Fn) Arg(string forFn)
		{
			if (argElements.Count != 1)
				throw new UnsupportedKqlException($"{forFn}() takes exactly 1 argument, got {argElements.Count}");
			return CompileCell(argElements[0].Element, ctx, rowParam);
		}

		static void RequireNumeric(Type t, string forFn)
		{
			// A typed conversion (toint/todouble) yields a nullable numeric; aggregate accumulators
			// already skip null argument values, so accept the nullable form too.
			if (!KqlScalar.IsNumericType(KqlScalar.NonNullable(t)))
				throw new UnsupportedKqlException($"{forFn}() requires a numeric argument, got {t.Name}");
		}

		switch (fn)
		{
			case "count":
				if (argElements.Count != 0)
					throw new UnsupportedKqlException($"count() takes no arguments, got {argElements.Count}");
				return new AggSpec(name, typeof(long), () => new CountAccumulator());

			case "countif":
				{
					var (type, f) = Arg("countif");
					if (type != typeof(bool))
						throw new UnsupportedKqlException($"countif() requires a boolean predicate, got {type.Name}");
					return new AggSpec(name, typeof(long), () => new CountIfAccumulator(f));
				}

			// sum/avg/min/max declare NULLABLE result types: an empty group, or one whose argument values
			// are all null (aggregates skip null arguments), yields null from the accumulator — so a
			// downstream `| where <agg> > 0` unboxes the cell as the nullable type (null → not matched)
			// instead of crashing on an object→non-nullable-value unbox.
			case "sum":
				{
					var (type, f) = Arg("sum");
					RequireNumeric(type, "sum");
					return KqlScalar.NonNullable(type) == typeof(double)
						? new AggSpec(name, typeof(double?), () => new SumDoubleAccumulator(f))
						: new AggSpec(name, typeof(long?), () => new SumLongAccumulator(f));
				}

			case "avg":
				{
					var (type, f) = Arg("avg");
					RequireNumeric(type, "avg");
					return new AggSpec(name, typeof(double?), () => new AvgAccumulator(f));
				}

			case "min":
				{
					var (type, f) = Arg("min");
					return new AggSpec(name, Nullable(type), () => new MinMaxAccumulator(f, min: true));
				}

			case "max":
				{
					var (type, f) = Arg("max");
					return new AggSpec(name, Nullable(type), () => new MinMaxAccumulator(f, min: false));
				}

			case "dcount":
				{
					var (_, f) = Arg("dcount");
					return new AggSpec(name, typeof(long), () => new DcountAccumulator(f));
				}

			default:
				throw new UnsupportedKqlException(
					$"aggregate '{fn}' not supported (supported: count, countif, sum, min, max, avg, dcount)");
		}
	}

	// The nullable form of a value type (int → int?, DateTime → DateTime?), passing reference types and
	// already-nullable types through — so an aggregate over any column can declare a null-capable result.
	static Type Nullable(Type t) =>
		t.IsValueType && !KqlScalar.IsNullable(t) ? typeof(Nullable<>).MakeGenericType(t) : t;

	sealed record AggSpec(string OutputName, Type ResultType, Func<Accumulator> Factory);

	// A per-group fold. Add is called once per row in the group; Result yields the final value.
	// Aggregates ignore null argument values (Kusto semantics); count() folds every row.
	abstract class Accumulator
	{
		public abstract void Add(object?[] row);
		public abstract object? Result { get; }
	}

	sealed class CountAccumulator : Accumulator
	{
		long _n;
		public override void Add(object?[] row) => _n++;
		public override object? Result => _n;
	}

	sealed class CountIfAccumulator(Func<object?[], object?> predicate) : Accumulator
	{
		long _n;
		public override void Add(object?[] row)
		{
			if (predicate(row) is true)
				_n++;
		}
		public override object? Result => _n;
	}

	sealed class SumLongAccumulator(Func<object?[], object?> arg) : Accumulator
	{
		long _sum;
		bool _any;
		public override void Add(object?[] row)
		{
			if (arg(row) is { } v)
			{
				_sum += Convert.ToInt64(v);
				_any = true;
			}
		}
		public override object? Result => _any ? _sum : null;
	}

	sealed class SumDoubleAccumulator(Func<object?[], object?> arg) : Accumulator
	{
		double _sum;
		bool _any;
		public override void Add(object?[] row)
		{
			if (arg(row) is { } v)
			{
				_sum += Convert.ToDouble(v);
				_any = true;
			}
		}
		public override object? Result => _any ? _sum : null;
	}

	sealed class AvgAccumulator(Func<object?[], object?> arg) : Accumulator
	{
		double _sum;
		long _n;
		public override void Add(object?[] row)
		{
			if (arg(row) is { } v)
			{
				_sum += Convert.ToDouble(v);
				_n++;
			}
		}
		public override object? Result => _n == 0 ? null : _sum / _n;
	}

	sealed class MinMaxAccumulator(Func<object?[], object?> arg, bool min) : Accumulator
	{
		object? _best;
		public override void Add(object?[] row)
		{
			if (arg(row) is not { } v)
				return;
			if (_best is null)
			{
				_best = v;
				return;
			}
			var cmp = Comparer<object>.Default.Compare(v, _best);
			if (min ? cmp < 0 : cmp > 0)
				_best = v;
		}
		public override object? Result => _best;
	}

	sealed class DcountAccumulator(Func<object?[], object?> arg) : Accumulator
	{
		readonly HashSet<object> _seen = [];
		public override void Add(object?[] row)
		{
			if (arg(row) is { } v && _seen.Add(v))
				GuardBufferCap(_seen.Count, "dcount (distinct values)");
		}
		public override object? Result => (long)_seen.Count;
	}

	static async IAsyncEnumerable<object?[]> StreamSummarize(
		IAsyncEnumerable<object?[]> source,
		Func<object?[], object?>[] groupExtractors,
		AggSpec[] aggs,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		var groups = new Dictionary<GroupKey, Accumulator[]>();

		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			var keyValues = new object?[groupExtractors.Length];
			for (var i = 0; i < groupExtractors.Length; i++)
				keyValues[i] = groupExtractors[i](row);
			var key = new GroupKey(keyValues);
			if (!groups.TryGetValue(key, out var accs))
			{
				accs = new Accumulator[aggs.Length];
				for (var i = 0; i < aggs.Length; i++)
					accs[i] = aggs[i].Factory();
				groups[key] = accs;
				GuardBufferCap(groups.Count, "summarize (distinct groups)");
			}
			foreach (var acc in accs)
				acc.Add(row);
		}

		// Kusto returns ONE row of default aggregates for a no-by `summarize` over EMPTY input (count()=0,
		// sum/min/max/avg = null) rather than zero rows. Fold a fresh accumulator set to produce it.
		if (groups.Count == 0 && groupExtractors.Length == 0)
		{
			var accs = new Accumulator[aggs.Length];
			for (var i = 0; i < aggs.Length; i++)
				accs[i] = aggs[i].Factory();
			var single = new object?[aggs.Length];
			for (var i = 0; i < aggs.Length; i++)
				single[i] = accs[i].Result;
			yield return single;
			yield break;
		}

		foreach (var (key, accs) in groups)
		{
			var result = new object?[key.Values.Length + accs.Length];
			for (var i = 0; i < key.Values.Length; i++)
				result[i] = key.Values[i];
			for (var i = 0; i < accs.Length; i++)
				result[key.Values.Length + i] = accs[i].Result;
			yield return result;
		}
	}

	sealed record GroupKey(object?[] Values)
	{
		public bool Equals(GroupKey? other)
		{
			if (other is null || Values.Length != other.Values.Length)
				return false;
			for (var i = 0; i < Values.Length; i++)
				if (!Equals(Values[i], other.Values[i]))
					return false;
			return true;
		}

		public override int GetHashCode()
		{
			var h = new HashCode();
			foreach (var v in Values)
				h.Add(v);
			return h.ToHashCode();
		}
	}

	static async IAsyncEnumerable<object?[]> StreamCount(
		IAsyncEnumerable<object?[]> source,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		long n = 0;
		await foreach (var _ in source.WithCancellation(ct).ConfigureAwait(false))
			n++;
		yield return [n];
	}

	// project's stream: build a working row (input cells copied, then each projected cell computed in
	// order over that same working row so a later projection sees earlier ones), then emit only the
	// projected slots [inputWidth ..) as the output row.
	static async IAsyncEnumerable<object?[]> StreamProject(
		IAsyncEnumerable<object?[]> source,
		int inputWidth,
		int workingWidth,
		Func<object?[], object?>[] producers,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			var work = new object?[workingWidth];
			Array.Copy(row, work, inputWidth);
			for (var i = 0; i < producers.Length; i++)
				work[inputWidth + i] = producers[i](work);
			var result = new object?[producers.Length];
			Array.Copy(work, inputWidth, result, 0, producers.Length);
			yield return result;
		}
	}

	static async IAsyncEnumerable<object?[]> StreamExtend(
		IAsyncEnumerable<object?[]> source,
		int inputWidth,
		int outputWidth,
		(int TargetIndex, Func<object?[], object?> Fn)[] writes,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			var result = new object?[outputWidth];
			Array.Copy(row, result, inputWidth);
			// Writes read from `result` (not the input row) and run in element order, so a later extend
			// expression observes the cells written by earlier ones in the same operator.
			foreach (var (idx, fn) in writes)
				result[idx] = fn(result);
			yield return result;
		}
	}

	// --- post-shape (in-memory) order by / take / top / distinct ---
	// These run after a shape change, over the streamed object?[] rows. order/top buffer the
	// whole stream (a sort must); take just truncates. Sort keys compile through the wave-1
	// scalar engine over the current row shape, so they may reference computed columns.

	static KqlResult ApplyPostTake(KqlResult input, TakeOperator take)
	{
		if (take.Expression is not LiteralExpression { LiteralValue: long n })
			throw new UnsupportedKqlException("take requires an integer literal");
		return new KqlResult(input.Columns, StreamTake(input.Rows, checked((int)n)));
	}

	static KqlResult ApplyPostSort(KqlResult input, SortOperator sort, DateTime now)
	{
		var keys = sort.Expressions.Select(e => CompileSortKey(e.Element, input, now)).ToArray();
		return new KqlResult(input.Columns, StreamSort(input.Rows, keys, limit: null));
	}

	static KqlResult ApplyPostTop(KqlResult input, TopOperator top, DateTime now)
	{
		if (top.Expression is not LiteralExpression { LiteralValue: long n })
			throw new UnsupportedKqlException("top requires an integer literal count");
		var key = CompileSortKey(top.ByExpression, input, now);
		return new KqlResult(input.Columns, StreamSort(input.Rows, [key], limit: checked((int)n)));
	}

	// where after a shape change: compile the predicate with the scalar engine over the current
	// (post-split) row shape and filter the streamed rows in memory. Lets a summarize/project result
	// be filtered by a computed column, e.g. `summarize C = count() by ServiceKey | where C > 10`.
	static KqlResult ApplyPostWhere(KqlResult input, FilterOperator filter, DateTime now)
	{
		var rowParam = Expr.Parameter(typeof(object?[]), "row");
		var ctx = new RowScalarContext(rowParam, input.Columns) { UtcNow = now };
		var body = KqlScalar.Compile(filter.Condition, ctx);
		if (body.Type != typeof(bool))
			throw new UnsupportedKqlException($"where condition must be boolean, got {body.Type.Name}");
		var predicate = Expr.Lambda<Func<object?[], bool>>(body, rowParam).Compile();
		return new KqlResult(input.Columns, StreamWhere(input.Rows, predicate));
	}

	// A sort key over a post-split row: (value extractor, descending). A bare expression or a
	// column ref sorts ascending unless followed by `desc`; default (no ordering) is descending
	// only for the whole `order by`/`top` when the user omits it — Kusto's default is descending,
	// which the `!IsAscending` below preserves.
	static (Func<object?[], object?> Key, bool Descending) CompileSortKey(Expression element, KqlResult input, DateTime now)
	{
		var rowParam = Expr.Parameter(typeof(object?[]), "row");
		var ctx = new RowScalarContext(rowParam, input.Columns) { UtcNow = now };
		var (expr, descending) = element switch
		{
			OrderedExpression { Expression: var e, Ordering: var o } => (e, !IsAscending(o)),
			_ => (element, true),
		};
		var (_, fn) = CompileCell(expr, ctx, rowParam);
		return (fn, descending);
	}

	static async IAsyncEnumerable<object?[]> StreamWhere(
		IAsyncEnumerable<object?[]> source,
		Func<object?[], bool> predicate,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
			if (predicate(row))
				yield return row;
	}

	static async IAsyncEnumerable<object?[]> StreamTake(
		IAsyncEnumerable<object?[]> source, int n, [EnumeratorCancellation] CancellationToken ct = default)
	{
		if (n <= 0)
			yield break;
		var emitted = 0;
		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			yield return row;
			if (++emitted >= n)
				yield break;
		}
	}

	static async IAsyncEnumerable<object?[]> StreamSort(
		IAsyncEnumerable<object?[]> source,
		IReadOnlyList<(Func<object?[], object?> Key, bool Descending)> keys,
		int? limit,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		var buffer = new List<object?[]>();
		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			buffer.Add(row);
			GuardBufferCap(buffer.Count, "order by/top");
		}

		IOrderedEnumerable<object?[]>? ordered = null;
		foreach (var (key, descending) in keys)
		{
			ordered = (ordered, descending) switch
			{
				(null, true) => buffer.OrderByDescending(key, BoxedComparer.Instance),
				(null, false) => buffer.OrderBy(key, BoxedComparer.Instance),
				(not null, true) => ordered.ThenByDescending(key, BoxedComparer.Instance),
				(not null, false) => ordered.ThenBy(key, BoxedComparer.Instance),
			};
		}

		var result = (IEnumerable<object?[]>?)ordered ?? buffer;
		if (limit is { } n)
			result = result.Take(n);
		foreach (var row in result)
			yield return row;
	}

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

	// distinct <cols> | distinct *. In-memory de-dup over the streamed rows. We deliberately do
	// NOT push this to SQL DISTINCT: the streamed rows are the full event shape (produced by
	// StreamEventRecordRows), and a distinct column may be a computed value like LevelName or a
	// json_extract of Properties that has no single SQLite column to DISTINCT on. De-duping the
	// projected key set in memory reuses the existing GroupKey equality and keeps one code path.
	static KqlResult ApplyDistinct(KqlResult input, DistinctOperator distinct)
	{
		var outputColumns = new List<KqlColumn>();
		var extractors = new List<Func<object?[], object?>>();

		foreach (var element in distinct.Expressions)
		{
			switch (element.Element)
			{
				case StarExpression:
					if (distinct.Expressions.Count != 1)
						throw new UnsupportedKqlException("distinct *: '*' cannot be combined with other columns");
					for (var i = 0; i < input.Columns.Count; i++)
					{
						var idx = i;
						outputColumns.Add(input.Columns[idx]);
						extractors.Add(row => row[idx]);
					}
					break;
				case NameReference n:
					{
						var idx = ResolveColumnIndexCI(input.Columns, n.SimpleName);
						if (idx >= 0)
						{
							outputColumns.Add(input.Columns[idx]);
							extractors.Add(row => row[idx]);
							break;
						}
						// Bare-name fallback: distinct Properties.<name> when the shape still has PropertiesJson.
						outputColumns.Add(new KqlColumn(n.SimpleName, typeof(string)));
						extractors.Add(BagValueExtractor(input.Columns, n.SimpleName, $"distinct: unknown column '{n.SimpleName}'"));
						break;
					}
				case PathExpression p when IsPropertiesPath(p, out var propKey):
					outputColumns.Add(new KqlColumn("Properties." + propKey, typeof(string)));
					extractors.Add(BagValueExtractor(input.Columns, propKey, "distinct Properties.<key>: input has no PropertiesJson column"));
					break;
				case ElementExpression el when IsPropertiesIndex(el, out var idxKey):
					outputColumns.Add(new KqlColumn("Properties." + idxKey, typeof(string)));
					extractors.Add(BagValueExtractor(input.Columns, idxKey, "distinct Properties[\"key\"]: input has no PropertiesJson column"));
					break;
				default:
					throw new UnsupportedKqlException(
						$"distinct expression '{element.Element.Kind}' not supported (use column refs, Properties.<key>, Properties[\"key\"], or '*')");
			}
		}

		return new KqlResult(outputColumns, StreamDistinct(input.Rows, extractors.ToArray()));
	}

	static async IAsyncEnumerable<object?[]> StreamDistinct(
		IAsyncEnumerable<object?[]> source,
		Func<object?[], object?>[] extractors,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		var seen = new HashSet<GroupKey>();
		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			var values = new object?[extractors.Length];
			for (var i = 0; i < extractors.Length; i++)
				values[i] = extractors[i](row);
			if (seen.Add(new GroupKey(values)))
			{
				GuardBufferCap(seen.Count, "distinct");
				yield return values;
			}
		}
	}

	// --- correlation ops (wave 5): join / lookup / mv-expand / parse. All are post-materialization
	// in-memory stages over the streamed object?[] rows. Cross-log/table addressing is out of scope:
	// a join/lookup right side must be a subquery over the SAME log (`events`), compiled by the same
	// transformer against the same IQueryable source (RunCorrelationSubquery). ---

	// Hard cap on the buffered build side (the fully materialized right subquery) of a join/lookup, so a
	// `join (events)` over a large log cannot exhaust the (small) prod VM's memory. Exceeding it is a
	// user-fixable error, not an internal fault, so it throws UnsupportedKqlException with a teaching
	// message. Overridable per async-context (AsyncLocal, isolated across parallel tests) so the cap is
	// exercisable without materializing 100k rows.
	internal const int JoinBuildSideCap = 100_000;
	static readonly AsyncLocal<int?> JoinBuildSideCapOverrideValue = new();
	internal static int? JoinBuildSideCapOverride
	{
		get => JoinBuildSideCapOverrideValue.Value;
		set => JoinBuildSideCapOverrideValue.Value = value;
	}

	// The same protection for EVERY other in-memory collection that grows with the scanned input:
	// sort's row buffer, summarize's group table, distinct's seen-set, dcount's value set. A query
	// whose buffered volume is not bounded by a service constant must fail as a user-fixable error
	// (narrow the query), never OOM the (small) prod VM. Overridable per async-context for tests,
	// like JoinBuildSideCapOverride.
	internal const int InMemoryBufferCap = 100_000;
	static readonly AsyncLocal<int?> InMemoryBufferCapOverrideValue = new();
	internal static int? InMemoryBufferCapOverride
	{
		get => InMemoryBufferCapOverrideValue.Value;
		set => InMemoryBufferCapOverrideValue.Value = value;
	}

	static void GuardBufferCap(int bufferedCount, string opName)
	{
		var cap = InMemoryBufferCapOverride ?? InMemoryBufferCap;
		if (bufferedCount > cap)
			throw new UnsupportedKqlException(
				$"{opName} buffered more than {cap} rows in memory; narrow the query (add where, take, or a tighter time range)");
	}

	enum JoinKind { InnerUnique, Inner, LeftOuter }

	// join [kind=innerunique|inner|leftouter] (<right subquery>) on <key>[, <key2>...]. Left columns
	// come first; right columns follow, each renamed <name>N (N = smallest suffix making it unique)
	// on a name collision with an already-present column — so a self-join produces Id, …, Id1, ….
	// Execution is a hash join with the (fully buffered) right subquery as the build side; the left
	// side streams. Memory profile: O(right result size). Default kind is innerunique (Kusto default:
	// the left side is de-duplicated by key, keeping the first row per key in input order).
	static KqlResult ApplyJoin(KqlResult left, JoinOperator op, DateTime now, SubqueryRunner runSub)
	{
		var kind = ParseJoinKind(op.Parameters, "join");
		if (op.ConditionClause is not JoinOnClause onClause)
			throw new UnsupportedKqlException("join requires an 'on' clause with equality key(s)");
		var right = runSub(op.Expression, "join");
		var (leftKeys, rightKeys, _) = ResolveJoinKeys(onClause, left, right, "join");
		var (columns, included) = BuildJoinColumns(left, right, NoExcludedColumns, rightNullable: kind == JoinKind.LeftOuter);
		return new KqlResult(columns,
			StreamJoinRows(left, right, leftKeys, rightKeys, kind, left.Columns.Count, included, columns.Count));
	}

	// lookup (<right>) on <key>: sugar over a leftouter join for dimension-style enrichment. The only
	// difference from a leftouter join (confirmed against the reference executor) is that the right-side
	// KEY columns are dropped from the output — they equal the left keys. Unmatched left rows keep their
	// columns with the appended right columns null. Reuses the same hash-join machinery as join.
	static KqlResult ApplyLookup(KqlResult left, LookupOperator op, DateTime now, SubqueryRunner runSub)
	{
		if (op.Parameters.Count > 0)
			throw new UnsupportedKqlException("lookup parameters not supported (lookup is always a leftouter enrichment)");
		if (op.LookupClause is not JoinOnClause onClause)
			throw new UnsupportedKqlException("lookup requires an 'on' clause with equality key(s)");
		var right = runSub(op.Expression, "lookup");
		var (leftKeys, rightKeys, rightKeyCols) = ResolveJoinKeys(onClause, left, right, "lookup");
		var exclude = new HashSet<int>(rightKeyCols.Where(i => i >= 0));
		var (columns, included) = BuildJoinColumns(left, right, exclude, rightNullable: true);
		return new KqlResult(columns,
			StreamJoinRows(left, right, leftKeys, rightKeys, JoinKind.LeftOuter, left.Columns.Count, included, columns.Count));
	}

	static readonly HashSet<int> NoExcludedColumns = [];

	static JoinKind ParseJoinKind(SyntaxList<NamedParameter> parameters, string opName)
	{
		var kind = JoinKind.InnerUnique;
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

	// The right side of a join/lookup: a full nested pipeline over the SAME log root. Cross-log/table
	// addressing is out of scope, so the leading table reference must match the current root (`events` or
	// `spans`) — anything else gets a precise "must be the same log" error rather than a generic
	// unknown-table one.
	static KqlResult RunCorrelationSubquery<T>(Expression rightExpr, IQueryable<T> source, DateTime now, string opName, RootSpec<T> spec, KqlTranslationOptions options)
	{
		var ops = FlattenPipeline(rightExpr).ToList();
		if (ops.Count == 0 || ops[0] is not NameReference tbl)
			throw new UnsupportedKqlException($"{opName} right side must be a subquery over '{spec.TableName}'");
		if (!string.Equals(tbl.SimpleName, spec.TableName, StringComparison.Ordinal))
			throw new UnsupportedKqlException(
				$"{opName} right side must be the same log ('{spec.TableName}'); cross-log/table joins are not supported (got '{tbl.SimpleName}')");
		return ExecutePipeline(source, rightExpr, now, spec, options);
	}

	// Resolves the on-clause into per-side key value-extractors. Two accepted forms (both equality):
	//   `on Col[, Col2...]`  — the same column name on both sides (incl. the Properties bare-name
	//                          fallback via the usual resolution);
	//   `on $left.A == $right.B` — an explicit equality between one column on each side.
	// Anything else (non-equality, or an equality that is not $left/$right) is rejected precisely.
	// The returned RightKeyColumns carry each right key's column index (or -1 for a Properties
	// fallback) so lookup can drop the right key columns from its output.
	static (Func<object?[], object?>[] Left, Func<object?[], object?>[] Right, int[] RightKeyColumns) ResolveJoinKeys(
		JoinOnClause onClause, KqlResult left, KqlResult right, string opName)
	{
		var lefts = new List<Func<object?[], object?>>();
		var rights = new List<Func<object?[], object?>>();
		var rightCols = new List<int>();

		foreach (var element in onClause.Expressions)
		{
			switch (element.Element)
			{
				case NameReference n:
					lefts.Add(ResolveKeyExtractor(left, n.SimpleName, opName, out _));
					rights.Add(ResolveKeyExtractor(right, n.SimpleName, opName, out var idx));
					rightCols.Add(idx);
					break;
				case BinaryExpression { Kind: SyntaxKind.EqualExpression, Left: var lhs, Right: var rhs }:
					{
						var a = DollarRef(lhs);
						var b = DollarRef(rhs);
						if (a is null || b is null || a.Value.IsLeft == b.Value.IsLeft)
							throw new UnsupportedKqlException(
								$"{opName} on: an equality must be '$left.col == $right.col' (got an unsupported on-clause)");
						var (leftName, rightName) = a.Value.IsLeft
							? (a.Value.Column, b.Value.Column)
							: (b.Value.Column, a.Value.Column);
						lefts.Add(ResolveKeyExtractor(left, leftName, opName, out _));
						rights.Add(ResolveKeyExtractor(right, rightName, opName, out var ridx));
						rightCols.Add(ridx);
						break;
					}
				default:
					throw new UnsupportedKqlException(
						$"{opName} on: only equality on column names is supported (col or $left.col == $right.col), got '{element.Element.Kind}'");
			}
		}

		if (lefts.Count == 0)
			throw new UnsupportedKqlException($"{opName} requires at least one 'on' key");
		return (lefts.ToArray(), rights.ToArray(), rightCols.ToArray());
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

	// A join-key value extractor over a row of `shape`: a direct column (cheap index) or, when the
	// name is not a column, the Properties.<name> bare-name fallback via the shared BagValueExtractor.
	// `columnIndex` is the resolved column index, or -1 for the Properties fallback (so lookup knows
	// there is no right key column to drop).
	static Func<object?[], object?> ResolveKeyExtractor(KqlResult shape, string name, string opName, out int columnIndex)
	{
		var idx = ResolveColumnIndexCI(shape.Columns, name);
		if (idx >= 0)
		{
			columnIndex = idx;
			return row => row[idx];
		}
		columnIndex = -1;
		return BagValueExtractor(shape.Columns, name, $"{opName} on: unknown column '{name}'");
	}

	// Builds the joined column list: left columns verbatim, then the right columns (minus any
	// excluded, used by lookup for the right key columns), each suffixed to a unique name on
	// collision. `included` maps output right-column slots back to right-row indices. When
	// `rightNullable` (a leftouter join or a lookup), each right value-type column is declared NULLABLE:
	// an unmatched left row appends null right cells, so a non-nullable declared type would crash the
	// downstream object→value unbox (strings are already null-capable and pass through unchanged).
	static (List<KqlColumn> Columns, int[] Included) BuildJoinColumns(
		KqlResult left, KqlResult right, ISet<int> excludeRight, bool rightNullable)
	{
		var columns = new List<KqlColumn>(left.Columns);
		var used = new HashSet<string>(columns.Select(c => c.Name), StringComparer.Ordinal);
		var included = new List<int>();
		for (var i = 0; i < right.Columns.Count; i++)
		{
			if (excludeRight.Contains(i))
				continue;
			var baseName = right.Columns[i].Name;
			var name = baseName;
			var n = 1;
			while (!used.Add(name))
				name = baseName + n++;
			var type = rightNullable ? Nullable(right.Columns[i].ClrType) : right.Columns[i].ClrType;
			columns.Add(new KqlColumn(name, type));
			included.Add(i);
		}
		return (columns, included.ToArray());
	}

	static async IAsyncEnumerable<object?[]> StreamJoinRows(
		KqlResult left,
		KqlResult right,
		Func<object?[], object?>[] leftKeys,
		Func<object?[], object?>[] rightKeys,
		JoinKind kind,
		int leftWidth,
		int[] rightIncluded,
		int outputWidth,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		// Build side: buffer the right subquery, bucketed by key. A key with any null component never
		// matches (Kusto join-on-null semantics). The buffer is capped so a runaway right side fails fast
		// with a teaching error instead of OOMing.
		var cap = JoinBuildSideCapOverride ?? JoinBuildSideCap;
		var buffered = 0;
		var buckets = new Dictionary<GroupKey, List<object?[]>>();
		await foreach (var r in right.Rows.WithCancellation(ct).ConfigureAwait(false))
		{
			if (++buffered > cap)
				throw new UnsupportedKqlException(
					$"join right side exceeded {cap} rows; narrow it with where/take");
			var key = MakeKey(rightKeys, r);
			if (key is null)
				continue;
			if (!buckets.TryGetValue(key, out var list))
				buckets[key] = list = [];
			list.Add(r);
		}

		var seenLeft = kind == JoinKind.InnerUnique ? new HashSet<GroupKey>() : null;
		await foreach (var l in left.Rows.WithCancellation(ct).ConfigureAwait(false))
		{
			var key = MakeKey(leftKeys, l);
			if (kind == JoinKind.InnerUnique)
			{
				if (key is null)
					continue; // a null key can never match under inner semantics
				if (!seenLeft!.Add(key))
					continue; // de-dup the left side, keeping the first row per key
			}

			List<object?[]>? matches = null;
			if (key is not null)
				buckets.TryGetValue(key, out matches);

			if (matches is { Count: > 0 })
			{
				foreach (var r in matches)
				{
					var res = new object?[outputWidth];
					Array.Copy(l, res, leftWidth);
					for (var j = 0; j < rightIncluded.Length; j++)
						res[leftWidth + j] = r[rightIncluded[j]];
					yield return res;
				}
			}
			else if (kind == JoinKind.LeftOuter)
			{
				var res = new object?[outputWidth];
				Array.Copy(l, res, leftWidth);
				yield return res; // right columns stay null
			}
			// inner / innerunique with no match: drop the left row
		}
	}

	// A join key from a row, or null when any component is null (a null key never matches in Kusto).
	// Each component is normalized so keys compare by VALUE, not boxed CLR type (see NormalizeKeyValue).
	static GroupKey? MakeKey(Func<object?[], object?>[] extractors, object?[] row)
	{
		var values = new object?[extractors.Length];
		for (var i = 0; i < extractors.Length; i++)
		{
			var v = extractors[i](row);
			if (v is null)
				return null;
			values[i] = NormalizeKeyValue(v);
		}
		return new GroupKey(values);
	}

	// Normalizes a join-key value so equal keys match across trivial CLR-type differences: every integral
	// width → long, float/double → double; strings, bools and datetimes pass through unchanged. Without
	// this, GroupKey's boxed object.Equals treats 2 (int) and 2L (long) as unequal — e.g. a raw `Level`
	// (int) column joined against a toint()-computed (long) key — so the join would silently emit nothing.
	// Cross-kind keys (string vs numeric) still never match, matching Kusto's same-typed-key requirement.
	static object NormalizeKeyValue(object v) => v switch
	{
		long l => l,
		int or short or sbyte or byte or ushort or uint => Convert.ToInt64(v),
		double d => d,
		float f => (double)f,
		_ => v,
	};

	// mv-expand <col>: one output row per element of a JSON-array value (Properties.<key> or a bare
	// property name — the value json_extract returns for an array is its raw JSON text). Elements are
	// string-typed like every other Properties value (a string element is its text; a number/bool/
	// object element is its raw JSON). A non-array, missing, or null value drops the row (Kusto's
	// default). PRODUCTION-ONLY differential coverage: the reference executor (KustoLoco) models
	// dynamics as real array columns and cannot expand our Properties-JSON strings, so there is no
	// differential test for this — semantics are pinned by unit tests instead.
	static KqlResult ApplyMvExpand(KqlResult input, MvExpandOperator op, DateTime now)
	{
		if (op.Parameters.Count > 0)
			throw new UnsupportedKqlException("mv-expand parameters (bagexpansion / with_itemindex) not supported");
		if (op.RowLimitClause is not null)
			throw new UnsupportedKqlException("mv-expand row limit not supported");
		if (op.Expressions.Count != 1)
			throw new UnsupportedKqlException("mv-expand supports exactly one column");
		if (op.Expressions[0].Element is not MvExpandExpression mve)
			throw new UnsupportedKqlException("mv-expand supports a column or Properties.<key>");
		if (mve.ToTypeOf is not null)
			throw new UnsupportedKqlException("mv-expand 'to typeof(...)' not supported (elements are string-typed)");

		var (outName, targetIndex, extractor) = ResolveMvColumn(input, mve.Expression);
		var columns = input.Columns.ToList();
		int outIndex;
		if (targetIndex >= 0)
		{
			columns[targetIndex] = new KqlColumn(outName, typeof(string)); // expanded in place, now string
			outIndex = targetIndex;
		}
		else
		{
			columns.Add(new KqlColumn(outName, typeof(string)));
			outIndex = columns.Count - 1;
		}
		return new KqlResult(columns, StreamMvExpand(input.Rows, extractor, outIndex, input.Columns.Count, columns.Count));
	}

	// Resolves the mv-expand target to (output column name, existing column index or -1, JSON-string
	// extractor). A bare name that is a column expands that column in place; otherwise it (and the
	// Properties.<key> path form) reads the property and appends a new column named after the leaf.
	static (string Name, int TargetIndex, Func<object?[], string?> Extractor) ResolveMvColumn(KqlResult input, Expression expr)
	{
		switch (expr)
		{
			case NameReference n:
				{
					var idx = ResolveColumnIndexCI(input.Columns, n.SimpleName);
					if (idx >= 0)
						return (n.SimpleName, idx, row => row[idx] as string);
					return (n.SimpleName, -1,
						BagValueExtractor(input.Columns, n.SimpleName, $"mv-expand: unknown column '{n.SimpleName}'"));
				}
			case PathExpression p when IsPropertiesPath(p, out var key):
				return (key, -1,
					BagValueExtractor(input.Columns, key, "mv-expand Properties.<key>: input has no PropertiesJson column"));
			case ElementExpression el when IsPropertiesIndex(el, out var idxKey):
				return (idxKey, -1,
					BagValueExtractor(input.Columns, idxKey, "mv-expand Properties[\"key\"]: input has no PropertiesJson column"));
			default:
				throw new UnsupportedKqlException("mv-expand supports a column, Properties.<key>, or Properties[\"key\"]");
		}
	}

	static async IAsyncEnumerable<object?[]> StreamMvExpand(
		IAsyncEnumerable<object?[]> source,
		Func<object?[], string?> extractor,
		int outIndex,
		int inputWidth,
		int outputWidth,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			var elements = TryParseJsonArray(extractor(row));
			if (elements is null)
				continue; // non-array / missing / null → drop the row (empty array drops it too)
			foreach (var element in elements)
			{
				var res = new object?[outputWidth];
				Array.Copy(row, res, inputWidth);
				res[outIndex] = element;
				yield return res;
			}
		}
	}

	// Parses a JSON array string into its elements as strings (string element → its text; other kinds
	// → raw JSON), or null when the value is not a JSON array. Kept separate from the iterator so the
	// JsonDocument is disposed before any yield.
	static List<string?>? TryParseJsonArray(string? json)
	{
		if (string.IsNullOrEmpty(json))
			return null;
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Array)
				return null;
			var list = new List<string?>();
			foreach (var el in doc.RootElement.EnumerateArray())
				list.Add(el.ValueKind switch
				{
					JsonValueKind.String => el.GetString(),
					JsonValueKind.Null or JsonValueKind.Undefined => null,
					_ => el.GetRawText(),
				});
			return list;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	// parse <source> with "lit" Col "lit" Col … — the simple (default) star-free Kusto form. Literal
	// segments are matched in order; the text between them fills the capture columns, which are
	// string-typed and join the row shape (replacing a same-named column in place, like extend). A
	// trailing capture (no following literal) takes the rest of the string. Non-matching rows are
	// RETAINED with null captures (Kusto's `parse` semantics — `parse-where`, which would drop them,
	// is not implemented). Unsupported flavors (kind=regex/relaxed, typed captures like Col:long,
	// `*` wildcards) throw precisely. PRODUCTION-ONLY: the reference executor does not implement
	// `parse`, so semantics are pinned by unit tests, not a differential.
	static KqlResult ApplyParse(KqlResult input, ParseOperator op, DateTime now)
	{
		if (op.Parameters.Count > 0)
			throw new UnsupportedKqlException(
				"parse: only the default simple star-free form is supported (kind=regex / kind=relaxed not supported)");

		var rowParam = Expr.Parameter(typeof(object?[]), "row");
		var ctx = new RowScalarContext(rowParam, input.Columns) { UtcNow = now };
		var srcExpr = KqlScalar.Compile(op.Expression, ctx);
		if (srcExpr.Type != typeof(string))
			throw new UnsupportedKqlException($"parse source must be a string column, got {srcExpr.Type.Name}");
		var srcFn = Expr.Lambda<Func<object?[], string?>>(srcExpr, rowParam).Compile();

		var segments = ParsePatternSegments(op.Patterns);
		var captureNames = segments.Where(s => s.IsCapture).Select(s => s.Value).ToList();

		var columns = input.Columns.ToList();
		var captureIndex = new int[captureNames.Count];
		for (var i = 0; i < captureNames.Count; i++)
		{
			var existing = FindColumnIndex(columns, captureNames[i]);
			if (existing >= 0)
			{
				columns[existing] = new KqlColumn(captureNames[i], typeof(string));
				captureIndex[i] = existing;
			}
			else
			{
				columns.Add(new KqlColumn(captureNames[i], typeof(string)));
				captureIndex[i] = columns.Count - 1;
			}
		}

		return new KqlResult(columns, StreamParse(input.Rows, srcFn, segments, captureIndex, input.Columns.Count, columns.Count));
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

	static async IAsyncEnumerable<object?[]> StreamParse(
		IAsyncEnumerable<object?[]> source,
		Func<object?[], string?> srcFn,
		IReadOnlyList<(bool IsCapture, string Value)> segments,
		int[] captureIndex,
		int inputWidth,
		int outputWidth,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			var captures = MatchParse(srcFn(row), segments, captureIndex.Length);
			var res = new object?[outputWidth];
			Array.Copy(row, res, inputWidth);
			for (var i = 0; i < captureIndex.Length; i++)
				res[captureIndex[i]] = captures[i];
			yield return res;
		}
	}

	// Simple (star-free) parse matcher: literals must appear in order; the text between them fills the
	// captures. Returns the capture values, or an all-null array on non-match (the row is still kept).
	static string?[] MatchParse(string? s, IReadOnlyList<(bool IsCapture, string Value)> segments, int captureCount)
	{
		var result = new string?[captureCount];
		if (s is null)
			return result;

		var pos = 0;
		var ci = 0;
		var i = 0;
		while (i < segments.Count)
		{
			if (!segments[i].IsCapture)
			{
				var lit = segments[i].Value;
				if (pos + lit.Length > s.Length || string.CompareOrdinal(s, pos, lit, 0, lit.Length) != 0)
					return new string?[captureCount]; // literal not found at position → non-match
				pos += lit.Length;
				i++;
			}
			else if (i + 1 < segments.Count)
			{
				// capture delimited by the following literal (segments never have adjacent captures)
				var lit = segments[i + 1].Value;
				var idx = lit.Length == 0 ? pos : s.IndexOf(lit, pos, StringComparison.Ordinal);
				if (idx < 0)
					return new string?[captureCount];
				result[ci++] = s.Substring(pos, idx - pos);
				pos = idx + lit.Length;
				i += 2;
			}
			else
			{
				result[ci++] = s[pos..]; // trailing capture takes the rest
				i++;
			}
		}
		return result;
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
	// column ref or a Properties access here (same reach as ApplySort); the post-split ApplyPostTop
	// handles computed keys. Default ordering is descending, matching Kusto.
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
		// key shapes. Computed keys stay post-split (ApplyPostSort/ApplyPostTop compile any scalar).
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
			// ResolveProperties / BagValueExtractor, see KqlPropertyKeys).
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
