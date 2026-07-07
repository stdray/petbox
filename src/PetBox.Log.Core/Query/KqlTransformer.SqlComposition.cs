using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using LinqToDB;
using LinqToDB.Async;
using Kusto.Language.Syntax;
using Expr = System.Linq.Expressions.Expression;
using ParamExpr = System.Linq.Expressions.ParameterExpression;
using LambdaExpr = System.Linq.Expressions.LambdaExpression;
using MemberBinding = System.Linq.Expressions.MemberBinding;

namespace PetBox.Log.Core.Query;

// ========================================================================================
// SINGLE-SQL-PATH MIGRATION (spec: kql-single-path-impl). Post-split ops migrated from the in-memory
// Stream* path to ONE composable SQL query: count, distinct, project, extend, order/sort, take, top.
//
// THE LOGICAL-TYPE ↔ STORAGE-TYPE MAPPING LAYER
//   A pipeline column carries a LOGICAL type (KqlColumn.ClrType — DateTime/TimeSpan/string/long/int/
//   double/bool, what the user/log_query/tests see) distinct from its STORAGE type (the CLR type the SQL
//   expression produces). The record/SQL scalar contexts represent a KQL datetime as epoch-ms `long` and a
//   timespan as ticks `long` (so the tree stays SQL-translatable); everything else has STORAGE == LOGICAL.
//   The ONLY diverging pairs: DateTime↔long(epoch-ms), DateTime?↔long?, TimeSpan↔long(ticks).
//   Storage→logical conversion happens EXACTLY ONCE, at materialization (StorageToLogical), so instant/
//   duration columns come back as DateTime/TimeSpan and pinned ClrTypes are preserved.
//
// COMPOSABLE STATE (SqlStage)
//   Instead of materializing after each op, a migrated op returns a SqlStage: the composable IQueryable
//   (element type = the record type, or a runtime-emitted DynRow after a project/extend/distinct), its
//   LOGICAL column schema, a ScalarContext factory over the element type (EmittedStorageScalarContext for
//   emitted rows — so a DOWNSTREAM stage resolves columns an upstream stage produced), and how to
//   materialize one element to a logical object?[]. Migrated ops CHAIN in SQL (project→order→take = one
//   query); at the first un-migrated op (summarize/mv-expand/parse/join/where-post-split) the stage
//   materializes and the remainder runs on the existing in-memory Stream* path.
//
// LOGICAL TYPING (Option 2)
//   Each project/extend output expression is compiled TWICE: over the record/emitted context (STORAGE,
//   for the SELECT) and over RowScalarContext purely to read its LOGICAL type (.Type). RowScalarContext is
//   the semantic authority (identical to the in-memory path): if it throws, the query errors identically
//   on both pipeline positions; if it accepts, the storage value is mapping-converted to that logical type.
//
// TRANSIENT bridge: if an output expression is not SQL-translatable in the storage context (it THROWS),
//   the WHOLE stage returns null and runs in-memory. As of the datetime-translation wave this fires for
//   ZERO expressions in the test suite (bin(datetime,timespan), the last gap, now translates via epoch-ms
//   bucketing). It remains a safety net whose resolution is always to ADD the SQL translation, never to
//   keep an expression in-memory as the goal.
//
// PROVIDER PARITY (DynRow / ordinal string order)
//   Production is always linq2db+SQLite; unit tests run over in-memory EnumerableQuery. Emitted rows derive
//   from DynRow (structural equality) so EnumerableQuery's .Distinct() dedups BY VALUE like SQL DISTINCT.
//   String ORDER BY passes StringComparer.Ordinal to OrderBy: linq2db ignores it (SQL default collation =
//   BINARY = ordinal-byte), EnumerableQuery honors it — so all three (in-memory Ordinal, SQLite BINARY,
//   EnumerableQuery) agree on ordinal. Numeric/temporal(epoch-ms)/bool keys are collation-free.
// ========================================================================================
public static partial class KqlTransformer
{
	// The composable SQL pipeline state (see header). Migrated ops transform one SqlStage into the next.
	sealed class SqlStage
	{
		public required IQueryable Query { get; init; }
		public required Type ElementType { get; init; }
		public required IReadOnlyList<KqlColumn> Columns { get; init; }      // LOGICAL output schema
		public required Func<ParamExpr, ScalarContext> MakeContext { get; init; } // compile exprs over ElementType (storage domain)
		public required Func<object, object?[]> ToLogicalRow { get; init; }  // one element → logical object?[]
	}

	// Compiles a join/lookup right subquery to a composable SqlStage (null when it isn't fully SQL).
	delegate SqlStage? SqlSubqueryRunner(Expression rightExpr, string opName);

	// The shared compose loop: threads a run of migrated ops through SqlStage transforms. Returns the
	// composed stage, how many ops composed, and a count-terminal if hit. Used by the top-level pipeline
	// AND by a join/lookup right subquery (so a right side composes as a SQL subquery too).
	static (SqlStage Stage, int Composed, KqlResult? Counted) ComposeLoop(
		SqlStage initial, IReadOnlyList<SyntaxNode> postOps, DateTime now, SqlSubqueryRunner sqlRunSub)
	{
		var stage = initial;
		var composed = 0;
		KqlResult? counted = null;
		for (; composed < postOps.Count; composed++)
		{
			if (postOps[composed] is CountOperator)
			{
				counted = SqlCount(stage);
				composed++;
				break;
			}
			// A no-`by` summarize is TERMINAL like count: a bare aggregate that always yields ONE row
			// (Kusto's empty-input one-default-row rule). Returns null → in-memory fallback.
			if (postOps[composed] is SummarizeOperator { ByClause: null } noBy)
			{
				var r = SqlSummarizeNoBy(stage, noBy, now);
				if (r is null)
					break;
				counted = r;
				composed++;
				break;
			}
			var next = postOps[composed] switch
			{
				DistinctOperator distinct => ComposeDistinct(stage, distinct, now),
				ProjectOperator project => ComposeProject(stage, project, now),
				ExtendOperator extend => ComposeExtend(stage, extend, now),
				SummarizeOperator summarize => ComposeSummarize(stage, summarize, now),
				ParseOperator parse => ComposeParse(stage, parse, now),
				JoinOperator join => ComposeJoin(stage, join, now, sqlRunSub),
				LookupOperator lookup => ComposeLookup(stage, lookup, now, sqlRunSub),
				SortOperator sort => ComposeOrder(stage, sort, now),
				TakeOperator take => ComposeTake(stage, take),
				TopOperator top => ComposeTop(stage, top, now),
				_ => null,
			};
			if (next is null)
				break;
			stage = next;
		}
		return (stage, composed, counted);
	}

	// The SQL right-subquery runner: validates the same-log rule (like RunCorrelationSubquery), then
	// composes the whole right pipeline to a SqlStage — null when it is not fully SQL-composable (any op
	// fell back, or a terminal count), so the caller falls the whole join back to the in-memory hash join.
	static SqlStage? TryComposeSubqueryStage<T>(SyntaxNode rightExpr, IQueryable<T> source, DateTime now,
		string opName, RootSpec<T> spec, KqlTranslationOptions options, SqlSubqueryRunner sqlRunSub)
	{
		var ops = FlattenPipeline(rightExpr).ToList();
		if (ops.Count == 0 || ops[0] is not NameReference tbl)
			throw new UnsupportedKqlException($"{opName} right side must be a subquery over '{spec.TableName}'");
		if (!string.Equals(tbl.SimpleName, spec.TableName, StringComparison.Ordinal))
			throw new UnsupportedKqlException(
				$"{opName} right side must be the same log ('{spec.TableName}'); cross-log/table joins are not supported (got '{tbl.SimpleName}')");

		var pipeline = ops.Skip(1).ToList();
		var splitAt = pipeline.FindIndex(IsShapeChangingOp);
		var (preOps, postOps) = splitAt < 0
			? (pipeline, new List<SyntaxNode>())
			: (pipeline.Take(splitAt).ToList(), pipeline.Skip(splitAt).ToList());

		var preResult = ApplyPipeline(source, preOps, spec.MakeContext);
		var (stage, composed, counted) = ComposeLoop(RecordStage(preResult, spec), postOps, now, sqlRunSub);
		if (counted is not null || composed < postOps.Count)
			return null; // right side isn't fully SQL → whole join falls back to in-memory
		return stage;
	}

