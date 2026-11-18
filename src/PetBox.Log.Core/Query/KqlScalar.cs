using System.Globalization;
using System.Reflection;
using Kusto.Language.Syntax;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Metrics;
using PetBox.Log.Core.Tracing;
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

	// Bridge a ticks-`long` DURATION into this context's duration representation: ticks (long) in the
	// SQL/record domain, TimeSpan in the in-memory/row domain — the timespan mirror of NullableInstant, and
	// the reason dayofweek()/make_timespan() can return a real Kusto `timespan` on both pipeline positions.
	// The SQL contexts keep the long (that IS the storage form of TimeSpan, see KqlTransformer's
	// logical-type ↔ storage-type mapping), so only the row domain overrides.
	public virtual Expr CoerceTicks(Expr ticks) => ticks;
}

// Shared machinery for the SQL/record contexts of the two table roots (events / spans). Leaves resolve
// to record property accesses, so the compiled tree is SQL-translatable by linq2db; instants are
// epoch-ms (long). A derived context supplies only what differs per root: the user-facing column list,
// each canonical column's access expression, the stored JSON bag (PropertiesJson / AttributesJson), and
// the literal coercion for its instant/duration columns. Everything else — bare-name resolution (exact,
// else a UNIQUE case-insensitive match, else the Properties fallback), the column-literal comparison
// fast path, and the instant plumbing — lands here ONCE for both roots.
abstract class SqlRecordScalarContext : ScalarContext
{
	// The user-facing scalar columns. Includes "PropertiesJson" (the raw JSON bag): the post-split
	// streamed shape exposes it as a real column on both roots, so the pre-split contexts must bind
	// the same name to the same value — otherwise the bare name silently falls back to a
	// Properties["PropertiesJson"] lookup (NULL) and a supported query returns empty instead of rows.
	protected abstract IReadOnlyList<string> KnownColumns { get; }

	// The access expression for a CANONICAL column name (an element of KnownColumns).
	protected abstract Expr KnownColumnAccess(string canonical);

	// The stored JSON bag backing Properties.<key> lookups.
	protected abstract Expr JsonBag { get; }

	// Literal coercion for the root's special (instant / duration) columns, by CANONICAL name.
	// Return null to fall through to the shared integer/string tail.
	protected abstract Expr? CoerceSpecialLiteral(string canonicalColumn, LiteralExpression literal);

	public override Expr ResolveColumn(string name)
	{
		var canonical = ResolveKnownName(name);
		if (IsKnownColumn(canonical))
			return KnownColumnAccess(canonical);
		// Bare-name fallback: a name that is not a known column resolves as a Properties.<name> lookup
		// (string-typed json_extract), so `where DeviceId == 'x'` filters on the property. Known columns
		// always win (matched above). Trade-off: a TRUE typo'd column (no case-insensitive match) yields
		// empty results rather than an error — accepted cost of the dynamic bag — but comparing a
		// fallback (string) name to a non-string literal still raises a precise type error.
		return ResolveProperties(canonical);
	}

	// Membership in the root's KnownColumns (exact/Ordinal) — the ONE scan shared by bare-name
	// resolution, canonicalization, and the bag-vs-column split in the literal coercion below.
	protected bool IsKnownColumn(string canonical)
	{
		foreach (var c in KnownColumns)
			if (string.Equals(c, canonical, StringComparison.Ordinal))
				return true;
		return false;
	}

	// Canonical column name for a bare reference: exact match wins; else a unique case-insensitive
	// match (so prod field-casing like `level` / `start` binds to the real column); else the original
	// name (→ the Properties fallback).
	protected string ResolveKnownName(string name)
	{
		if (IsKnownColumn(name))
			return name;
		string? ci = null;
		foreach (var c in KnownColumns)
			if (string.Equals(c, name, StringComparison.OrdinalIgnoreCase))
			{
				if (ci is not null)
					return name; // ambiguous (cannot happen for the fixed schemas) → Properties
				ci = c;
			}
		return ci ?? name;
	}

	// All Properties access — bare-name fallback, Properties.<key>, Properties["key"] — is a FLAT
	// lookup: the requested key is NORMALIZED here (one of the two search-boundary seam owners, see
	// KqlPropertyKeys; KqlTransformer.BagValueExtractor is the other) and embedded as a double-QUOTED
	// JSON-path label, so SQLite's BUILTIN json_extract resolves dotted OTLP keys
	// (petbox.request_chars) natively — dots inside a quoted label are literal key characters. One
	// representation everywhere: the string-typed JSON value (string → its text, number → SQLite's
	// canonical text form, bool → 'true'/'false') or null when missing.
	public override Expr ResolveProperties(string key)
	{
		var method = typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.JsonExtract))!;
		var path = KqlSqlExpressions.JsonPath(KqlPropertyKeys.Normalize(key));
		return Expr.Call(method, JsonBag, Expr.Constant(path, typeof(string)));
	}

	// now() in the SQL context is a constant epoch-ms long, so `where Timestamp > ago(1h)` compares
	// long-to-long and translates to SQLite as a literal.
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
		// The fast path builds a DIRECT Expr.Equal/GreaterThan over the column access, which requires both
		// operands share the type and yields a non-null bool. A NULLABLE value-type column (metrics' wide
		// optional scalars — Value/Count/Sum/StartTime/…) satisfies neither: the operand types wouldn't
		// match a plain long/int/double literal, and the result would be a bool? the pre-split `where`
		// rejects. Fall through so the general comparison path unifies nullability and null-guards to a
		// bool (Kusto null-excluding semantics). Non-nullable columns (events/spans, and metrics' int/
		// string columns) keep the fast path unchanged.
		if (KqlScalar.IsNullable(access.Type))
			return null;
		// Coerce against the RESOLVED canonical name: the user may have typed `start` / `timestamp`
		// (bound case-insensitively by ResolveColumn), and the special-column check must agree with
		// that binding, not with the raw spelling.
		var canonical = ResolveKnownName(columnName);
		// A null coercion is the SECOND yield (like the nullable-column one above): the literal is valid but
		// its type does not match the column's storage type, so only the general path can compare them.
		if (CoerceLiteral(literal, access.Type, canonical, isBagValue: !IsKnownColumn(canonical)) is not { } coerced)
			return null;
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

	// Root-specific instant/duration columns first (datetime → epoch-ms, timespan → ticks); integer /
	// string columns require the matching literal kind. Preserves the exact user-facing messages the
	// `where` path has always produced. Returns null to YIELD the fast path (see TryColumnLiteralComparison):
	// the literal is comparable, just not in the column's storage type.
	Expr? CoerceLiteral(LiteralExpression literal, Type targetType, string column, bool isBagValue)
	{
		if (CoerceSpecialLiteral(column, literal) is { } special)
			return special;

		if (targetType == typeof(long) || targetType == typeof(int))
		{
			// An integer column against a REAL literal (`Id > 0.5`) is a legal Kusto comparison — numeric, with
			// both sides promoted to double. The fast path cannot express it (a direct Expr.GreaterThan needs
			// matching operand types), so it yields exactly like it does for a nullable column above, and the
			// general Compare/UnifyComparable path promotes and compares. Only a truly incomparable literal
			// (string/bool/…) still raises the precise column-named error below.
			if (literal.LiteralValue is double or decimal)
				return null;
			if (literal.LiteralValue is not long n)
				throw new UnsupportedKqlException($"{column} comparison requires an integer literal");
			if (targetType == typeof(int))
			{
				// An out-of-range literal is a user error, not an engine fault: teach, don't overflow.
				if (n is < int.MinValue or > int.MaxValue)
					throw new UnsupportedKqlException($"{column} literal {n} is out of range for an int column");
				return Expr.Constant((int)n);
			}
			return Expr.Constant(n);
		}

		if (targetType == typeof(string))
		{
			if (literal.LiteralValue is string s)
				return Expr.Constant(s, typeof(string));
			// A bag value compared against a non-string literal: teach BOTH working forms. The string
			// form is honest since bag lookups yield text identically pre- and post-split (see
			// KqlSqlExpressions.JsonExtract's CASE/CAST shape); toint()/todouble() give true numeric
			// comparison. Explicit conversions stay the convention — a silent literal-to-text coercion
			// would false-negative on equivalent spellings ("200" vs 200.0).
			throw new UnsupportedKqlException(isBagValue
				? $"{column} is a Properties value (text); compare against a string literal, or wrap it in toint()/todouble() for a numeric comparison"
				: $"{column} comparison requires a string literal");
		}

		throw new UnsupportedKqlException($"cannot coerce literal for column '{column}' of type {targetType.Name}");
	}
}

// Leaves resolve to LogEntryRecord property accesses → the tree is SQL-translatable.
sealed class RecordScalarContext(ParamExpr row) : SqlRecordScalarContext
{
	static readonly string[] Columns =
		["Id", "Level", "LevelName", "Timestamp", "ServiceKey", "Message", "MessageTemplate", "Exception", "PropertiesJson"];

	protected override IReadOnlyList<string> KnownColumns => Columns;

	protected override Expr JsonBag => Expr.Property(row, nameof(LogEntryRecord.PropertiesJson));

	protected override Expr KnownColumnAccess(string canonical) => canonical switch
	{
		"Id" => Expr.Property(row, nameof(LogEntryRecord.Id)),
		"Level" => Expr.Property(row, nameof(LogEntryRecord.Level)),
		// A computed name column; the CASE-mapped KqlSqlExpressions.LevelName keeps it SQL-translatable
		// in a pre-split where/order (a raw LogLevelNames.ToName call has no linq2db translation and
		// failed at enumeration on a real log DB).
		"LevelName" => Expr.Call(
			typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.LevelName))!,
			Expr.Property(row, nameof(LogEntryRecord.Level))),
		"Timestamp" => Expr.Property(row, nameof(LogEntryRecord.TimestampMs)),
		"ServiceKey" => Expr.Property(row, nameof(LogEntryRecord.ServiceKey)),
		"Message" => Expr.Property(row, nameof(LogEntryRecord.Message)),
		"MessageTemplate" => Expr.Property(row, nameof(LogEntryRecord.MessageTemplate)),
		"Exception" => Expr.Property(row, nameof(LogEntryRecord.Exception)),
		"PropertiesJson" => Expr.Property(row, nameof(LogEntryRecord.PropertiesJson)),
		_ => throw new UnsupportedKqlException($"unknown column '{canonical}'"), // unreachable
	};

	// Timestamp is stored as epoch-ms, so a datetime() literal is converted to ms.
	protected override Expr? CoerceSpecialLiteral(string canonical, LiteralExpression literal)
	{
		if (canonical != "Timestamp")
			return null;
		if (literal.LiteralValue is not DateTime dt)
			throw new UnsupportedKqlException("Timestamp comparison requires a datetime() literal");
		return Expr.Constant(KqlSqlExpressions.ToUnixMs(dt));
	}
}

// Leaves resolve to SpanRecord property accesses → the tree is SQL-translatable. The `spans` root's
// counterpart to RecordScalarContext: instants stay epoch-ms (long) in the SQL domain, matching events'
// Timestamp convention, so the whole datetime machinery (CoerceInstant / now / ago / startof*) is reused
// unchanged. Start/End are derived from the stored unix-ns columns; Duration is a computed tick count;
// Kind/Status expose a name form (KindName/StatusName) like Level/LevelName; attributes resolve through
// AttributesJson (the PropertiesJson analog).
sealed class SpanRecordScalarContext(ParamExpr row) : SqlRecordScalarContext
{
	static readonly string[] Columns =
	[
		"SpanId", "TraceId", "ParentSpanId", "Name", "Kind", "KindName",
		"Start", "End", "Duration", "Status", "StatusName", "StatusDescription", "PropertiesJson",
	];

	protected override IReadOnlyList<string> KnownColumns => Columns;

	protected override Expr JsonBag => Expr.Property(row, nameof(SpanRecord.AttributesJson));

	protected override Expr KnownColumnAccess(string canonical) => canonical switch
	{
		"SpanId" => Expr.Property(row, nameof(SpanRecord.SpanId)),
		"TraceId" => Expr.Property(row, nameof(SpanRecord.TraceId)),
		"ParentSpanId" => Expr.Property(row, nameof(SpanRecord.ParentSpanId)),
		"Name" => Expr.Property(row, nameof(SpanRecord.Name)),
		"Kind" => Expr.Property(row, nameof(SpanRecord.Kind)),
		// Computed name columns via the CASE-mapped KqlSqlExpressions helpers — SQL-translatable in a
		// pre-split where/order, exactly like events' LevelName.
		"KindName" => Expr.Call(
			typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.SpanKindName))!,
			Expr.Property(row, nameof(SpanRecord.Kind))),
		// Start/End are stored as unix-ns; the SQL instant domain is epoch-ms (long), matching events'
		// TimestampMs (epoch-ms = unix-ns / 1_000_000, integer division → ms precision).
		"Start" => EpochMs(nameof(SpanRecord.StartUnixNs)),
		"End" => EpochMs(nameof(SpanRecord.EndUnixNs)),
		// Duration is a tick count (100ns units) = (End - Start) unix-ns / 100, so a timespan literal
		// compares via its .Ticks.
		"Duration" => Expr.Divide(
			Expr.Subtract(Expr.Property(row, nameof(SpanRecord.EndUnixNs)), Expr.Property(row, nameof(SpanRecord.StartUnixNs))),
			Expr.Constant(100L)),
		"Status" => Expr.Property(row, nameof(SpanRecord.StatusCode)),
		"StatusName" => Expr.Call(
			typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.SpanStatusName))!,
			Expr.Property(row, nameof(SpanRecord.StatusCode))),
		"StatusDescription" => Expr.Property(row, nameof(SpanRecord.StatusDescription)),
		// The streamed span row names its JSON bag "PropertiesJson" (carrying AttributesJson, see
		// LogSchema.SpanRecordColumns) — the pre-split binding must agree.
		"PropertiesJson" => Expr.Property(row, nameof(SpanRecord.AttributesJson)),
		_ => throw new UnsupportedKqlException($"unknown column '{canonical}'"), // unreachable
	};

	Expr EpochMs(string nsProperty) =>
		Expr.Divide(Expr.Property(row, nsProperty), Expr.Constant(1_000_000L));

	// Start/End compare against a datetime() literal (→ epoch-ms); Duration against a timespan literal
	// (→ ticks).
	protected override Expr? CoerceSpecialLiteral(string canonical, LiteralExpression literal)
	{
		if (canonical is "Start" or "End")
		{
			if (literal.LiteralValue is not DateTime dt)
				throw new UnsupportedKqlException($"{canonical} comparison requires a datetime() literal");
			return Expr.Constant(KqlSqlExpressions.ToUnixMs(dt));
		}
		if (canonical == "Duration")
		{
			if (literal.LiteralValue is not TimeSpan ts)
				throw new UnsupportedKqlException("Duration comparison requires a timespan literal");
			return Expr.Constant(ts.Ticks);
		}
		return null;
	}
}

