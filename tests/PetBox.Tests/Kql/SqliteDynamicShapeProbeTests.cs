using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace PetBox.Tests.Kql;

// RESEARCH PROBE — SPIKE A (spec: kql-single-path-impl): the CORE architectural blocker for the
// single-SQL-path KQL rewrite. Today every shape-changing stage grows/renames an object?[] row whose
// SCHEMA is discovered at KQL-parse time (unknown at C# compile time). An IQueryable/linq2db rewrite
// needs a concrete CLR element type per stage. This probe answers: can linq2db build a CHAINED pipeline
// where each stage's element TYPE is decided at RUNTIME (Reflection.Emit types + hand-built expression
// trees, NOT compile-time anonymous types) and STILL emit ONE clean nested SQL query on SQLite —
// without falling back to client-eval or throwing?
//
// Logical chain (the mv-expand/parse/join half is proven separately):
//   project  (pick+rename a runtime-chosen subset: Level→K, Val→V)
//   extend   (add computed column V2 = V * 2)
//   where    (on the PROJECTED/computed column: V2 > 3)
//   summarize count() by <runtime key K>
//   order by the aggregate (Cnt desc)
//
// It runs the SAME chain TWICE: once with COMPILE-TIME POCOs (the control — isolates linq2db behavior
// from bugs in my expression-tree building), once with RUNTIME-EMITTED types (the real proof). Both dump
// their SQL and assert identical results. Tagged Research so it never runs in the default test/CI path.
[Trait("Category", "Research")]
public sealed class SqliteDynamicShapeProbeTests : IDisposable
{
	readonly ITestOutputHelper _output;
	readonly string _tempDir;
	readonly string _dbPath;
	readonly DataConnection _db;

	[Table("log")]
	public sealed class LogRow
	{
		[Column] public long Id { get; set; }
		[Column] public string Level { get; set; } = "";
		[Column] public double Val { get; set; }
	}

	// Compile-time control types (the baseline — NOT the dynamic proof).
	public sealed class S1 { public string K { get; set; } = ""; public double V { get; set; } }
	public sealed class S2 { public string K { get; set; } = ""; public double V { get; set; } public double V2 { get; set; } }
	public sealed class S4 { public string K { get; set; } = ""; public long Cnt { get; set; } }

	public SqliteDynamicShapeProbeTests(ITestOutputHelper output)
	{
		_output = output;
		_tempDir = Path.Combine(Path.GetTempPath(), "petbox-spikeA-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_dbPath = Path.Combine(_tempDir, "probe.db");

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			using var cmd = conn.CreateCommand();
			// V2 = Val*2 > 3 keeps rows 2..6; group by Level → ERROR×2, INFO×2, WARN×1.
			cmd.CommandText = """
				CREATE TABLE log(id INTEGER, level TEXT, val REAL);
				INSERT INTO log VALUES
					(1, 'INFO',  1.0),
					(2, 'ERROR', 2.5),
					(3, 'INFO',  3.0),
					(4, 'ERROR', 4.5),
					(5, 'INFO',  5.0),
					(6, 'WARN',  6.5);
				""";
			cmd.ExecuteNonQuery();
		}

		_db = new DataConnection(new DataOptions().UseSQLite($"Data Source={_dbPath}"));
	}

	public void Dispose()
	{
		_db.Dispose();
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
	}

	// The expected result of BOTH chains (order by Cnt desc; the two 2-counts tie).
	static readonly (string K, long Cnt)[] Expected = [("ERROR", 2), ("INFO", 2), ("WARN", 1)];

	// ---- CONTROL: the same chain with COMPILE-TIME POCOs. Proves the CHAIN SHAPE composes to nested
	// SQL, independent of any runtime type-emit / expression-tree building. ----
	[Fact]
	public void A0_CompileTimeChain_Control()
	{
		var q = _db.GetTable<LogRow>()
			.Select(r => new S1 { K = r.Level, V = r.Val })
			.Select(s => new S2 { K = s.K, V = s.V, V2 = s.V * 2 })
			.Where(s => s.V2 > 3.0)
			.GroupBy(s => s.K)
			.Select(g => new S4 { K = g.Key, Cnt = g.Count() })
			.OrderByDescending(x => x.Cnt);

		var sql = q.ToSqlQuery().Sql;
		_output.WriteLine("SPIKE A0 (compile-time control) SQL:");
		_output.WriteLine(sql);

		var rows = q.ToList().Select(x => (x.K, x.Cnt)).ToList();
		_output.WriteLine("SPIKE A0 rows: " + string.Join(", ", rows.Select(r => $"{r.K}={r.Cnt}")));
		rows.Should().BeEquivalentTo(Expected);
	}

