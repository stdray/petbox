using PetBox.Data.Schema;

namespace PetBox.Tests.Data;

// Contract tests for SqlNormalizer.Hash. The hash is the source of truth for
// SqliteHashingJournal idempotency — when a pet re-applies a migration name,
// matching hash = no-op (200), different hash = conflict (409).
//
// Two contract dimensions:
// 1. Equivalence: cosmetically-different SQL with same semantics → same hash.
// 2. Non-equivalence: semantically-different SQL → different hash.
//
// If we ever swap SQL.Formatter for a different normalizer, these tests stay
// (they document the contract, not the implementation).
public sealed class SqlNormalizerTests
{
	// --- Equivalence: should hash IDENTICALLY ---

	[Theory]
	[InlineData("CREATE TABLE x (id INTEGER)", "create table x (id integer)")]
	[InlineData("SELECT * FROM votes", "select * from votes")]
	public void Hash_CaseInsensitiveKeywords(string a, string b)
	{
		SqlNormalizer.Hash(a).Should().Be(SqlNormalizer.Hash(b));
	}

	[Theory]
	[InlineData("CREATE TABLE x (id INTEGER)", "CREATE  TABLE  x  (id INTEGER)")]
	[InlineData("CREATE TABLE x (id INTEGER)", "CREATE\tTABLE x (id INTEGER)")]
	[InlineData("CREATE TABLE x (id INTEGER)", "  CREATE TABLE x (id INTEGER)  ")]
	public void Hash_IgnoresExtraWhitespace(string a, string b)
	{
		SqlNormalizer.Hash(a).Should().Be(SqlNormalizer.Hash(b));
	}

	[Theory]
	[InlineData("CREATE TABLE x (id INTEGER)\n", "CREATE TABLE x (id INTEGER)\r\n")]
	[InlineData("CREATE TABLE a (id INTEGER);\nCREATE TABLE b (id INTEGER)",
				"CREATE TABLE a (id INTEGER);\r\nCREATE TABLE b (id INTEGER)")]
	public void Hash_CRLF_vs_LF(string a, string b)
	{
		SqlNormalizer.Hash(a).Should().Be(SqlNormalizer.Hash(b));
	}

	[Theory]
	[InlineData("CREATE TABLE x (id INTEGER)", "CREATE TABLE x (id INTEGER);")]
	[InlineData("CREATE TABLE x (id INTEGER)", "CREATE TABLE x (id INTEGER);;;")]
	[InlineData("CREATE TABLE x (id INTEGER)", "CREATE TABLE x (id INTEGER)  ;  \n  ")]
	public void Hash_IgnoresTrailingSemicolonsAndWhitespace(string a, string b)
	{
		SqlNormalizer.Hash(a).Should().Be(SqlNormalizer.Hash(b));
	}

	[Fact]
	public void Hash_IgnoresLineComments()
	{
		var a = "CREATE TABLE x (id INTEGER)";
		var b = "-- M001: create x\nCREATE TABLE x (id INTEGER)";
		var c = "CREATE TABLE x (id INTEGER) -- inline note\n";
		SqlNormalizer.Hash(a).Should().Be(SqlNormalizer.Hash(b));
		SqlNormalizer.Hash(a).Should().Be(SqlNormalizer.Hash(c));
	}

	[Fact]
	public void Hash_IgnoresBlockComments()
	{
		var a = "CREATE TABLE x (id INTEGER)";
		var b = "/* M001 */ CREATE TABLE x (id INTEGER)";
		var c = "CREATE TABLE /* primary key */ x (id INTEGER)";
		SqlNormalizer.Hash(a).Should().Be(SqlNormalizer.Hash(b));
		SqlNormalizer.Hash(a).Should().Be(SqlNormalizer.Hash(c));
	}

	[Fact]
	public void Hash_MultiStatement_SameAcrossWhitespaceVariants()
	{
		var a = "CREATE TABLE a (id INTEGER); CREATE TABLE b (id INTEGER)";
		var b = "CREATE TABLE a (id INTEGER);\nCREATE TABLE b (id INTEGER)";
		var c = "CREATE TABLE a (id INTEGER) ;\r\n  CREATE TABLE b (id INTEGER) ;";
		SqlNormalizer.Hash(a).Should().Be(SqlNormalizer.Hash(b));
		SqlNormalizer.Hash(a).Should().Be(SqlNormalizer.Hash(c));
	}

	// --- Non-equivalence: should hash DIFFERENTLY ---

	[Fact]
	public void Hash_StringLiteralContentMatters()
	{
		// Whitespace INSIDE string literals is semantic — must NOT collapse.
		var a = "INSERT INTO t (v) VALUES ('a b')";
		var b = "INSERT INTO t (v) VALUES ('a  b')";
		SqlNormalizer.Hash(a).Should().NotBe(SqlNormalizer.Hash(b));
	}

	[Fact]
	public void Hash_StringLiteralCaseMatters()
	{
		var a = "INSERT INTO t (v) VALUES ('Hello')";
		var b = "INSERT INTO t (v) VALUES ('hello')";
		SqlNormalizer.Hash(a).Should().NotBe(SqlNormalizer.Hash(b));
	}

	[Fact]
	public void Hash_ColumnTypeChange_DifferentHash()
	{
		var a = "CREATE TABLE x (id INTEGER)";
		var b = "CREATE TABLE x (id TEXT)";
		SqlNormalizer.Hash(a).Should().NotBe(SqlNormalizer.Hash(b));
	}

	[Fact]
	public void Hash_TableNameChange_DifferentHash()
	{
		var a = "CREATE TABLE foo (id INTEGER)";
		var b = "CREATE TABLE bar (id INTEGER)";
		SqlNormalizer.Hash(a).Should().NotBe(SqlNormalizer.Hash(b));
	}

	[Fact]
	public void Hash_AddedColumn_DifferentHash()
	{
		var a = "CREATE TABLE x (id INTEGER)";
		var b = "CREATE TABLE x (id INTEGER, name TEXT)";
		SqlNormalizer.Hash(a).Should().NotBe(SqlNormalizer.Hash(b));
	}

	[Fact]
	public void Hash_EscapedQuoteInString_PreservedCorrectly()
	{
		// '' inside a string is an escaped single quote — not the end of literal.
		var a = "INSERT INTO t (v) VALUES ('it''s')";
		var b = "INSERT INTO t (v) VALUES ('its')";
		SqlNormalizer.Hash(a).Should().NotBe(SqlNormalizer.Hash(b));
	}

	// --- Edge cases ---

	[Fact]
	public void Hash_EmptyString_StableValue()
	{
		var h = SqlNormalizer.Hash("");
		h.Should().NotBeNullOrEmpty();
		h.Length.Should().Be(64);
	}

	[Fact]
	public void Hash_OnlyComments_SameAsEmpty()
	{
		SqlNormalizer.Hash("-- just a comment\n").Should().Be(SqlNormalizer.Hash(""));
		SqlNormalizer.Hash("/* block */").Should().Be(SqlNormalizer.Hash(""));
	}

	[Fact]
	public void Hash_DeterministicAcrossCalls()
	{
		var sql = "CREATE TABLE x (id INTEGER)";
		var h1 = SqlNormalizer.Hash(sql);
		var h2 = SqlNormalizer.Hash(sql);
		h1.Should().Be(h2);
	}

	[Fact]
	public void Hash_OutputFormat_Hex64Lowercase()
	{
		var h = SqlNormalizer.Hash("CREATE TABLE x (id INTEGER)");
		h.Length.Should().Be(64);
		h.Should().MatchRegex("^[0-9a-f]{64}$");
	}
}
