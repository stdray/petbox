using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using PetBox.Log.Core.Data;

namespace PetBox.Tests.Kql;

// INFRA PROBE — proves the vendored sqlean `regexp` extension (nalgeon/sqlean 0.28.3) is loaded on every
// SQLite connection a LogDb opens, via LoadSqleanRegexpInterceptor (wired in LogDb.CreateOptions next to
// RegisterKqlFunctionsInterceptor). This exercises the REAL prod connection path: we build a LogDb through
// LogDb.CreateOptions and run raw scalar SQL over its linq2db connection, so if the interceptor did not
// load sqlean these `regexp_*` calls would throw "no such function".
//
// The KQL translator does NOT use sqlean yet (the .NET UDFs remain the live regex path); this test only
// guards the substrate so the later mapping swap has a proven, loaded foundation. Sqlite-backend only.
public sealed class SqleanRegexpProbeTests : IDisposable
{
	readonly SqliteConnection _keepAlive;
	readonly LogDb _db;

	public SqleanRegexpProbeTests()
	{
		// Shared-cache in-memory DB kept alive for the fixture; LogDb opens its own connections to the same
		// name, and each open triggers LoadSqleanRegexpInterceptor to load sqlean regexp on that connection.
		var connectionString = $"Data Source=file:petbox-sqlean-{Guid.NewGuid():N}?mode=memory&cache=shared";
		_keepAlive = new SqliteConnection(connectionString);
		_keepAlive.Open();
		_db = new LogDb(LogDb.CreateOptions(connectionString));
	}

	public void Dispose()
	{
		_db.Dispose();
		_keepAlive.Dispose();
	}

	[Fact]
	public void RegexpLike_MatchAndNoMatch()
	{
		_db.Execute<long>("SELECT regexp_like('abc', 'b')").Should().Be(1);
		_db.Execute<long>("SELECT regexp_like('abc', 'z')").Should().Be(0);
	}

	[Fact]
	public void RegexpLike_NullSubject_IsFalse()
	{
		// sqlean regexp_like on a NULL subject yields 0 (not NULL) — the shape the later KQL mapping relies on.
		_db.Execute<long>("SELECT regexp_like(NULL, 'b')").Should().Be(0);
	}

	[Fact]
	public void RegexpCapture_Group1_ExtractsValue()
	{
		_db.Execute<string?>("SELECT regexp_capture('user=42', 'user=([0-9]+)', 1)").Should().Be("42");
	}

	[Fact]
	public void RegexpCapture_Group0_IsWholeMatch()
	{
		_db.Execute<string?>("SELECT regexp_capture('user=42', 'user=([0-9]+)', 0)").Should().Be("user=42");
	}

	[Fact]
	public void RegexpCapture_NoMatch_IsNull()
	{
		_db.Execute<string?>("SELECT regexp_capture('nope', 'user=([0-9]+)', 1)").Should().BeNull();
	}

	[Fact]
	public void RegexpLike_UnicodePropertyClass_Matches()
	{
		// \p{L} (any Unicode letter) must match the Greek capital omega — proves sqlean's PCRE-style engine,
		// not SQLite's absent builtin.
		_db.Execute<long>("SELECT regexp_like('Ω', '\\p{L}')").Should().Be(1);
	}

	[Fact]
	public void RegexpLike_WordBoundary_Matches()
	{
		_db.Execute<long>("SELECT regexp_like('foo bar', '\\bbar\\b')").Should().Be(1);
		_db.Execute<long>("SELECT regexp_like('foobar', '\\bbar\\b')").Should().Be(0);
	}
}
