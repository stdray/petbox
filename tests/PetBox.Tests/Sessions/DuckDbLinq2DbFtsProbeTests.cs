using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.DuckDB;
using LinqToDB.Mapping;

namespace PetBox.Tests.Sessions;

// SPIKE (work chore sessions-duckdb-raw-ado-to-linq2db): de-risks the migration of
// DuckDbSessionEpisodicIndex off raw DuckDBConnection/ADO onto linq2db's DataConnection —
// specifically that (a) a DataConnection opened on DuckDB's ":memory:" DataSource keeps the
// SAME in-memory database alive across separate calls (Execute → Insert → FromSql), exactly
// like the raw DuckDBConnection field does today, and (b) create_fts_index + match_bm25
// (DuckDB-specific SQL the linq2db expression tree cannot model) still work as hand-written
// SQL text run THROUGH linq2db (Execute/FromSql), not raw ADO.
[Trait("Category", "Research")]
public sealed class DuckDbLinq2DbFtsProbeTests
{
	[Table("messages")]
	sealed class MessageRow
	{
		[Column] public long Version { get; set; }
		[Column] public string Content { get; set; } = "";
	}

	sealed class BmRow
	{
		public long Version { get; set; }
		public double Score { get; set; }
	}

	[Fact]
	public void DataConnection_OnMemoryDuckDb_PersistsAcrossCalls_AndFtsBm25RoundTrips()
	{
		using var db = DuckDBTools.CreateDataConnection("DataSource=:memory:");

		// separate Execute call #1: extension load
		db.Execute("INSTALL fts; LOAD fts;");
		// separate Execute call #2: schema — if :memory: did NOT persist across calls, this
		// table would vanish before the insert below could see it.
		db.Execute("CREATE TABLE messages (version BIGINT PRIMARY KEY, content VARCHAR)");

		using (db.BeginTransaction())
		{
			db.Insert(new MessageRow { Version = 1, Content = "вчера мы запустили векторизацию на проде" });
			db.Insert(new MessageRow { Version = 2, Content = "обсуждали дизайн дайджеста" });
			db.CommitTransaction();
		}

		// separate Execute call #3: build the FTS index over rows inserted in a prior call —
		// only possible if the in-memory DB persisted through calls #1/#2 above.
		db.Execute("PRAGMA create_fts_index('messages', 'version', 'content', stemmer='russian')");

		var query = "запустила векторизацию"; // different wordform — proves the stemmer ran
		var k = 5;
		var hits = db.FromSql<BmRow>($"""
			SELECT version, score FROM (
				SELECT version, fts_main_messages.match_bm25(version, {query}) AS score FROM messages
			) WHERE score IS NOT NULL ORDER BY score DESC LIMIT {k}
			""").ToList();

		hits.Should().NotBeEmpty();
		hits[0].Version.Should().Be(1);

		// Independent proof the :memory: content survived across the separate calls above:
		// read the row back via a fresh linq2db table query, not just via the FTS index.
		var direct = db.GetTable<MessageRow>().Where(m => m.Version == 2).Single();
		direct.Content.Should().Be("обсуждали дизайн дайджеста");
	}
}