	// The initial stage: the pre-split linq2db IQueryable of the record type; columns/context/materializer
	// come straight from the root spec (identical to the pre-migration record shape).
	static SqlStage RecordStage<T>(IQueryable<T> query, RootSpec<T> spec) => new()
	{
		Query = query,
		ElementType = typeof(T),
		Columns = spec.Columns,
		MakeContext = spec.MakeContext,
		ToLogicalRow = o => spec.ToRow((T)o),
	};

	// Same stage shape, new (ordered/limited) query — for order/take/top, which do not change the schema.
	static SqlStage WithQuery(SqlStage stage, IQueryable query) => new()
	{
		Query = query,
		ElementType = stage.ElementType,
		Columns = stage.Columns,
		MakeContext = stage.MakeContext,
		ToLogicalRow = stage.ToLogicalRow,
	};

	static KqlResult Materialize(SqlStage stage) =>
		new(stage.Columns, AsAsyncRows(stage.Query, stage.ElementType, stage.ToLogicalRow));

	// ---- count: COUNT(*) over the current stage's query, one SQL round-trip; output identical to
	// StreamCount ({ Count: long }). Reflective over the (possibly emitted) element type. ----
	static KqlResult SqlCount(SqlStage stage) => new([new KqlColumn("Count", typeof(long))], SqlCountRows(stage));

