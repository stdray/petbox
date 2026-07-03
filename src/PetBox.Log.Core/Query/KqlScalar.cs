using System.Reflection;
using Kusto.Language.Syntax;
using PetBox.Log.Core.Data;
using Expr = System.Linq.Expressions.Expression;
using ParamExpr = System.Linq.Expressions.ParameterExpression;

namespace PetBox.Log.Core.Query;

// The scalar expression engine. A KQL scalar (column ref, literal, arithmetic, comparison,
// in/between, iff/case, and — future waves — string/datetime/typed-conversion functions)
// compiles to a System.Linq.Expressions node. The tree is IDENTICAL regardless of where it
// runs; the ONLY thing that varies is how a column/Properties leaf resolves, which each
// ScalarContext supplies:
//
//   RecordScalarContext — leaves are LogEntryRecord property accesses, so the whole tree is
//     SQL-translatable by linq2db. Used by `where` (pre-split), which runs as SQLite SQL.
//   RowScalarContext    — leaves index into a materialized object?[] row, so the tree is
//     compiled to a delegate and evaluated in-memory. Used by the post-split stages
//     (extend / computed project), where the pipeline no longer maps to a single table.
//
// The SQL-vs-in-memory boundary is therefore the pipeline split (KqlTransformer.Execute):
// everything a `where` touches stays in the linq2db Expression path; anything a
// shape-changing operator computes evaluates in memory. linq2db reaches ~100% of SQLite and
// is cheaply extensible ([Sql.Expression], see KqlSqlExpressions), so the SQL side can grow
// without leaving the Expression path.

// A scalar KQL function: receives its already-compiled argument expressions plus the compile
// context, and produces the resulting expression. New waves register more entries in
// KqlScalarFunctions without touching the compiler or the transformer.
delegate Expr KqlScalarFunction(IReadOnlyList<Expr> args, ScalarContext ctx);

abstract class ScalarContext
{
	// The single wall-clock instant now()/ago() resolve to, evaluated once at query compile time and
	// threaded down from KqlTransformer so every operator in a query sees the same "now" (and tests
	// can pin it). Always UTC.
	public DateTime UtcNow { get; init; }

	// Resolve a bare column reference (e.g. Level, Message) to an expression yielding its value.
	public abstract Expr ResolveColumn(string name);

	// Resolve a Properties.<key> reference to an expression yielding the (string) JSON value.
	public abstract Expr ResolveProperties(string key);

	// Fast-path for `column OP literal`: a context may coerce the literal to the column's
	// storage type (datetime -> epoch-ms in the record context) and emit a precise,
	// column-named error. Return null to fall through to the general compile-both-sides path.
	public virtual Expr? TryColumnLiteralComparison(string columnName, Expr columnAccess, LiteralExpression literal, SyntaxKind op) => null;

	// now() in this context's instant representation: epoch-ms (long) for the SQL/record context,
	// DateTime for the in-memory/row context. See CoerceInstant for the representation contract.
	public abstract Expr CurrentInstant();

	// Normalize a datetime-typed scalar into this context's instant representation so datetime
	// functions and comparisons operate on a single type: epoch-ms (long) under RecordScalarContext
	// (SQL-translatable, matching the TimestampMs column) or DateTime under RowScalarContext.
	public abstract Expr CoerceInstant(Expr e);

	// Bridge a NULLABLE epoch-ms (long?, where null means "not a valid datetime" — Kusto's null) into
	// this context's nullable instant representation: long? (epoch-ms) for the SQL/record context,
	// DateTime? for the in-memory/row context. Used by todatetime(), whose result may be null.
	public abstract Expr NullableInstant(Expr epochMsNullable);
}

// Leaves resolve to LogEntryRecord property accesses → the tree is SQL-translatable.
sealed class RecordScalarContext(ParamExpr row) : ScalarContext
{
	public override Expr ResolveColumn(string name) => name switch
	{
		"Id" => Expr.Property(row, nameof(LogEntryRecord.Id)),
		"Level" => Expr.Property(row, nameof(LogEntryRecord.Level)),
		"LevelName" => LevelName(),
		"Timestamp" => Expr.Property(row, nameof(LogEntryRecord.TimestampMs)),
		"ServiceKey" => Expr.Property(row, nameof(LogEntryRecord.ServiceKey)),
		"Message" => Expr.Property(row, nameof(LogEntryRecord.Message)),
		"MessageTemplate" => Expr.Property(row, nameof(LogEntryRecord.MessageTemplate)),
		"Exception" => Expr.Property(row, nameof(LogEntryRecord.Exception)),
		// Bare-name fallback: a name that is not a known event column resolves as a Properties.<name>
		// lookup (string-typed json_extract), so `where DeviceId == 'x'` filters on the property. Known
		// columns always win (matched above). Trade-off: a typo'd column now yields empty results
		// rather than an error — but comparing a fallback (string) name to a non-string literal still
		// raises a precise type error.
		_ => ResolveProperties(name),
	};

