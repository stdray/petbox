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

// RESEARCH PROBE — MICRO-PROBE C (spec: kql-single-path-impl): the LAST un-probed gate for the
// single-SQL-path rewrite — the hardest op, join/lookup. Spike A proved project/extend/where/summarize/
// order compose as runtime-shaped SQL; this proves the dynamic EQUI-JOIN shape does too. Mirrors Spike
// A's mechanism (Reflection.Emit result type + hand-built expression trees), joining two seeded [Table]
// sources on a key and projecting a RUNTIME-shaped result:
//   INNER      → Queryable.Join                                  (expect INNER JOIN)
//   LEFTOUTER  → Queryable.GroupJoin + SelectMany + DefaultIfEmpty (expect LEFT JOIN, no apply/client-eval)
// Both dump the executed SQL and assert the joined rows. Tagged Research so it never runs in the default
// test/CI path (see build.cs Filter="Category!=Research").
[Trait("Category", "Research")]
public sealed class SqliteDynamicJoinProbeTests : IDisposable
{
	readonly ITestOutputHelper _output;
	readonly string _tempDir;
	readonly string _dbPath;
	readonly DataConnection _db;

	[Table("orders")]
	public sealed class OrderRow
	{
		[Column] public long Id { get; set; }
		[Column("cust_key")] public string CustKey { get; set; } = "";
		[Column] public double Amount { get; set; }
	}

	[Table("customers")]
	public sealed class CustRow
	{
		[Column("c_key")] public string CKey { get; set; } = "";
		[Column] public string Name { get; set; } = "";
	}

	// Compile-time intermediate holder for the GroupJoin stage (its shape is fixed: an outer row plus the
	// grouped inner rows). Only the FINAL projected result is runtime-emitted — matching the ask ("two
	// seeded [Table] sources, projecting a runtime-shaped result").
	public sealed class GjHolder
	{
		public OrderRow O { get; set; } = null!;
		public IEnumerable<CustRow> G { get; set; } = null!;
	}

