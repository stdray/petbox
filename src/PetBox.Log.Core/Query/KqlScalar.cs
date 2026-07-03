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
	// Resolve a bare column reference (e.g. Level, Message) to an expression yielding its value.
	public abstract Expr ResolveColumn(string name);

	// Resolve a Properties.<key> reference to an expression yielding the (string) JSON value.
	public abstract Expr ResolveProperties(string key);

	// Fast-path for `column OP literal`: a context may coerce the literal to the column's
	// storage type (datetime -> epoch-ms in the record context) and emit a precise,
	// column-named error. Return null to fall through to the general compile-both-sides path.
	public virtual Expr? TryColumnLiteralComparison(string columnName, Expr columnAccess, LiteralExpression literal, SyntaxKind op) => null;
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
		_ => throw new UnsupportedKqlException($"column '{name}' not supported"),
	};

	public override Expr ResolveProperties(string key)
	{
		var propertiesJson = Expr.Property(row, nameof(LogEntryRecord.PropertiesJson));
		var path = Expr.Constant("$." + key, typeof(string));
		var method = typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.JsonExtract))!;
		return Expr.Call(method, propertiesJson, path);
	}

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
		if (idx < 0)
			throw new UnsupportedKqlException($"unknown column '{name}'");
		return Cell(idx, columns[idx].ClrType);
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
		SyntaxKind.ContainsExpression => StringMatch(b, ctx, caseSensitive: false),
		SyntaxKind.ContainsCsExpression => StringMatch(b, ctx, caseSensitive: true),
		SyntaxKind.HasExpression => StringMatch(b, ctx, caseSensitive: false),
		SyntaxKind.HasCsExpression => StringMatch(b, ctx, caseSensitive: true),
		_ when IsComparison(b.Kind) => Comparison(b, ctx),
		_ => throw new UnsupportedKqlException($"binary '{b.Kind}' not supported"),
	};

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
		return Compare(le, re, b.Kind);
	}

	static Expr Compare(Expr le, Expr re, SyntaxKind kind)
	{
		(le, re) = UnifyComparable(le, re, kind);
		return kind switch
		{
			SyntaxKind.EqualExpression => Expr.Equal(le, re),
			SyntaxKind.NotEqualExpression => Expr.NotEqual(le, re),
			SyntaxKind.LessThanExpression => Expr.LessThan(le, re),
			SyntaxKind.LessThanOrEqualExpression => Expr.LessThanOrEqual(le, re),
			SyntaxKind.GreaterThanExpression => Expr.GreaterThan(le, re),
			SyntaxKind.GreaterThanOrEqualExpression => Expr.GreaterThanOrEqual(le, re),
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
			var eq = Expr.Equal(l, r);
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
		var inRange = Expr.AndAlso(Expr.GreaterThanOrEqual(v1, lo1), Expr.LessThanOrEqual(v2, hi1));
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
	// equal reference/value types. Strings and bools only support equality, not ordering.
	static (Expr, Expr) UnifyComparable(Expr le, Expr re, SyntaxKind kind)
	{
		if (IsNumeric(le.Type) && IsNumeric(re.Type))
		{
			var t = CommonNumeric(le.Type, re.Type);
			return (ConvertTo(le, t), ConvertTo(re, t));
		}
		if (le.Type == re.Type)
		{
			if ((le.Type == typeof(string) || le.Type == typeof(bool)) && !IsEqualityOnly(kind))
				throw new UnsupportedKqlException($"'{le.Type.Name}' operands support only == and !=, not {kind}");
			return (le, re);
		}
		throw new UnsupportedKqlException($"cannot compare {le.Type.Name} with {re.Type.Name}");
	}

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
