using System.Runtime.CompilerServices;
using Kusto.Language;
using Kusto.Language.Syntax;
using LinqToDB;
using YobaBox.Log.Core.Data;
using Expr = System.Linq.Expressions.Expression;
using ParamExpr = System.Linq.Expressions.ParameterExpression;

namespace YobaBox.Log.Core.Query;

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
				ProjectOperator p => ApplyProject(current, p),
				CountOperator => ApplyCount(current),
				SummarizeOperator s => ApplySummarize(current, s),
				_ => throw new UnsupportedKqlException($"operator '{op.Kind}' not supported in shape-changing pipeline"),
			};
		}
		return current;
	}

	static KqlResult ApplyProject(KqlResult input, ProjectOperator project)
	{
		var specs = new List<(string OutputName, int SourceIndex)>();
		var newColumns = new List<KqlColumn>();

		foreach (var element in project.Expressions)
		{
			var (outputName, sourceName) = element.Element switch
			{
				NameReference n => (n.SimpleName, n.SimpleName),
				SimpleNamedExpression { Name: NameDeclaration alias, Expression: NameReference src }
					=> (alias.Name.SimpleName, src.SimpleName),
				_ => throw new UnsupportedKqlException(
					$"project expression '{element.Element.Kind}' not supported (only column refs and 'alias = column')"),
			};

			var sourceIndex = FindColumnIndex(input.Columns, sourceName);
			if (sourceIndex < 0)
				throw new UnsupportedKqlException($"project: unknown column '{sourceName}'");

			specs.Add((outputName, sourceIndex));
			newColumns.Add(new KqlColumn(outputName, input.Columns[sourceIndex].ClrType));
		}

		return new KqlResult(newColumns, StreamProjected(input.Rows, specs.Select(s => s.SourceIndex).ToArray()));
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
							extractors.Add(row => KqlSqlExpressions.JsonExtractScalar(row[propIdx] as string, "$." + propKey));
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

	static async IAsyncEnumerable<object?[]> StreamProjected(
		IAsyncEnumerable<object?[]> source,
		int[] indices,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var row in source.WithCancellation(ct).ConfigureAwait(false))
		{
			var result = new object?[indices.Length];
			for (var i = 0; i < indices.Length; i++)
				result[i] = row[indices[i]];
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
		var body = BuildExpression(filter.Condition, row);
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
		var access = BuildColumnAccess(row, columnName);
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

	static Expr BuildExpression(Expression node, ParamExpr row) => node switch
	{
		BinaryExpression binary => BuildBinary(binary, row),
		FunctionCallExpression call when IsNotCall(call) => Expr.Not(BuildExpression(NotArgument(call), row)),
		ParenthesizedExpression paren => BuildExpression(paren.Expression, row),
		_ => throw new UnsupportedKqlException($"expression '{node.Kind}' not supported"),
	};

	static bool IsNotCall(FunctionCallExpression call) =>
		string.Equals(call.Name.SimpleName, "not", StringComparison.Ordinal);

	static Expression NotArgument(FunctionCallExpression call)
	{
		var args = call.ArgumentList.Expressions;
		if (args.Count != 1)
			throw new UnsupportedKqlException($"not() takes exactly one argument, got {args.Count}");
		return args[0].Element;
	}

	static Expr BuildBinary(BinaryExpression binary, ParamExpr row)
	{
		if (binary.Kind == SyntaxKind.AndExpression)
			return Expr.AndAlso(BuildExpression(binary.Left, row), BuildExpression(binary.Right, row));
		if (binary.Kind == SyntaxKind.OrExpression)
			return Expr.OrElse(BuildExpression(binary.Left, row), BuildExpression(binary.Right, row));

		var (column, access) = BuildLhs(binary.Left, row, binary.Kind);

		if (binary.Right is not LiteralExpression literal)
			throw new UnsupportedKqlException($"right side of '{binary.Kind}' must be a literal, got {binary.Right.Kind}");

		if (binary.Kind is SyntaxKind.ContainsExpression or SyntaxKind.ContainsCsExpression)
			return BuildContains(access, column, literal, binary.Kind == SyntaxKind.ContainsCsExpression);

		if (binary.Kind == SyntaxKind.HasExpression)
			return BuildContains(access, column, literal, caseSensitive: false);

		if (binary.Kind == SyntaxKind.HasCsExpression)
			return BuildContains(access, column, literal, caseSensitive: true);

		var coerced = CoerceLiteral(literal, access.Type, column);

		return binary.Kind switch
		{
			SyntaxKind.EqualExpression => Expr.Equal(access, coerced),
			SyntaxKind.NotEqualExpression => Expr.NotEqual(access, coerced),
			SyntaxKind.LessThanExpression => Expr.LessThan(access, coerced),
			SyntaxKind.LessThanOrEqualExpression => Expr.LessThanOrEqual(access, coerced),
			SyntaxKind.GreaterThanExpression => Expr.GreaterThan(access, coerced),
			SyntaxKind.GreaterThanOrEqualExpression => Expr.GreaterThanOrEqual(access, coerced),
			_ => throw new UnsupportedKqlException($"binary '{binary.Kind}' not supported"),
		};
	}

	static (string column, Expr access) BuildLhs(Expression left, ParamExpr row, SyntaxKind op) => left switch
	{
		NameReference n => (n.SimpleName, BuildColumnAccess(row, n.SimpleName)),
		PathExpression p when IsPropertiesPath(p, out var key) =>
			("Properties." + key, BuildPropertiesAccess(row, key)),
		_ => throw new UnsupportedKqlException(
			$"left side of '{op}' must be a column name or Properties.<key>, got {left.Kind}"),
	};

	static bool IsPropertiesPath(PathExpression path, out string key)
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

	static Expr BuildPropertiesAccess(ParamExpr row, string key)
	{
		var propertiesJson = Expr.Property(row, nameof(LogEntryRecord.PropertiesJson));
		var path = Expr.Constant("$." + key, typeof(string));
		var method = typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.JsonExtract))!;
		return Expr.Call(method, propertiesJson, path);
	}

	static Expr BuildColumnAccess(ParamExpr row, string column) => column switch
	{
		"Id" => Expr.Property(row, nameof(LogEntryRecord.Id)),
		"Level" => Expr.Property(row, nameof(LogEntryRecord.Level)),
		"LevelName" => BuildLevelName(row),
		"Timestamp" => Expr.Property(row, nameof(LogEntryRecord.TimestampMs)),
		"ServiceKey" => Expr.Property(row, nameof(LogEntryRecord.ServiceKey)),
		"Message" => Expr.Property(row, nameof(LogEntryRecord.Message)),
		"MessageTemplate" => Expr.Property(row, nameof(LogEntryRecord.MessageTemplate)),
		"Exception" => Expr.Property(row, nameof(LogEntryRecord.Exception)),
		_ => throw new UnsupportedKqlException($"column '{column}' not supported"),
	};

	static Expr BuildLevelName(ParamExpr row)
	{
		var level = Expr.Property(row, nameof(LogEntryRecord.Level));
		var toName = typeof(KqlTransformer).GetMethod(nameof(ToLevelName),
			System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
		return Expr.Call(toName, level);
	}

	static string ToLevelName(int level) => level switch
	{
		0 => "Verbose",
		1 => "Debug",
		2 => "Information",
		3 => "Warning",
		4 => "Error",
		5 => "Fatal",
		_ => "Unknown",
	};

	static Expr CoerceLiteral(LiteralExpression literal, Type targetType, string column)
	{
		if (column == "Timestamp")
		{
			if (literal.LiteralValue is not DateTime dt)
				throw new UnsupportedKqlException("Timestamp comparison requires a datetime() literal");
			var utc = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
			return Expr.Constant(new DateTimeOffset(utc).ToUnixTimeMilliseconds());
		}

		if (targetType == typeof(long) || targetType == typeof(int))
		{
			if (literal.LiteralValue is long n)
				return targetType == typeof(int) ? Expr.Constant(checked((int)n)) : Expr.Constant(n);
			throw new UnsupportedKqlException($"{column} comparison requires an integer literal");
		}

		if (targetType == typeof(string))
		{
			if (literal.LiteralValue is string s)
				return Expr.Constant(s, typeof(string));
			throw new UnsupportedKqlException($"{column} comparison requires a string literal");
		}

		throw new UnsupportedKqlException($"cannot coerce literal for column '{column}' of type {targetType.Name}");
	}

	static Expr BuildContains(Expr access, string column, LiteralExpression literal, bool caseSensitive)
	{
		if (access.Type != typeof(string))
			throw new UnsupportedKqlException($"contains requires a string column, got '{column}'");
		if (literal.LiteralValue is not string needle)
			throw new UnsupportedKqlException("contains requires a string literal");

		var method = typeof(string).GetMethod(nameof(string.Contains), [typeof(string), typeof(StringComparison)])!;
		var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		return Expr.Call(access, method, Expr.Constant(needle), Expr.Constant(comparison));
	}
}
