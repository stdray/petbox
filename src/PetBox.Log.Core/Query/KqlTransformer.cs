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

	public static KqlResult Execute(IQueryable<LogEntryRecord> source, KustoCode code)
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

		var pipeline = operators.Skip(1).ToList();
		var splitAt = pipeline.FindIndex(IsShapeChangingOp);

		var (preOps, postOps) = splitAt < 0
			? (pipeline, new List<SyntaxNode>())
			: (pipeline.Take(splitAt).ToList(), pipeline.Skip(splitAt).ToList());

		var preResult = ApplyPipeline(source, preOps);
		var eventShape = new KqlResult(EventRecordColumns, StreamEventRecordRows(preResult));

		return postOps.Count == 0
			? eventShape
			: ApplyShapeChanges(eventShape, postOps);
	}

	static bool IsShapeChangingOp(SyntaxNode op) =>
		op is ProjectOperator or CountOperator or SummarizeOperator or ExtendOperator;

	public static bool HasShapeChangingOps(KustoCode code)
	{
		ArgumentNullException.ThrowIfNull(code);
		return FlattenPipeline(code.Syntax).Any(IsShapeChangingOp);
	}

	public static IQueryable<LogEntryRecord> Apply(IQueryable<LogEntryRecord> source, KustoCode code)
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

		var q = source;
		foreach (var op in operators.Skip(1))
		{
			q = op switch
			{
				FilterOperator f => ApplyWhere(q, f),
				TakeOperator t => ApplyTake(q, t),
				SortOperator s => ApplySort(q, s),
				_ => throw new UnsupportedKqlException($"operator '{op.Kind}' not supported"),
			};
		}
		return q;
	}

	static KqlResult ApplyShapeChanges(KqlResult input, IReadOnlyList<SyntaxNode> ops)
	{
		var current = input;
		foreach (var op in ops)
		{
			current = op switch
			{
				ExtendOperator e => ApplyExtend(current, e),
				ProjectOperator p => ApplyProject(current, p),
				CountOperator => ApplyCount(current),
				SummarizeOperator s => ApplySummarize(current, s),
				_ => throw new UnsupportedKqlException($"operator '{op.Kind}' not supported in shape-changing pipeline"),
			};
		}
		return current;
	}

	// project selects/renames/computes columns. A bare column ref or `alias = column` is a
	// pass-through (keeps the source type); `alias = <expression>` compiles a scalar over the
	// current row shape and evaluates it in-memory (post-split). An un-aliased computed
	// expression is rejected — KQL auto-names it, which we don't reproduce.
	static KqlResult ApplyProject(KqlResult input, ProjectOperator project)
	{
		var newColumns = new List<KqlColumn>();
		var producers = new List<Func<object?[], object?>>();
		var rowParam = Expr.Parameter(typeof(object?[]), "row");
		var ctx = new RowScalarContext(rowParam, input.Columns);

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
			if (sourceIndex < 0)
				throw new UnsupportedKqlException($"project: unknown column '{sourceName}'");
			newColumns.Add(new KqlColumn(outputName, input.Columns[sourceIndex].ClrType));
			var captured = sourceIndex;
			producers.Add(row => row[captured]);
		}
	}

	// extend appends computed columns (evaluated in-memory over the current row). Re-using an
	// existing name replaces that column in place (KQL extend semantics). Each expression sees
	// the input columns only.
	static KqlResult ApplyExtend(KqlResult input, ExtendOperator extend)
	{
		var columns = input.Columns.ToList();
		var rowParam = Expr.Parameter(typeof(object?[]), "row");
		var ctx = new RowScalarContext(rowParam, input.Columns);
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

	static KqlResult ApplySummarize(KqlResult input, SummarizeOperator op)
	{
		var aggSpecs = new List<(string OutputName, KqlAggregate Kind)>();
		foreach (var element in op.Aggregates)
		{
			var (name, call) = element.Element switch
			{
				FunctionCallExpression f => ($"{f.Name.SimpleName}_", f),
				SimpleNamedExpression { Name: NameDeclaration alias, Expression: FunctionCallExpression f }
					=> (alias.Name.SimpleName, f),
				_ => throw new UnsupportedKqlException($"summarize aggregate '{element.Element.Kind}' not supported"),
			};

			if (!string.Equals(call.Name.SimpleName, "count", StringComparison.Ordinal))
				throw new UnsupportedKqlException($"aggregate '{call.Name.SimpleName}' not supported (only count() for now)");

			aggSpecs.Add((name, KqlAggregate.Count));
		}

		var extractors = new List<Func<object?[], object?>>();
		var outputColumns = new List<KqlColumn>();

		if (op.ByClause is not null)
		{
			foreach (var element in op.ByClause.Expressions)
			{
				switch (element.Element)
				{
					case NameReference n:
						{
							var idx = FindColumnIndex(input.Columns, n.SimpleName);
							if (idx < 0)
								throw new UnsupportedKqlException($"summarize by: unknown column '{n.SimpleName}'");
							outputColumns.Add(new KqlColumn(n.SimpleName, input.Columns[idx].ClrType));
							var captured = idx;
							extractors.Add(row => row[captured]);
							break;
						}
					case PathExpression p when IsPropertiesPath(p, out var propKey):
						{
							var propIdx = FindColumnIndex(input.Columns, nameof(LogEntryRecord.PropertiesJson));
							if (propIdx < 0)
								throw new UnsupportedKqlException("summarize by Properties.<key>: input has no PropertiesJson column");
							outputColumns.Add(new KqlColumn("Properties." + propKey, typeof(string)));
							var key = propKey;
							extractors.Add(row => KqlSqlExpressions.JsonExtract(row[propIdx] as string, "$." + key));
							break;
						}
					default:
						throw new UnsupportedKqlException(
							$"summarize by '{element.Element.Kind}' not supported (column ref or Properties.<key>)");
				}
			}
		}

		foreach (var (name, _) in aggSpecs)
			outputColumns.Add(new KqlColumn(name, typeof(long)));

		return new KqlResult(outputColumns, StreamSummarize(input.Rows, extractors.ToArray(), aggSpecs.Count));
	}

	enum KqlAggregate { Count }

	static async IAsyncEnumerable<object?[]> StreamSummarize(
		IAsyncEnumerable<object?[]> source,
		Func<object?[], object?>[] groupExtractors,
		int aggCount,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		var groups = new Dictionary<GroupKey, long>();

		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			var keyValues = new object?[groupExtractors.Length];
			for (var i = 0; i < groupExtractors.Length; i++)
				keyValues[i] = groupExtractors[i](row);
			var key = new GroupKey(keyValues);
			groups.TryGetValue(key, out var count);
			groups[key] = count + 1;
		}

		foreach (var (key, count) in groups)
		{
			var result = new object?[key.Values.Length + aggCount];
			for (var i = 0; i < key.Values.Length; i++)
				result[i] = key.Values[i];
			for (var i = 0; i < aggCount; i++)
				result[key.Values.Length + i] = count;
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

	static int FindColumnIndex(IReadOnlyList<KqlColumn> columns, string name)
	{
		for (var i = 0; i < columns.Count; i++)
			if (string.Equals(columns[i].Name, name, StringComparison.Ordinal))
				return i;
		return -1;
	}

	static IQueryable<LogEntryRecord> ApplyPipeline(IQueryable<LogEntryRecord> source, IReadOnlyList<SyntaxNode> operators)
	{
		var q = source;
		foreach (var op in operators)
		{
			q = op switch
			{
				FilterOperator f => ApplyWhere(q, f),
				TakeOperator t => ApplyTake(q, t),
				SortOperator s => ApplySort(q, s),
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

	static IQueryable<LogEntryRecord> ApplyWhere(IQueryable<LogEntryRecord> source, FilterOperator filter)
	{
		var row = Expr.Parameter(typeof(LogEntryRecord), "e");
		var body = KqlScalar.Compile(filter.Condition, new RecordScalarContext(row));
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
				OrderedExpression { Expression: NameReference n, Ordering: var o }
					=> (n.SimpleName, !string.Equals(o?.AscOrDescKeyword?.Text, "asc", StringComparison.Ordinal)),
				NameReference n => (n.SimpleName, true),
				_ => throw new UnsupportedKqlException($"order-by expression '{element.Element.Kind}' not supported"),
			};

			ordered = ApplyOrder(source, ordered, columnName, descending);
		}
		return ordered ?? source;
	}

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