// Leaves resolve to MetricPointRecord property accesses → the tree is SQL-translatable. The `metrics`
// root's counterpart to RecordScalarContext/SpanRecordScalarContext: instants stay epoch-ms (long) in
// the SQL domain (Time / StartTime derived from the stored unix-ns columns, matching events' Timestamp
// convention). MetricType exposes a name form (TypeName) like Level/LevelName; Value is the unified
// COALESCE(ValueDouble, ValueLong); the wide optional scalars stay nullable (a null-capable metric
// column is filtered via the general nullable-comparison path, not the fast path). Attributes resolve
// through AttributesJson (the PropertiesJson analog).
sealed class MetricRecordScalarContext(ParamExpr row) : SqlRecordScalarContext
{
	static readonly string[] Columns =
	[
		"MetricName", "MetricType", "TypeName", "Unit", "Description", "Time", "StartTime",
		"Value", "ValueDouble", "ValueLong", "Count", "Sum", "Min", "Max",
		"Temporality", "IsMonotonic", "Scale", "ZeroCount", "Flags", "PropertiesJson",
	];

	protected override IReadOnlyList<string> KnownColumns => Columns;

	protected override Expr JsonBag => Expr.Property(row, nameof(MetricPointRecord.AttributesJson));

	protected override Expr KnownColumnAccess(string canonical) => canonical switch
	{
		"MetricName" => Expr.Property(row, nameof(MetricPointRecord.MetricName)),
		"MetricType" => Expr.Property(row, nameof(MetricPointRecord.MetricType)),
		// Computed name column via the CASE-mapped KqlSqlExpressions helper — SQL-translatable in a
		// pre-split where/order, exactly like events' LevelName and spans' KindName.
		"TypeName" => Expr.Call(
			typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.MetricTypeName))!,
			Expr.Property(row, nameof(MetricPointRecord.MetricType))),
		"Unit" => Expr.Property(row, nameof(MetricPointRecord.Unit)),
		"Description" => Expr.Property(row, nameof(MetricPointRecord.Description)),
		// Time / StartTime are stored as unix-ns; the SQL instant domain is epoch-ms (long), matching
		// events' TimestampMs (epoch-ms = unix-ns / 1_000_000, integer division → ms precision).
		// StartTime is nullable (StartUnixNs is optional), so its epoch-ms form is long?.
		"Time" => Expr.Divide(Expr.Property(row, nameof(MetricPointRecord.TimeUnixNs)), Expr.Constant(1_000_000L)),
		"StartTime" => Expr.Divide(
			Expr.Property(row, nameof(MetricPointRecord.StartUnixNs)), Expr.Constant((long?)1_000_000L, typeof(long?))),
		// The unified value: COALESCE(ValueDouble, ValueLong) as a nullable double.
		"Value" => Expr.Call(
			typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.MetricValue))!,
			Expr.Property(row, nameof(MetricPointRecord.ValueDouble)),
			Expr.Property(row, nameof(MetricPointRecord.ValueLong))),
		"ValueDouble" => Expr.Property(row, nameof(MetricPointRecord.ValueDouble)),
		"ValueLong" => Expr.Property(row, nameof(MetricPointRecord.ValueLong)),
		"Count" => Expr.Property(row, nameof(MetricPointRecord.Count)),
		"Sum" => Expr.Property(row, nameof(MetricPointRecord.Sum)),
		"Min" => Expr.Property(row, nameof(MetricPointRecord.Min)),
		"Max" => Expr.Property(row, nameof(MetricPointRecord.Max)),
		"Temporality" => Expr.Property(row, nameof(MetricPointRecord.AggregationTemporality)),
		"IsMonotonic" => Expr.Property(row, nameof(MetricPointRecord.IsMonotonic)),
		"Scale" => Expr.Property(row, nameof(MetricPointRecord.Scale)),
		"ZeroCount" => Expr.Property(row, nameof(MetricPointRecord.ZeroCount)),
		"Flags" => Expr.Property(row, nameof(MetricPointRecord.Flags)),
		// The streamed metric row names its JSON bag "PropertiesJson" (carrying AttributesJson, see
		// KqlTransformer.MetricPointRecordColumns) — the pre-split binding must agree.
		"PropertiesJson" => Expr.Property(row, nameof(MetricPointRecord.AttributesJson)),
		_ => throw new UnsupportedKqlException($"unknown column '{canonical}'"), // unreachable
	};

	// Time / StartTime compare against a datetime() literal (→ epoch-ms). Time is non-nullable so it uses
	// the fast path (this coercion); StartTime is nullable so it falls through to the general path, where
	// the datetime literal is normalized to epoch-ms instead — this arm is kept for parity/robustness.
	protected override Expr? CoerceSpecialLiteral(string canonical, LiteralExpression literal)
	{
		if (canonical is "Time" or "StartTime")
		{
			if (literal.LiteralValue is not DateTime dt)
				throw new UnsupportedKqlException($"{canonical} comparison requires a datetime() literal");
			return Expr.Constant(KqlSqlExpressions.ToUnixMs(dt));
		}
		return null;
	}
}

// Leaves index into a materialized object?[] row → the tree is evaluated in-memory.
sealed class RowScalarContext(ParamExpr rowArray, IReadOnlyList<KqlColumn> columns) : ScalarContext
{
	public override Expr ResolveColumn(string name)
	{
		// Exact match, else a unique case-insensitive match to a real column (prod field-casing).
		var idx = KqlTransformer.ResolveColumnIndexCI(columns, name);
		if (idx >= 0)
			return Cell(idx, columns[idx].ClrType);
		// Bare-name fallback (post-split): an unknown name is a Properties.<name> lookup, but only when
		// the current row shape still carries PropertiesJson. After a project/summarize that dropped it,
		// there is nothing to look up, so the precise "unknown column" error stands.
		if (IndexOf(nameof(LogEntryRecord.PropertiesJson)) < 0)
			throw new UnsupportedKqlException($"unknown column '{name}'");
		return ResolveProperties(name);
	}

	// Flat lookup under the NORMALIZED key over the row's PropertiesJson cell — the same JsonGet body
	// the SQL path's quoted-label json_extract mirrors (see SqlRecordScalarContext.ResolveProperties),
	// so pipeline position is neutral.
	public override Expr ResolveProperties(string key)
	{
		var idx = IndexOf(nameof(LogEntryRecord.PropertiesJson));
		if (idx < 0)
			throw new UnsupportedKqlException("Properties.<key>: input has no PropertiesJson column");
		var jsonCell = Cell(idx, typeof(string));
		var method = typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.JsonGet))!;
		return Expr.Call(method, jsonCell, Expr.Constant(KqlPropertyKeys.Normalize(key), typeof(string)));
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

	// In-memory durations are TimeSpan (the logical type a timespan column carries here), so a ticks-long
	// duration converts once, at the same boundary FromUnixMs crosses for instants.
	public override Expr CoerceTicks(Expr ticks) =>
		Expr.Call(
			typeof(KqlSqlExpressions).GetMethod(nameof(KqlSqlExpressions.FromTicksN))!,
			ticks.Type == typeof(long?) ? ticks : Expr.Convert(ticks, typeof(long?)));
}

// A layered scalar context for compiling project/extend output expressions in the SQL/record domain
// (kql-single-path-impl). A bare name resolves FIRST through `lookup` (an earlier same-stage computed
// column — sequential refs like `extend A = …, B = f(A)`), else through the base record context. Instants,
// Properties and now() delegate to the base, so the storage representation is identical to a pre-split
// where (epoch-ms long for datetime). TryColumnLiteralComparison is intentionally NOT overridden — it
// inherits the null default, matching RowScalarContext, so a computed comparison takes the general
// compile-both-sides path on either pipeline position.
sealed class DerivedColumnContext(ScalarContext baseCtx, Func<string, Expr?> lookup) : ScalarContext
{
	public override Expr ResolveColumn(string name) => lookup(name) ?? baseCtx.ResolveColumn(name);
	public override Expr ResolveProperties(string key) => baseCtx.ResolveProperties(key);
	public override Expr CurrentInstant() => baseCtx.CurrentInstant();
	public override Expr CoerceInstant(Expr e) => baseCtx.CoerceInstant(e);
	public override Expr NullableInstant(Expr epochMsNullable) => baseCtx.NullableInstant(epochMsNullable);
	public override Expr CoerceTicks(Expr ticks) => baseCtx.CoerceTicks(ticks);
}

// One column of an emitted storage-typed row: its logical name/type (what the user sees) and the emitted
// property holding its STORAGE value (epoch-ms long for datetime, ticks long for timespan, else logical).
readonly record struct EmittedCol(string Name, Type LogicalType, string StorageProp);

// A scalar context over an EMITTED storage-typed row — the output of an upstream SQL project/extend/
// distinct — so a downstream stage (order/take/top, or a further project/extend) resolves the columns the
// upstream produced (kql-single-path-impl composable state). The storage domain matches the record
// contexts (epoch-ms long for datetime, ticks long for timespan), so a compiled tree stays SQL-translatable
// and internally consistent across chained stages; the single storage→logical conversion still happens
// once at the final materialization.
sealed class EmittedStorageScalarContext : SqlRecordScalarContext
{
	readonly ParamExpr _row;
	readonly IReadOnlyList<EmittedCol> _schema;
	readonly string[] _names;

	public EmittedStorageScalarContext(ParamExpr row, IReadOnlyList<EmittedCol> schema)
	{
		_row = row;
		_schema = schema;
		_names = schema.Select(s => s.Name).ToArray();
	}

	EmittedCol Col(string canonical) => _schema.First(s => string.Equals(s.Name, canonical, StringComparison.Ordinal));
	bool HasBag => _schema.Any(s => string.Equals(s.Name, nameof(LogEntryRecord.PropertiesJson), StringComparison.Ordinal));

	protected override IReadOnlyList<string> KnownColumns => _names;
	protected override Expr KnownColumnAccess(string canonical) => Expr.Property(_row, Col(canonical).StorageProp);

	protected override Expr JsonBag => HasBag
		? Expr.Property(_row, Col(nameof(LogEntryRecord.PropertiesJson)).StorageProp)
		: throw new UnsupportedKqlException("Properties.<key>: input has no PropertiesJson column");

	// Mirror RowScalarContext's post-split semantics: an unknown name is a Properties fallback only while
	// the shape still carries PropertiesJson; otherwise it is the precise "unknown column" error (not the
	// bag-missing one), so error parity holds against the in-memory path after a project dropped the bag.
	public override Expr ResolveColumn(string name)
	{
		var canonical = ResolveKnownName(name);
		if (IsKnownColumn(canonical))
			return KnownColumnAccess(canonical);
		if (!HasBag)
			throw new UnsupportedKqlException($"unknown column '{name}'");
		return ResolveProperties(canonical);
	}

	protected override Expr? CoerceSpecialLiteral(string canonical, LiteralExpression literal)
	{
		// A bag value (Properties.<key> in a post-shape `where`) reaches here with `canonical` = the bag KEY,
		// which is NOT an emitted column — so there is no special datetime/timespan coercion (exactly as the
		// record context only special-cases its known Timestamp column, and returns null otherwise). Without
		// this guard Col(canonical) throws "sequence contains no matching element" and the bag comparison —
		// which must be byte-identical to the pre-split path — crashes instead of falling through to the
		// general string/int literal handling.
		if (!IsKnownColumn(canonical))
			return null;
		var nn = KqlScalar.NonNullable(Col(canonical).LogicalType);
		if (nn == typeof(DateTime))
		{
			if (literal.LiteralValue is not DateTime dt)
				throw new UnsupportedKqlException($"{canonical} comparison requires a datetime() literal");
			return Expr.Constant(KqlSqlExpressions.ToUnixMs(dt));
		}
		if (nn == typeof(TimeSpan))
		{
			if (literal.LiteralValue is not TimeSpan ts)
				throw new UnsupportedKqlException($"{canonical} comparison requires a timespan literal");
			return Expr.Constant(ts.Ticks);
		}
		return null;
	}
}

static class KqlScalar
{
	// --- THE CAPABILITY MANIFEST: what this engine can compile. ---
	// KqlCompletionService answers "may I offer this?" from here instead of from its own list, so the
	// completer CANNOT drift from the compiler: the names below are the DisplayTexts Kusto.Language's
	// ScalarInfix completion emits, and every one of them has a live arm in Binary()/In()/Between().
	// Adding an operator arm means adding its name; a name without an arm is caught by the round-trip
	// test (KqlOperatorCapabilityTests) that compiles every entry through the production path.
	// Deliberately ABSENT: `like`/`!like`, `has_ipv4*` and the rest of the unsupported operator surface —
	// they are filtered OUT of the completer rather than implemented.
	public static readonly IReadOnlySet<string> SupportedInfixOperators = new HashSet<string>(StringComparer.Ordinal)
	{
		// logical
		"and", "or",
		// arithmetic (Arithmetic)
		"+", "-", "*", "/", "%",
		// comparison (IsComparison)
		"==", "!=", "<", "<=", ">", ">=",
		"=~", "!~",
		// membership / ranges
		"in", "!in", "in~", "!in~", "between", "!between",
		// substring predicates
		"contains", "!contains", "contains_cs", "!contains_cs",
		"startswith", "!startswith", "startswith_cs", "!startswith_cs",
		"endswith", "!endswith", "endswith_cs", "!endswith_cs",
		// term predicates (boundary regexes, see HasFn/HasAffix/HasList)
		"has", "!has", "has_cs", "!has_cs",
		"hasprefix", "!hasprefix", "hasprefix_cs", "!hasprefix_cs",
		"hassuffix", "!hassuffix", "hassuffix_cs", "!hassuffix_cs",
		"has_any", "has_all",
		"matches regex",
	};

	public static bool CanCompileOperator(string displayText) => SupportedInfixOperators.Contains(displayText);

