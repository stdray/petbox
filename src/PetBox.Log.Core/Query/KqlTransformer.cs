using System.Runtime.CompilerServices;
using Kusto.Language;
using Kusto.Language.Syntax;
using LinqToDB;
using PetBox.Log.Core.Data;
using Expr = System.Linq.Expressions.Expression;
using ParamExpr = System.Linq.Expressions.ParameterExpression;

namespace PetBox.Log.Core.Query;

public static class KqlTransformer
{
	public const string EventsTable = "events";

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

	public static KqlResult Execute(IQueryable<LogEntryRecord> source, KustoCode code, TimeProvider? clock = null)
	{
		var parseErrors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
		if (parseErrors.Count > 0)
			throw new UnsupportedKqlException("KQL parse error: " + string.Join("; ", parseErrors.Select(d => d.Message)));

		var operators = FlattenPipeline(code.Syntax).ToList();
		if (operators.Count == 0)
			throw new UnsupportedKqlException("empty query");

		if (operators[0] is not NameReference tableName)
			throw new UnsupportedKqlException($"expected table reference, got {operators[0].GetType().Name}");
		if (!string.Equals(tableName.SimpleName, EventsTable, StringComparison.Ordinal))
			throw new UnsupportedKqlException($"unknown table '{tableName.SimpleName}'; only '{EventsTable}' is supported");

		// now()/ago() resolve to this single instant for the whole query (see ScalarContext.UtcNow).
		var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;

		var pipeline = operators.Skip(1).ToList();
		var splitAt = pipeline.FindIndex(IsShapeChangingOp);

		var (preOps, postOps) = splitAt < 0
			? (pipeline, new List<SyntaxNode>())
			: (pipeline.Take(splitAt).ToList(), pipeline.Skip(splitAt).ToList());

		var preResult = ApplyPipeline(source, preOps, now);
		var eventShape = new KqlResult(EventRecordColumns, StreamEventRecordRows(preResult));

		return postOps.Count == 0
			? eventShape
			: ApplyShapeChanges(eventShape, postOps, now);
	}

	// An operator whose output is no longer the event shape. `distinct` reduces to its chosen
	// columns; the others compute/aggregate. `order by` / `take` / `top` are NOT here — they
	// preserve the current shape and are handled both pre-split (SQL) and post-split (in-memory),
	// so a query that only sorts/limits stays entirely in the linq2db path.
	static bool IsShapeChangingOp(SyntaxNode op) =>
		op is ProjectOperator or CountOperator or SummarizeOperator or ExtendOperator or DistinctOperator;

	public static bool HasShapeChangingOps(KustoCode code)
	{
		ArgumentNullException.ThrowIfNull(code);
		return FlattenPipeline(code.Syntax).Any(IsShapeChangingOp);
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
		if (!string.Equals(tableName.SimpleName, EventsTable, StringComparison.Ordinal))
			throw new UnsupportedKqlException($"unknown table '{tableName.SimpleName}'; only '{EventsTable}' is supported");

		var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
		var q = source;
		foreach (var op in operators.Skip(1))
		{
			q = op switch
			{
				FilterOperator f => ApplyWhere(q, f, now),
				TakeOperator t => ApplyTake(q, t),
				SortOperator s => ApplySort(q, s),
				TopOperator t => ApplyTop(q, t),
				_ => throw new UnsupportedKqlException($"operator '{op.Kind}' not supported"),
			};
		}
		return q;
	}

	static KqlResult ApplyShapeChanges(KqlResult input, IReadOnlyList<SyntaxNode> ops, DateTime now)
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
		var newColumns = new List<KqlColumn>();
		var producers = new List<Func<object?[], object?>>();
		var rowParam = Expr.Parameter(typeof(object?[]), "row");
		var ctx = new RowScalarContext(rowParam, input.Columns) { UtcNow = now };