	public SqliteDynamicJoinProbeTests(ITestOutputHelper output)
	{
		_output = output;
		_tempDir = Path.Combine(Path.GetTempPath(), "petbox-probeC-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_dbPath = Path.Combine(_tempDir, "probe.db");

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			using var cmd = conn.CreateCommand();
			// order 3 (key 'z') has no matching customer → dropped by inner, null-filled by leftouter.
			cmd.CommandText = """
				CREATE TABLE orders(id INTEGER, cust_key TEXT, amount REAL);
				CREATE TABLE customers(c_key TEXT, name TEXT);
				INSERT INTO orders VALUES (1,'a',10.0),(2,'b',20.0),(3,'z',30.0);
				INSERT INTO customers VALUES ('a','Alice'),('b','Bob');
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

	// INNER: Queryable.Join over a runtime-emitted result type.
	[Fact]
	public void C1_InnerJoin_RuntimeShapedResult()
	{
		var orderRow = typeof(OrderRow);
		var custRow = typeof(CustRow);
		var tRes = EmitRowType("JRes", [("OrderId", typeof(long)), ("Cust", typeof(string)), ("Name", typeof(string))]);

		var outer = _db.GetTable<OrderRow>();
		var inner = _db.GetTable<CustRow>();

		// (o, c) => new JRes { OrderId = o.Id, Cust = o.CustKey, Name = c.Name }
		var oP = Expression.Parameter(orderRow, "o");
		var cP = Expression.Parameter(custRow, "c");
		var resultSelector = Expression.Lambda(
			MemberInit(tRes,
			[
				("OrderId", Expression.Property(oP, orderRow.GetProperty("Id")!)),
				("Cust", Expression.Property(oP, orderRow.GetProperty("CustKey")!)),
				("Name", Expression.Property(cP, custRow.GetProperty("Name")!)),
			]), oP, cP);

		var join = QueryableMethod("Join", 4, [orderRow, custRow, typeof(string), tRes],
			m => m.GetParameters().Length == 5);
		var call = Expression.Call(join,
			outer.Expression,
			Expression.Constant(inner),
			Expression.Quote(KeyLambda(orderRow, orderRow.GetProperty("CustKey")!)),
			Expression.Quote(KeyLambda(custRow, custRow.GetProperty("CKey")!)),
			Expression.Quote(resultSelector));
		var q = outer.Provider.CreateQuery(call);

		var rows = Materialize(q, tRes);
		_output.WriteLine("PROBE C1 (inner join) executed SQL:");
		_output.WriteLine(_db.LastQuery);
		_output.WriteLine("PROBE C1 rows: " + string.Join(", ", rows.Select(r => $"{r["OrderId"]}:{r["Name"]}")));

		var pairs = rows.Select(r => ((long)r["OrderId"]!, (string?)r["Name"])).ToList();
		pairs.Should().BeEquivalentTo(new[] { (1L, "Alice"), (2L, "Bob") });

		var sql = (_db.LastQuery ?? "").ToUpperInvariant();
		sql.Should().Contain("JOIN", "runtime-shaped inner join must render a SQL JOIN");
		sql.Should().NotContain("APPLY", "must not degrade to a correlated APPLY (SQLite would reject it)");
		_output.WriteLine("PROBE C1 VERDICT: EMITS-CLEAN — runtime-shaped INNER JOIN as one SQL query.");
	}

	// LEFTOUTER: GroupJoin + SelectMany + DefaultIfEmpty over a runtime-emitted result type.
	[Fact]
	public void C2_LeftOuterJoin_RuntimeShapedResult()
	{
		var orderRow = typeof(OrderRow);
		var custRow = typeof(CustRow);
		var holder = typeof(GjHolder);
		var tRes = EmitRowType("LRes", [("OrderId", typeof(long)), ("Cust", typeof(string)), ("Name", typeof(string))]);

		var outer = _db.GetTable<OrderRow>();
		var inner = _db.GetTable<CustRow>();

		// GroupJoin(outer, inner, o => o.CustKey, c => c.CKey, (o, g) => new GjHolder { O = o, G = g })
		var oP = Expression.Parameter(orderRow, "o");
		var gP = Expression.Parameter(typeof(IEnumerable<CustRow>), "g");
		var gjResult = Expression.Lambda(
			Expression.MemberInit(Expression.New(holder),
				Expression.Bind(holder.GetProperty("O")!, oP),
				Expression.Bind(holder.GetProperty("G")!, gP)),
			oP, gP);

		var groupJoin = QueryableMethod("GroupJoin", 4, [orderRow, custRow, typeof(string), holder],
			m => m.GetParameters().Length == 5);
		var gjCall = Expression.Call(groupJoin,
			outer.Expression,
			Expression.Constant(inner),
			Expression.Quote(KeyLambda(orderRow, orderRow.GetProperty("CustKey")!)),
			Expression.Quote(KeyLambda(custRow, custRow.GetProperty("CKey")!)),
			Expression.Quote(gjResult));
		var grouped = outer.Provider.CreateQuery(gjCall);

		// SelectMany(h => h.G.DefaultIfEmpty(), (h, c) => new LRes { OrderId=h.O.Id, Cust=h.O.CustKey, Name=c.Name })
		var hP = Expression.Parameter(holder, "h");
		var defaultIfEmpty = typeof(Enumerable).GetMethods()
			.Single(m => m.Name == "DefaultIfEmpty" && m.GetParameters().Length == 1)
			.MakeGenericMethod(custRow);
		var collectionSelector = Expression.Lambda(
			Expression.Call(defaultIfEmpty, Expression.Property(hP, holder.GetProperty("G")!)), hP);

		var hP2 = Expression.Parameter(holder, "h");
		var cP2 = Expression.Parameter(custRow, "c");
		var oProp = Expression.Property(hP2, holder.GetProperty("O")!);
		var manyResult = Expression.Lambda(
			MemberInit(tRes,
			[
				("OrderId", Expression.Property(oProp, orderRow.GetProperty("Id")!)),
				("Cust", Expression.Property(oProp, orderRow.GetProperty("CustKey")!)),
				("Name", Expression.Property(cP2, custRow.GetProperty("Name")!)),
			]), hP2, cP2);

		var selectMany = QueryableMethod("SelectMany", 3, [holder, custRow, tRes],
			m => m.GetParameters().Length == 3
				&& m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);
		var smCall = Expression.Call(selectMany,
			grouped.Expression,
			Expression.Quote(collectionSelector),
			Expression.Quote(manyResult));
		var q = grouped.Provider.CreateQuery(smCall);

		var rows = Materialize(q, tRes);
		_output.WriteLine("PROBE C2 (left outer join) executed SQL:");
		_output.WriteLine(_db.LastQuery);
		_output.WriteLine("PROBE C2 rows: " + string.Join(", ", rows.Select(r => $"{r["OrderId"]}:{r["Name"] ?? "<null>"}")));

		var pairs = rows.Select(r => ((long)r["OrderId"]!, (string?)r["Name"])).ToList();
		pairs.Should().BeEquivalentTo(new[] { (1L, "Alice"), (2L, "Bob"), (3L, (string?)null) });

		var sql = (_db.LastQuery ?? "").ToUpperInvariant();
		sql.Should().Contain("LEFT JOIN", "left-outer must render a SQL LEFT JOIN, not an apply/subquery-per-row");
		sql.Should().NotContain("APPLY", "must not degrade to a correlated APPLY (SQLite would reject it)");
		_output.WriteLine("PROBE C2 VERDICT: EMITS-CLEAN — runtime-shaped LEFT JOIN as one SQL query, no apply.");
	}

	// ============================ dynamic-shape helpers (mirror Spike A) ============================

	static readonly ModuleBuilder Module =
		AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("KqlDynJoin"), AssemblyBuilderAccess.Run)
			.DefineDynamicModule("main");

	static Type EmitRowType(string name, IReadOnlyList<(string Name, Type Type)> fields)
	{
		var tb = Module.DefineType(name + "_" + Guid.NewGuid().ToString("N")[..8],
			TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.AutoLayout | TypeAttributes.AnsiClass);
		tb.DefineDefaultConstructor(MethodAttributes.Public);
		foreach (var (fname, ftype) in fields)
		{
			var fb = tb.DefineField("_" + fname, ftype, FieldAttributes.Private);
			var pb = tb.DefineProperty(fname, PropertyAttributes.None, ftype, null);
			var getter = tb.DefineMethod("get_" + fname,
				MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, ftype, Type.EmptyTypes);
			var gil = getter.GetILGenerator();
			gil.Emit(OpCodes.Ldarg_0); gil.Emit(OpCodes.Ldfld, fb); gil.Emit(OpCodes.Ret);
			pb.SetGetMethod(getter);
			var setter = tb.DefineMethod("set_" + fname,
				MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, null, [ftype]);
			var sil = setter.GetILGenerator();
			sil.Emit(OpCodes.Ldarg_0); sil.Emit(OpCodes.Ldarg_1); sil.Emit(OpCodes.Stfld, fb); sil.Emit(OpCodes.Ret);
			pb.SetSetMethod(setter);
		}
		return tb.CreateType();
	}

	static Expression MemberInit(Type outType, IReadOnlyList<(string Prop, Expression Value)> binds)
	{
		var bindings = binds.Select(b => (MemberBinding)Expression.Bind(outType.GetProperty(b.Prop)!, b.Value)).ToArray();
		return Expression.MemberInit(Expression.New(outType), bindings);
	}

	// x => x.<keyProp>  (a single-column key selector).
	static LambdaExpression KeyLambda(Type inType, PropertyInfo keyProp)
	{
		var p = Expression.Parameter(inType, "x");
		return Expression.Lambda(Expression.Property(p, keyProp), p);
	}

	static MethodInfo QueryableMethod(string name, int genericArgCount, Type[] typeArgs, Func<MethodInfo, bool> extra) =>
		typeof(Queryable).GetMethods()
			.Single(m => m.Name == name
				&& m.IsGenericMethodDefinition
				&& m.GetGenericArguments().Length == genericArgCount
				&& extra(m))
			.MakeGenericMethod(typeArgs);

	// Enumerate a non-generic IQueryable and reflect each row's properties into a dictionary.
	static List<Dictionary<string, object?>> Materialize(IQueryable q, Type elementType)
	{
		var props = elementType.GetProperties();
		var rows = new List<Dictionary<string, object?>>();
		foreach (var o in (IEnumerable)q)
		{
			var d = new Dictionary<string, object?>();
			foreach (var p in props)
				d[p.Name] = p.GetValue(o);
			rows.Add(d);
		}
		return rows;
	}
}