	// ScalarPrefix completion items (`datetime()`, `toscalar`, …) are two different animals, so this set is
	// NOT empty — it is exactly the typed-LITERAL forms. `datetime(…)`, `timespan(…)`, `int/long/real/…(…)`
	// are parsed into a LiteralExpression and compiled by Literal(), so they work as offered. The rest reach
	// Compile as node kinds the engine does not model at all: `dynamic()` → DynamicExpression, `toscalar` →
	// ToScalarExpression, `guid()` → a Guid literal (no dynamic values, no to-scalar subquery, no guid type),
	// and every one of them throws. Bare `dynamic` (a type name, not a call) goes with them — same missing
	// dynamic model. Measured, not assumed: each form was run through the production path.
	public static readonly IReadOnlySet<string> SupportedScalarPrefixes = new HashSet<string>(StringComparer.Ordinal)
	{
		"datetime()", "timespan()", "int()", "long()", "real()", "decimal()", "double()",
	};

	public static bool CanCompileScalarPrefix(string displayText) => SupportedScalarPrefixes.Contains(displayText);

	// The aggregate functions the compiler implements. The list itself lives NEXT TO the aggregate switch it
	// describes (KqlTransformer.SupportedAggregates, whose default arm quotes it) — this is only the
	// completer-facing question, answered from that one home so the two cannot disagree.
	public static bool CanCompileAggregate(string displayText) =>
		KqlTransformer.SupportedAggregates.Contains(FunctionName(displayText), StringComparer.Ordinal);

	// A completer DP: `name(args…)` ("tostring(value)", "now([offset])"); the engine's answer is the
	// registry's, matched on the name up to the parenthesis.
	public static bool CanCompileFunction(string displayText) =>
		KqlScalarFunctions.TryResolve(FunctionName(displayText), out _);

	static string FunctionName(string displayText)
	{
		var paren = displayText.IndexOf('(');
		return paren > 0 ? displayText[..paren] : displayText;
	}

	public static Expr Compile(Expression node, ScalarContext ctx) => node switch
	{
		LiteralExpression lit => Literal(lit),
		NameReference n => ctx.ResolveColumn(n.SimpleName),
		PathExpression p when KqlTransformer.IsPropertiesPath(p, out var key) => ctx.ResolveProperties(key),
		// Properties["any.key"] — the canonical form for dotted OTLP attribute keys (bracket indexing
		// with a string-literal selector; a computed selector throws precisely inside the recognizer).
		ElementExpression el when KqlTransformer.IsPropertiesIndex(el, out var key) => ctx.ResolveProperties(key),
		ParenthesizedExpression paren => Compile(paren.Expression, ctx),
		PrefixUnaryExpression u => Unary(u, ctx),
		BinaryExpression b => Binary(b, ctx),
		InExpression inx => In(inx, ctx),
		BetweenExpression btw => Between(btw, ctx),
		// has_any/has_all are their OWN node types (Left + a parenthesized ExpressionList), not a
		// BinaryExpression — see HasList.
		HasAnyExpression any => HasList(any.Left, any.Right, ctx, "has_any", all: false),
		HasAllExpression all => HasList(all.Left, all.Right, ctx, "has_all", all: true),
		FunctionCallExpression call => Call(call, ctx),
		_ => throw new UnsupportedKqlException($"expression '{node.Kind}' not supported"),
	};

	static Expr Literal(LiteralExpression lit) => lit.LiteralValue switch
	{
		long l => Expr.Constant(l),
		int i => Expr.Constant((long)i),
		double d => FiniteReal(d),
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
		// contains = substring; has = whole-term (lowered to a boundary regex, see HasFn). startswith/
		// endswith and the case-sensitive _cs variants translate to fixed-length substring compares.
		// The `!`-prefixed spellings are SEPARATE SyntaxKinds in Kusto.Language (`!contains` is
		// NotContainsExpression, not a negation node), so every positive arm needs its negated twin
		// listed here — an omission is not a wrong answer, it is `binary '<Kind>' not supported`.
		SyntaxKind.ContainsExpression => StringMatch(b, ctx, caseSensitive: false),
		SyntaxKind.NotContainsExpression => StringMatch(b, ctx, caseSensitive: false, negate: true),
		SyntaxKind.ContainsCsExpression => StringMatch(b, ctx, caseSensitive: true),
		SyntaxKind.NotContainsCsExpression => StringMatch(b, ctx, caseSensitive: true, negate: true),
		SyntaxKind.StartsWithExpression => StringFn(b, ctx, "startswith", nameof(KqlSqlExpressions.StartsWithI)),
		SyntaxKind.NotStartsWithExpression => StringFn(b, ctx, "!startswith", nameof(KqlSqlExpressions.StartsWithI), negate: true),
		SyntaxKind.StartsWithCsExpression => StringFn(b, ctx, "startswith_cs", nameof(KqlSqlExpressions.StartsWithCs)),
		SyntaxKind.NotStartsWithCsExpression => StringFn(b, ctx, "!startswith_cs", nameof(KqlSqlExpressions.StartsWithCs), negate: true),
		SyntaxKind.EndsWithExpression => StringFn(b, ctx, "endswith", nameof(KqlSqlExpressions.EndsWithI)),
		SyntaxKind.NotEndsWithExpression => StringFn(b, ctx, "!endswith", nameof(KqlSqlExpressions.EndsWithI), negate: true),
		SyntaxKind.EndsWithCsExpression => StringFn(b, ctx, "endswith_cs", nameof(KqlSqlExpressions.EndsWithCs)),
		SyntaxKind.NotEndsWithCsExpression => StringFn(b, ctx, "!endswith_cs", nameof(KqlSqlExpressions.EndsWithCs), negate: true),
		SyntaxKind.HasExpression => HasFn(b, ctx, "has", caseInsensitive: true),
		SyntaxKind.NotHasExpression => HasFn(b, ctx, "!has", caseInsensitive: true, negate: true),
		SyntaxKind.HasCsExpression => HasFn(b, ctx, "has_cs", caseInsensitive: false),
		SyntaxKind.NotHasCsExpression => HasFn(b, ctx, "!has_cs", caseInsensitive: false, negate: true),
		SyntaxKind.HasPrefixExpression => HasAffix(b, ctx, "hasprefix", caseInsensitive: true, prefix: true),
		SyntaxKind.NotHasPrefixExpression => HasAffix(b, ctx, "!hasprefix", caseInsensitive: true, prefix: true, negate: true),
		SyntaxKind.HasPrefixCsExpression => HasAffix(b, ctx, "hasprefix_cs", caseInsensitive: false, prefix: true),
		SyntaxKind.NotHasPrefixCsExpression => HasAffix(b, ctx, "!hasprefix_cs", caseInsensitive: false, prefix: true, negate: true),
		SyntaxKind.HasSuffixExpression => HasAffix(b, ctx, "hassuffix", caseInsensitive: true, prefix: false),
		SyntaxKind.NotHasSuffixExpression => HasAffix(b, ctx, "!hassuffix", caseInsensitive: true, prefix: false, negate: true),
		SyntaxKind.HasSuffixCsExpression => HasAffix(b, ctx, "hassuffix_cs", caseInsensitive: false, prefix: false),
		SyntaxKind.NotHasSuffixCsExpression => HasAffix(b, ctx, "!hassuffix_cs", caseInsensitive: false, prefix: false, negate: true),
		SyntaxKind.MatchesRegexExpression => StringFn(b, ctx, "matches regex", nameof(KqlSqlExpressions.MatchesRegex)),
		// `=~` / `!~` are string EQUALITY, not a comparison of the `==` family — see EqualsTilde.
		SyntaxKind.EqualTildeExpression => EqualsTilde(b, ctx, negate: false),
		SyntaxKind.BangTildeExpression => EqualsTilde(b, ctx, negate: true),
		_ when IsComparison(b.Kind) => Comparison(b, ctx),
		_ => throw new UnsupportedKqlException($"binary '{b.Kind}' not supported"),
	};

	// A `column <op> 'literal'` string predicate backed by a KqlSqlExpressions method (which carries
	// both the SQLite translation and the in-memory body). Left must be string-typed, right a string
	// literal (KQL requires a constant term/pattern for these operators).
	static Expr StringFn(BinaryExpression b, ScalarContext ctx, string opName, string method, bool negate = false)
	{
		var access = Compile(b.Left, ctx);
		if (access.Type != typeof(string))
			throw new UnsupportedKqlException($"'{opName}' requires a string operand on the left, got {access.Type.Name}");
		if (b.Right is not LiteralExpression { LiteralValue: string needle })
			throw new UnsupportedKqlException($"'{opName}' requires a string literal on the right");
		var mi = typeof(KqlSqlExpressions).GetMethod(method)!;
		var call = Expr.Call(mi, access, Expr.Constant(needle));
		return negate ? Negated(call, access) : call;
	}

	// `has` / `has_cs`: NO dedicated function — lower to a boundary regex over the SAME regexp shim
	// `matches regex` uses (KqlSqlExpressions.MatchesRegex), so it rides that native per-dialect SQL on
	// both dialects. The term is a required string literal, so the whole pattern is built at COMPILE time.
	// TermStart/TermEnd reproduce KQL's term-char rule (letters/digits are term chars, everything else —
	// INCLUDING underscore — is a delimiter, which is why a plain `\b` would be wrong); `(?i)` gives `has`
	// its case-insensitivity, `has_cs` omits it. An empty term matches nothing (Kusto semantics).
	//
	// DO NOT "FIX" THIS TO BE STRICT ABOUT THE NEEDLE. Kusto's "RHS is a whole term in LHS" is a SIMPLIFICATION
	// of the implementation, which is an index PRE-FILTER followed by an EXACT LITERAL SCAN: the term index
	// narrows the candidate rows by the needle's words, then the scan compares the whole needle against the
	// stored string. Measured behaviour on real ADX (Stack Overflow 79092741) — `Message has "Heart Beat"`
	// MATCHES "as if it was a single whole term"; SO 74827840 states the shape outright for `has "#ell#"`:
	// "index for initial filtering on the term (ell), followed by a narrowed data scan (to filter out on the
	// entire searched string)". So a needle CARRYING a delimiter is a legitimate match — measured on this
	// engine: `"x a b y" has 'a b'` → TRUE, `"Heart Beat now" has 'Heart Beat'` → TRUE, `"rate_l exceeded" has
	// 'rate_l'` → TRUE — and empty-ing them was a regression that silently dropped the most natural user
	// query, the exact class of wrong answer this engine is being hardened against. The boundary regex below
	// is that literal scan. It anchors BOTH ends of the needle, so `has 'rate_l'` still finds nothing inside
	// `rate_limit` (the next character is a letter); whether real ADX matches that middle case is UNVERIFIED
	// here, so it is left alone rather than guessed at in either direction.
	static Expr HasFn(BinaryExpression b, ScalarContext ctx, string opName, bool caseInsensitive, bool negate = false)
	{
		var access = Compile(b.Left, ctx);
		if (access.Type != typeof(string))
			throw new UnsupportedKqlException($"'{opName}' requires a string operand on the left, got {access.Type.Name}");
		if (b.Right is not LiteralExpression { LiteralValue: string term })
			throw new UnsupportedKqlException($"'{opName}' requires a string literal on the right");
		// An empty `has` term matches NOTHING in Kusto (it is not an empty-substring match), so its
		// negation matches every NON-NULL value — Negated() adds the null exclusion.
		if (term.Length == 0)
			return negate ? Negated(Expr.Constant(false), access) : Expr.Constant(false);
		var pattern = (caseInsensitive ? "(?i)" : "") + TermStart + Re2Escape(term) + TermEnd;
		var call = Expr.Call(SqlMethod(nameof(KqlSqlExpressions.MatchesRegex)), access, Expr.Constant(pattern));
		return negate ? Negated(call, access) : call;
	}

	// A Kusto TERM boundary, from datatypes-string-operators #What-is-a-term: "each string value is broken
	// into maximal sequences of alphanumeric characters" — so letters/digits are term chars and everything
	// else (underscore included) is a delimiter. One definition, shared by has / hasprefix / hassuffix /
	// has_any / has_all; the regexes they build differ only in which side is anchored.
	const string TermStart = "(^|[^\\p{L}\\p{N}])";
	const string TermEnd = "([^\\p{L}\\p{N}]|$)";

	// `hasprefix` / `hassuffix` (+ the `_cs` and `!` twins): Kusto's TERM-PREFIX / TERM-SUFFIX rule. Verified
	// against the official docs (kusto/query/hasprefix-operator, hassuffix-operator and the operators table
	// in datatypes-string-operators): "RHS is a term prefix / term suffix in LHS", illustrated by
	// `"North America" hasprefix "ame"` and `"North America" hassuffix "ica"` — BOTH true. The needle is
	// therefore a prefix/suffix of ANY term in the value, NOT of the first/last term only, which is exactly
	// `has` (HasFn) with one of its two boundaries dropped: anchored on the term side, free on the other.
	// Same vehicle (MatchesRegex), same empty-needle rule, same Negated() null rule as `has`.
	static Expr HasAffix(BinaryExpression b, ScalarContext ctx, string opName, bool caseInsensitive, bool prefix, bool negate = false)
	{
		var access = Compile(b.Left, ctx);
		if (access.Type != typeof(string))
			throw new UnsupportedKqlException($"'{opName}' requires a string operand on the left, got {access.Type.Name}");
		if (b.Right is not LiteralExpression { LiteralValue: string term })
			throw new UnsupportedKqlException($"'{opName}' requires a string literal on the right");
		// A TERM is a maximal run of letters/digits (see TermStart), so a needle carrying ANY other character —
		// `rate_l`, `a b`, `x-y` — can never be a prefix/suffix of one: the answer is a CONSTANT, not a match
		// (the empty needle is the same arm). Leaving it to the regex would be a false positive: the anchored
		// boundary would admit `rate_limit hasprefix 'rate_l'`, where '_' starts a new term.
		if (term.Length == 0 || !IsTermRun(term))
			return negate ? Negated(Expr.Constant(false), access) : Expr.Constant(false);
		var body = Re2Escape(term);
		var pattern = (caseInsensitive ? "(?i)" : "") + (prefix ? TermStart + body : body + TermEnd);
		var call = Expr.Call(SqlMethod(nameof(KqlSqlExpressions.MatchesRegex)), access, Expr.Constant(pattern));
		return negate ? Negated(call, access) : call;
	}