		foreach (var element in project.Expressions)
		{
			switch (element.Element)
			{
				case NameReference n:
					AddPassThrough(n.SimpleName, n.SimpleName);
					break;
				case SimpleNamedExpression { Name: NameDeclaration alias, Expression: NameReference src }:
					AddPassThrough(alias.Name.SimpleName, src.SimpleName);
					break;
				case SimpleNamedExpression { Name: NameDeclaration alias, Expression: var expr }:
					var (type, fn) = CompileCell(expr, ctx, rowParam);
					newColumns.Add(new KqlColumn(alias.Name.SimpleName, type));
					producers.Add(fn);
					break;
				default:
					throw new UnsupportedKqlException(
						$"project expression '{element.Element.Kind}' not supported (use a column ref or 'name = expression')");
			}
		}

		return new KqlResult(newColumns, StreamCells(input.Rows, producers.ToArray()));

		void AddPassThrough(string outputName, string sourceName)
		{
			var sourceIndex = FindColumnIndex(input.Columns, sourceName);
			if (sourceIndex >= 0)
			{
				newColumns.Add(new KqlColumn(outputName, input.Columns[sourceIndex].ClrType));
				var captured = sourceIndex;
				producers.Add(row => row[captured]);
				return;
			}
			// Bare-name fallback: an unknown name is a Properties.<name> lookup, when the row shape still
			// carries PropertiesJson (else the precise unknown-column error stands).
			var propIdx = FindColumnIndex(input.Columns, nameof(LogEntryRecord.PropertiesJson));
			if (propIdx < 0)
				throw new UnsupportedKqlException($"project: unknown column '{sourceName}'");
			newColumns.Add(new KqlColumn(outputName, typeof(string)));
			var path = "$." + sourceName;
			producers.Add(row => KqlSqlExpressions.JsonExtract(row[propIdx] as string, path));
		}
	}

	// extend appends computed columns (evaluated in-memory over the current row). Re-using an
	// existing name replaces that column in place (KQL extend semantics). Each expression sees
	// the input columns only.
	static KqlResult ApplyExtend(KqlResult input, ExtendOperator extend, DateTime now)
	{
		var columns = input.Columns.ToList();
		var rowParam = Expr.Parameter(typeof(object?[]), "row");
		var ctx = new RowScalarContext(rowParam, input.Columns) { UtcNow = now };
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

	// A by-key: (output name, CLR type, value extractor). Bare column refs stay a cheap index
	// pass-through; Properties.<key> is json_extract; anything else (incl. bin) compiles as a
	// scalar. An un-aliased function call (e.g. `by bin(Timestamp, 1h)`) is named after its
	// first column argument, matching Kusto's default column naming.
	static (string Name, Type Type, Func<object?[], object?> Fn) CompileByKey(
		Expression element, KqlResult input, RowScalarContext ctx, ParamExpr rowParam)
	{
		switch (element)
		{
			case NameReference n:
				{
					var idx = FindColumnIndex(input.Columns, n.SimpleName);
					if (idx >= 0)
						return (n.SimpleName, input.Columns[idx].ClrType, row => row[idx]);
					// Bare-name fallback: group by Properties.<name> when the shape still has PropertiesJson.
					var propIdx = FindColumnIndex(input.Columns, nameof(LogEntryRecord.PropertiesJson));
					if (propIdx < 0)
						throw new UnsupportedKqlException($"summarize by: unknown column '{n.SimpleName}'");
					var path = "$." + n.SimpleName;
					return (n.SimpleName, typeof(string),
						row => KqlSqlExpressions.JsonExtract(row[propIdx] as string, path));
				}
			case PathExpression p when IsPropertiesPath(p, out var propKey):
				{
					var propIdx = FindColumnIndex(input.Columns, nameof(LogEntryRecord.PropertiesJson));
					if (propIdx < 0)
						throw new UnsupportedKqlException("summarize by Properties.<key>: input has no PropertiesJson column");
					return ("Properties." + propKey, typeof(string),
						row => KqlSqlExpressions.JsonExtract(row[propIdx] as string, "$." + propKey));
				}
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
					$"summarize by '{element.Kind}' not supported (column ref, Properties.<key>, or 'name = expression')");
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

			case "sum":
				{
					var (type, f) = Arg("sum");
					RequireNumeric(type, "sum");
					return KqlScalar.NonNullable(type) == typeof(double)
						? new AggSpec(name, typeof(double), () => new SumDoubleAccumulator(f))
						: new AggSpec(name, typeof(long), () => new SumLongAccumulator(f));
				}

			case "avg":
				{
					var (type, f) = Arg("avg");
					RequireNumeric(type, "avg");
					return new AggSpec(name, typeof(double), () => new AvgAccumulator(f));
				}

			case "min":
				{
					var (type, f) = Arg("min");
					return new AggSpec(name, type, () => new MinMaxAccumulator(f, min: true));
				}

			case "max":
				{
					var (type, f) = Arg("max");
					return new AggSpec(name, type, () => new MinMaxAccumulator(f, min: false));
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
			if (arg(row) is { } v)
				_seen.Add(v);
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
			}
			foreach (var acc in accs)
				acc.Add(row);
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

	static async IAsyncEnumerable<object?[]> StreamCells(
		IAsyncEnumerable<object?[]> source,
		Func<object?[], object?>[] producers,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			var result = new object?[producers.Length];
			for (var i = 0; i < producers.Length; i++)
				result[i] = producers[i](row);
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
			foreach (var (idx, fn) in writes)
				result[idx] = fn(row);
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
			buffer.Add(row);

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
	// smallest value, matching Kusto's nulls-first ascending order.
	sealed class BoxedComparer : IComparer<object?>
	{
		public static readonly BoxedComparer Instance = new();
		public int Compare(object? x, object? y) => Comparer<object>.Default.Compare(x!, y!);
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
						var idx = FindColumnIndex(input.Columns, n.SimpleName);
						if (idx >= 0)
						{
							outputColumns.Add(input.Columns[idx]);
							extractors.Add(row => row[idx]);
							break;
						}
						// Bare-name fallback: distinct Properties.<name> when the shape still has PropertiesJson.
						var propIdx = FindColumnIndex(input.Columns, nameof(LogEntryRecord.PropertiesJson));
						if (propIdx < 0)
							throw new UnsupportedKqlException($"distinct: unknown column '{n.SimpleName}'");
						outputColumns.Add(new KqlColumn(n.SimpleName, typeof(string)));
						var path = "$." + n.SimpleName;
						extractors.Add(row => KqlSqlExpressions.JsonExtract(row[propIdx] as string, path));
						break;
					}
				case PathExpression p when IsPropertiesPath(p, out var propKey):
					{
						var propIdx = FindColumnIndex(input.Columns, nameof(LogEntryRecord.PropertiesJson));
						if (propIdx < 0)
							throw new UnsupportedKqlException("distinct Properties.<key>: input has no PropertiesJson column");
						outputColumns.Add(new KqlColumn("Properties." + propKey, typeof(string)));
						extractors.Add(row => KqlSqlExpressions.JsonExtract(row[propIdx] as string, "$." + propKey));
						break;
					}
				default:
					throw new UnsupportedKqlException(
						$"distinct expression '{element.Element.Kind}' not supported (use column refs, Properties.<key>, or '*')");
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
				yield return values;
		}
	}

	static int FindColumnIndex(IReadOnlyList<KqlColumn> columns, string name)
	{
		for (var i = 0; i < columns.Count; i++)
			if (string.Equals(columns[i].Name, name, StringComparison.Ordinal))
				return i;
		return -1;
	}

	static IQueryable<LogEntryRecord> ApplyPipeline(IQueryable<LogEntryRecord> source, IReadOnlyList<SyntaxNode> operators, DateTime now)
	{
		var q = source;
		foreach (var op in operators)
		{
			q = op switch
			{
				FilterOperator f => ApplyWhere(q, f, now),
				TakeOperator t => ApplyTake(q, t),
				SortOperator s => ApplySort(q, s),
				TopOperator t => ApplyTop(q, t),
				_ => throw new UnsupportedKqlException($"operator '{op.Kind}' not supported"),
			};
		}
		return q;
	}

	static async IAsyncEnumerable<object?[]> StreamEventRecordRows(
		IQueryable<LogEntryRecord> query,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var r in query.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
		{
			yield return
			[
				r.Id,
				r.ServiceKey,
				DateTimeOffset.FromUnixTimeMilliseconds(r.TimestampMs).UtcDateTime,
				r.Level,
				LevelName(r.Level),
				r.Message,
				r.MessageTemplate,
				r.Exception,
				r.PropertiesJson,
			];
		}
	}

	static string LevelName(int level) => level switch
	{
		0 => "Verbose",
		1 => "Debug",
		2 => "Information",
		3 => "Warning",
		4 => "Error",
		5 => "Fatal",
		_ => "Unknown",
	};

	static IEnumerable<SyntaxNode> FlattenPipeline(SyntaxNode root)
	{
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

	static IQueryable<LogEntryRecord> ApplyWhere(IQueryable<LogEntryRecord> source, FilterOperator filter, DateTime now)
	{
		var row = Expr.Parameter(typeof(LogEntryRecord), "e");
		var body = KqlScalar.Compile(filter.Condition, new RecordScalarContext(row) { UtcNow = now });
		if (body.Type != typeof(bool))
			throw new UnsupportedKqlException($"where condition must be boolean, got {body.Type.Name}");
		var predicate = Expr.Lambda<Func<LogEntryRecord, bool>>(body, row);
		return source.Where(predicate);
	}

	static IQueryable<LogEntryRecord> ApplyTake(IQueryable<LogEntryRecord> source, TakeOperator take)
	{
		if (take.Expression is not LiteralExpression { LiteralValue: long n })
			throw new UnsupportedKqlException("take requires an integer literal");
		return source.Take(checked((int)n));
	}

	static IQueryable<LogEntryRecord> ApplySort(IQueryable<LogEntryRecord> source, SortOperator sort)
	{
		IOrderedQueryable<LogEntryRecord>? ordered = null;
		foreach (var element in sort.Expressions)
		{
			var (columnName, descending) = element.Element switch
			{
				OrderedExpression { Expression: NameReference n, Ordering: var o } => (n.SimpleName, !IsAscending(o)),
				NameReference n => (n.SimpleName, true),
				_ => throw new UnsupportedKqlException($"order-by expression '{element.Element.Kind}' not supported"),
			};

			ordered = ApplyOrder(source, ordered, columnName, descending);
		}
		return ordered ?? source;
	}

	// `top N by <column> [asc|desc]` on the SQL path = ORDER BY + LIMIT. The by-expression is a
	// bare column ref here (same reach as ApplySort); the post-split ApplyPostTop handles
	// computed keys. Default ordering is descending, matching Kusto.
	static IQueryable<LogEntryRecord> ApplyTop(IQueryable<LogEntryRecord> source, TopOperator top)
	{
		if (top.Expression is not LiteralExpression { LiteralValue: long n })
			throw new UnsupportedKqlException("top requires an integer literal count");
		var (columnName, descending) = top.ByExpression switch
		{
			OrderedExpression { Expression: NameReference nm, Ordering: var o } => (nm.SimpleName, !IsAscending(o)),
			NameReference nm => (nm.SimpleName, true),
			_ => throw new UnsupportedKqlException($"top by-expression '{top.ByExpression.Kind}' not supported (use a column ref)"),
		};
		return ApplyOrder(source, null, columnName, descending).Take(checked((int)n));
	}

	static bool IsAscending(OrderingClause? o) =>
		string.Equals(o?.AscOrDescKeyword?.Text, "asc", StringComparison.Ordinal);

	static IOrderedQueryable<LogEntryRecord> ApplyOrder(
		IQueryable<LogEntryRecord> source,
		IOrderedQueryable<LogEntryRecord>? prior,
		string columnName,
		bool descending)
	{
		var row = Expr.Parameter(typeof(LogEntryRecord), "e");
		var access = new RecordScalarContext(row).ResolveColumn(columnName);
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
			.MakeGenericMethod(typeof(LogEntryRecord), keyType);

		var callExpr = Expr.Call(queryableMethod, (prior ?? source).Expression, Expr.Quote(keyLambda));
		return (IOrderedQueryable<LogEntryRecord>)source.Provider.CreateQuery<LogEntryRecord>(callExpr);
	}

	// Recognizes a `Properties.<key>` path (the only nested access the schema exposes). Shared
	// by the scalar engine (KqlScalar) and summarize's by-clause.
	internal static bool IsPropertiesPath(PathExpression path, out string key)
	{
		if (path.Expression is NameReference { SimpleName: "Properties" }
			&& path.Selector is NameReference sel)
		{
			key = sel.SimpleName;
			return true;
		}
		key = "";
		return false;
	}
}