	public override Expr ResolveProperties(string key)
	{
		var propertiesJson = Expr.Property(row, nameof(LogEntryRecord.PropertiesJson));
		var path = Expr.Constant("$." + key, typeof(string));
		var method = typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.JsonExtract))!;
		return Expr.Call(method, propertiesJson, path);
	}

	// now() in the SQL context is a constant epoch-ms long, so `where Timestamp > ago(1h)` compares
	// long-to-long against TimestampMs and translates to SQLite as a literal.
	public override Expr CurrentInstant() => Expr.Constant(KqlSqlExpressions.ToUnixMs(UtcNow));

	// SQL instants are epoch-ms longs. A long passes through; a datetime() literal (a compile-time
	// DateTime constant) folds to its epoch-ms constant. A non-constant DateTime cannot be produced
	// SQL-side here, so it is rejected with a clear message rather than emitting untranslatable SQL.
	public override Expr CoerceInstant(Expr e)
	{
		if (e.Type == typeof(long))
			return e;
		if (e.Type == typeof(DateTime))
		{
			if (e is System.Linq.Expressions.ConstantExpression { Value: DateTime dt })
				return Expr.Constant(KqlSqlExpressions.ToUnixMs(dt));
			throw new UnsupportedKqlException(
				"a computed datetime is not supported inside a SQL `where`; use it in project/extend/summarize instead");
		}
		throw new UnsupportedKqlException($"expected a datetime, got {e.Type.Name}");
	}

	// SQL instants are epoch-ms longs, so a nullable instant is already a long? — pass it through.
	public override Expr NullableInstant(Expr epochMsNullable) => epochMsNullable;

	public override Expr? TryColumnLiteralComparison(string columnName, Expr access, LiteralExpression literal, SyntaxKind op)
	{
		if (!KqlScalar.IsComparison(op))
			return null;
		var coerced = CoerceLiteral(literal, access.Type, columnName);
		return op switch
		{
			SyntaxKind.EqualExpression => Expr.Equal(access, coerced),
			SyntaxKind.NotEqualExpression => Expr.NotEqual(access, coerced),
			SyntaxKind.LessThanExpression => Expr.LessThan(access, coerced),
			SyntaxKind.LessThanOrEqualExpression => Expr.LessThanOrEqual(access, coerced),
			SyntaxKind.GreaterThanExpression => Expr.GreaterThan(access, coerced),
			SyntaxKind.GreaterThanOrEqualExpression => Expr.GreaterThanOrEqual(access, coerced),
			_ => null,
		};
	}

	Expr LevelName()
	{
		var level = Expr.Property(row, nameof(LogEntryRecord.Level));
		var toName = typeof(RecordScalarContext).GetMethod(nameof(ToLevelName), BindingFlags.Static | BindingFlags.NonPublic)!;
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

	// Timestamp is stored as epoch-ms, so a datetime() literal is converted to ms; integer /
	// string columns require the matching literal kind. Preserves the exact user-facing
	// messages the `where` path has always produced.
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
}

// Leaves index into a materialized object?[] row → the tree is evaluated in-memory.
sealed class RowScalarContext(ParamExpr rowArray, IReadOnlyList<KqlColumn> columns) : ScalarContext
{
	public override Expr ResolveColumn(string name)
	{
		var idx = IndexOf(name);
		if (idx >= 0)
			return Cell(idx, columns[idx].ClrType);
		// Bare-name fallback (post-split): an unknown name is a Properties.<name> lookup, but only when
		// the current row shape still carries PropertiesJson. After a project/summarize that dropped it,
		// there is nothing to look up, so the precise "unknown column" error stands.
		if (IndexOf(nameof(LogEntryRecord.PropertiesJson)) < 0)
			throw new UnsupportedKqlException($"unknown column '{name}'");
		return ResolveProperties(name);
	}

	public override Expr ResolveProperties(string key)
	{
		var idx = IndexOf(nameof(LogEntryRecord.PropertiesJson));
		if (idx < 0)
			throw new UnsupportedKqlException("Properties.<key>: input has no PropertiesJson column");
		var jsonCell = Cell(idx, typeof(string));
		var path = Expr.Constant("$." + key, typeof(string));
		var method = typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.JsonExtract))!;
		return Expr.Call(method, jsonCell, path);
	}

	int IndexOf(string name)
	{
		for (var i = 0; i < columns.Count; i++)
			if (string.Equals(columns[i].Name, name, StringComparison.Ordinal))
				return i;
		return -1;
	}

	Expr Cell(int idx, Type type) => Expr.Convert(Expr.ArrayIndex(rowArray, Expr.Constant(idx)), type);

	// In-memory instants are DateTime, matching the materialized Timestamp column.
	public override Expr CurrentInstant() => Expr.Constant(UtcNow);

	public override Expr CoerceInstant(Expr e)
	{
		if (e.Type == typeof(DateTime))
			return e;
		if (e.Type == typeof(long)) // an epoch-ms value that reached the in-memory path
			return Expr.Call(typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.FromUnixMs))!, e);
		throw new UnsupportedKqlException($"expected a datetime, got {e.Type.Name}");
	}

	// In-memory instants are DateTime, so a nullable instant is DateTime?: convert the epoch-ms long?
	// (null-preserving) via FromUnixMsN.
	public override Expr NullableInstant(Expr epochMsNullable) =>
		Expr.Call(typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.FromUnixMsN))!, epochMsNullable);
}

static class KqlScalar
{
	public static Expr Compile(Expression node, ScalarContext ctx) => node switch
	{
		LiteralExpression lit => Literal(lit),
		NameReference n => ctx.ResolveColumn(n.SimpleName),
		PathExpression p when KqlTransformer.IsPropertiesPath(p, out var key) => ctx.ResolveProperties(key),
		ParenthesizedExpression paren => Compile(paren.Expression, ctx),
		PrefixUnaryExpression u => Unary(u, ctx),
		BinaryExpression b => Binary(b, ctx),
		InExpression inx => In(inx, ctx),
		BetweenExpression btw => Between(btw, ctx),
		FunctionCallExpression call => Call(call, ctx),
		_ => throw new UnsupportedKqlException($"expression '{node.Kind}' not supported"),
	};

	static Expr Literal(LiteralExpression lit) => lit.LiteralValue switch
	{
		long l => Expr.Constant(l),
		int i => Expr.Constant((long)i),
		double d => Expr.Constant(d),
		decimal m => Expr.Constant((double)m),
		bool b => Expr.Constant(b),
		string s => Expr.Constant(s, typeof(string)),
		DateTime dt => Expr.Constant(dt),
		TimeSpan ts => Expr.Constant(ts),
		null => throw new UnsupportedKqlException("null literal not supported"),
		_ => throw new UnsupportedKqlException($"literal of type '{lit.LiteralValue.GetType().Name}' not supported"),
	};