	// Is the whole needle a run of TERM chars? Used by HasAffix ONLY: `hasprefix`/`hassuffix` really are about
	// TERM boundaries, so a needle carrying any other character cannot be a prefix/suffix of one. `has` itself
	// is deliberately NOT gated on this — see the block comment above HasFn (index pre-filter + exact literal
	// scan, where a delimiter-carrying needle is a legitimate match).
	// NOTE: `char.IsLetterOrDigit` is `\p{L}` + `\p{Nd}` — NARROWER than the `\p{N}` the boundary fragments
	// above use, which also covers `Nl` (e.g. the Roman numeral Ⅻ) and `No` (superscript ²). A needle made of
	// those is refused here while the regex would treat it as term text; both readings are defensible and no
	// measured query depends on it, so the predicate is left as it is rather than widened.
	static bool IsTermRun(string term)
	{
		foreach (var c in term)
			if (!char.IsLetterOrDigit(c))
				return false;
		return true;
	}

	// `has_any` / `has_all`: the OR / AND of plain `has` over the list elements — Kusto: "Same as `has` but
	// works on any/all of the elements" — so each term reuses the SAME term-boundary regex (and therefore
	// the same element-escaped literal match). Both spellings are case-INsensitive (there is no `_cs` twin)
	// and neither has a negated form; a null subject is pinned to FALSE by MatchesRegex, which is already
	// the Kusto answer for an un-negated predicate, so no Negated() wrapper applies. An empty ELEMENT
	// matches nothing (the `has` rule, which is about the EMPTY needle only — a delimiter-carrying element is
	// a legitimate match, see HasFn): dropped from `has_any`, fatal to `has_all`. An empty LIST is the
	// OR/AND identity — false for has_any, vacuously TRUE for has_all.
	static Expr HasList(Expression left, ExpressionList right, ScalarContext ctx, string opName, bool all)
	{
		var access = Compile(left, ctx);
		if (access.Type != typeof(string))
			throw new UnsupportedKqlException($"'{opName}' requires a string operand on the left, got {access.Type.Name}");
		Expr? acc = null;
		foreach (var element in right.Expressions)
		{
			if (element.Element is not LiteralExpression { LiteralValue: string term })
				throw new UnsupportedKqlException($"'{opName}' requires a list of string literals");
			if (term.Length == 0)
			{
				if (all)
					return Expr.Constant(false);
				continue;
			}
			var pattern = "(?i)" + TermStart + Re2Escape(term) + TermEnd;
			var call = Expr.Call(SqlMethod(nameof(KqlSqlExpressions.MatchesRegex)), access, Expr.Constant(pattern));
			acc = acc is null ? call : all ? Expr.AndAlso(acc, call) : Expr.OrElse(acc, call);
		}
		return acc ?? Expr.Constant(all);
	}

	// `=~` / `!~`: case-INSENSITIVE (in)equality — `=~` is NOT `==` (which is case-sensitive in Kusto, and
	// stays that way through the comparison path). Both operands are strings compared by one lower()-folding
	// shim, so the same ASCII-only fold as startswith/endswith applies. A null on EITHER side excludes the
	// row in BOTH spellings (Kusto's null rule), which the shim gives `=~` for free (NULL predicate) but not
	// `!~` through a bare NOT — hence the Negated() wrapper, with an explicit IS NOT NULL per string operand.
	static Expr EqualsTilde(BinaryExpression b, ScalarContext ctx, bool negate)
	{
		var opName = negate ? "!~" : "=~";
		var le = Compile(b.Left, ctx);
		var re = Compile(b.Right, ctx);
		if (le.Type != typeof(string) || re.Type != typeof(string))
			throw new UnsupportedKqlException(
				$"'{opName}' requires string operands, got {FriendlyTypeName(le.Type)} and {FriendlyTypeName(re.Type)}");
		var call = Expr.Call(SqlMethod(nameof(KqlSqlExpressions.EqualsI)), le, re);
		return negate ? Negated(call, le, re) : call;
	}

	static MethodInfo SqlMethod(string name) => typeof(KqlSqlExpressions).GetMethod(name)!;

	// Backslash-escape the RE2/PCRE2 metacharacters so a literal `has`/`has_cs` term matches literally
	// inside the generated boundary regex (a term like `a.b` or `a|b` must not act as a pattern). Covers
	// the full metachar set `. ^ $ * + ? ( ) [ ] { } | \` — the chars that would otherwise carry special
	// meaning on both sqlean (PCRE2) and DuckDB (RE2).
	static string Re2Escape(string term)
	{
		const string meta = ".^$*+?()[]{}|\\";
		var sb = new System.Text.StringBuilder(term.Length + 8);
		foreach (var c in term)
		{
			if (meta.Contains(c))
				sb.Append('\\');
			sb.Append(c);
		}
		return sb.ToString();
	}

	static Expr Arithmetic(BinaryExpression b, ScalarContext ctx)
	{
		var l = RequireNumeric(Compile(b.Left, ctx), b.Kind.ToString());
		var r = RequireNumeric(Compile(b.Right, ctx), b.Kind.ToString());
		// Nullable-aware promotion: `100.0 * toint(Properties.X) / 8441721` mixes double, long? and long →
		// the common type is double?, and the Expr.Multiply/Divide below LIFT over it, so a row whose property
		// is missing/malformed yields null (Kusto null propagation) instead of failing the whole query.
		var t = CommonValueType([l.Type, r.Type]);
		l = RealGuard(ConvertToValue(l, t));
		r = RealGuard(ConvertToValue(r, t));
		var node = b.Kind switch
		{
			SyntaxKind.AddExpression => Expr.Add(l, r),
			SyntaxKind.SubtractExpression => Expr.Subtract(l, r),
			SyntaxKind.MultiplyExpression => Expr.Multiply(l, r),
			SyntaxKind.DivideExpression => Expr.Divide(l, r),
			SyntaxKind.ModuloExpression => Expr.Modulo(l, r),
			_ => throw new UnsupportedKqlException($"arithmetic '{b.Kind}' not supported"),
		};
		RefuseNonFiniteConstant(node);
		return node;
	}

	// --- non-finite reals: NO SQL literal can express one. ---
	// linq2db folds an all-constant expression ITSELF at SQL-generation time and prints a double with
	// double.ToString(), so a NaN/±Infinity reaches the statement as a BARE IDENTIFIER. Measured on
	// work/kql-nan-infinity-constants: `todouble(Message) == 0.0/0.0` rendered `... = NaN` and
	// `todouble(Message) > 1.0/0.0` rendered `... > Infinity`, so SQLite answered `SQLite Error 1: 'no such
	// column: NaN'` — a raw binder message where a KQL-level answer belongs; a `1e400` LITERAL (which
	// Kusto.Language parses straight to +Infinity) did the same through Literal().
	//
	// The fix is a COMPILE-TIME refusal, uniform across dialects on purpose. The other honest shape —
	// constant-folding the COMPARISON (`x == NaN` is false, `x != NaN` is true) — cannot be done without
	// picking one dialect's answer: SQLite holds no NaN at all, while DuckDB holds a real one and its TOTAL
	// ORDER makes `NaN = NaN` TRUE (KqlSqlExpressions.IsNaNFn needed a per-dialect arm for exactly this).
	// The scalar compiler has no dialect to branch on — it emits ONE tree for both stores and per-dialect
	// differences live in the [Sql.Expression(ProviderName.X)] arms of KqlSqlExpressions — so a fold here
	// would silently contradict one of them. A refusal is true on both. The spelled-out alternative is the
	// one the engine already supports: isnan()/isinf() over a value.
	static UnsupportedKqlException NonFiniteReal(string what, double value) =>
		new($"{what}: {RealName(value)} has no SQL literal — use isnan()/isinf() to test a VALUE instead");

	static string RealName(double value) => double.IsNaN(value) ? "NaN" : value > 0 ? "+Infinity" : "-Infinity";

	// A double LITERAL constant (`1e400` is +Infinity before any arithmetic runs).
	static Expr FiniteReal(double value) => double.IsFinite(value)
		? Expr.Constant(value)
		: throw NonFiniteReal("this real literal overflows", value);

	// Refuse an arithmetic node that CONSTANT-FOLDS to NaN/±Infinity — the value linq2db is about to print as
	// an identifier (see above). Only the REAL domain can land there, so an integer tree is left alone
	// (KQL's `/` on two integers IS integer division). Runs on the WRAPPED node because RealGuard's identity
	// casts are part of what linq2db evaluates through — ConstantDouble is the walker that sees through them.
	static void RefuseNonFiniteConstant(Expr node)
	{
		if (NonNullable(node.Type) == typeof(double) && ConstantDouble(node) is { } value && !double.IsFinite(value))
			throw NonFiniteReal($"'{ArithmeticOpName(node.NodeType)}' on constant operands", value);
	}

	static string ArithmeticOpName(System.Linq.Expressions.ExpressionType type) => type switch
	{
		System.Linq.Expressions.ExpressionType.Add => "+",
		System.Linq.Expressions.ExpressionType.Subtract => "-",
		System.Linq.Expressions.ExpressionType.Multiply => "*",
		System.Linq.Expressions.ExpressionType.Divide => "/",
		System.Linq.Expressions.ExpressionType.Modulo => "%",
		_ => type.ToString(),
	};

	// The COMPILE-TIME value of an expression, when it has one: a numeric constant, the Convert chain a typed
	// conversion adds (`todouble(1)` is Convert(Convert(1, double), double?)), the identity real-domain guard,
	// or another REAL arithmetic node (so `(1.0-1.0)/0.0` — the natural way to write a NaN — is seen through).
	// Anything else is null and the guard stays silent: a column; a ServerSideOnly [Sql.Expression] shim
	// (ParseDouble & co. THROW in C#, so linq2db cannot fold those either — measured: `todouble('1')/todouble(
	// '0')` stays real SQL); and any integer-typed node, whose own arithmetic follows integer rules.
	static double? ConstantDouble(Expr e) => e switch
	{
		System.Linq.Expressions.ConstantExpression { Value: double d } => d,
		System.Linq.Expressions.ConstantExpression { Value: long l } => l,
		System.Linq.Expressions.ConstantExpression { Value: int i } => i,
		System.Linq.Expressions.UnaryExpression { NodeType: System.Linq.Expressions.ExpressionType.Convert } u
			=> ConstantDouble(u.Operand),
		System.Linq.Expressions.UnaryExpression { NodeType: System.Linq.Expressions.ExpressionType.Negate } u
			=> ConstantDouble(u.Operand) is { } v ? -v : null,
		System.Linq.Expressions.MethodCallExpression { Arguments.Count: 1 } call when IsRealGuard(call.Method)
			=> ConstantDouble(call.Arguments[0]),
		System.Linq.Expressions.BinaryExpression b when NonNullable(b.Type) == typeof(double) => FoldReal(b),
		_ => null,
	};

	static double? FoldReal(System.Linq.Expressions.BinaryExpression b) =>
		ConstantDouble(b.Left) is { } l && ConstantDouble(b.Right) is { } r
			? b.NodeType switch
			{
				System.Linq.Expressions.ExpressionType.Add => l + r,
				System.Linq.Expressions.ExpressionType.Subtract => l - r,
				System.Linq.Expressions.ExpressionType.Multiply => l * r,
				System.Linq.Expressions.ExpressionType.Divide => l / r,
				System.Linq.Expressions.ExpressionType.Modulo => l % r,
				_ => null,
			}
			: null;

	static bool IsRealGuard(MethodInfo method) => method.DeclaringType == typeof(KqlSqlExpressions)
		&& method.Name is nameof(KqlSqlExpressions.AsReal) or nameof(KqlSqlExpressions.AsRealN);

	// Pin a double-typed arithmetic operand to the SQL REAL domain. linq2db prints a double constant with an
	// integral value as a bare integer (`100.0` → `100`) and ELIDES Expr.Convert, so SQLite/DuckDB — dynamically
	// typed — would run `100 * chars / 8441721` as INTEGER math and truncate the quotient. An explicit
	// CAST (KqlSqlExpressions.AsReal) restores real division. Integral targets are untouched (KQL's `/` on two
	// integers IS integer division, matching Kusto).
	static Expr RealGuard(Expr e) => NonNullable(e.Type) != typeof(double)
		? e
		: Expr.Call(
			typeof(KqlSqlExpressions).GetMethod(IsNullable(e.Type)
				? nameof(KqlSqlExpressions.AsRealN)
				: nameof(KqlSqlExpressions.AsReal))!,
			e);

	static Expr StringMatch(BinaryExpression b, ScalarContext ctx, bool caseSensitive, bool negate = false)
	{
		var access = Compile(b.Left, ctx);
		if (access.Type != typeof(string))
			throw new UnsupportedKqlException($"contains/has requires a string operand on the left, got {access.Type.Name}");
		if (b.Right is not LiteralExpression { LiteralValue: string needle })
			throw new UnsupportedKqlException("contains/has requires a string literal");
		var method = typeof(string).GetMethod(nameof(string.Contains), [typeof(string), typeof(StringComparison)])!;
		var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		var call = Expr.Call(access, method, Expr.Constant(needle), Expr.Constant(comparison));
		return negate ? Negated(call, access) : call;
	}