	static async IAsyncEnumerable<object?[]> SqlCountRows(SqlStage stage, [EnumeratorCancellation] CancellationToken ct = default)
	{
		var m = typeof(KqlTransformer).GetMethod(nameof(LongCountGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
			.MakeGenericMethod(stage.ElementType);
		var n = await ((Task<long>)m.Invoke(null, [stage.Query, ct])!).ConfigureAwait(false);
		yield return [n];
	}

	// Fully-qualified linq2db LongCountAsync (binds the IQueryable overload, not the BCL IAsyncEnumerable one).
	static Task<long> LongCountGeneric<TE>(IQueryable<TE> query, CancellationToken ct) => AsyncExtensions.LongCountAsync(query, ct);

	// ---- distinct: SELECT DISTINCT <resolved cols>. Resolves each target over the stage context (record
	// or emitted), so `distinct A` works after a project too. Dedup happens in SQL (the InMemoryBufferCap is
	// moot for a migrated distinct). ----
	static SqlStage ComposeDistinct(SqlStage stage, DistinctOperator distinct, DateTime now)
	{
		var param = Expr.Parameter(stage.ElementType, "e");
		var ctx = stage.MakeContext(param);

		var cols = new List<SqlCol>();
		foreach (var element in distinct.Expressions)
		{
			switch (element.Element)
			{
				case StarExpression:
					if (distinct.Expressions.Count != 1)
						throw new UnsupportedKqlException("distinct *: '*' cannot be combined with other columns");
					foreach (var col in stage.Columns)
						cols.Add(new(col.Name, col.ClrType, ctx.ResolveColumn(col.Name)));
					break;
				case NameReference n:
					{
						var idx = ResolveColumnIndexCI(stage.Columns, n.SimpleName);
						if (idx >= 0)
							cols.Add(new(stage.Columns[idx].Name, stage.Columns[idx].ClrType, ctx.ResolveColumn(n.SimpleName)));
						else
							cols.Add(new(n.SimpleName, typeof(string), ctx.ResolveProperties(n.SimpleName)));
						break;
					}
				case PathExpression p when IsPropertiesPath(p, out var propKey):
					cols.Add(new("Properties." + propKey, typeof(string), ctx.ResolveProperties(propKey)));
					break;
				case ElementExpression el when IsPropertiesIndex(el, out var idxKey):
					cols.Add(new("Properties." + idxKey, typeof(string), ctx.ResolveProperties(idxKey)));
					break;
				default:
					throw new UnsupportedKqlException(
						$"distinct expression '{element.Element.Kind}' not supported (use column refs, Properties.<key>, Properties[\"key\"], or '*')");
			}
		}

		return BuildEmittedStage(stage.Query, stage.ElementType, param, cols, distinct: true, now);
	}

	// ---- project: SELECT <renamed/computed cols>. Dual-compile (storage + logical). Sequential refs
	// resolve through DerivedColumnContext (input columns win; a projected NEW name is referenceable). ----
	static SqlStage? ComposeProject(SqlStage stage, ProjectOperator project, DateTime now)
	{
		var param = Expr.Parameter(stage.ElementType, "e");
		var baseCtx = stage.MakeContext(param);
		var inputNames = new HashSet<string>(stage.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
		var defined = new Dictionary<string, Expr>(StringComparer.OrdinalIgnoreCase);
		var storageCtx = new DerivedColumnContext(baseCtx,
			name => inputNames.Contains(name) ? null : defined.GetValueOrDefault(name)) { UtcNow = now };

		var rowParam = Expr.Parameter(typeof(object?[]), "row");
		var working = stage.Columns.ToList();

		var cols = new List<SqlCol>();
		foreach (var element in project.Expressions)
		{
			var (name, exprSyntax) = element.Element switch
			{
				NameReference n => (n.SimpleName, (Expression)n),
				SimpleNamedExpression { Name: NameDeclaration alias, Expression: var e } => (alias.Name.SimpleName, e),
				_ => throw new UnsupportedKqlException(
					$"project expression '{element.Element.Kind}' not supported (use a column ref or 'name = expression')"),
			};
			if (!TryCompileStorage(exprSyntax, storageCtx, out var storage))
				return null; // transient: stage runs in-memory until the expr is SQL-translated
			var logicalType = KqlScalar.Compile(exprSyntax, new RowScalarContext(rowParam, working) { UtcNow = now }).Type;
			working.Add(new KqlColumn(name, logicalType));
			if (!inputNames.Contains(name))
				defined[name] = storage;
			cols.Add(new(name, logicalType, storage));
		}

		return BuildEmittedStage(stage.Query, stage.ElementType, param, cols, distinct: false, now);
	}

	// ---- extend: passes ALL input columns through and appends/replaces the computed ones. An extended
	// name (incl. a replaced column) shadows the base for a later same-stage ref. ----
	static SqlStage? ComposeExtend(SqlStage stage, ExtendOperator extend, DateTime now)
	{
		var param = Expr.Parameter(stage.ElementType, "e");
		var baseCtx = stage.MakeContext(param);
		var defined = new Dictionary<string, Expr>(StringComparer.Ordinal);
		var storageCtx = new DerivedColumnContext(baseCtx, name => defined.GetValueOrDefault(name)) { UtcNow = now };

		var rowParam = Expr.Parameter(typeof(object?[]), "row");
		var columns = stage.Columns.ToList();

		foreach (var element in extend.Expressions)
		{
			if (element.Element is not SimpleNamedExpression { Name: NameDeclaration alias, Expression: var exprSyntax })
				throw new UnsupportedKqlException(
					$"extend expression '{element.Element.Kind}' not supported (use 'name = expression')");
			if (!TryCompileStorage(exprSyntax, storageCtx, out var storage))
				return null; // transient in-memory until SQL-translated
			var name = alias.Name.SimpleName;
			var logicalType = KqlScalar.Compile(exprSyntax, new RowScalarContext(rowParam, columns) { UtcNow = now }).Type;
			var existing = FindColumnIndex(columns, name);
			if (existing >= 0)
				columns[existing] = new KqlColumn(name, logicalType);
			else
				columns.Add(new KqlColumn(name, logicalType));
			defined[name] = storage;
		}

		var cols = columns
			.Select(col => new SqlCol(col.Name, col.ClrType, defined.GetValueOrDefault(col.Name) ?? baseCtx.ResolveColumn(col.Name)))
			.ToList();
		return BuildEmittedStage(stage.Query, stage.ElementType, param, cols, distinct: false, now);
	}

	// ---- order by / sort: ORDER BY over the stage. Keys compile over the stage context (storage domain);
	// a computed key that is not yet SQL-translatable returns null (transient in-memory). String keys use
	// the ordinal comparer so all three providers agree (see header). Shape is unchanged. ----
	static SqlStage? ComposeOrder(SqlStage stage, SortOperator sort, DateTime now)
	{
		var param = Expr.Parameter(stage.ElementType, "e");
		var ctx = stage.MakeContext(param);

		var keys = new List<(Expr Key, bool Desc)>();
		foreach (var element in sort.Expressions)
		{
			var (exprSyntax, desc) = SortKey(element.Element);
			if (!TryCompileStorage(exprSyntax, ctx, out var key))
				return null;
			keys.Add((key, desc));
		}

		var ordered = stage.Query;
		for (var i = 0; i < keys.Count; i++)
			ordered = DynOrderBy(ordered, stage.ElementType, Expr.Lambda(keys[i].Key, param), keys[i].Key.Type, keys[i].Desc, isThen: i > 0);
		return WithQuery(stage, ordered);
	}

	// ---- take / limit: pure truncation. ----
	static SqlStage ComposeTake(SqlStage stage, TakeOperator take)
	{
		if (take.Expression is not LiteralExpression { LiteralValue: long n })
			throw new UnsupportedKqlException("take requires an integer literal");
		return WithQuery(stage, DynTake(stage.Query, stage.ElementType, checked((int)n)));
	}

	// ---- top N by: ORDER BY <key> + LIMIT N. Same collation/transient rules as order by. ----
	static SqlStage? ComposeTop(SqlStage stage, TopOperator top, DateTime now)
	{
		if (top.Expression is not LiteralExpression { LiteralValue: long n })
			throw new UnsupportedKqlException("top requires an integer literal count");
		var param = Expr.Parameter(stage.ElementType, "e");
		var ctx = stage.MakeContext(param);
		var (exprSyntax, desc) = SortKey(top.ByExpression);
		if (!TryCompileStorage(exprSyntax, ctx, out var key))
			return null;
		var ordered = DynOrderBy(stage.Query, stage.ElementType, Expr.Lambda(key, param), key.Type, desc, isThen: false);
		return WithQuery(stage, DynTake(ordered, stage.ElementType, checked((int)n)));
	}

	// A sort element → (key expression, descending). Kusto default (no asc/desc) is descending.
	static (Expression Expr, bool Desc) SortKey(SyntaxNode element) => element switch
	{
		OrderedExpression { Expression: var e, Ordering: var o } => (e, !IsAscending(o)),
		_ => ((Expression)element, true),
	};

	// ---- parse (star-free literal form): reproduces the in-memory MatchParse position logic in SQL via
	// substr/instr. Adds/replaces the capture columns (string), keeping every row (non-match → all captures
	// null, row retained). Promise-neutral (NO regex — literal matching only, byte-identical to MatchParse
	// for BMP text; astral chars are the one residual, see BuildParseCaptures). Composes on SqlStage. A
	// non-SQL-translatable source returns null (transient in-memory); pattern-shape errors (typed/regex/
	// adjacent captures) throw exactly like ApplyParse. ----
	static SqlStage? ComposeParse(SqlStage stage, ParseOperator op, DateTime now)
	{
		if (op.Parameters.Count > 0)
			throw new UnsupportedKqlException(
				"parse: only the default simple star-free form is supported (kind=regex / kind=relaxed not supported)");

		var param = Expr.Parameter(stage.ElementType, "e");
		var ctx = stage.MakeContext(param);
		if (!TryCompileStorage(op.Expression, ctx, out var srcExpr))
			return null;
		if (srcExpr.Type != typeof(string))
			throw new UnsupportedKqlException($"parse source must be a string column, got {srcExpr.Type.Name}");

		var segments = ParsePatternSegments(op.Patterns);
		var captures = BuildParseCaptures(srcExpr, segments);

		// Output = all input columns (passthrough) + the capture columns (string), replace-in-place on a
		// name collision — identical to ApplyParse's column shaping.
		var columns = stage.Columns.ToList();
		var captureByName = new Dictionary<string, Expr>(StringComparer.Ordinal);
		foreach (var (name, value) in captures)
		{
			var existing = FindColumnIndex(columns, name);
			if (existing >= 0)
				columns[existing] = new KqlColumn(name, typeof(string));
			else
				columns.Add(new KqlColumn(name, typeof(string)));
			captureByName[name] = value;
		}

		var cols = columns
			.Select(col => new SqlCol(col.Name, col.ClrType, captureByName.GetValueOrDefault(col.Name) ?? ctx.ResolveColumn(col.Name)))
			.ToList();
		return BuildEmittedStage(stage.Query, stage.ElementType, param, cols, distinct: false, now);
	}

	// Reproduces MatchParse's exact position walk as SQL expressions: a running 0-based `pos` and a `failed`
	// flag are threaded through the segments (literal must match at pos via substr(=); a capture runs up to
	// the next literal via instr; a trailing capture takes the rest). Every capture is gated by the FINAL
	// (src != null AND NOT failed) — so any mismatch nulls ALL captures while the row is kept, matching
	// MatchParse. BMP-byte-identical; the one residual is astral (SQLite instr/substr count code POINTS,
	// .NET IndexOf/Substring count UTF-16 code UNITS) — see the report.
	static List<(string Name, Expr Value)> BuildParseCaptures(Expr src, IReadOnlyList<(bool IsCapture, string Value)> segments)
	{
		var captures = new List<(string, Expr)>();
		Expr pos = Expr.Constant(0L);
		Expr failed = Expr.Constant(false);

		var i = 0;
		while (i < segments.Count)
		{
			if (!segments[i].IsCapture)
			{
				var lit = segments[i].Value;
				var here = SubstrLen(src, pos, Expr.Constant((long)lit.Length));
				failed = Expr.OrElse(failed, Expr.Not(Expr.Equal(here, Expr.Constant(lit, typeof(string)))));
				pos = Expr.Add(pos, Expr.Constant((long)lit.Length));
				i += 1;
			}
			else if (i + 1 < segments.Count)
			{
				var name = segments[i].Value;
				var lnext = segments[i + 1].Value;
				if (lnext.Length == 0)
				{
					captures.Add((name, Expr.Constant("", typeof(string)))); // pos unchanged; empty delimiter
				}
				else
				{
					var rel = InstrCall(SubstrRest(src, pos), Expr.Constant(lnext, typeof(string))); // 1-based or 0
					var relMinus1 = Expr.Subtract(rel, Expr.Constant(1L));
					captures.Add((name, SubstrLen(src, pos, relMinus1))); // text before lnext
					failed = Expr.OrElse(failed, Expr.Equal(rel, Expr.Constant(0L))); // not found → fail
					pos = Expr.Add(Expr.Add(pos, relMinus1), Expr.Constant((long)lnext.Length));
				}
				i += 2;
			}
			else
			{
				captures.Add((segments[i].Value, SubstrRest(src, pos))); // trailing capture takes the rest
				i += 1;
			}
		}

		var matched = Expr.AndAlso(Expr.NotEqual(src, Expr.Constant(null, typeof(string))), Expr.Not(failed));
		return captures
			.Select(c => (c.Item1, (Expr)Expr.Condition(matched, c.Item2, Expr.Constant(null, typeof(string)))))
			.ToList();
	}

	static readonly MethodInfo Substr3 = typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.Substring3))!;
	static readonly MethodInfo Substr2 = typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.Substring2))!;
	static readonly MethodInfo InstrM = typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.Instr))!;
	static Expr SubstrLen(Expr s, Expr start, Expr len) => Expr.Call(Substr3, s, start, len);
	static Expr SubstrRest(Expr s, Expr start) => Expr.Call(Substr2, s, start);
	static Expr InstrCall(Expr s, Expr needle) => Expr.Call(InstrM, s, needle);

	// ---- join (SQL): reproduces ApplyJoin/StreamJoinRows semantics as a SQL join on the composable
	// SqlStage. Migrates kind=inner (Queryable.Join) and kind=leftouter (GroupJoin+SelectMany+DefaultIfEmpty);
	// innerunique (window-dedup) is not yet migrated → return null (in-memory fallback). The right side is
	// compiled to a SqlStage via sqlRunSub; if it isn't fully SQL, the whole join falls back. Null join keys
	// never match (KQL): inner pre-filters BOTH sides' null keys (also aligns EnumerableQuery with SQL, which
	// would otherwise match null==null); leftouter filters ONLY the right (left null-key rows survive via
	// DefaultIfEmpty, never matching a filtered right). ----
	static SqlStage? ComposeJoin(SqlStage left, JoinOperator op, DateTime now, SqlSubqueryRunner sqlRunSub)
	{
		var kind = ParseJoinKind(op.Parameters, "join");
		if (op.ConditionClause is not JoinOnClause onClause)
			throw new UnsupportedKqlException("join requires an 'on' clause with equality key(s)");
		var right = sqlRunSub(op.Expression, "join");
		if (right is null)
			return null;
		return kind switch
		{
			// innerunique (Kusto DEFAULT): dedup the LEFT by key first, then inner join.
			JoinKind.InnerUnique => BuildInnerJoin(left, right, onClause, "join", now, dedupLeft: true),
			JoinKind.Inner => BuildInnerJoin(left, right, onClause, "join", now, dedupLeft: false),
			JoinKind.LeftOuter => BuildLeftOuterJoin(left, right, onClause, "join", excludeRightKeys: false, now),
			_ => null,
		};
	}

	// lookup = leftouter enrichment with the right KEY columns dropped from the output (they equal the left
	// keys). Reuses the leftouter machinery with excludeRightKeys=true.
	static SqlStage? ComposeLookup(SqlStage left, LookupOperator op, DateTime now, SqlSubqueryRunner sqlRunSub)
	{
		if (op.Parameters.Count > 0)
			throw new UnsupportedKqlException("lookup parameters not supported (lookup is always a leftouter enrichment)");
		if (op.LookupClause is not JoinOnClause onClause)
			throw new UnsupportedKqlException("lookup requires an 'on' clause with equality key(s)");
		var right = sqlRunSub(op.Expression, "lookup");
		if (right is null)
			return null;
		return BuildLeftOuterJoin(left, right, onClause, "lookup", excludeRightKeys: true, now);
	}

	// Resolves the on-clause into per-key (left expr over lParam, right expr over rKeyParam, right column
	// index). Same forms as ResolveJoinKeys: `on Col` and `on $left.A == $right.B`. Numeric keys widen to a
	// common type (NormalizeKeyValue); an incompatible cross-kind pair → null (fall back rather than mis-join).
	static List<(Expr L, Expr R, int RightCol)>? ResolveJoinKeysSql(
		JoinOnClause onClause, SqlStage left, ScalarContext lctx, SqlStage right, ScalarContext rctx, string opName)
	{
		var keys = new List<(Expr, Expr, int)>();
		foreach (var element in onClause.Expressions)
		{
			string leftName, rightName;
			switch (element.Element)
			{
				case NameReference n:
					leftName = rightName = n.SimpleName;
					break;
				case BinaryExpression { Kind: SyntaxKind.EqualExpression, Left: var lhs, Right: var rhs }:
					var a = DollarRef(lhs);
					var b = DollarRef(rhs);
					if (a is null || b is null || a.Value.IsLeft == b.Value.IsLeft)
						throw new UnsupportedKqlException(
							$"{opName} on: an equality must be '$left.col == $right.col' (got an unsupported on-clause)");
					(leftName, rightName) = a.Value.IsLeft ? (a.Value.Column, b.Value.Column) : (b.Value.Column, a.Value.Column);
					break;
				default:
					throw new UnsupportedKqlException(
						$"{opName} on: only equality on column names is supported (col or $left.col == $right.col), got '{element.Element.Kind}'");
			}
			var lk = lctx.ResolveColumn(leftName);
			var rk = rctx.ResolveColumn(rightName);
			var common = UnifyKeyType(lk.Type, rk.Type);
			if (common is null)
				return null;
			keys.Add((Coerce(lk, common), Coerce(rk, common), ResolveColumnIndexCI(right.Columns, rightName)));
		}
		if (keys.Count == 0)
			throw new UnsupportedKqlException($"{opName} requires at least one 'on' key");
		return keys;
	}

	// inner (dedupLeft=false) or innerunique (dedupLeft=true — dedup the LEFT by key, keeping the first row
	// per key, via GroupBy(key).Select(g => First()); linq2db translates that to a ROW_NUMBER window dedup
	// and EnumerableQuery keeps the first-in-input-order row, so both providers agree, matching today's
	// StreamJoinRows innerunique).
	static SqlStage? BuildInnerJoin(SqlStage left, SqlStage right, JoinOnClause onClause, string opName, DateTime now, bool dedupLeft)
	{
		var lParam = Expr.Parameter(left.ElementType, "l");
		var lctx = left.MakeContext(lParam);
		var rParam = Expr.Parameter(right.ElementType, "r");
		var rctx = right.MakeContext(rParam);

		var keys = ResolveJoinKeysSql(onClause, left, lctx, right, rctx, opName);
		if (keys is null)
			return null;

		// Pre-filter null keys on BOTH sides (a null key never matches; also aligns EnumerableQuery with SQL).
		var leftQ = FilterNonNullKeys(left.Query, left.ElementType, lParam, keys.Select(k => k.L).ToList());
		var rightQ = FilterNonNullKeys(right.Query, right.ElementType, rParam, keys.Select(k => k.R).ToList());

		var keyType = EmitRowType(keys.Select((k, i) => ($"K{i}", k.L.Type)).ToList());
		var leftKey = Expr.Lambda(KeyInit(keyType, keys.Select(k => k.L)), lParam);
		var rightKey = Expr.Lambda(KeyInit(keyType, keys.Select(k => k.R)), rParam);

		if (dedupLeft)
			leftQ = DedupByKey(leftQ, left.ElementType, keyType, leftKey);

		var outCols = BuildJoinColumns(left, lctx, lParam, right, rctx, rParam, new HashSet<int>(), rightNullable: false);
		var resultType = EmitRowType(outCols.Select((c, i) => ($"C{i}", c.Storage.Type)).ToList());
		var resultSel = Expr.Lambda(
			Expr.MemberInit(Expr.New(resultType),
				outCols.Select((c, i) => (MemberBinding)Expr.Bind(resultType.GetProperty($"C{i}")!, c.Storage)).ToArray()),
			lParam, rParam);

		var joined = DynJoin(leftQ, rightQ, left.ElementType, right.ElementType, keyType, resultType, leftKey, rightKey, resultSel);
		return MakeEmittedStage(joined, resultType, outCols.Select(c => (c.Name, c.LogicalType)).ToList(), now);
	}

	// leftouter via GroupJoin + SelectMany + DefaultIfEmpty (probe C). Left columns are flattened into an
	// emitted Holder (so the final projection reads them from h, not the join-time left param); the right
	// element `r` is null for an unmatched left (DefaultIfEmpty), so right columns are null-guarded
	// (`r != null ? storage : null`) and widened to nullable. Only RIGHT null keys are filtered.
	static SqlStage? BuildLeftOuterJoin(SqlStage left, SqlStage right, JoinOnClause onClause, string opName, bool excludeRightKeys, DateTime now)
	{
		var lParam = Expr.Parameter(left.ElementType, "l");
		var lctx = left.MakeContext(lParam);
		var rKeyParam = Expr.Parameter(right.ElementType, "rk");
		var rKeyCtx = right.MakeContext(rKeyParam);

		var keys = ResolveJoinKeysSql(onClause, left, lctx, right, rKeyCtx, opName);
		if (keys is null)
			return null;

		var rightQ = FilterNonNullKeys(right.Query, right.ElementType, rKeyParam, keys.Select(k => k.R).ToList());

		var keyType = EmitRowType(keys.Select((k, i) => ($"K{i}", k.L.Type)).ToList());
		var leftKey = Expr.Lambda(KeyInit(keyType, keys.Select(k => k.L)), lParam);
		var rightKey = Expr.Lambda(KeyInit(keyType, keys.Select(k => k.R)), rKeyParam);

		// GroupJoin: (l, g) => new Holder { C0..=left storages, G=g }.
		var leftStorages = left.Columns.Select(c => lctx.ResolveColumn(c.Name)).ToList();
		var enumRight = typeof(IEnumerable<>).MakeGenericType(right.ElementType);
		var holderType = EmitRowType(
			leftStorages.Select((s, i) => ($"C{i}", s.Type)).Append(("G", enumRight)).ToList());
		var gParam = Expr.Parameter(enumRight, "g");
		var holderInit = Expr.MemberInit(Expr.New(holderType),
			leftStorages.Select((s, i) => (MemberBinding)Expr.Bind(holderType.GetProperty($"C{i}")!, s))
				.Append((MemberBinding)Expr.Bind(holderType.GetProperty("G")!, gParam)).ToArray());
		var grouped = DynGroupJoin(left.Query, rightQ, left.ElementType, right.ElementType, keyType, holderType,
			leftKey, rightKey, Expr.Lambda(holderInit, lParam, gParam));

		// SelectMany: h => h.G.DefaultIfEmpty(), (h, r) => new Result { left from h.Ci, right null-guarded }.
		var hParam = Expr.Parameter(holderType, "h");
		var collectionSel = Expr.Lambda(Expr.Call(EnumM("DefaultIfEmpty", 1, right.ElementType), Expr.Property(hParam, "G")), hParam);

		var hParam2 = Expr.Parameter(holderType, "h");
		var rParam = Expr.Parameter(right.ElementType, "r");
		var rCtx = right.MakeContext(rParam);
		var rNotNull = Expr.NotEqual(rParam, Expr.Constant(null, right.ElementType));

		var outCols = new List<(string Name, Type Logical, Expr Storage)>();
		var used = new HashSet<string>(StringComparer.Ordinal);
		for (var i = 0; i < left.Columns.Count; i++)
		{
			used.Add(left.Columns[i].Name);
			outCols.Add((left.Columns[i].Name, left.Columns[i].ClrType, Expr.Property(hParam2, $"C{i}")));
		}
		var excluded = excludeRightKeys ? keys.Select(k => k.RightCol).Where(x => x >= 0).ToHashSet() : [];
		for (var j = 0; j < right.Columns.Count; j++)
		{
			if (excluded.Contains(j))
				continue;
			var baseName = right.Columns[j].Name;
			var name = baseName;
			var n = 1;
			while (!used.Add(name))
				name = baseName + n++;
			var storage = rCtx.ResolveColumn(right.Columns[j].Name);
			var nt = NullableType(storage.Type);
			var guarded = Expr.Condition(rNotNull, Coerce(storage, nt), Expr.Constant(null, nt));
			outCols.Add((name, NullableType(right.Columns[j].ClrType), guarded));
		}

		var resultType = EmitRowType(outCols.Select((c, i) => ($"C{i}", c.Storage.Type)).ToList());
		var resultSel = Expr.Lambda(
			Expr.MemberInit(Expr.New(resultType),
				outCols.Select((c, i) => (MemberBinding)Expr.Bind(resultType.GetProperty($"C{i}")!, c.Storage)).ToArray()),
			hParam2, rParam);
		var joined = DynSelectMany(grouped, holderType, right.ElementType, resultType, collectionSel, resultSel);
		return MakeEmittedStage(joined, resultType, outCols.Select(c => (c.Name, c.Logical)).ToList(), now);
	}

	// left columns verbatim, then right columns (minus excluded), renamed <name>N on a collision with an
	// already-present name — mirrors BuildJoinColumns. Storage exprs are over lParam (left) / rParam (right).
	static List<SqlCol> BuildJoinColumns(SqlStage left, ScalarContext lctx, ParamExpr lParam, SqlStage right, ScalarContext rctx, ParamExpr rParam, ISet<int> excludeRight, bool rightNullable)
	{
		var cols = new List<SqlCol>();
		var used = new HashSet<string>(StringComparer.Ordinal);
		foreach (var c in left.Columns)
		{
			used.Add(c.Name);
			cols.Add(new SqlCol(c.Name, c.ClrType, lctx.ResolveColumn(c.Name)));
		}
		for (var i = 0; i < right.Columns.Count; i++)
		{
			if (excludeRight.Contains(i))
				continue;
			var baseName = right.Columns[i].Name;
			var name = baseName;
			var n = 1;
			while (!used.Add(name))
				name = baseName + n++;
			var logical = rightNullable ? NullableType(right.Columns[i].ClrType) : right.Columns[i].ClrType;
			var storage = rctx.ResolveColumn(right.Columns[i].Name);
			if (rightNullable)
				storage = Expr.Convert(storage, NullableType(storage.Type));
			cols.Add(new SqlCol(name, logical, storage));
		}
		return cols;
	}

	// The nullable form of a value type (int→int?), passing reference/already-nullable types through.
	static Type NullableType(Type t) =>
		t.IsValueType && !KqlScalar.IsNullable(t) ? typeof(Nullable<>).MakeGenericType(t) : t;

	// A common CLR type for a left/right key pair, or null when incompatible (cross-kind → never matches).
	// Same type passes through; numerics widen (integral→long, real→double), matching NormalizeKeyValue.
	static Type? UnifyKeyType(Type l, Type r)
	{
		if (l == r)
			return l;
		var nl = KqlScalar.NonNullable(l);
		var nr = KqlScalar.NonNullable(r);
		if (KqlScalar.IsNumericType(nl) && KqlScalar.IsNumericType(nr))
			return nl == typeof(double) || nl == typeof(float) || nr == typeof(double) || nr == typeof(float)
				? typeof(double)
				: typeof(long);
		return null;
	}

	static Expr Coerce(Expr e, Type t) => e.Type == t ? e : Expr.Convert(e, t);

	static Expr KeyInit(Type keyType, IEnumerable<Expr> parts) =>
		Expr.MemberInit(Expr.New(keyType),
			parts.Select((p, i) => (MemberBinding)Expr.Bind(keyType.GetProperty($"K{i}")!, p)).ToArray());

	// Filters a side to rows whose (nullable) key components are all non-null (KQL: null keys never match).
	static IQueryable FilterNonNullKeys(IQueryable query, Type elemType, ParamExpr param, IReadOnlyList<Expr> keyExprs)
	{
		Expr? pred = null;
		foreach (var k in keyExprs)
		{
			if (k.Type.IsValueType && !KqlScalar.IsNullable(k.Type))
				continue; // a non-nullable value type is never null
			var notNull = Expr.NotEqual(k, Expr.Constant(null, k.Type));
			pred = pred is null ? notNull : Expr.AndAlso(pred, notNull);
		}
		return pred is null ? query : DynWhere(query, elemType, Expr.Lambda(pred, param));
	}

	static IQueryable DynWhere(IQueryable source, Type elementType, LambdaExpr predicate)
	{
		var method = typeof(Queryable).GetMethods()
			.Single(m => m.Name == "Where" && m.GetParameters().Length == 2
				&& m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2)
			.MakeGenericMethod(elementType);
		return source.Provider.CreateQuery(Expr.Call(method, source.Expression, Expr.Quote(predicate)));
	}

	static IQueryable DynJoin(IQueryable outer, IQueryable inner, Type outerT, Type innerT, Type keyT, Type resultT,
		LambdaExpr outerKey, LambdaExpr innerKey, LambdaExpr resultSel)
	{
		var method = typeof(Queryable).GetMethods()
			.Single(m => m.Name == "Join" && m.GetParameters().Length == 5 && m.GetGenericArguments().Length == 4)
			.MakeGenericMethod(outerT, innerT, keyT, resultT);
		return outer.Provider.CreateQuery(Expr.Call(method,
			outer.Expression, Expr.Constant(inner), Expr.Quote(outerKey), Expr.Quote(innerKey), Expr.Quote(resultSel)));
	}

	// innerunique left dedup: GroupBy(key).Select(g => g.First()) — one row per key (first in input order).
	static IQueryable DedupByKey(IQueryable query, Type elementType, Type keyType, LambdaExpr keySelector)
	{
		var grouped = DynGroupBy(query, elementType, keyType, keySelector);
		var groupingType = typeof(IGrouping<,>).MakeGenericType(keyType, elementType);
		var g = Expr.Parameter(groupingType, "g");
		var first = Expr.Lambda(Expr.Call(EnumM("First", 1, elementType), g), g);
		return DynSelect(grouped, groupingType, elementType, first);
	}

	static IQueryable DynGroupJoin(IQueryable outer, IQueryable inner, Type outerT, Type innerT, Type keyT, Type resultT,
		LambdaExpr outerKey, LambdaExpr innerKey, LambdaExpr resultSel)
	{
		var method = typeof(Queryable).GetMethods()
			.Single(m => m.Name == "GroupJoin" && m.GetParameters().Length == 5 && m.GetGenericArguments().Length == 4)
			.MakeGenericMethod(outerT, innerT, keyT, resultT);
		return outer.Provider.CreateQuery(Expr.Call(method,
			outer.Expression, Expr.Constant(inner), Expr.Quote(outerKey), Expr.Quote(innerKey), Expr.Quote(resultSel)));
	}

	// SelectMany(source, collectionSelector: Func<TSource, IEnumerable<TColl>>, resultSelector: Func<TSource, TColl, TResult>).
	static IQueryable DynSelectMany(IQueryable source, Type sourceT, Type collT, Type resultT, LambdaExpr collectionSel, LambdaExpr resultSel)
	{
		var method = typeof(Queryable).GetMethods()
			.Single(m => m.Name == "SelectMany" && m.GetParameters().Length == 3 && m.GetGenericArguments().Length == 3
				&& m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2)
			.MakeGenericMethod(sourceT, collT, resultT);
		return source.Provider.CreateQuery(Expr.Call(method, source.Expression, Expr.Quote(collectionSel), Expr.Quote(resultSel)));
	}

	// One output column of a SQL-composed stage: logical name/type + the SQL STORAGE expression.
	readonly record struct SqlCol(string Name, Type LogicalType, Expr Storage);

	// record-context compile guarded for the transient migration bridge and computed-key coverage: an
	// expression not yet SQL-translatable throws UnsupportedKqlException here → the caller runs in-memory
	// FOR NOW. Any other exception propagates (a real fault, not a coverage gap).
	static bool TryCompileStorage(Expression exprSyntax, ScalarContext ctx, out Expr storage)
	{
		try
		{
			storage = KqlScalar.Compile(exprSyntax, ctx);
			return true;
		}
		catch (UnsupportedKqlException)
		{
			storage = null!;
			return false;
		}
	}

	// Emits a storage-typed DynRow, projects (optionally DISTINCT) into it, and wraps the result as a
	// composable emitted SqlStage (schema/context/materializer derived from the columns).
	static SqlStage BuildEmittedStage(IQueryable source, Type inType, ParamExpr param, IReadOnlyList<SqlCol> cols, bool distinct, DateTime now)
	{
		var fields = cols.Select((c, i) => ($"C{i}", c.Storage.Type)).ToList();
		var rowType = EmitRowType(fields);
		var bindings = cols
			.Select((c, i) => (MemberBinding)Expr.Bind(rowType.GetProperty($"C{i}")!, c.Storage))
			.ToArray();
		var projector = Expr.Lambda(Expr.MemberInit(Expr.New(rowType), bindings), param);

		var q = DynSelect(source, inType, rowType, projector);
		if (distinct)
			q = DynDistinct(q, rowType);

		return MakeEmittedStage(q, rowType, cols.Select(c => (c.Name, c.LogicalType)).ToList(), now);
	}

	// Wraps an already-projected (element type = `rowType`, an emitted DynRow with props C0..Cn) IQueryable
	// as a composable SqlStage: builds the schema/context/materializer from the logical columns. `rowType`'s
	// C{i} property holds the STORAGE value; the i-th logical column maps it back at materialization.
	static SqlStage MakeEmittedStage(IQueryable query, Type rowType, IReadOnlyList<(string Name, Type Logical)> cols, DateTime now)
	{
		var schema = cols.Select((c, i) => new EmittedCol(c.Name, c.Logical, $"C{i}")).ToList();
		var props = cols.Select((c, i) => rowType.GetProperty($"C{i}")!).ToArray();
		var converters = cols.Select(c => StorageToLogical(c.Logical)).ToArray();
		var columns = cols.Select(c => new KqlColumn(c.Name, c.Logical)).ToList();

		object?[] ToRow(object o)
		{
			var arr = new object?[props.Length];
			for (var i = 0; i < props.Length; i++)
				arr[i] = converters[i](props[i].GetValue(o));
			return arr;
		}

		return new SqlStage
		{
			Query = query,
			ElementType = rowType,
			Columns = columns,
			MakeContext = p => new EmittedStorageScalarContext(p, schema) { UtcNow = now },
			ToLogicalRow = ToRow,
		};
	}

	// ---- summarize (WITH `by`): GROUP BY the resolved keys, project keys + aggregates. The DB groups
	// (relieving the in-memory group-table / dcount-set caps), and the result composes further in SQL.
	// no-`by` summarize (bounded to one row incl. the Kusto empty-input default-row rule) stays in-memory
	// for now — returned as null. Any key/aggregate not yet SQL-translatable (or a deferred case: string
	// min/max collation) also returns null → the whole summarize runs in-memory unchanged. ----
	static SqlStage? ComposeSummarize(SqlStage stage, SummarizeOperator op, DateTime now)
	{
		if (op.ByClause is null)
			return null;

		var x = Expr.Parameter(stage.ElementType, "x");
		var ctx = stage.MakeContext(x);
		var rowParam = Expr.Parameter(typeof(object?[]), "row");

		// by-keys → (name, logical type, storage expr over x)
		var keys = new List<SqlCol>();
		foreach (var element in op.ByClause.Expressions)
		{
			if (!TrySummarizeKey(element.Element, stage, ctx, x, rowParam, now, out var key))
				return null;
			keys.Add(key);
		}

		// aggregates → (name, result logical type, builder producing the aggregate expr over the grouping)
		var aggs = new List<(string Name, Type Logical, Func<ParamExpr, Expr> Build)>();
		foreach (var element in op.Aggregates)
		{
			if (!TrySummarizeAgg(element.Element, ctx, x, rowParam, stage, now, out var agg))
				return null;
			aggs.Add(agg);
		}

		// GROUP BY: source.GroupBy(x => new KeyType { K0 = key0(x), ... }). KeyType derives from DynRow so
		// EnumerableQuery groups by VALUE (matching SQL GROUP BY) — same reason distinct needs it.
		var keyType = EmitRowType(keys.Select((k, i) => ($"K{i}", k.Storage.Type)).ToList());
		var keyInit = Expr.MemberInit(Expr.New(keyType),
			keys.Select((k, i) => (MemberBinding)Expr.Bind(keyType.GetProperty($"K{i}")!, k.Storage)).ToArray());
		var grouped = DynGroupBy(stage.Query, stage.ElementType, keyType, Expr.Lambda(keyInit, x));

		// SELECT g => new ResultType { C0 = g.Key.K0, ..., <aggregates> }. Result cols = keys then aggregates.
		var groupingType = typeof(IGrouping<,>).MakeGenericType(keyType, stage.ElementType);
		var g = Expr.Parameter(groupingType, "g");
		var keyAccess = Expr.Property(g, "Key");

		var outCols = new List<(string Name, Type Logical, Expr Bind)>();
		for (var i = 0; i < keys.Count; i++)
			outCols.Add((keys[i].Name, keys[i].LogicalType, Expr.Property(keyAccess, $"K{i}")));
		foreach (var agg in aggs)
			outCols.Add((agg.Name, agg.Logical, agg.Build(g)));

		var resultType = EmitRowType(outCols.Select((c, i) => ($"C{i}", c.Bind.Type)).ToList());
		var resultInit = Expr.MemberInit(Expr.New(resultType),
			outCols.Select((c, i) => (MemberBinding)Expr.Bind(resultType.GetProperty($"C{i}")!, c.Bind)).ToArray());
		var projected = DynSelect(grouped, groupingType, resultType, Expr.Lambda(resultInit, g));

		return MakeEmittedStage(projected, resultType, outCols.Select(c => (c.Name, c.Logical)).ToList(), now);
	}

	// no-`by` summarize: a BARE aggregate. GroupBy(constant) yields one row over non-empty input and ZERO
	// over empty; Kusto requires ONE default row over empty (count()→0, sum/avg/min/max→null, dcount/countif
	// →0), so a 0-row result synthesizes the per-aggregate defaults. Terminal (returns a KqlResult, not a
	// composable stage). Any aggregate not SQL-translatable → null (in-memory fallback).
	static KqlResult? SqlSummarizeNoBy(SqlStage stage, SummarizeOperator op, DateTime now)
	{
		var x = Expr.Parameter(stage.ElementType, "x");
		var ctx = stage.MakeContext(x);
		var rowParam = Expr.Parameter(typeof(object?[]), "row");

		var aggs = new List<(string Name, Type Logical, Func<ParamExpr, Expr> Build, object? Default)>();
		foreach (var element in op.Aggregates)
		{
			if (!TrySummarizeAgg(element.Element, ctx, x, rowParam, stage, now, out var agg))
				return null;
			// Empty-group default: a non-nullable value-type result (count/countif/dcount → long) defaults to
			// its zero (0); a nullable/ref result (sum/avg/min/max) defaults to null. Matches StreamSummarize's
			// fresh-accumulator fold.
			var def = KqlScalar.IsNullable(agg.Logical) || !agg.Logical.IsValueType
				? null
				: Activator.CreateInstance(agg.Logical);
			aggs.Add((agg.Name, agg.Logical, agg.Build, def));
		}

		// GroupBy(x => 1) → a single constant group. Select the aggregates over it.
		var grouped = DynGroupBy(stage.Query, stage.ElementType, typeof(int), Expr.Lambda(Expr.Constant(1), x));
		var groupingType = typeof(IGrouping<,>).MakeGenericType(typeof(int), stage.ElementType);
		var g = Expr.Parameter(groupingType, "g");
		var binds = aggs.Select(a => a.Build(g)).ToList();
		var resultType = EmitRowType(binds.Select((e, i) => ($"C{i}", e.Type)).ToList());
		var resultSel = Expr.Lambda(
			Expr.MemberInit(Expr.New(resultType),
				binds.Select((e, i) => (MemberBinding)Expr.Bind(resultType.GetProperty($"C{i}")!, e)).ToArray()),
			g);
		var projected = DynSelect(grouped, groupingType, resultType, resultSel);

		var inner = MakeEmittedStage(projected, resultType, aggs.Select(a => (a.Name, a.Logical)).ToList(), now);
		return new KqlResult(inner.Columns, NoByRows(Materialize(inner), aggs.Select(a => a.Default).ToArray()));
	}

	// Yields the bare-aggregate row, or the synthesized default row when the group was empty (0 rows).
	static async IAsyncEnumerable<object?[]> NoByRows(KqlResult inner, object?[] defaultRow,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		var any = false;
		await foreach (var row in inner.Rows.WithCancellation(ct).ConfigureAwait(false))
		{
			any = true;
			yield return row;
		}
		if (!any)
			yield return defaultRow;
	}

	static bool TrySummarizeKey(SyntaxNode element, SqlStage stage, ScalarContext ctx, ParamExpr x, ParamExpr rowParam, DateTime now, out SqlCol key)
	{
		key = default;
		switch (element)
		{
			case NameReference n:
				{
					var idx = ResolveColumnIndexCI(stage.Columns, n.SimpleName);
					if (idx >= 0)
					{
						if (!TryCompileStorage(n, ctx, out var e))
							return false;
						key = new SqlCol(stage.Columns[idx].Name, stage.Columns[idx].ClrType, e);
						return true;
					}
					key = new SqlCol(n.SimpleName, typeof(string), ctx.ResolveProperties(n.SimpleName));
					return true;
				}
			case PathExpression p when IsPropertiesPath(p, out var propKey):
				key = new SqlCol("Properties." + propKey, typeof(string), ctx.ResolveProperties(propKey));
				return true;
			case ElementExpression el when IsPropertiesIndex(el, out var idxKey):
				key = new SqlCol("Properties." + idxKey, typeof(string), ctx.ResolveProperties(idxKey));
				return true;
			case SimpleNamedExpression { Name: NameDeclaration alias, Expression: var expr }:
				return TryKeyExpr(expr, alias.Name.SimpleName, stage, ctx, x, rowParam, now, out key);
			case FunctionCallExpression f:
				return TryKeyExpr(f, DefaultKeyName(f), stage, ctx, x, rowParam, now, out key);
			default:
				throw new UnsupportedKqlException(
					$"summarize by '{element.Kind}' not supported (column ref, Properties.<key>, Properties[\"key\"], or 'name = expression')");
		}
	}

	static bool TryKeyExpr(Expression expr, string name, SqlStage stage, ScalarContext ctx, ParamExpr x, ParamExpr rowParam, DateTime now, out SqlCol key)
	{
		key = default;
		if (!TryCompileStorage(expr, ctx, out var storage))
			return false;
		var logicalType = KqlScalar.Compile(expr, new RowScalarContext(rowParam, stage.Columns) { UtcNow = now }).Type;
		key = new SqlCol(name, logicalType, storage);
		return true;
	}

	static bool TrySummarizeAgg(SyntaxNode element, ScalarContext ctx, ParamExpr x, ParamExpr rowParam, SqlStage stage, DateTime now,
		out (string Name, Type Logical, Func<ParamExpr, Expr> Build) agg)
	{
		agg = default;
		var (name, call) = element switch
		{
			FunctionCallExpression f => ($"{f.Name.SimpleName}_", f),
			SimpleNamedExpression { Name: NameDeclaration alias, Expression: FunctionCallExpression f } => (alias.Name.SimpleName, f),
			_ => throw new UnsupportedKqlException($"summarize aggregate '{element.Kind}' not supported"),
		};
		var fn = call.Name.SimpleName;
		var args = call.ArgumentList.Expressions;
		var source = x.Type;

		bool Arg(string forFn, out Expr arg)
		{
			arg = null!;
			if (args.Count != 1)
				throw new UnsupportedKqlException($"{forFn}() takes exactly 1 argument, got {args.Count}");
			return TryCompileStorage(args[0].Element, ctx, out arg);
		}

		static void RequireNumeric(Type t, string forFn)
		{
			if (!KqlScalar.IsNumericType(KqlScalar.NonNullable(t)))
				throw new UnsupportedKqlException($"{forFn}() requires a numeric argument, got {t.Name}");
		}

		switch (fn)
		{
			case "count":
				if (args.Count != 0)
					throw new UnsupportedKqlException($"count() takes no arguments, got {args.Count}");
				agg = (name, typeof(long), g => Expr.Convert(Expr.Call(EnumM("Count", 1, source), g), typeof(long)));
				return true;

			case "countif":
				{
					if (!Arg("countif", out var pred))
						return false;
					if (pred.Type != typeof(bool))
						throw new UnsupportedKqlException($"countif() requires a boolean predicate, got {pred.Type.Name}");
					// SUM(CASE WHEN pred THEN 1 ELSE 0 END) — the safe form (never null; every group non-empty).
					var sel = Expr.Lambda(Expr.Condition(pred, Expr.Constant(1L), Expr.Constant(0L)), x);
					agg = (name, typeof(long), g => Expr.Call(SumM(source, typeof(long)), g, sel));
					return true;
				}

			case "sum":
				{
					if (!Arg("sum", out var arg))
						return false;
					RequireNumeric(arg.Type, "sum");
					var resT = KqlScalar.NonNullable(arg.Type) == typeof(double) ? typeof(double?) : typeof(long?);
					var sel = Expr.Lambda(Expr.Convert(arg, resT), x);
					agg = (name, resT, g => Expr.Call(SumM(source, resT), g, sel));
					return true;
				}

			case "avg":
				{
					if (!Arg("avg", out var arg))
						return false;
					RequireNumeric(arg.Type, "avg");
					var sel = Expr.Lambda(Expr.Convert(arg, typeof(double?)), x);
					agg = (name, typeof(double?), g => Expr.Call(AvgM(source, typeof(double?)), g, sel));
					return true;
				}

			case "min":
			case "max":
				{
					if (!Arg(fn, out var arg))
						return false;
					var argLogical = KqlScalar.Compile(args[0].Element, new RowScalarContext(rowParam, stage.Columns) { UtcNow = now }).Type;
					var nnLogical = KqlScalar.NonNullable(argLogical);
					// Numeric / datetime / timespan min-max: storage is a numeric/long, ordered natively. String
					// min/max stays IN-MEMORY (return false): linq2db's Min/Max cannot carry an IComparer (it
					// throws InvalidCastException), so the ordinal collation can't be forced on both providers —
					// see the reported blocker. Other types also stay in-memory.
					if (nnLogical == typeof(string)
						|| (!KqlScalar.IsNumericType(nnLogical) && nnLogical != typeof(DateTime) && nnLogical != typeof(TimeSpan)))
						return false;
					var mm = fn == "min" ? "Min" : "Max";
					var storageResT = Nullable(arg.Type);
					var sel = Expr.Lambda(Expr.Convert(arg, storageResT), x);
					agg = (name, Nullable(argLogical), g => Expr.Call(MinMaxSelM(mm, source, storageResT), g, sel));
					return true;
				}

			case "dcount":
				{
					if (!Arg("dcount", out var arg))
						return false;
					var argT = arg.Type;
					var sel = Expr.Lambda(arg, x);
					var nullable = !argT.IsValueType || KqlScalar.IsNullable(argT);
					agg = (name, typeof(long), g =>
					{
						Expr selected = Expr.Call(SelectM(source, argT), g, sel);
						if (nullable)
						{
							var v = Expr.Parameter(argT, "v");
							selected = Expr.Call(WhereM(argT), selected,
								Expr.Lambda(Expr.NotEqual(v, Expr.Constant(null, argT)), v));
						}
						var distinct = Expr.Call(EnumM("Distinct", 1, argT), selected);
						return Expr.Convert(Expr.Call(EnumM("Count", 1, argT), distinct), typeof(long));
					});
					return true;
				}

			default:
				throw new UnsupportedKqlException(
					$"aggregate '{fn}' not supported (supported: count, countif, sum, min, max, avg, dcount)");
		}
	}

	// --- Enumerable/Queryable aggregate method resolvers (the aggregate exprs sit inside the GROUP BY
	// Select projection, on the IGrouping, so linq2db translates them to COUNT/SUM/MIN/MAX/AVG/COUNT DISTINCT
	// and EnumerableQuery evaluates them in-memory). ---

	static MethodInfo EnumM(string name, int genericArity, params Type[] typeArgs) =>
		typeof(Enumerable).GetMethods()
			.Single(m => m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == genericArity
				&& m.GetParameters().Length == 1)
			.MakeGenericMethod(typeArgs);

	static MethodInfo SumM(Type source, Type sel) => SelectorAgg("Sum", source, sel);
	static MethodInfo AvgM(Type source, Type sel) => SelectorAgg("Average", source, sel);

	// Sum/Average: Xxx<TSource>(IEnumerable<TSource>, Func<TSource, sel>) — generic arity 1, selector return == sel.
	static MethodInfo SelectorAgg(string name, Type source, Type sel) =>
		typeof(Enumerable).GetMethods()
			.Single(m => m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1
				&& m.GetParameters().Length == 2
				&& m.GetParameters()[1].ParameterType.IsGenericType
				&& m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2
				&& m.GetParameters()[1].ParameterType.GetGenericArguments()[1] == sel)
			.MakeGenericMethod(source);

	// Min/Max: Xxx<TSource, TResult>(IEnumerable<TSource>, Func<TSource, TResult>) — generic arity 2, Func selector.
	static MethodInfo MinMaxSelM(string name, Type source, Type result) =>
		typeof(Enumerable).GetMethods()
			.Single(m => m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2
				&& m.GetParameters().Length == 2
				&& m.GetParameters()[1].ParameterType.IsGenericType
				&& m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2)
			.MakeGenericMethod(source, result);

	static MethodInfo SelectM(Type source, Type result) =>
		typeof(Enumerable).GetMethods()
			.Single(m => m.Name == "Select" && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 2
				&& m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2)
			.MakeGenericMethod(source, result);

	static MethodInfo WhereM(Type source) =>
		typeof(Enumerable).GetMethods()
			.Single(m => m.Name == "Where" && m.GetParameters().Length == 2
				&& m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2)
			.MakeGenericMethod(source);

	static IQueryable DynGroupBy(IQueryable source, Type sourceType, Type keyType, LambdaExpr keySelector)
	{
		var method = typeof(Queryable).GetMethods()
			.Single(m => m.Name == "GroupBy" && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 2
				&& m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2)
			.MakeGenericMethod(sourceType, keyType);
		return source.Provider.CreateQuery(Expr.Call(method, source.Expression, Expr.Quote(keySelector)));
	}

	// storage-value → logical-value converter, derived from the LOGICAL type: instants (epoch-ms long) →
	// DateTime; durations (ticks long) → TimeSpan; else identity. Null-safe. The single storage→logical seam.
	static Func<object?, object?> StorageToLogical(Type logicalType)
	{
		var nn = KqlScalar.NonNullable(logicalType);
		if (nn == typeof(DateTime))
			return v => v is null ? null : KqlSqlExpressions.FromUnixMs(Convert.ToInt64(v));
		if (nn == typeof(TimeSpan))
			return v => v is null ? null : TimeSpan.FromTicks(Convert.ToInt64(v));
		return v => v;
	}

	// --- runtime-typed Queryable composition helpers (the proven spike mechanism) ---

	static IQueryable DynSelect(IQueryable source, Type inType, Type outType, LambdaExpr selector)
	{
		var method = typeof(Queryable).GetMethods()
			.Single(m => m.Name == "Select"
				&& m.GetParameters().Length == 2
				&& m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2)
			.MakeGenericMethod(inType, outType);
		return source.Provider.CreateQuery(Expr.Call(method, source.Expression, Expr.Quote(selector)));
	}

	static IQueryable DynDistinct(IQueryable source, Type elementType)
	{
		var method = typeof(Queryable).GetMethods()
			.Single(m => m.Name == "Distinct" && m.GetParameters().Length == 1)
			.MakeGenericMethod(elementType);
		return source.Provider.CreateQuery(Expr.Call(method, source.Expression));
	}

	static IQueryable DynTake(IQueryable source, Type elementType, int count)
	{
		var method = typeof(Queryable).GetMethods()
			.Single(m => m.Name == "Take" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(int))
			.MakeGenericMethod(elementType);
		return source.Provider.CreateQuery(Expr.Call(method, source.Expression, Expr.Constant(count)));
	}

	// OrderBy/ThenBy(+Descending). For a STRING key, uses the 3-arg IComparer overload with
	// StringComparer.Ordinal (ordinal on EnumerableQuery; ignored by linq2db → SQLite BINARY = ordinal).
	static IQueryable DynOrderBy(IQueryable source, Type elementType, LambdaExpr keySelector, Type keyType, bool descending, bool isThen)
	{
		var name = (isThen, descending) switch
		{
			(false, false) => "OrderBy",
			(false, true) => "OrderByDescending",
			(true, false) => "ThenBy",
			(true, true) => "ThenByDescending",
		};
		if (keyType == typeof(string))
		{
			var m3 = typeof(Queryable).GetMethods()
				.Single(m => m.Name == name && m.GetParameters().Length == 3)
				.MakeGenericMethod(elementType, keyType);
			return source.Provider.CreateQuery(Expr.Call(m3, source.Expression, Expr.Quote(keySelector),
				Expr.Constant(StringComparer.Ordinal, typeof(IComparer<string>))));
		}
		var m2 = typeof(Queryable).GetMethods()
			.Single(m => m.Name == name && m.GetParameters().Length == 2)
			.MakeGenericMethod(elementType, keyType);
		return source.Provider.CreateQuery(Expr.Call(m2, source.Expression, Expr.Quote(keySelector)));
	}

	// Structural-equality base for every runtime-emitted projection row: linq2db ignores it (SQL does value
	// comparison in the engine); EnumerableQuery uses it so in-memory .Distinct() also dedups by value.
	public abstract class DynRow
	{
		public override bool Equals(object? obj)
		{
			if (obj is null || obj.GetType() != GetType())
				return false;
			foreach (var p in GetType().GetProperties())
				if (!Equals(p.GetValue(this), p.GetValue(obj)))
					return false;
			return true;
		}

		public override int GetHashCode()
		{
			var h = new HashCode();
			foreach (var p in GetType().GetProperties())
				h.Add(p.GetValue(this));
			return h.ToHashCode();
		}
	}

	static readonly ModuleBuilder DynModule =
		AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("KqlSqlComposition"), AssemblyBuilderAccess.Run)
			.DefineDynamicModule("main");

	// Emits a public class (deriving from DynRow) with one public auto-property per (name, STORAGE type).
	static Type EmitRowType(IReadOnlyList<(string Name, Type StorageType)> fields)
	{
		var tb = DynModule.DefineType(
			"Row_" + Guid.NewGuid().ToString("N")[..12],
			TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.AutoLayout | TypeAttributes.AnsiClass,
			typeof(DynRow));
		tb.DefineDefaultConstructor(MethodAttributes.Public);
		foreach (var (fname, ftype) in fields)
		{
			var fb = tb.DefineField("_" + fname, ftype, FieldAttributes.Private);
			var pb = tb.DefineProperty(fname, PropertyAttributes.None, ftype, null);
			var getter = tb.DefineMethod("get_" + fname,
				MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, ftype, Type.EmptyTypes);
			var gil = getter.GetILGenerator();
			gil.Emit(OpCodes.Ldarg_0);
			gil.Emit(OpCodes.Ldfld, fb);
			gil.Emit(OpCodes.Ret);
			pb.SetGetMethod(getter);
			var setter = tb.DefineMethod("set_" + fname,
				MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, null, [ftype]);
			var sil = setter.GetILGenerator();
			sil.Emit(OpCodes.Ldarg_0);
			sil.Emit(OpCodes.Ldarg_1);
			sil.Emit(OpCodes.Stfld, fb);
			sil.Emit(OpCodes.Ret);
			pb.SetSetMethod(setter);
		}
		return tb.CreateType();
	}

	// Streams a runtime-element-typed IQueryable to logical object?[] rows via the SAME linq2db
	// AsAsyncEnumerable path StreamRecordRows uses (real SQL on linq2db; sync fallback on EnumerableQuery).
	static IAsyncEnumerable<object?[]> AsAsyncRows(IQueryable query, Type elementType, Func<object, object?[]> toRow)
	{
		var m = typeof(KqlTransformer)
			.GetMethod(nameof(AsAsyncRowsGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
			.MakeGenericMethod(elementType);
		// Reflection must supply the [EnumeratorCancellation] ct even though it defaults; None is correct —
		// the real token is injected at enumeration via WithCancellation, which [EnumeratorCancellation] honors.
		return (IAsyncEnumerable<object?[]>)m.Invoke(null, [query, toRow, CancellationToken.None])!;
	}

	static async IAsyncEnumerable<object?[]> AsAsyncRowsGeneric<TRow>(
		IQueryable<TRow> query, Func<object, object?[]> toRow, [EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var o in query.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
			yield return toRow(o!);
	}
}