	static Expr Unary(PrefixUnaryExpression u, ScalarContext ctx)
	{
		var operand = Compile(u.Expression, ctx);
		return u.Kind switch
		{
			SyntaxKind.UnaryMinusExpression => Expr.Negate(RequireNumeric(operand, "unary '-'")),
			SyntaxKind.UnaryPlusExpression => RequireNumeric(operand, "unary '+'"),
			_ => throw new UnsupportedKqlException($"unary '{u.Kind}' not supported"),
		};
	}

	static Expr Binary(BinaryExpression b, ScalarContext ctx) => b.Kind switch
	{
		SyntaxKind.AndExpression => Expr.AndAlso(RequireBool(Compile(b.Left, ctx)), RequireBool(Compile(b.Right, ctx))),
		SyntaxKind.OrExpression => Expr.OrElse(RequireBool(Compile(b.Left, ctx)), RequireBool(Compile(b.Right, ctx))),
		SyntaxKind.AddExpression
			or SyntaxKind.SubtractExpression
			or SyntaxKind.MultiplyExpression
			or SyntaxKind.DivideExpression
			or SyntaxKind.ModuloExpression => Arithmetic(b, ctx),
		// contains = substring; has = whole-term (see KqlSqlExpressions.Has). startswith/endswith and
		// the case-sensitive _cs variants translate to fixed-length substring compares.
		SyntaxKind.ContainsExpression => StringMatch(b, ctx, caseSensitive: false),
		SyntaxKind.ContainsCsExpression => StringMatch(b, ctx, caseSensitive: true),
		SyntaxKind.StartsWithExpression => StringFn(b, ctx, "startswith", nameof(KqlSqlExpressions.StartsWithI)),
		SyntaxKind.StartsWithCsExpression => StringFn(b, ctx, "startswith_cs", nameof(KqlSqlExpressions.StartsWithCs)),
		SyntaxKind.EndsWithExpression => StringFn(b, ctx, "endswith", nameof(KqlSqlExpressions.EndsWithI)),
		SyntaxKind.EndsWithCsExpression => StringFn(b, ctx, "endswith_cs", nameof(KqlSqlExpressions.EndsWithCs)),
		SyntaxKind.HasExpression => StringFn(b, ctx, "has", nameof(KqlSqlExpressions.Has)),
		SyntaxKind.HasCsExpression => StringFn(b, ctx, "has_cs", nameof(KqlSqlExpressions.HasCs)),
		SyntaxKind.MatchesRegexExpression => StringFn(b, ctx, "matches regex", nameof(KqlSqlExpressions.MatchesRegex)),
		_ when IsComparison(b.Kind) => Comparison(b, ctx),
		_ => throw new UnsupportedKqlException($"binary '{b.Kind}' not supported"),
	};

	// A `column <op> 'literal'` string predicate backed by a KqlSqlExpressions method (which carries
	// both the SQLite translation and the in-memory body). Left must be string-typed, right a string
	// literal (KQL requires a constant term/pattern for these operators).
	static Expr StringFn(BinaryExpression b, ScalarContext ctx, string opName, string method)
	{
		var access = Compile(b.Left, ctx);
		if (access.Type != typeof(string))
			throw new UnsupportedKqlException($"'{opName}' requires a string operand on the left, got {access.Type.Name}");
		if (b.Right is not LiteralExpression { LiteralValue: string needle })
			throw new UnsupportedKqlException($"'{opName}' requires a string literal on the right");
		var mi = typeof(KqlSqlExpressions).GetMethod(method)!;
		return Expr.Call(mi, access, Expr.Constant(needle));
	}

	static Expr Arithmetic(BinaryExpression b, ScalarContext ctx)
	{
		var l = RequireNumeric(Compile(b.Left, ctx), b.Kind.ToString());
		var r = RequireNumeric(Compile(b.Right, ctx), b.Kind.ToString());
		var t = CommonNumeric(l.Type, r.Type);
		l = ConvertTo(l, t);
		r = ConvertTo(r, t);
		return b.Kind switch
		{
			SyntaxKind.AddExpression => Expr.Add(l, r),
			SyntaxKind.SubtractExpression => Expr.Subtract(l, r),
			SyntaxKind.MultiplyExpression => Expr.Multiply(l, r),
			SyntaxKind.DivideExpression => Expr.Divide(l, r),
			SyntaxKind.ModuloExpression => Expr.Modulo(l, r),
			_ => throw new UnsupportedKqlException($"arithmetic '{b.Kind}' not supported"),
		};
	}

	static Expr StringMatch(BinaryExpression b, ScalarContext ctx, bool caseSensitive)
	{
		var access = Compile(b.Left, ctx);
		if (access.Type != typeof(string))
			throw new UnsupportedKqlException($"contains/has requires a string operand on the left, got {access.Type.Name}");
		if (b.Right is not LiteralExpression { LiteralValue: string needle })
			throw new UnsupportedKqlException("contains/has requires a string literal");
		var method = typeof(string).GetMethod(nameof(string.Contains), [typeof(string), typeof(StringComparison)])!;
		var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		return Expr.Call(access, method, Expr.Constant(needle), Expr.Constant(comparison));
	}

	static Expr Comparison(BinaryExpression b, ScalarContext ctx)
	{
		// column OP literal: let the context coerce the literal (datetime->ms, int, string) and
		// emit precise column-named errors. Falls through to the general path otherwise.
		if (TryColumnRef(b.Left, ctx, out var colName, out var access) && b.Right is LiteralExpression lit)
		{
			var special = ctx.TryColumnLiteralComparison(colName, access, lit, b.Kind);
			if (special is not null)
				return special;
		}

		var le = Compile(b.Left, ctx);
		var re = Compile(b.Right, ctx);
		(le, re) = NormalizeInstants(le, re, ctx);
		return Compare(le, re, b.Kind);
	}