	// `!<op>` is NOT plain `not(<op>)`: Kusto excludes a null operand from BOTH the positive and the
	// negated spelling (a null matches nothing, and its negation matches nothing either) — exactly the
	// `!in` / `!between` rule implemented in In()/Between(). It CANNOT be left to the shim under the NOT:
	// the `has` family lowers to MatchesRegex, whose SQL deliberately PINS a null subject to FALSE
	// (IFNULL/COALESCE), so NOT(that FALSE) would flip to TRUE and wrongly KEEP the null row. Hence the
	// explicit non-null operand check AND-ed in front, on top of NullGuard for nullable value operands.
	static Expr Negated(Expr positive, params Expr[] operands)
	{
		// A STRING operand needs its own IS NOT NULL: the shim under the NOT may pin a null to FALSE
		// (MatchesRegex does), which NOT() would flip to TRUE and wrongly keep the row. Value operands are
		// covered by NullGuard below.
		var guarded = NullGuard(Expr.Not(positive), operands);
		foreach (var operand in operands)
			if (operand.Type == typeof(string))
				guarded = Expr.AndAlso(Expr.Call(SqlMethod(nameof(KqlSqlExpressions.IsNotNull)), operand), guarded);
		return guarded;
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

	// A datetime()/timespan literal compiles to a DateTime/TimeSpan constant, but the corresponding
	// EXPRESSION in the record context is a long (epoch-ms for instants like Timestamp/Start/End; ticks
	// for spans' Duration). When the two meet in a comparison — plain, reversed (`200ms < Duration`),
	// `in`, or `between` — coerce the literal into the long domain so both sides compare numerically.
	// (In the row context instants are DateTime and durations TimeSpan — never long — so this never
	// fires there and pipeline position doesn't change semantics.)
	static (Expr, Expr) NormalizeInstants(Expr le, Expr re, ScalarContext ctx)
	{
		// The record-context instant is long (or long? for todatetime); coerce a datetime() literal on
		// the other side into that epoch-ms domain so both operands compare as ms.
		if (le is System.Linq.Expressions.ConstantExpression { Value: DateTime } && IsEpochMs(re.Type))
			return (ctx.CoerceInstant(le), re);
		if (re is System.Linq.Expressions.ConstantExpression { Value: DateTime } && IsEpochMs(le.Type))
			return (le, ctx.CoerceInstant(re));
		// A timespan literal against a long expression: the record context stores durations as ticks
		// (spans' Duration), so fold the literal to its tick count. NOTE: like the DateTime branch above,
		// this cannot verify the long side really is a duration — `Id > 1h` compares against ticks
		// silently — the accepted cost of the untagged long domain.
		if (le is System.Linq.Expressions.ConstantExpression { Value: TimeSpan lts } && IsEpochMs(re.Type))
			return (Expr.Constant(lts.Ticks), re);
		if (re is System.Linq.Expressions.ConstantExpression { Value: TimeSpan rts } && IsEpochMs(le.Type))
			return (le, Expr.Constant(rts.Ticks));
		return (le, re);
	}

	static bool IsEpochMs(Type t) => t == typeof(long) || t == typeof(long?);

	static Expr Compare(Expr le, Expr re, SyntaxKind kind)
	{
		(le, re) = UnifyComparable(le, re, kind);
		// liftToNull:false so a comparison with a nullable operand yields bool (not bool?).
		var cmp = kind switch
		{
			SyntaxKind.EqualExpression => Expr.Equal(le, re, liftToNull: false, method: null),
			SyntaxKind.NotEqualExpression => Expr.NotEqual(le, re, liftToNull: false, method: null),
			SyntaxKind.LessThanExpression => Expr.LessThan(le, re, liftToNull: false, method: null),
			SyntaxKind.LessThanOrEqualExpression => Expr.LessThanOrEqual(le, re, liftToNull: false, method: null),
			SyntaxKind.GreaterThanExpression => Expr.GreaterThan(le, re, liftToNull: false, method: null),
			SyntaxKind.GreaterThanOrEqualExpression => Expr.GreaterThanOrEqual(le, re, liftToNull: false, method: null),
			_ => throw new UnsupportedKqlException($"comparison '{kind}' not supported"),
		};
		// Kusto excludes a null operand from EVERY comparison (==, !=, <, <=, >, >=): a typed conversion of
		// a malformed/missing value is null, and null matches nothing. C#'s lifted `!=` would instead
		// return TRUE when exactly one side is null (so `where toint(x) != 5` would wrongly KEEP the null
		// rows), while SQLite already yields NULL there (excluded) — a SQL-vs-in-memory divergence by
		// pipeline position. Guard BOTH paths identically by requiring every nullable operand non-null.
		// Built as a literal AndAlso so linq2db compiles the SAME tree the in-memory path runs.
		// NOTE: two-valued, not Kusto's three-valued logic — not(<null compare>) gives not(false)=true
		// here vs Kusto's not(null)=null; that edge is accepted and deliberately left unimplemented.
		return NullGuard(cmp, le, re);
	}

	// AND `comparison` with a HasValue check for each nullable operand, so a null operand forces the whole
	// comparison to false. A no-op when neither operand is a Nullable<T> (strings/bools/plain numerics).
	static Expr NullGuard(Expr comparison, params Expr[] operands)
	{
		var guarded = comparison;
		foreach (var op in operands)
			if (IsNullable(op.Type))
				guarded = Expr.AndAlso(Expr.Property(op, "HasValue"), guarded);
		return guarded;
	}

	// The two KqlSqlExpressions shims In() reaches by name — the EXISTING SQL-translatable, dual-dialect,
	// nullable-aware ones (no new shim is introduced by the `~` support): ToLower is `lower({0})`, IsNotNull is
	// `({0} IS NOT NULL)`. Resolved once here because KqlScalar has no SqlM helper of its own (that one lives
	// on KqlScalarFunctions) and a reflection `!` is not the house style.
	static readonly MethodInfo LowerSql = SqlShim(nameof(KqlSqlExpressions.ToLower));
	static readonly MethodInfo IsNotNullSql = SqlShim(nameof(KqlSqlExpressions.IsNotNull));

	static MethodInfo SqlShim(string name) =>
		typeof(KqlSqlExpressions).GetMethod(name)
		?? throw new InvalidOperationException($"{nameof(KqlSqlExpressions)}.{name} is missing");

	static Expr In(InExpression inx, ScalarContext ctx)
	{
		// Kusto's `in~` (InCsExpression) and `!in~` (NotInCsExpression) are the CASE-INSENSITIVE membership
		// spellings. READ THIS BEFORE "FIXING" THE NAME: on every OTHER operator the `_cs` suffix means
		// case-SENSITIVE (`contains_cs`, `has_cs`), but Kusto.Language names the tilde forms with a `Cs` that
		// means the OPPOSITE — it is Kusto's own historical spelling of the `~` form. So `in~` without case
		// folding is silently ordinal and returns wrong rows with no error. Do not invert this.
		// The fold is `lower()`, hence ASCII-ONLY on SQLite — which is what Kusto documents for the `~`
		// operators ("case-insensitive operators are currently supported only for ASCII text"). DuckDB's
		// `lower()` folds Unicode too, and the `(?i)` regex lowering of `has` folds it on BOTH backends, so
		// the engine is ASCII-only in some places and Unicode-wide in others: observations/
		// kql-case-folding-differs-by-mechanism-and-backend.
		var fold = inx.Kind is SyntaxKind.InCsExpression or SyntaxKind.NotInCsExpression;
		var negate = inx.Kind is SyntaxKind.NotInExpression or SyntaxKind.NotInCsExpression;
		var le = Compile(inx.Left, ctx);
		var elements = inx.Right.Expressions.Select(e => Compile(e.Element, ctx)).ToList();
		if (elements.Count == 0)
			return Expr.Constant(negate);

		// A STRING left has no HasValue for NullGuard to test, so the NEGATED spellings need an explicit non-null
		// check: measured, the negated membership SQL KEEPS a null-operand row (`Properties["env"] !in ('prod')`
		// returned [1,2] where Kusto gives [1]; the tilde twin had the same defect), which is exactly the Kusto
		// null rule the `!has` family already had to restore via Negated(). Applies to `!in` AND `!in~` — a null
		// operand matches nothing, and its negation matches nothing either. Case folding stays `~`-only.
		var stringOperand = NonNullable(le.Type) == typeof(string);
		var stringFold = fold && stringOperand;

		Expr? acc = null;
		foreach (var element in elements)
		{
			// datetime()/timespan literals in the list normalize into the record context's long domain
			// (epoch-ms / ticks), exactly like a plain comparison — so `Duration in (100ms, 50ms)` and
			// `Start in (datetime(...))` work identically pre- and post-split.
			var (nl, nr) = NormalizeInstants(le, element, ctx);
			var (l, r) = UnifyComparable(nl, nr, SyntaxKind.EqualExpression);
			// Fold BOTH sides, but only for string membership: `1 in~ (1)` or a datetime in~ a list are
			// order-equality either way, so the plain path is the CORRECT one for non-strings (and for
			// `in` / `!in`, which are ordinal by definition and stay so).
			if (stringFold)
				(l, r) = (Expr.Call(LowerSql, l), Expr.Call(LowerSql, r));
			var eq = Expr.Equal(l, r, liftToNull: false, method: null);
			acc = acc is null ? eq : Expr.OrElse(acc, eq);
		}
		Expr result = negate ? Expr.Not(acc!) : acc!;
		// NullGuard excludes a null operand whose type carries HasValue (the nullable value operands); a STRING
		// left gets the explicit IS NOT NULL above instead. For membership the rule is Kusto's: a null left
		// matches nothing, and its negation matches nothing either — the positive spelling already drops the row
		// on its own (`NULL = 'x'` is NULL), the negated one needed the guard.
		result = NullGuard(result, le);
		return negate && stringOperand ? Expr.AndAlso(Expr.Call(IsNotNullSql, le), result) : result;
	}

	static Expr Between(BetweenExpression btw, ScalarContext ctx)
	{
		var value = Compile(btw.Left, ctx);
		var lo = Compile(btw.Right.First, ctx);
		var hi = Compile(btw.Right.Second, ctx);
		// Range bounds normalize like comparison operands (datetime → epoch-ms, timespan → ticks in the
		// record context), so `Timestamp between (datetime .. datetime)` / `Duration between (a .. b)`
		// work pre-split too.
		(var v1, lo) = NormalizeInstants(value, lo, ctx);
		(var v2, hi) = NormalizeInstants(value, hi, ctx);
		(v1, var lo1) = UnifyComparable(v1, lo, SyntaxKind.GreaterThanOrEqualExpression);
		(v2, var hi1) = UnifyComparable(v2, hi, SyntaxKind.LessThanOrEqualExpression);
		var inRange = Expr.AndAlso(
			Expr.GreaterThanOrEqual(v1, lo1, liftToNull: false, method: null),
			Expr.LessThanOrEqual(v2, hi1, liftToNull: false, method: null));
		Expr result = btw.Kind == SyntaxKind.NotBetweenExpression ? Expr.Not(inRange) : inRange;
		// A null value is excluded from both `between` and `!between` (Kusto null semantics).
		return NullGuard(result, value);
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
			case ElementExpression el when KqlTransformer.IsPropertiesIndex(el, out var key):
				columnName = $"Properties[\"{key}\"]";
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

	// The numeric-CONTEXT test: a numeric OR its nullable form. Every value in the Properties bag is text, so
	// numeric analysis of a property goes through toint/tolong/todouble, which (Kusto semantics: a malformed or
	// missing value is null) yield long?/double?. A numeric context must therefore accept Nullable<T> wherever
	// it accepts T — arithmetic, unary +/-, and bin() all lift instead of rejecting, and the null PROPAGATES
	// (null operand → null result, null bin key → its own bucket). No hidden coalesce to 0: that would silently
	// skew sum/avg.
	public static bool IsNumericValue(Type t) => IsNumeric(NonNullable(t));

	static bool IsEqualityOnly(SyntaxKind kind) => kind is
		SyntaxKind.EqualExpression or SyntaxKind.NotEqualExpression;

	public static Expr RequireBool(Expr e) => e.Type == typeof(bool)
		? e
		: throw new UnsupportedKqlException($"expected a boolean expression, got {e.Type.Name}");

	// Accepts a nullable numeric (see IsNumericValue) and passes it through UNCHANGED — the caller's
	// Expr.Negate / Expr.Add / … then LIFTS over Nullable<T>, giving Kusto's null-propagating arithmetic.
	static Expr RequireNumeric(Expr e, string what) => IsNumericValue(e.Type)
		? e
		: throw new UnsupportedKqlException($"{what} requires a numeric operand, got {FriendlyTypeName(e.Type)}");

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
		// Numeric promotion, nullable-aware: a typed conversion (toint/todouble) yields a nullable
		// numeric, so `iff(cond, toint(x), 0)` mixes long? and long — unify to the nullable common type
		// (long?) rather than rejecting them as "incompatible".
		if (distinct.All(t => IsNumeric(NonNullable(t))))
		{
			var common = distinct.Select(NonNullable).Aggregate(CommonNumeric);
			return distinct.Any(IsNullable) ? typeof(Nullable<>).MakeGenericType(common) : common;
		}
		// Same underlying value type mixed with its nullable form (e.g. bool and bool?) → the nullable one.
		var underlyings = distinct.Select(NonNullable).Distinct().ToList();
		if (underlyings.Count == 1 && underlyings[0].IsValueType)
			return typeof(Nullable<>).MakeGenericType(underlyings[0]);
		throw new UnsupportedKqlException(
			$"incompatible result types: {string.Join(", ", distinct.Select(FriendlyTypeName))}");
	}

	public static Expr ConvertToValue(Expr e, Type t)
	{
		if (e.Type == t)
			return e;
		// Numeric widening, nullable-aware (int→long?, long→long?, long?→double?, …) via the same
		// two-step lifted convert the comparison path uses.
		if (IsNumeric(NonNullable(e.Type)) && IsNumeric(NonNullable(t)))
			return ConvertComparable(e, t);
		// Widen a non-null value to its nullable form (bool→bool?, datetime→datetime?).
		if (IsNullable(t) && NonNullable(t) == e.Type)
			return Expr.Convert(e, t);
		throw new UnsupportedKqlException($"cannot convert {FriendlyTypeName(e.Type)} to {FriendlyTypeName(t)}");
	}

	// User-facing KQL type name for an expression's CLR type (nullable-stripped): keeps error messages
	// in the language the user writes rather than leaking raw CLR names like Int64 / Nullable`1.
	public static string FriendlyTypeName(Type t)
	{
		var u = NonNullable(t);
		return u == typeof(long) ? "long"
			: u == typeof(int) ? "int"
			: u == typeof(double) ? "double"
			: u == typeof(bool) ? "bool"
			: u == typeof(string) ? "string"
			: u == typeof(DateTime) ? "datetime"
			: u == typeof(TimeSpan) ? "timespan"
			: u.Name;
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
			// null / emptiness tests. Plain registry entries, not operators: each needs only the type of its
			// ONE argument, so it composes anywhere a scalar does (`where isnull(Properties["x"])`,
			// `extend E = isempty(Message)`). The negation is spelled as its own KQL function
			// (isnotnull/isnotempty) and is NOT `not(isnull(...))` — Kusto's null rule makes the two
			// equivalent here because both are total (never null-valued) predicates.
			["isnull"] = (a, c) => IsNullFn(a, "isnull", negate: false),
			["isnotnull"] = (a, c) => IsNullFn(a, "isnotnull", negate: true),
			["isempty"] = (a, c) => IsEmptyFn(a, "isempty", negate: false),
			["isnotempty"] = (a, c) => IsEmptyFn(a, "isnotempty", negate: true),

			// --- wave: string / math / datetime-part / conversion scalars (card
			// work/kql-scalar-strings-math-datetime). Every entry carries a PORTABLE per-dialect SQL
			// translation: this engine has no in-memory execution path (KqlTransformer.ExecutePipeline), so
			// a shim that cannot translate SQLite AND DuckDB identically is not a fallback, it is a broken
			// query. ---
			//
			// THE DROP LIST — the scalars this wave deliberately does NOT register. Each absence is a TYPE-MODEL or
			// single-SQL-path limit, not an oversight, and none of them may be faked with a string/double stand-in:
			//   split           — returns `dynamic` (an array); the engine models no dynamic type.
			//   strcat_array    — takes a `dynamic` array; same missing type.
			//   replace_strings — takes a dynamic array of from/to pairs; same missing type.
			//   toguid          — `guid` is not a modelled type (a string stand-in would not order/compare as one).
			//   todecimal       — `decimal` is not a modelled type (double loses the exactness the type exists for).
			//   typeof          — the type name of a value is per-row and DYNAMIC, and the dialects spell it
			//                     differently ('integer'/'real' vs 'INTEGER'/'DECIMAL'); a compile-time CLR name
			//                     would lie for every Properties value.
			//   countof         — counts OVERLAPPING occurrences, which needs a per-row loop no SQL dialect has;
			//                     the non-overlapping replace() trick would answer 2 where Kusto says 3 — silently wrong.
			//   reverse         — no SQLite reverse() and no portable character loop.
			//   totimespan      — the string→timespan grammar ("[-][d.]hh:mm:ss[.fffffff]") has no portable parse.

			// string
			["strlen"] = (a, c) => StringFn1(a, "strlen", nameof(KqlSqlExpressions.StrLen)),
			["indexof"] = IndexOf,
			["replace_string"] = ReplaceString,
			["trim"] = (a, c) => Trim(a, "trim"),
			["trim_start"] = (a, c) => Trim(a, "trim_start"),
			["trim_end"] = (a, c) => Trim(a, "trim_end"),
			["strcat_delim"] = StrcatDelim,
			["format_bytes"] = FormatBytes,
			["isascii"] = (a, c) => StringFn1(a, "isascii", nameof(KqlSqlExpressions.IsAscii)),
			["isutf8"] = (a, c) => StringFn1(a, "isutf8", nameof(KqlSqlExpressions.IsUtf8)),

			// math
			["abs"] = Abs,
			["floor"] = (a, c) => RealFn(a, "floor", nameof(KqlSqlExpressions.Floor)),
			["ceiling"] = (a, c) => RealFn(a, "ceiling", nameof(KqlSqlExpressions.Ceiling)),
			["round"] = Round,
			["sqrt"] = (a, c) => RealFn(a, "sqrt", nameof(KqlSqlExpressions.Sqrt)),
			["exp"] = (a, c) => RealFn(a, "exp", nameof(KqlSqlExpressions.Exp)),
			["log"] = (a, c) => RealFn(a, "log", nameof(KqlSqlExpressions.LogNatural)),
			["log2"] = (a, c) => RealFn(a, "log2", nameof(KqlSqlExpressions.Log2)),
			["log10"] = (a, c) => RealFn(a, "log10", nameof(KqlSqlExpressions.Log10)),
			["sign"] = Sign,
			["pow"] = Pow,
			["pi"] = Pi,
			["isnan"] = (a, c) => RealPred(a, "isnan", nameof(KqlSqlExpressions.IsNaNFn)),
			["isinf"] = (a, c) => RealPred(a, "isinf", nameof(KqlSqlExpressions.IsInfFn)),
			["min_of"] = (a, c) => MinMax(a, c, "min_of", max: false),
			["max_of"] = (a, c) => MinMax(a, c, "max_of", max: true),
			["coalesce"] = Coalesce,

			// datetime parts / arithmetic
			["dayofweek"] = (a, c) => InstantExtract(a, c, "dayofweek", nameof(KqlSqlExpressions.DayOfWeekTicks), ticks: true),
			["dayofmonth"] = (a, c) => InstantExtract(a, c, "dayofmonth", nameof(KqlSqlExpressions.DayOfMonthOfMs)),
			["dayofyear"] = (a, c) => InstantExtract(a, c, "dayofyear", nameof(KqlSqlExpressions.DayOfYearOfMs)),
			["monthofyear"] = (a, c) => InstantExtract(a, c, "monthofyear", nameof(KqlSqlExpressions.MonthOfMs)),
			["hourofday"] = (a, c) => InstantExtract(a, c, "hourofday", nameof(KqlSqlExpressions.HourOfMs)),
			["week_of_year"] = (a, c) => InstantExtract(a, c, "week_of_year", nameof(KqlSqlExpressions.WeekOfYearOfMs)),
			["datetime_part"] = DateTimePart,
			["datetime_add"] = DateTimeAdd,
			["endofday"] = (a, c) => EndOf(a, c, "endofday"),
			["endofweek"] = (a, c) => EndOf(a, c, "endofweek"),
			["endofmonth"] = (a, c) => EndOf(a, c, "endofmonth"),
			["make_datetime"] = MakeDateTime,
			["make_timespan"] = MakeTimespan,

			// conversion
			["toboolean"] = ToBoolConv,
			["tohex"] = ToHex,
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

		// In-memory instant domain (RowScalarContext): DateTime, or DateTime? when the instant came from
		// todatetime() — every bag value is TEXT, so `bin(todatetime(Properties.X), 1h)` is THE time-histogram
		// idiom over the property bag, and todatetime maps a malformed/missing value to null (Kusto). The
		// nullable arm PROPAGATES that null (its own bucket) exactly like the numeric arm's BinLongN/BinDoubleN.
		if (KqlScalar.NonNullable(value.Type) == typeof(DateTime) && step.Type == typeof(TimeSpan))
			return Expr.Call(
				Method(KqlScalar.IsNullable(value.Type) ? nameof(BinDateTimeN) : nameof(BinDateTime)), value, step);

		// SQL/record domain: a datetime is epoch-ms `long` (long? for a todatetime result) and the timespan step
		// is a constant literal. Bucket by absolute ticks (BinDateTimeMs / the null-propagating BinDateTimeMsN)
		// to match the in-memory BinDateTime exactly. A non-constant step or a sub-millisecond step (which the
		// ms-precision epoch-ms round-trip cannot represent losslessly) throws → the transient bridge runs that
		// stage in-memory (both are unreachable in practice — timespan literals are whole-ms constants).
		if ((value.Type == typeof(long) || value.Type == typeof(long?)) && step.Type == typeof(TimeSpan))
		{
			if (step is not System.Linq.Expressions.ConstantExpression { Value: TimeSpan ts })
				throw new UnsupportedKqlException("bin() with a datetime value requires a constant timespan step");
			if (ts.Ticks <= 0)
				throw new UnsupportedKqlException("bin() step must be a positive timespan");
			if (ts.Ticks % TimeSpan.TicksPerMillisecond != 0)
				throw new UnsupportedKqlException("bin() with a datetime value requires a whole-millisecond timespan step");
			var binMs = KqlScalar.IsNullable(value.Type)
				? nameof(KqlSqlExpressions.BinDateTimeMsN)
				: nameof(KqlSqlExpressions.BinDateTimeMs);
			return Expr.Call(SqlM(binMs), value, Expr.Constant(ts.Ticks));
		}

		// Nullable numerics count as numeric here: bin(toint(Properties.RespChars), 5000) is THE histogram
		// idiom over the (string-typed) property bag, and toint yields long?.
		if (KqlScalar.IsNumericValue(value.Type) && KqlScalar.IsNumericValue(step.Type))
		{
			// CommonValueType is nullable-aware: any nullable operand makes the common type nullable, which
			// selects the null-propagating BinLongN/BinDoubleN — a null value bins to null (its own bucket in
			// `summarize … by bin(x, n)`), never to 0.
			var t = KqlScalar.CommonValueType([value.Type, step.Type]);
			// Route through the SQL-translatable KqlSqlExpressions.BinLong/BinDouble (not an in-memory-only
			// helper) so numeric bin works as a `summarize … by bin(col, n)` GROUP BY key too, not only as a
			// client-evaluated projection. The C# bodies preserve the exact floor-toward-negative-infinity
			// semantics for the in-memory path.
			var integral = KqlScalar.NonNullable(t) == typeof(long);
			var target = integral
				? (KqlScalar.IsNullable(t) ? typeof(long?) : typeof(long))
				: (KqlScalar.IsNullable(t) ? typeof(double?) : typeof(double));
			var method = (integral, KqlScalar.IsNullable(t)) switch
			{
				(true, false) => nameof(KqlSqlExpressions.BinLong),
				(true, true) => nameof(KqlSqlExpressions.BinLongN),
				(false, false) => nameof(KqlSqlExpressions.BinDouble),
				(false, true) => nameof(KqlSqlExpressions.BinDoubleN),
			};
			return Expr.Call(SqlM(method), KqlScalar.ConvertToValue(value, target), KqlScalar.ConvertToValue(step, target));
		}

		throw new UnsupportedKqlException(
			$"bin() supports (datetime, timespan) or (numeric, numeric), got ({KqlScalar.FriendlyTypeName(value.Type)}, {KqlScalar.FriendlyTypeName(step.Type)})");
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
		// Nullable variants (the result of a typed conversion like toint/tobool): null-propagate to a
		// null string so tostring(toint(x)) composes instead of throwing on the Nullable`1 type.
		if (a.Type == typeof(long?))
			return Expr.Call(SqlM(nameof(KqlSqlExpressions.LongToStringN)), a);
		if (a.Type == typeof(int?))
			return Expr.Call(SqlM(nameof(KqlSqlExpressions.LongToStringN)), Expr.Convert(a, typeof(long?)));
		if (a.Type == typeof(bool?))
			return Expr.Call(SqlM(nameof(KqlSqlExpressions.BoolToStringN)), a);
		throw new UnsupportedKqlException(
			$"tostring() supports string, integer, and boolean arguments, got {KqlScalar.FriendlyTypeName(a.Type)}");
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

	// isnull/isnotnull: total predicates over ANY type. A nullable operand (todatetime/toint/…) tests
	// HasValue, a string rides the IS [NOT] NULL shims, and a non-nullable value type can never be null —
	// a compile-time constant, which is the honest answer rather than a redundant runtime check.
	static Expr IsNullFn(IReadOnlyList<Expr> args, string fn, bool negate)
	{
		if (args.Count != 1)
			throw new UnsupportedKqlException($"{fn}() takes exactly 1 argument, got {args.Count}");
		var a = args[0];
		if (KqlScalar.IsNullable(a.Type))
		{
			var hasValue = Expr.Property(a, "HasValue");
			return negate ? hasValue : Expr.Not(hasValue);
		}
		if (a.Type == typeof(string))
			return Expr.Call(SqlM(negate ? nameof(KqlSqlExpressions.IsNotNull) : nameof(KqlSqlExpressions.IsNull)), a);
		return Expr.Constant(negate);
	}

	// isempty/isnotempty: "null or empty string" and its complement. Strings only — KQL's isempty is a
	// string test (any other type is a user error worth naming, not an implicit coercion).
	static Expr IsEmptyFn(IReadOnlyList<Expr> args, string fn, bool negate)
	{
		if (args.Count != 1)
			throw new UnsupportedKqlException($"{fn}() takes exactly 1 argument, got {args.Count}");
		if (args[0].Type != typeof(string))
			throw new UnsupportedKqlException($"{fn}() requires a string argument, got {KqlScalar.FriendlyTypeName(args[0].Type)}");
		return Expr.Call(SqlM(negate ? nameof(KqlSqlExpressions.IsNotEmpty) : nameof(KqlSqlExpressions.IsEmpty)), args[0]);
	}

	static MethodInfo SqlM(string name) => typeof(KqlSqlExpressions).GetMethod(name)!;

	// --- shared coercion helpers for the string / math / datetime / conversion scalars ---

	// The single argument of a 1-argument function, with a function-named error.
	static Expr OneArg(IReadOnlyList<Expr> args, string fn)
	{
		if (args.Count != 1)
			throw new UnsupportedKqlException($"{fn}() takes exactly 1 argument, got {args.Count}");
		return args[0];
	}

	static void RequireString(Expr e, string fn)
	{
		if (e.Type != typeof(string))
			throw new UnsupportedKqlException($"{fn}() requires a string argument, got {KqlScalar.FriendlyTypeName(e.Type)}");
	}

	// A numeric argument lifted to a NULLABLE target numeric (long?/double?), so each math scalar has ONE
	// signature instead of a T/T? pair and stays TOTAL: a malformed/missing bag value arrives as null
	// through toint/todouble and must PROPAGATE (Kusto's null rule), never throw.
	static Expr LiftNumeric(Expr e, Type target, string fn)
	{
		if (!KqlScalar.IsNumericValue(e.Type))
			throw new UnsupportedKqlException(
				$"{fn}() requires a numeric argument, got {KqlScalar.FriendlyTypeName(e.Type)}");
		return KqlScalar.ConvertToValue(e, typeof(Nullable<>).MakeGenericType(target));
	}

	// The REAL domain a Kusto math scalar works in: lift to double?, then pin to the SQL REAL type
	// (AsRealN), so an integral-valued double CONSTANT is not printed as a bare integer literal — the
	// long-standing linq2db hazard documented on KqlSqlExpressions.AsReal — and DuckDB does not resolve the
	// overload against DECIMAL/INTEGER.
	static Expr ToReal(Expr e, string fn) =>
		Expr.Call(SqlM(nameof(KqlSqlExpressions.AsRealN)), LiftNumeric(e, typeof(double), fn));

	static Expr RealFn(IReadOnlyList<Expr> args, string fn, string method) =>
		Expr.Call(SqlM(method), ToReal(OneArg(args, fn), fn));

	static Expr RealPred(IReadOnlyList<Expr> args, string fn, string method) =>
		Expr.Call(SqlM(method), ToReal(OneArg(args, fn), fn));

	static Expr StringFn1(IReadOnlyList<Expr> args, string fn, string method)
	{
		var a = OneArg(args, fn);
		RequireString(a, fn);
		return Expr.Call(SqlM(method), a);
	}

	// Kusto casts a non-string scalar to string inside the string functions ("If the arguments aren't of
	// string type, they'll be forcibly converted to string") — reuse tostring()'s coercion.
	static Expr ToStr(Expr e, string fn) => e.Type == typeof(string) ? e : ToStringConv([e], null!);

	// A datetime argument in the EPOCH-MS domain these shims speak: native for the SQL/record context,
	// bridged through ToUnixMs in the row context (the same bridge StartOf uses).
	static Expr ToMs(Expr instant) =>
		instant.Type == typeof(DateTime) ? Expr.Call(SqlM(nameof(KqlSqlExpressions.ToUnixMs)), instant) : instant;

	// ms-domain → this context's instant representation (the mirror of ToMs).
	static Expr FromMs(Expr ms, bool asDateTime) =>
		asDateTime ? Expr.Call(SqlM(nameof(KqlSqlExpressions.FromUnixMs)), ms) : ms;

	// floor-order-invariant CommonValueType that names the function in its error instead of saying "incompatible
	// result types" with no context.
	static Type Unified(IReadOnlyList<Expr> args, string fn)
	{
		try
		{
			return KqlScalar.CommonValueType(args.Select(a => a.Type).ToList());
		}
		catch (UnsupportedKqlException ex)
		{
			throw new UnsupportedKqlException($"{fn}(): {ex.Message}");
		}
	}

	// --- string scalars ---

	// indexof(string, match[, start[, length[, occurrence]]]) — ZERO-based, -1 when absent (kusto/query/
	// indexof-function). `occurrence` (the Nth match) needs a per-row loop no SQL dialect can express, so it
	// is refused PRECISELY instead of silently returning the first match's index; the default value 1 is of
	// course accepted.
	static Expr IndexOf(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count is < 2 or > 5)
			throw new UnsupportedKqlException(
				$"indexof() takes 2 to 5 arguments (string, match[, start[, length[, occurrence]]]), got {args.Count}");
		RequireString(args[0], "indexof");
		RequireString(args[1], "indexof");
		if (args.Count == 5 && args[4] is not System.Linq.Expressions.ConstantExpression { Value: 1L })
			throw new UnsupportedKqlException(
				"indexof() occurrence other than 1 is not supported: locating the Nth match needs a per-row loop "
				+ "that no SQL dialect offers (this engine has a single SQL path). Use a 4-argument indexof().");
		var start = args.Count >= 3 ? RequireLong(args[2], "indexof() start") : Expr.Constant(0L);
		var length = args.Count >= 4 ? RequireLong(args[3], "indexof() length") : Expr.Constant(-1L);
		return Expr.Call(SqlM(nameof(KqlSqlExpressions.IndexOf)), args[0], args[1], start, length);
	}

	// replace_string(text, lookup, rewrite) — all NON-overlapping matches (kusto/query/replace-string-function).
	static Expr ReplaceString(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 3)
			throw new UnsupportedKqlException(
				$"replace_string() takes exactly 3 arguments (text, lookup, rewrite), got {args.Count}");
		foreach (var a in args)
			RequireString(a, "replace_string");
		return Expr.Call(SqlM(nameof(KqlSqlExpressions.ReplaceString)), args[0], args[1], args[2]);
	}