	// ---- THE REAL PROOF: the SAME chain with RUNTIME-EMITTED types built via Reflection.Emit, wired
	// with hand-built expression trees (Queryable.Select/Where/GroupBy/OrderByDescending calls). No
	// compile-time knowledge of S1/S2/S4 shapes. ----
	[Fact]
	public void A1_DynamicChain_RuntimeEmittedTypes()
	{
		var logRow = typeof(LogRow);

		// Stage types decided at RUNTIME (as they would be from a parsed KQL pipeline).
		var t1 = EmitRowType("S1", [("K", typeof(string)), ("V", typeof(double))]);
		var t2 = EmitRowType("S2", [("K", typeof(string)), ("V", typeof(double)), ("V2", typeof(double))]);
		var t4 = EmitRowType("S4", [("K", typeof(string)), ("Cnt", typeof(long))]);

		IQueryable q = _db.GetTable<LogRow>();

		// 1) project: new S1 { K = r.Level, V = r.Val }
		q = Select(q, logRow, t1, p =>
		[
			("K", Expression.Property(p, logRow.GetProperty("Level")!)),
			("V", Expression.Property(p, logRow.GetProperty("Val")!)),
		]);

		// 2) extend: new S2 { K = p.K, V = p.V, V2 = p.V * 2 }
		q = Select(q, t1, t2, p =>
		[
			("K", Expression.Property(p, t1.GetProperty("K")!)),
			("V", Expression.Property(p, t1.GetProperty("V")!)),
			("V2", Expression.Multiply(Expression.Property(p, t1.GetProperty("V")!), Expression.Constant(2.0))),
		]);

		// 3) where: p.V2 > 3.0  (predicate over a PROJECTED/computed column)
		q = Where(q, t2, p =>
			Expression.GreaterThan(Expression.Property(p, t2.GetProperty("V2")!), Expression.Constant(3.0)));

		// 4) summarize count() by K:  GroupBy(p => p.K).Select(g => new S4 { K = g.Key, Cnt = g.Count() })
		var grouped = GroupBy(q, t2, typeof(string), p => Expression.Property(p, t2.GetProperty("K")!));
		var groupingType = typeof(IGrouping<,>).MakeGenericType(typeof(string), t2);
		q = Select(grouped, groupingType, t4, g =>
		[
			("K", Expression.Property(g, groupingType.GetProperty("Key")!)),
			// Enumerable.Count<T> returns int; the KQL count() column is long — widen so the member-init
			// binds to the long Cnt property (SQL side casts the aggregate to INTEGER either way).
			("Cnt", Expression.Convert(Expression.Call(CountMethod(t2), g), typeof(long))),
		]);

		// 5) order by Cnt desc
		q = OrderByDescending(q, t4, typeof(long), x => Expression.Property(x, t4.GetProperty("Cnt")!));

		// Enumerate the non-generic IQueryable FIRST (ToSqlQuery is generic-only and cannot bind a
		// runtime element type), then read the verbatim executed SQL off the connection. Enumerating
		// also proves linq2db actually ran ONE SQL statement rather than client-evaluating.
		var kProp = t4.GetProperty("K")!;
		var cntProp = t4.GetProperty("Cnt")!;
		var rows = new List<(string, long)>();
		foreach (var o in (IEnumerable)q)
			rows.Add(((string)kProp.GetValue(o)!, Convert.ToInt64(cntProp.GetValue(o))));

		_output.WriteLine("SPIKE A1 (runtime-emitted-type dynamic chain) executed SQL:");
		_output.WriteLine(_db.LastQuery);
		_output.WriteLine("SPIKE A1 rows: " + string.Join(", ", rows.Select(r => $"{r.Item1}={r.Item2}")));
		rows.Should().BeEquivalentTo(Expected);

		_output.WriteLine("SPIKE A1 VERDICT: EMITS-CLEAN — a 5-stage chain whose per-stage element TYPES "
			+ "were emitted at runtime composed into one nested SQL query on SQLite and returned the "
			+ "expected grouped/ordered result.");
	}

	// =====================================================================================
	// Runtime type emission + expression-tree query building helpers.
	// =====================================================================================