	// A datetime() literal compiles to a DateTime constant, but a datetime-valued EXPRESSION in the
	// record context is epoch-ms (long). When the two are compared — e.g. startofday(Timestamp) ==
	// datetime(...) — coerce the literal into the other operand's instant representation so both are
	// epoch-ms. (In the row context both sides are already DateTime, so this is a no-op there.)
	static (Expr, Expr) NormalizeInstants(Expr le, Expr re, ScalarContext ctx)
	{
		// The record-context instant is long (or long? for todatetime); coerce a datetime() literal on
		// the other side into that epoch-ms domain so both operands compare as ms.
		if (le is System.Linq.Expressions.ConstantExpression { Value: DateTime } && IsEpochMs(re.Type))
			return (ctx.CoerceInstant(le), re);
		if (re is System.Linq.Expressions.ConstantExpression { Value: DateTime } && IsEpochMs(le.Type))
			return (le, ctx.CoerceInstant(re));
		return (le, re);
	}

	static bool IsEpochMs(Type t) => t == typeof(long) || t == typeof(long?);

	static Expr Compare(Expr le, Expr re, SyntaxKind kind)
	{
		(le, re) = UnifyComparable(le, re, kind);
		// liftToNull:false so a comparison with a nullable operand yields bool (not bool?): a null
		// operand makes the comparison false, matching SQL's NULL-is-not-true and Kusto's null-in-where
		// (typed conversions of malformed values produce null, which must simply not match).
		return kind switch
		{
			SyntaxKind.EqualExpression => Expr.Equal(le, re, liftToNull: false, method: null),
			SyntaxKind.NotEqualExpression => Expr.NotEqual(le, re, liftToNull: false, method: null),
			SyntaxKind.LessThanExpression => Expr.LessThan(le, re, liftToNull: false, method: null),
			SyntaxKind.LessThanOrEqualExpression => Expr.LessThanOrEqual(le, re, liftToNull: false, method: null),
			SyntaxKind.GreaterThanExpression => Expr.GreaterThan(le, re, liftToNull: false, method: null),
			SyntaxKind.GreaterThanOrEqualExpression => Expr.GreaterThanOrEqual(le, re, liftToNull: false, method: null),
			_ => throw new UnsupportedKqlException($"comparison '{kind}' not supported"),
		};
	}

	static Expr In(InExpression inx, ScalarContext ctx)
	{
		var negate = inx.Kind is SyntaxKind.NotInExpression or SyntaxKind.NotInCsExpression;
		var le = Compile(inx.Left, ctx);
		var elements = inx.Right.Expressions.Select(e => Compile(e.Element, ctx)).ToList();
		if (elements.Count == 0)
			return Expr.Constant(negate);

		Expr? acc = null;
		foreach (var element in elements)
		{
			var (l, r) = UnifyComparable(le, element, SyntaxKind.EqualExpression);
			var eq = Expr.Equal(l, r, liftToNull: false, method: null);
			acc = acc is null ? eq : Expr.OrElse(acc, eq);
		}
		return negate ? Expr.Not(acc!) : acc!;
	}

	static Expr Between(BetweenExpression btw, ScalarContext ctx)
	{
		var value = Compile(btw.Left, ctx);
		var lo = Compile(btw.Right.First, ctx);
		var hi = Compile(btw.Right.Second, ctx);
		var (v1, lo1) = UnifyComparable(value, lo, SyntaxKind.GreaterThanOrEqualExpression);
		var (v2, hi1) = UnifyComparable(value, hi, SyntaxKind.LessThanOrEqualExpression);
		var inRange = Expr.AndAlso(
			Expr.GreaterThanOrEqual(v1, lo1, liftToNull: false, method: null),
			Expr.LessThanOrEqual(v2, hi1, liftToNull: false, method: null));
		return btw.Kind == SyntaxKind.NotBetweenExpression ? Expr.Not(inRange) : inRange;
	}

	static Expr Call(FunctionCallExpression call, ScalarContext ctx)
	{
		var name = call.Name.SimpleName;
		if (!KqlScalarFunctions.TryResolve(name, out var fn))
			throw new UnsupportedKqlException($"function '{name}' not supported");
		var args = call.ArgumentList.Expressions.Select(e => Compile(e.Element, ctx)).ToList();
		return fn(args, ctx);
	}

	static bool TryColumnRef(Expression node, ScalarContext ctx, out string columnName, out Expr access)
	{
		switch (node)
		{
			case NameReference n:
				columnName = n.SimpleName;
				access = ctx.ResolveColumn(n.SimpleName);
				return true;
			case PathExpression p when KqlTransformer.IsPropertiesPath(p, out var key):
				columnName = "Properties." + key;
				access = ctx.ResolveProperties(key);
				return true;
			default:
				columnName = "";
				access = null!;
				return false;
		}
	}

	// --- shared helpers used by the compiler and the scalar-function registry ---

	public static bool IsComparison(SyntaxKind kind) => kind is
		SyntaxKind.EqualExpression or SyntaxKind.NotEqualExpression
		or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
		or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression;

	static bool IsNumeric(Type t) => t == typeof(int) || t == typeof(long) || t == typeof(double);

	// Exposed for the scalar-function registry (bin, aggregate args): the same numeric-kind
	// test the compiler uses internally.
	public static bool IsNumericType(Type t) => IsNumeric(t);

	static bool IsEqualityOnly(SyntaxKind kind) => kind is
		SyntaxKind.EqualExpression or SyntaxKind.NotEqualExpression;

	public static Expr RequireBool(Expr e) => e.Type == typeof(bool)
		? e
		: throw new UnsupportedKqlException($"expected a boolean expression, got {e.Type.Name}");

	static Expr RequireNumeric(Expr e, string what) => IsNumeric(e.Type)
		? e
		: throw new UnsupportedKqlException($"{what} requires a numeric operand, got {e.Type.Name}");