	// trim/trim_start/trim_end take a REGEX and strip ALL leading/trailing matches of it (kusto/query/
	// trim-function). A single regexp_replace cannot loop, so the pattern is rewritten at COMPILE time into an
	// anchored, greedily repeated one — `^(?:P)+` / `(?:P)+$` — whose repetition consumes the same run of
	// matches Kusto's loop does. Hence the literal-regex requirement: the pattern becomes part of the SQL.
	static Expr Trim(IReadOnlyList<Expr> args, string fn)
	{
		if (args.Count != 2)
			throw new UnsupportedKqlException($"{fn}() takes exactly 2 arguments (regex, source), got {args.Count}");
		if (args[0] is not System.Linq.Expressions.ConstantExpression { Value: string regex })
			throw new UnsupportedKqlException($"{fn}() requires a string LITERAL regular expression as its first argument");
		if (regex.Length == 0)
			throw new UnsupportedKqlException($"{fn}() requires a non-empty regular expression");
		RequireString(args[1], fn);
		var source = args[1];
		if (fn is "trim" or "trim_start")
			source = Expr.Call(SqlM(nameof(KqlSqlExpressions.RegexTrim)), source,
				Expr.Constant("^(?:" + regex + ")+", typeof(string)));
		if (fn is "trim" or "trim_end")
			source = Expr.Call(SqlM(nameof(KqlSqlExpressions.RegexTrim)), source,
				Expr.Constant("(?:" + regex + ")+$", typeof(string)));
		return source;
	}