	static readonly ModuleBuilder Module =
		AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("KqlDynShapes"), AssemblyBuilderAccess.Run)
			.DefineDynamicModule("main");

	// Emits a public class with the given public auto-properties. linq2db maps a projection target's
	// public properties to SQL SELECT columns; no [Column]/[Table] attrs are needed for a projection
	// element type (only the root [Table] LogRow needs them).
	static Type EmitRowType(string name, IReadOnlyList<(string Name, Type Type)> fields)
	{
		var tb = Module.DefineType(name + "_" + Guid.NewGuid().ToString("N")[..8],
			TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.AutoLayout | TypeAttributes.AnsiClass);
		// Parameterless ctor (member-init needs it).
		tb.DefineDefaultConstructor(MethodAttributes.Public);

		foreach (var (fname, ftype) in fields)
		{
			var fb = tb.DefineField("_" + fname, ftype, FieldAttributes.Private);
			var pb = tb.DefineProperty(fname, PropertyAttributes.None, ftype, null);

			var getter = tb.DefineMethod("get_" + fname,
				MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
				ftype, Type.EmptyTypes);
			var gil = getter.GetILGenerator();
			gil.Emit(OpCodes.Ldarg_0);
			gil.Emit(OpCodes.Ldfld, fb);
			gil.Emit(OpCodes.Ret);
			pb.SetGetMethod(getter);

			var setter = tb.DefineMethod("set_" + fname,
				MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
				null, [ftype]);
			var sil = setter.GetILGenerator();
			sil.Emit(OpCodes.Ldarg_0);
			sil.Emit(OpCodes.Ldarg_1);
			sil.Emit(OpCodes.Stfld, fb);
			sil.Emit(OpCodes.Ret);
			pb.SetSetMethod(setter);
		}
		return tb.CreateType();
	}

	// source.Select(p => new TOut { prop = value, ... }) built entirely from expression trees.
	static IQueryable Select(IQueryable source, Type inType, Type outType,
		Func<ParameterExpression, IReadOnlyList<(string Prop, Expression Value)>> body)
	{
		var p = Expression.Parameter(inType, "x");
		var bindings = body(p)
			.Select(m => (MemberBinding)Expression.Bind(outType.GetProperty(m.Prop)!, m.Value))
			.ToArray();
		var init = Expression.MemberInit(Expression.New(outType), bindings);
		var lambda = Expression.Lambda(init, p);
		var method = QueryableMethod("Select", 2, [inType, outType],
			m => m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);
		var call = Expression.Call(method, source.Expression, Expression.Quote(lambda));
		return source.Provider.CreateQuery(call);
	}

	static IQueryable Where(IQueryable source, Type inType, Func<ParameterExpression, Expression> predicate)
	{
		var p = Expression.Parameter(inType, "x");
		var lambda = Expression.Lambda(predicate(p), p);
		var method = QueryableMethod("Where", 1, [inType],
			m => m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);
		var call = Expression.Call(method, source.Expression, Expression.Quote(lambda));
		return source.Provider.CreateQuery(call);
	}

	static IQueryable GroupBy(IQueryable source, Type inType, Type keyType, Func<ParameterExpression, Expression> keySelector)
	{
		var p = Expression.Parameter(inType, "x");
		var lambda = Expression.Lambda(keySelector(p), p);
		var method = QueryableMethod("GroupBy", 2, [inType, keyType],
			m => m.GetParameters().Length == 2
				&& m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);
		var call = Expression.Call(method, source.Expression, Expression.Quote(lambda));
		return source.Provider.CreateQuery(call);
	}

	static IQueryable OrderByDescending(IQueryable source, Type inType, Type keyType, Func<ParameterExpression, Expression> keySelector)
	{
		var p = Expression.Parameter(inType, "x");
		var lambda = Expression.Lambda(keySelector(p), p);
		var method = QueryableMethod("OrderByDescending", 2, [inType, keyType],
			m => m.GetParameters().Length == 2);
		var call = Expression.Call(method, source.Expression, Expression.Quote(lambda));
		return source.Provider.CreateQuery(call);
	}

	// g.Count() over an IGrouping<TKey,TElement> → Enumerable.Count<TElement>(IEnumerable<TElement>).
	static MethodInfo CountMethod(Type elementType) =>
		typeof(Enumerable).GetMethods()
			.Single(m => m.Name == "Count" && m.GetParameters().Length == 1)
			.MakeGenericMethod(elementType);

	static MethodInfo QueryableMethod(string name, int genericArgCount, Type[] typeArgs, Func<MethodInfo, bool> extra) =>
		typeof(Queryable).GetMethods()
			.Single(m => m.Name == name
				&& m.IsGenericMethodDefinition
				&& m.GetGenericArguments().Length == genericArgCount
				&& m.GetParameters().Length == 2
				&& extra(m))
			.MakeGenericMethod(typeArgs);
}