	static Type CommonNumeric(Type a, Type b)
	{
		if (!IsNumeric(a) || !IsNumeric(b))
			throw new UnsupportedKqlException($"cannot combine {a.Name} and {b.Name} arithmetically");
		return a == typeof(double) || b == typeof(double) ? typeof(double) : typeof(long);
	}

	static Expr ConvertTo(Expr e, Type t) => e.Type == t ? e : Expr.Convert(e, t);

	// Coerce two operands of a comparison to a common comparable type: numeric promotion, or
	// equal reference/value types. Strings and bools only support equality, not ordering. Either side
	// may be nullable (typed conversions of malformed values yield null); the common type is then that
	// nullable type so the comparison lifts (see Compare, which lifts to bool, not bool?).
	static (Expr, Expr) UnifyComparable(Expr le, Expr re, SyntaxKind kind)
	{
		var lt = NonNullable(le.Type);
		var rt = NonNullable(re.Type);
		var anyNullable = IsNullable(le.Type) || IsNullable(re.Type);

		if (IsNumeric(lt) && IsNumeric(rt))
		{
			var t = CommonNumeric(lt, rt);
			var target = anyNullable ? typeof(Nullable<>).MakeGenericType(t) : t;
			return (ConvertComparable(le, target), ConvertComparable(re, target));
		}
		if (lt == rt)
		{
			if ((lt == typeof(string) || lt == typeof(bool)) && !IsEqualityOnly(kind))
				throw new UnsupportedKqlException($"'{lt.Name}' operands support only == and !=, not {kind}");
			if (anyNullable && lt.IsValueType)
			{
				var target = typeof(Nullable<>).MakeGenericType(lt);
				return (ConvertComparable(le, target), ConvertComparable(re, target));
			}
			return (le, re);
		}
		throw new UnsupportedKqlException($"cannot compare {le.Type.Name} with {re.Type.Name}");
	}

	// Convert a comparison operand to the (possibly nullable) target type. Numeric widening to a
	// nullable target goes through the underlying numeric first so int→long? is a two-step lifted
	// convert, never an illegal single-step one.
	static Expr ConvertComparable(Expr e, Type target)
	{
		if (e.Type == target)
			return e;
		if (!IsNullable(target))
			return ConvertTo(e, target);
		var underlying = NonNullable(target);
		if (NonNullable(e.Type) != underlying)
			e = IsNullable(e.Type)
				? Expr.Convert(e, typeof(Nullable<>).MakeGenericType(underlying))
				: Expr.Convert(e, underlying);
		return e.Type == target ? e : Expr.Convert(e, target);
	}

	public static bool IsNullable(Type t) => Nullable.GetUnderlyingType(t) is not null;

	// The underlying type of a Nullable<T>, or the type itself.
	public static Type NonNullable(Type t) => Nullable.GetUnderlyingType(t) ?? t;

	// Unify the result branches of iff()/case() to a single type (Expr.Condition requires
	// both branches share a type). Numeric branches promote; otherwise all must already agree.
	public static (Expr, Expr) UnifyValues(Expr a, Expr b)
	{
		var t = CommonValueType([a.Type, b.Type]);
		return (ConvertToValue(a, t), ConvertToValue(b, t));
	}

	public static Type CommonValueType(IReadOnlyList<Type> types)
	{
		var distinct = types.Distinct().ToList();
		if (distinct.Count == 1)
			return distinct[0];
		if (distinct.All(IsNumeric))
			return distinct.Aggregate(CommonNumeric);
		throw new UnsupportedKqlException(
			$"incompatible result types: {string.Join(", ", distinct.Select(t => t.Name))}");
	}

	public static Expr ConvertToValue(Expr e, Type t)
	{
		if (e.Type == t)
			return e;
		if (IsNumeric(e.Type) && IsNumeric(t))
			return Expr.Convert(e, t);
		throw new UnsupportedKqlException($"cannot convert {e.Type.Name} to {t.Name}");
	}
}

// The scalar-function registry: the single plug-in point for scalar functions. Later waves
// (string: tolower/substring/strcat/…; datetime: ago/now/startof*/…; typed conversions:
// tostring/toint/…) add entries here — self-contained (each coerces its own args and picks
// its own SQL-translatable vs in-memory strategy via the ScalarContext) — without changing
// the compiler or the transformer.
static class KqlScalarFunctions
{
	static readonly IReadOnlyDictionary<string, KqlScalarFunction> Registry =
		new Dictionary<string, KqlScalarFunction>(StringComparer.Ordinal)
		{
			["not"] = Not,
			["iff"] = Iff,
			["iif"] = Iff,
			["case"] = Case,
			["bin"] = Bin,
			["tolower"] = Tolower,
			["toupper"] = Toupper,
			["substring"] = Substring,
			["strcat"] = Strcat,
			["extract"] = Extract,
			["parse_json"] = ParseJson,
			["todynamic"] = ParseJson,
			["now"] = Now,
			["ago"] = Ago,
			["startofday"] = (a, c) => StartOf(a, c, "startofday", nameof(KqlSqlExpressions.StartOfDayMs)),
			["startofweek"] = (a, c) => StartOf(a, c, "startofweek", nameof(KqlSqlExpressions.StartOfWeekMs)),
			["startofmonth"] = (a, c) => StartOf(a, c, "startofmonth", nameof(KqlSqlExpressions.StartOfMonthMs)),
			["startofyear"] = (a, c) => StartOf(a, c, "startofyear", nameof(KqlSqlExpressions.StartOfYearMs)),
			["datetime_diff"] = DateTimeDiff,
			["tostring"] = ToStringConv,
			["toint"] = (a, c) => ToLongConv(a, "toint"),
			["tolong"] = (a, c) => ToLongConv(a, "tolong"),
			["todouble"] = ToDoubleConv,
			["tobool"] = ToBoolConv,
			["todatetime"] = ToDateTimeConv,
		};