	// strcat_delim(delimiter, arg1, arg2, …) — 2..64 values joined by the first argument (kusto/query/
	// strcat-delim-function; `strcat_delim('-', 1, '2')` = '1-2'). A NULL argument renders as EMPTY, exactly
	// like strcat, so the fold rides the same StrCat2 shim (a plain `||` on both dialects).
	static Expr StrcatDelim(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count < 3)
			throw new UnsupportedKqlException(
				$"strcat_delim() takes a delimiter and at least 2 values, got {args.Count} arguments");
		RequireString(args[0], "strcat_delim");
		var delimiter = args[0];
		var acc = ToStr(args[1], "strcat_delim");
		for (var i = 2; i < args.Count; i++)
			acc = Expr.Call(SqlM(nameof(KqlSqlExpressions.StrCat2)),
				Expr.Call(SqlM(nameof(KqlSqlExpressions.StrCat2)), acc, delimiter),
				ToStr(args[i], "strcat_delim"));
		return acc;
	}

	// format_bytes(size[, precision[, units]]) — 1024-based units, auto-selected by magnitude unless `units`
	// is given, precision defaulting to 0 (kusto/query/format-bytes-function: 10332,1 -> '10.1 KB';
	// 20010332 -> '19 MB'; 20010332,2 -> '19.08 MB'; 20010332,0,'KB' -> '19541 KB'). The ladder is folded as a
	// CASE over the magnitude and each rung renders with printf, which measured identical on SQLite and
	// DuckDB. `precision` and `units` are compile-time literals because they pick the format string and the
	// divisor.
	static Expr FormatBytes(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count is < 1 or > 3)
			throw new UnsupportedKqlException(
				$"format_bytes() takes 1 to 3 arguments (size[, precision[, units]]), got {args.Count}");
		var size = ToReal(args[0], "format_bytes");
		var digits = 0L;
		if (args.Count >= 2)
		{
			if (args[1] is not System.Linq.Expressions.ConstantExpression { Value: long literalDigits })
				throw new UnsupportedKqlException("format_bytes() precision must be an integer literal");
			digits = literalDigits;
		}
		if (digits is < 0 or > 15)
			throw new UnsupportedKqlException($"format_bytes() precision {digits} is out of range (0-15)");
		string? units = null;
		if (args.Count == 3)
		{
			if (args[2] is not System.Linq.Expressions.ConstantExpression { Value: string u })
				throw new UnsupportedKqlException(
					"format_bytes() units must be a string literal (Bytes, KB, MB, GB, TB, PB, EB), or '' for auto-selection");
			units = u.Length == 0 ? null : u;
		}
		return FormatBytesLadder(size, digits, units);
	}

	static readonly string[] ByteUnits = ["Bytes", "KB", "MB", "GB", "TB", "PB", "EB"];

	static Expr FormatBytesLadder(Expr size, long precision, string? units)
	{
		var format = "%." + precision.ToString(CultureInfo.InvariantCulture) + "f";
		Expr Rung(int k) => Expr.Call(SqlM(nameof(KqlSqlExpressions.FormatBytesRung)),
			Expr.Divide(size, Expr.Constant(Math.Pow(1024, k), typeof(double?))),
			Expr.Constant(format, typeof(string)),
			Expr.Constant(ByteUnits[k], typeof(string)));

		if (units is not null)
		{
			var k = Array.FindIndex(ByteUnits, u => string.Equals(u, units, StringComparison.OrdinalIgnoreCase));
			if (k < 0)
				throw new UnsupportedKqlException(
					$"format_bytes() unit '{units}' is not one of Bytes, KB, MB, GB, TB, PB, EB");
			return Rung(k);
		}

		// Auto-selection: the largest rung whose divisor does not exceed |size|; a null size stays null (every
		// rung then renders NULL, because printf(NULL) and the `||` concatenation are null-propagating).
		var acc = Rung(ByteUnits.Length - 1);
		for (var k = ByteUnits.Length - 2; k >= 0; k--)
			acc = Expr.Condition(
				Expr.LessThan(size, Expr.Constant(Math.Pow(1024, k + 1), typeof(double?)), liftToNull: false, method: null),
				Rung(k), acc);
		return acc;
	}

	// --- math scalars ---

	static Expr Abs(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		var a = OneArg(args, "abs");
		if (!KqlScalar.IsNumericValue(a.Type))
			throw new UnsupportedKqlException($"abs() requires a numeric argument, got {KqlScalar.FriendlyTypeName(a.Type)}");
		return KqlScalar.NonNullable(a.Type) == typeof(double)
			? Expr.Call(SqlM(nameof(KqlSqlExpressions.AbsDouble)), ToReal(a, "abs"))
			: Expr.Call(SqlM(nameof(KqlSqlExpressions.AbsLong)), LiftNumeric(a, typeof(long), "abs"));
	}

	static Expr Sign(IReadOnlyList<Expr> args, ScalarContext ctx) =>
		Expr.Call(SqlM(nameof(KqlSqlExpressions.Sign)), ToReal(OneArg(args, "sign"), "sign"));

	static Expr Pow(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 2)
			throw new UnsupportedKqlException($"pow() takes exactly 2 arguments (base, exponent), got {args.Count}");
		return Expr.Call(SqlM(nameof(KqlSqlExpressions.Pow)), ToReal(args[0], "pow"), ToReal(args[1], "pow"));
	}

	static Expr Round(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count is not (1 or 2))
			throw new UnsupportedKqlException($"round() takes 1 or 2 arguments (number[, precision]), got {args.Count}");
		var precision = args.Count == 2 ? RequireLong(args[1], "round() precision") : Expr.Constant(0L);
		return Expr.Call(SqlM(nameof(KqlSqlExpressions.RoundDouble)), ToReal(args[0], "round"), precision);
	}

	static Expr Pi(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 0)
			throw new UnsupportedKqlException($"pi() takes no arguments, got {args.Count}");
		return Expr.Call(SqlM(nameof(KqlSqlExpressions.Pi)));
	}

	// min_of/max_of(arg, arg_2, …) — 2..64 arguments of ONE type, non-null values taking precedence
	// (kusto/query/max-of-function). Numerics fold through a HasValue-guarded comparison (the same shape
	// NullGuard builds, so it translates identically); strings fold through the MinOfString/MaxOfString
	// shims, whose SQL compares with `>`/`<` (ordinal in both dialects) and whose body uses CompareOrdinal.
	static Expr MinMax(IReadOnlyList<Expr> args, ScalarContext ctx, string fn, bool max)
	{
		if (args.Count < 2)
			throw new UnsupportedKqlException($"{fn}() takes at least 2 arguments, got {args.Count}");
		if (args.Count > 64)
			throw new UnsupportedKqlException($"{fn}() supports at most 64 arguments, got {args.Count}");
		var t = Unified(args, fn);
		var isString = KqlScalar.NonNullable(t) == typeof(string);
		var isInstant = KqlScalar.NonNullable(t) == typeof(DateTime);
		var unified = args.Select(a => KqlScalar.ConvertToValue(a, t)).ToList();
		if (isInstant)
			unified = unified.Select(a => ctx.CoerceInstant(a)).ToList();
		var acc = unified[0];
		for (var i = 1; i < unified.Count; i++)
			acc = isString
				? Expr.Call(SqlM(max ? nameof(KqlSqlExpressions.MaxOfString) : nameof(KqlSqlExpressions.MinOfString)), acc, unified[i])
				: MinMaxNumeric(acc, unified[i], max);
		return acc;
	}

	static Expr MinMaxNumeric(Expr a, Expr b, bool max)
	{
		var cmp = max
			? Expr.GreaterThan(a, b, liftToNull: false, method: null)
			: Expr.LessThan(a, b, liftToNull: false, method: null);
		var pick = Expr.Condition(cmp, a, b);
		if (!KqlScalar.IsNullable(a.Type))
			return pick;
		return Expr.Condition(Expr.Property(a, "HasValue"),
			Expr.Condition(Expr.Property(b, "HasValue"), pick, a),
			b);
	}

	// coalesce(arg, arg_2, …) — "the first arg whose value isn't null (or not-empty for string expressions)"
	// (kusto/query/coalesce-function). A non-nullable argument always wins, so that fold is resolved at
	// COMPILE time.
	static Expr Coalesce(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count == 0)
			throw new UnsupportedKqlException("coalesce() takes at least 1 argument");
		if (args.Count > 64)
			throw new UnsupportedKqlException($"coalesce() supports at most 64 arguments, got {args.Count}");
		var t = Unified(args, fn: "coalesce");
		var acc = KqlScalar.ConvertToValue(args[0], t);
		for (var i = 1; i < args.Count; i++)
			acc = CoalescePair(acc, KqlScalar.ConvertToValue(args[i], t));
		return acc;
	}

	static Expr CoalescePair(Expr a, Expr b)
	{
		if (KqlScalar.NonNullable(a.Type) == typeof(string))
			return Expr.Call(SqlM(nameof(KqlSqlExpressions.CoalesceString)), a, b);
		return KqlScalar.IsNullable(a.Type)
			? Expr.Condition(Expr.Property(a, "HasValue"), a, b)
			: a;
	}

	// --- datetime parts / arithmetic ---

	// A calendar part of an instant as an integer (dayofmonth/dayofyear/monthofyear/hourofday/week_of_year
	// and the `dayofweek` TIMESPAN, which is `ticks = true`). The instant is bridged into the epoch-ms domain
	// the shims speak; `ticks` results are bridged back into the context's duration representation.
	static Expr InstantExtract(IReadOnlyList<Expr> args, ScalarContext ctx, string fn, string method, bool ticks = false)
	{
		var value = Expr.Call(SqlM(method), ToMs(ctx.CoerceInstant(OneArg(args, fn))));
		return ticks ? ctx.CoerceTicks(value) : value;
	}

	// datetime_part(part, datetime) — an integer part of the instant (kusto/query/datetime-part-function;
	// 2017-10-30 01:02:03.765 → year 2017, quarter 4, month 10, week_of_year 44, day 30, dayOfYear 303,
	// hour 1, minute 2, second 3, millisecond 765). microsecond/nanosecond are refused: the engine's instant
	// representation is an epoch-MILLISECOND long, so those digits do not exist.
	static Expr DateTimePart(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 2)
			throw new UnsupportedKqlException($"datetime_part() takes exactly 2 arguments (part, datetime), got {args.Count}");
		if (args[0] is not System.Linq.Expressions.ConstantExpression { Value: string part })
			throw new UnsupportedKqlException("datetime_part() part must be a string literal");
		var ms = ToMs(ctx.CoerceInstant(args[1]));
		Expr Call(string m) => Expr.Call(SqlM(m), ms);
		return part.ToLowerInvariant() switch
		{
			"year" => Call(nameof(KqlSqlExpressions.YearOfMs)),
			"quarter" => Call(nameof(KqlSqlExpressions.QuarterOfMs)),
			"month" => Call(nameof(KqlSqlExpressions.MonthOfMs)),
			"week_of_year" => Call(nameof(KqlSqlExpressions.WeekOfYearOfMs)),
			"day" => Call(nameof(KqlSqlExpressions.DayOfMonthOfMs)),
			"dayofyear" => Call(nameof(KqlSqlExpressions.DayOfYearOfMs)),
			"hour" => Call(nameof(KqlSqlExpressions.HourOfMs)),
			"minute" => Call(nameof(KqlSqlExpressions.MinuteOfMs)),
			"second" => Call(nameof(KqlSqlExpressions.SecondOfMs)),
			// ((ms % 1000) + 1000) % 1000 — the non-negative remainder, on both dialects (SQLite's % truncates
			// toward zero, so a pre-epoch instant would otherwise yield a negative millisecond).
			"millisecond" => Expr.Modulo(
				Expr.Add(Expr.Modulo(ms, Expr.Constant(1000L)), Expr.Constant(1000L)), Expr.Constant(1000L)),
			_ => throw UnsupportedPart("datetime_part", part,
				"year, quarter, month, week_of_year, day, dayofyear, hour, minute, second, millisecond"),
		};
	}

	static UnsupportedKqlException UnsupportedPart(string fn, string part, string supported) =>
		new($"{fn}() part '{part}' is not supported (supported: {supported}); microsecond/nanosecond are below the "
			+ "engine's millisecond instant resolution, and year/quarter/month arithmetic has no portable SQL "
			+ "translation (SQLite normalises an end-of-month overflow where Kusto clamps)");

	// datetime_add(part, amount, datetime) — a period multiplied by an amount, added to an instant
	// (kusto/query/datetime-add-function). FIXED-WIDTH parts are pure epoch-ms arithmetic and translate on
	// both dialects; the CALENDAR parts (year/quarter/month) are refused by name — see UnsupportedPart.
	static Expr DateTimeAdd(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count != 3)
			throw new UnsupportedKqlException($"datetime_add() takes exactly 3 arguments (part, amount, datetime), got {args.Count}");
		if (args[0] is not System.Linq.Expressions.ConstantExpression { Value: string part })
			throw new UnsupportedKqlException("datetime_add() part must be a string literal");
		var amount = RequireLong(args[1], "datetime_add() amount");
		var instant = ctx.CoerceInstant(args[2]);
		var asDateTime = instant.Type == typeof(DateTime);
		var ms = ToMs(instant);
		var added = part.ToLowerInvariant() switch
		{
			"week" => Offset(ms, amount, 604_800_000L),
			"day" => Offset(ms, amount, 86_400_000L),
			"hour" => Offset(ms, amount, 3_600_000L),
			"minute" => Offset(ms, amount, 60_000L),
			"second" => Offset(ms, amount, 1_000L),
			"millisecond" => Expr.Add(ms, amount),
			_ => throw UnsupportedPart("datetime_add", part,
				"week, day, hour, minute, second, millisecond"),
		};
		return FromMs(added, asDateTime);
	}

	static Expr Offset(Expr ms, Expr amount, long unitMs) =>
		Expr.Add(ms, Expr.Multiply(amount, Expr.Constant(unitMs)));

	// endofday/endofweek/endofmonth(date[, offset]) — the LAST instant of the containing day/week/month,
	// shifted by `offset` of those units (kusto/query/endofday-function: endofday(2017-01-01 10:10:17) is
	// 2017-01-01T23:59:59.9999999; endofweek is SATURDAY-anchored, so the week starts on the preceding
	// Sunday — the same anchor startofweek uses). The engine's resolution is a MILLISECOND, so `end` is
	// .999: Kusto's 7-digit .9999999 truncated to the storage resolution.
	static Expr EndOf(IReadOnlyList<Expr> args, ScalarContext ctx, string fn)
	{
		if (args.Count is not (1 or 2))
			throw new UnsupportedKqlException($"{fn}() takes 1 or 2 arguments (date[, offset]), got {args.Count}");
		var instant = ctx.CoerceInstant(args[0]);
		var asDateTime = instant.Type == typeof(DateTime);
		var ms = ToMs(instant);
		var offset = args.Count == 2 ? RequireLong(args[1], $"{fn}() offset") : Expr.Constant(0L);
		var last = fn switch
		{
			"endofday" => Expr.Add(Offset(Expr.Call(SqlM(nameof(KqlSqlExpressions.StartOfDayMs)), ms), offset, 86_400_000L),
				Expr.Constant(86_399_999L)),
			"endofweek" => Expr.Subtract(
				Offset(Expr.Call(SqlM(nameof(KqlSqlExpressions.StartOfWeekMs)), ms),
					Expr.Add(offset, Expr.Constant(1L)), 604_800_000L), Expr.Constant(1L)),
			_ => EndOfMonth(ms, offset),
		};
		return FromMs(last, asDateTime);
	}

	// The last millisecond of the containing month, for the two offsets that are EXACT in arithmetic: 0 =
	// start of the NEXT month minus one millisecond (any instant in month M plus 32 days lands in M+1, since
	// no month is longer than 31 days), -1 = start of THIS month minus one millisecond. Any other offset
	// needs calendar-month arithmetic, which has no portable translation (see UnsupportedPart).
	static Expr EndOfMonth(Expr ms, Expr offset)
	{
		if (TryConstLong(offset, out var n) && n is 0 or -1)
			return Expr.Subtract(
				Expr.Call(SqlM(nameof(KqlSqlExpressions.StartOfMonthMs)),
					n == 0 ? Expr.Add(ms, Expr.Constant(32 * 86_400_000L)) : ms),
				Expr.Constant(1L));
		throw new UnsupportedKqlException(
			"endofmonth() supports the offsets 0 and -1 only: other month offsets need calendar-month arithmetic, "
			+ "which has no portable SQL translation (SQLite normalises an end-of-month overflow where Kusto clamps)");
	}

	// A compile-time integer constant, folded through the unary-minus/convert wrappers a Kusto literal arrives
	// in (`-1` parses as UnaryMinus(1), so it is NOT a ConstantExpression on its own).
	static bool TryConstLong(Expr e, out long value)
	{
		switch (e)
		{
			case System.Linq.Expressions.ConstantExpression { Value: long v }:
				value = v;
				return true;
			case System.Linq.Expressions.UnaryExpression
			{
				NodeType: System.Linq.Expressions.ExpressionType.Negate,
				Operand: System.Linq.Expressions.ConstantExpression { Value: long v },
			}:
				value = -v;
				return true;
			case System.Linq.Expressions.UnaryExpression
			{
				NodeType: System.Linq.Expressions.ExpressionType.Convert,
				Operand: System.Linq.Expressions.ConstantExpression { Value: long v },
			}:
				value = v;
				return true;
			default:
				value = 0;
				return false;
		}
	}

	// make_datetime(year, month, day[, hour[, minute[, second]]]) — a datetime from calendar fields, folded at
	// COMPILE time like now()/ago() (kusto/query/make-datetime-function). .NET's DateTime constructor does the
	// validation, which no dialect offers portably: measured, SQLite silently NORMALISES '2024-02-31' to
	// 2024-03-02 while DuckDB raises — a silent year-shifting divergence this refuses to ship. Kusto yields
	// NULL for an impossible date; the engine has no null datetime literal, so the fields are refused by name
	// instead.
	static Expr MakeDateTime(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count is < 3 or > 6)
			throw new UnsupportedKqlException(
				$"make_datetime() takes 3 to 6 arguments (year, month, day[, hour[, minute[, second]]]), got {args.Count}");

		long Field(int i, string what)
		{
			if (args[i] is not System.Linq.Expressions.ConstantExpression { Value: long v })
				throw new UnsupportedKqlException(
					$"make_datetime() requires constant arguments (it is folded at compile time); {what} is not an integer literal");
			return v;
		}

		double FieldReal(int i, string what) => args[i] is System.Linq.Expressions.ConstantExpression c
			? c.Value switch
			{
				long l => l,
				double d => d,
				_ => throw new UnsupportedKqlException($"make_datetime() {what} must be a numeric literal"),
			}
			: throw new UnsupportedKqlException(
				$"make_datetime() requires constant arguments (it is folded at compile time); {what} is not a literal");

		var year = Field(0, "year");
		var month = Field(1, "month");
		var day = Field(2, "day");
		var hour = args.Count >= 4 ? Field(3, "hour") : 0;
		var minute = args.Count >= 5 ? Field(4, "minute") : 0;
		var second = args.Count >= 6 ? FieldReal(5, "second") : 0;
		if (year is < 1 or > 9999 || hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or >= 60)
			throw InvalidDateTime(year, month, day, hour, minute, second);
		DateTime dt;
		try
		{
			dt = new DateTime((int)year, (int)month, (int)day, (int)hour, (int)minute, 0, DateTimeKind.Utc)
				.AddMilliseconds(Math.Round(second * 1000, MidpointRounding.AwayFromZero));
		}
		catch (ArgumentOutOfRangeException)
		{
			throw InvalidDateTime(year, month, day, hour, minute, second);
		}
		return ctx.CoerceInstant(Expr.Constant(dt));
	}

	static UnsupportedKqlException InvalidDateTime(long y, long mo, long d, long h, long mi, double s) =>
		new($"make_datetime({y}, {mo}, {d}, {h}, {mi}, {s.ToString(CultureInfo.InvariantCulture)}) is not a valid "
			+ "datetime (Kusto yields null here; the engine folds make_datetime at compile time and refuses rather "
			+ "than shifting the date)");

	// make_timespan(hour, minute[, second]) / make_timespan(day, hour, minute, second) — a duration from its
	// parts (kusto/query/make-timespan-function). Hour 0-23, minute 0-59, second < 60 — out of range yields NULL,
	// which the shim's CASE reproduces on both dialects (units the arity does not carry are 0). The result is a
	// TIMESPAN: ticks in the SQL/record domain, TimeSpan in the row domain (CoerceTicks).
	static Expr MakeTimespan(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count is not (2 or 3 or 4))
			throw new UnsupportedKqlException(
				$"make_timespan() takes 2, 3 or 4 arguments — (hour, minute[, second]) or (day, hour, minute, second) — got {args.Count}");
		Expr day, hour, minute;
		var secondIndex = -1;
		if (args.Count == 4)
		{
			day = RequireLong(args[0], "make_timespan() day");
			hour = RequireLong(args[1], "make_timespan() hour");
			minute = RequireLong(args[2], "make_timespan() minute");
			secondIndex = 3;
		}
		else
		{
			day = Expr.Constant(0L);
			hour = RequireLong(args[0], "make_timespan() hour");
			minute = RequireLong(args[1], "make_timespan() minute");
			secondIndex = args.Count == 3 ? 2 : -1;
		}
		var second = secondIndex < 0
			? Expr.Constant(0.0)
			: (Expr)Expr.Convert(LiftNumeric(args[secondIndex], typeof(double), "make_timespan"), typeof(double));
		return ctx.CoerceTicks(Expr.Call(SqlM(nameof(KqlSqlExpressions.MakeTimespanTicks)), day, hour, minute, second));
	}

	// --- tohex ---

	// tohex(value[, minLength]) — the two's-complement hex rendering (kusto/query/tohex-function: tohex(256) =
	// '100', tohex(-256) = 'ffffffffffffff00', tohex(256, 8) = '00000100'). minLength is a compile-time
	// literal (1-16; it picks the printf width) and, per Kusto, is a MINIMUM — a longer rendering is never
	// truncated — so it is applied as the printf/ToString width. An int argument renders its 32-bit two's
	// complement ('ffffff00'), a long its 64-bit one.
	static Expr ToHex(IReadOnlyList<Expr> args, ScalarContext ctx)
	{
		if (args.Count is not (1 or 2))
			throw new UnsupportedKqlException($"tohex() takes 1 or 2 arguments (value[, minLength]), got {args.Count}");
		var value = args[0];
		var integral = KqlScalar.NonNullable(value.Type);
		if (integral != typeof(long) && integral != typeof(int))
			throw new UnsupportedKqlException(
				$"tohex() requires an integer argument, got {KqlScalar.FriendlyTypeName(value.Type)}");
		long minLength = 0;
		if (args.Count == 2)
		{
			if (args[1] is not System.Linq.Expressions.ConstantExpression { Value: long n })
				throw new UnsupportedKqlException("tohex() minLength must be an integer literal");
			minLength = Math.Clamp(n, 0, 16);
		}
		var format = minLength == 0
			? "%x"
			: "%0" + minLength.ToString(CultureInfo.InvariantCulture) + "x";
		var method = integral == typeof(int)
			? nameof(KqlSqlExpressions.ToHexInt)
			: nameof(KqlSqlExpressions.ToHexLong);
		return Expr.Call(SqlM(method), LiftNumeric(value, typeof(long), "tohex"), Expr.Constant(format, typeof(string)));
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

	// The null-propagating in-memory counterpart (a todatetime() result is DateTime? in the row context):
	// null in → null out, i.e. its own bucket. A separate NAME, not an overload — Method() resolves by
	// GetMethod(name). The step guard still fires for a non-null value, so a bad step stays loud.
	static DateTime? BinDateTimeN(DateTime? v, TimeSpan step) =>
		v is { } dt ? BinDateTime(dt, step) : null;

}