	public static bool TryResolve(string name, out KqlScalarFunction fn) => Registry.TryGetValue(name, out fn!);

	static Expr Not(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 1)
			throw new UnsupportedKqlException($"not() takes exactly 1 argument, got {args.Count}");
		return Expr.Not(KqlScalar.RequireBool(args[0]));
	}

	static Expr Iff(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 3)
			throw new UnsupportedKqlException($"iff()/iif() takes exactly 3 arguments, got {args.Count}");
		var cond = KqlScalar.RequireBool(args[0]);
		var (a, b) = KqlScalar.UnifyValues(args[1], args[2]);
		return Expr.Condition(cond, a, b);
	}

	static Expr Case(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count < 3 || args.Count % 2 == 0)
			throw new UnsupportedKqlException(
				$"case() takes an odd number (>= 3) of arguments (predicate, value, …, else), got {args.Count}");

		var valueTypes = new List<Type>();
		for (var i = 1; i < args.Count; i += 2)
			valueTypes.Add(args[i].Type);
		valueTypes.Add(args[^1].Type);
		var commonType = KqlScalar.CommonValueType(valueTypes);

		var result = KqlScalar.ConvertToValue(args[^1], commonType);
		for (var i = args.Count - 3; i >= 0; i -= 2)
		{
			var cond = KqlScalar.RequireBool(args[i]);
			var value = KqlScalar.ConvertToValue(args[i + 1], commonType);
			result = Expr.Condition(cond, value, result);
		}
		return result;
	}

	// bin(value, step): floor `value` down to the nearest multiple of `step`. Two shapes:
	//   (datetime, timespan) → datetime, floored on the tick timeline (so bin(t, 1h) is the
	//     top of the hour). This is the time-bucket used by `summarize … by bin(Timestamp, 1h)`.
	//   (numeric, numeric)   → numeric, floor(value/step)*step. Result is long when both sides
	//     are integral, otherwise double.
	// The step is a normal compiled expression (usually a literal, but any timespan/numeric
	// expression works); a non-positive step throws at evaluation. Registering bin here means
	// it is equally usable as a plain scalar (e.g. `extend Hour = bin(Timestamp, 1h)`).
	static Expr Bin(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 2)
			throw new UnsupportedKqlException($"bin() takes exactly 2 arguments (value, step), got {args.Count}");
		var value = args[0];
		var step = args[1];

		if (value.Type == typeof(DateTime) && step.Type == typeof(TimeSpan))
			return Expr.Call(Method(nameof(BinDateTime)), value, step);

		if (KqlScalar.IsNumericType(value.Type) && KqlScalar.IsNumericType(step.Type))
		{
			var t = KqlScalar.CommonValueType([value.Type, step.Type]);
			return t == typeof(long)
				? Expr.Call(Method(nameof(BinLong)), KqlScalar.ConvertToValue(value, typeof(long)), KqlScalar.ConvertToValue(step, typeof(long)))
				: Expr.Call(Method(nameof(BinDouble)), KqlScalar.ConvertToValue(value, typeof(double)), KqlScalar.ConvertToValue(step, typeof(double)));
		}

		throw new UnsupportedKqlException(
			$"bin() supports (datetime, timespan) or (numeric, numeric), got ({value.Type.Name}, {step.Type.Name})");
	}

	// --- string functions (each backed by a dual SQL/in-memory KqlSqlExpressions method) ---

	static Expr Tolower(IReadOnlyList<Expr> args, ScalarContext ctx) =>
		StringXform(args, "tolower", nameof(KqlSqlExpressions.ToLower));

	static Expr Toupper(IReadOnlyList<Expr> args, ScalarContext ctx) =>
		StringXform(args, "toupper", nameof(KqlSqlExpressions.ToUpper));

	static Expr StringXform(IReadOnlyList<Expr> args, string fn, string method)
	{
		if (args.Count != 1)
			throw new UnsupportedKqlException($"{fn}() takes exactly 1 argument, got {args.Count}");
		if (args[0].Type != typeof(string))
			throw new UnsupportedKqlException($"{fn}() requires a string argument, got {args[0].Type.Name}");
		return Expr.Call(SqlM(method), args[0]);
	}

	static Expr Substring(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count is not (2 or 3))
			throw new UnsupportedKqlException($"substring() takes 2 or 3 arguments (source, start[, length]), got {args.Count}");
		if (args[0].Type != typeof(string))
			throw new UnsupportedKqlException($"substring() requires a string source, got {args[0].Type.Name}");
		var start = RequireLong(args[1], "substring() start");
		if (args.Count == 2)
			return Expr.Call(SqlM(nameof(KqlSqlExpressions.Substring2)), args[0], start);
		var length = RequireLong(args[2], "substring() length");
		return Expr.Call(SqlM(nameof(KqlSqlExpressions.Substring3)), args[0], start, length);
	}

	static Expr Strcat(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count < 1)
			throw new UnsupportedKqlException("strcat() takes at least 1 argument");
		foreach (var a in args)
			if (a.Type != typeof(string))
				throw new UnsupportedKqlException($"strcat() requires string arguments, got {a.Type.Name}");
		// Fold pairwise through StrCat2 so a null operand renders as empty (Kusto semantics). A
		// single argument still routes through StrCat2 to apply the same null-to-empty coercion.
		if (args.Count == 1)
			return Expr.Call(SqlM(nameof(KqlSqlExpressions.StrCat2)), args[0], Expr.Constant("", typeof(string)));
		var acc = args[0];
		for (var i = 1; i < args.Count; i++)
			acc = Expr.Call(SqlM(nameof(KqlSqlExpressions.StrCat2)), acc, args[i]);
		return acc;
	}

	static Expr Extract(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 3)
			throw new UnsupportedKqlException($"extract() takes exactly 3 arguments (regex, captureGroup, source), got {args.Count}");
		if (args[0].Type != typeof(string))
			throw new UnsupportedKqlException($"extract() regex must be a string, got {args[0].Type.Name}");
		var group = RequireLong(args[1], "extract() captureGroup");
		if (args[2].Type != typeof(string))
			throw new UnsupportedKqlException($"extract() source must be a string, got {args[2].Type.Name}");
		return Expr.Call(SqlM(nameof(KqlSqlExpressions.Extract)), args[0], group, args[2]);
	}

	// parse_json/todynamic: dynamic values are not modeled, so this is a string passthrough — it
	// returns its input JSON text unchanged. Field access is via the Properties.<key> path form.
	static Expr ParseJson(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 1)
			throw new UnsupportedKqlException($"parse_json() takes exactly 1 argument, got {args.Count}");
		if (args[0].Type != typeof(string))
			throw new UnsupportedKqlException($"parse_json() requires a string argument, got {args[0].Type.Name}");
		return args[0];
	}

	// --- datetime functions. Instants are epoch-ms (long) in the SQL/record context and DateTime in
	// the in-memory/row context; ScalarContext.CoerceInstant/CurrentInstant bridge the two, and the
	// ms-domain helpers (StartOf*Ms, YearOfMs, …) carry both a SQLite translation and a C# body. ---

	static Expr Now(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 0)
			throw new UnsupportedKqlException($"now() takes no arguments, got {args.Count}");
		return ctx.CurrentInstant();
	}

	static Expr Ago(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 1)
			throw new UnsupportedKqlException($"ago() takes exactly 1 argument (a timespan), got {args.Count}");
		if (args[0].Type != typeof(TimeSpan))
			throw new UnsupportedKqlException($"ago() requires a timespan argument, got {args[0].Type.Name}");
		// now - span, folded to a constant instant for the usual constant `ago(1h)`.
		if (args[0] is System.Linq.Expressions.ConstantExpression { Value: TimeSpan span })
			return ctx.CoerceInstant(Expr.Constant(ctx.UtcNow - span));
		var subtract = typeof(DateTime).GetMethod(nameof(DateTime.Subtract), [typeof(TimeSpan)])!;
		return ctx.CoerceInstant(Expr.Call(Expr.Constant(ctx.UtcNow), subtract, args[0]));
	}

	static Expr StartOf(IReadOnlyList<Expr> args, ScalarContext ctx, string fn, string msMethod)
	{
		if (args.Count != 1)
			throw new UnsupportedKqlException($"{fn}() takes exactly 1 argument (a datetime), got {args.Count}");
		var instant = ctx.CoerceInstant(args[0]);
		var wasDateTime = instant.Type == typeof(DateTime);
		var ms = wasDateTime ? Expr.Call(SqlM(nameof(KqlSqlExpressions.ToUnixMs)), instant) : instant;
		var startMs = Expr.Call(SqlM(msMethod), ms);
		return wasDateTime ? Expr.Call(SqlM(nameof(KqlSqlExpressions.FromUnixMs)), startMs) : startMs;
	}

	// datetime_diff(period, datetime1, datetime2) = datetime1 - datetime2 counted in whole periods,
	// where each operand is first truncated to its period boundary (Kusto counts boundary crossings,
	// not the raw truncated difference). Fixed-width parts divide epoch-ms (day/hour/… boundaries are
	// epoch-aligned); week uses the Sunday-anchored startofweek; year/quarter/month are calendar-field
	// differences. Result is a signed long.
	static Expr DateTimeDiff(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 3)
			throw new UnsupportedKqlException($"datetime_diff() takes exactly 3 arguments (period, datetime1, datetime2), got {args.Count}");
		if (args[0] is not System.Linq.Expressions.ConstantExpression { Value: string part })
			throw new UnsupportedKqlException("datetime_diff() period must be a string literal");

		var aMs = ToEpochMs(ctx.CoerceInstant(args[1]));
		var bMs = ToEpochMs(ctx.CoerceInstant(args[2]));

		long? fixedUnit = part.ToLowerInvariant() switch
		{
			"millisecond" => 1L,
			"second" => 1_000L,
			"minute" => 60_000L,
			"hour" => 3_600_000L,
			"day" => 86_400_000L,
			_ => null,
		};
		if (fixedUnit is { } unit)
			return Expr.Subtract(
				Expr.Divide(aMs, Expr.Constant(unit)),
				Expr.Divide(bMs, Expr.Constant(unit)));

		Expr YearOf(Expr ms) => Expr.Call(SqlM(nameof(KqlSqlExpressions.YearOfMs)), ms);
		Expr MonthOf(Expr ms) => Expr.Call(SqlM(nameof(KqlSqlExpressions.MonthOfMs)), ms);
		Expr TotalMonths(Expr ms) => Expr.Add(Expr.Multiply(YearOf(ms), Expr.Constant(12L)), MonthOf(ms));
		Expr Quarters(Expr ms) => Expr.Add(
			Expr.Multiply(YearOf(ms), Expr.Constant(4L)),
			Expr.Divide(Expr.Subtract(MonthOf(ms), Expr.Constant(1L)), Expr.Constant(3L)));

		return part.ToLowerInvariant() switch
		{
			"week" => Expr.Divide(
				Expr.Subtract(
					Expr.Call(SqlM(nameof(KqlSqlExpressions.StartOfWeekMs)), aMs),
					Expr.Call(SqlM(nameof(KqlSqlExpressions.StartOfWeekMs)), bMs)),
				Expr.Constant(604_800_000L)),
			"year" => Expr.Subtract(YearOf(aMs), YearOf(bMs)),
			"month" => Expr.Subtract(TotalMonths(aMs), TotalMonths(bMs)),
			"quarter" => Expr.Subtract(Quarters(aMs), Quarters(bMs)),
			_ => throw new UnsupportedKqlException(
				$"datetime_diff() period '{part}' not supported (year, quarter, month, week, day, hour, minute, second, millisecond)"),
		};
	}

	static Expr ToEpochMs(Expr instant) =>
		instant.Type == typeof(long)
			? instant
			: Expr.Call(SqlM(nameof(KqlSqlExpressions.ToUnixMs)), instant);

	// --- typed conversions: tostring / toint|tolong / todouble / tobool / todatetime. Each yields the
	// target type — nullable for the string-parse path, since Kusto maps a malformed value to null, and
	// that null flows through comparisons as "not matched" (see Compare's liftToNull:false). Numeric
	// inputs convert directly (Expr.Convert → a faithful SQLite CAST); string inputs (the Properties
	// case) route through the registered SQLite parse functions in KqlSqlExpressions. ---

	static Expr ToStringConv(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 1)
			throw new UnsupportedKqlException($"tostring() takes exactly 1 argument, got {args.Count}");
		var a = args[0];
		if (a.Type == typeof(string))
			return a; // already a string (incl. Properties.<key>) — identity
		if (a.Type == typeof(long))
			return Expr.Call(SqlM(nameof(KqlSqlExpressions.LongToString)), a);
		if (a.Type == typeof(int))
			return Expr.Call(SqlM(nameof(KqlSqlExpressions.LongToString)), Expr.Convert(a, typeof(long)));
		if (a.Type == typeof(bool))
			return Expr.Call(SqlM(nameof(KqlSqlExpressions.BoolToString)), a);
		throw new UnsupportedKqlException(
			$"tostring() supports string, integer, and boolean arguments, got {a.Type.Name}");
	}

	static Expr ToLongConv(IReadOnlyList<Expr> args, string fn)
	{
		if (args.Count != 1)
			throw new UnsupportedKqlException($"{fn}() takes exactly 1 argument, got {args.Count}");
		var a = args[0];
		if (a.Type == typeof(string))
			return Expr.Call(SqlM(nameof(KqlSqlExpressions.ParseLong)), a);
		var u = KqlScalar.NonNullable(a.Type);
		if (u == typeof(long) || u == typeof(int) || u == typeof(double))
			return ToNullableNumeric(a, typeof(long));
		throw new UnsupportedKqlException($"{fn}() cannot convert {a.Type.Name} to a long");
	}

	static Expr ToDoubleConv(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 1)
			throw new UnsupportedKqlException($"todouble() takes exactly 1 argument, got {args.Count}");
		var a = args[0];
		if (a.Type == typeof(string))
			return Expr.Call(SqlM(nameof(KqlSqlExpressions.ParseDouble)), a);
		var u = KqlScalar.NonNullable(a.Type);
		if (u == typeof(long) || u == typeof(int) || u == typeof(double))
			return ToNullableNumeric(a, typeof(double));
		throw new UnsupportedKqlException($"todouble() cannot convert {a.Type.Name} to a double");
	}

	static Expr ToBoolConv(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 1)
			throw new UnsupportedKqlException($"tobool() takes exactly 1 argument, got {args.Count}");
		var a = args[0];
		if (a.Type == typeof(string))
			return Expr.Call(SqlM(nameof(KqlSqlExpressions.ParseBool)), a);
		if (KqlScalar.NonNullable(a.Type) == typeof(bool))
			return a.Type == typeof(bool?) ? a : Expr.Convert(a, typeof(bool?));
		throw new UnsupportedKqlException($"tobool() cannot convert {a.Type.Name} to a bool");
	}

	static Expr ToDateTimeConv(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 1)
			throw new UnsupportedKqlException($"todatetime() takes exactly 1 argument, got {args.Count}");
		var a = args[0];
		Expr epochMs;
		if (a.Type == typeof(string))
			epochMs = Expr.Call(SqlM(nameof(KqlSqlExpressions.ParseDateTimeMs)), a);
		else if (a.Type == typeof(long)) // already epoch-ms (the record instant)
			epochMs = Expr.Convert(a, typeof(long?));
		else if (a.Type == typeof(DateTime)) // the row instant
			epochMs = Expr.Convert(Expr.Call(SqlM(nameof(KqlSqlExpressions.ToUnixMs)), a), typeof(long?));
		else
			throw new UnsupportedKqlException($"todatetime() supports a string or datetime argument, got {a.Type.Name}");
		return ctx.NullableInstant(epochMs);
	}

	// Widen a non-null numeric (int/long/double) to a nullable target numeric (long?/double?), going
	// through the underlying type first so the lift is always a legal two-step convert.
	static Expr ToNullableNumeric(Expr e, Type numeric)
	{
		var target = typeof(Nullable<>).MakeGenericType(numeric);
		var num = e.Type == numeric ? e : Expr.Convert(e, numeric);
		return Expr.Convert(num, target);
	}

	static Expr RequireLong(Expr e, string what) => e.Type == typeof(long)
		? e
		: e.Type == typeof(int)
			? Expr.Convert(e, typeof(long))
			: throw new UnsupportedKqlException($"{what} requires an integer, got {e.Type.Name}");

	static MethodInfo SqlM(string name) => typeof(KqlSqlExpressions).GetMethod(name)!;

	static MethodInfo Method(string name) =>
		typeof(KqlScalarFunctions).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

	static DateTime BinDateTime(DateTime v, TimeSpan step)
	{
		if (step.Ticks <= 0)
			throw new UnsupportedKqlException("bin() step must be a positive timespan");
		var floored = v.Ticks / step.Ticks * step.Ticks;
		return new DateTime(floored, v.Kind);
	}

	static long BinLong(long v, long step)
	{
		if (step <= 0)
			throw new UnsupportedKqlException("bin() step must be positive");
		var q = v / step;
		if (v % step != 0 && v < 0)
			q--; // floor toward negative infinity, not truncate toward zero
		return q * step;
	}

	static double BinDouble(double v, double step)
	{
		if (step <= 0)
			throw new UnsupportedKqlException("bin() step must be positive");
		return Math.Floor(v / step) * step;
	}
}
