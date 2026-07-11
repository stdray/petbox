using System.Data;
using FluentMigrator.Builders.Execute;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;

namespace PetBox.Tests.Data;

// THE GUARD IS THE POINT OF SqliteDdl, so it gets its own test.
//
// SqliteDdl exists to make database-specific DDL (partial indexes, FTS5, triggers, INSERT..SELECT)
// SAY that it is SQLite-specific — and to STOP if it ever runs somewhere else. The tempting
// alternative, `IfDatabase("sqlite")`, does the opposite: on another engine it SKIPS the
// expression, leaving the schema quietly INCOMPLETE (no unique-active-key index, no search_fts)
// with not one line of red anywhere. So: prove empirically that the guard THROWS.
//
// The fake IExecuteExpressionRoot below stands in for FluentMigrator's expression collector: it
// captures exactly what the helper queues (the WithConnection guard callback and the SQL text) in
// order, so the tests drive the SAME delegate the real runner would invoke, just with a
// connection of our choosing.
public sealed class SqliteDdlTests
{
	// A non-SQLite connection reaching the guard = hard failure, naming the operation and why it
	// is SQLite-specific. (No other DB engine needs to be installed to prove it: the runner hands
	// the callback an IDbConnection, so a non-SQLite IDbConnection is a faithful stand-in.)
	[Fact]
	public void Guard_throws_when_the_connection_is_not_sqlite()
	{
		var exec = new CapturingExecuteRoot();
		var ddl = new SqliteDdl(exec);

		ddl.PartialIndex("ux_x_active", "x", ["Key"], "ActiveTo IS NULL", unique: true);

		var act = () => exec.Guards.Single()(new NotSqliteConnection(), null!);

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*PartialIndex(ux_x_active)*")            // WHICH operation
			.WithMessage("*NotSqliteConnection*")                  // what it was pointed at
			.WithMessage("*Filter()*")                             // WHY it is SQLite-specific
			.WithMessage("*INCOMPLETE*");                          // and why we refuse to skip it
	}

	// A processor with no connection (preview / script generation) cannot be verified as SQLite
	// either — same hard stop rather than a hopeful pass.
	[Fact]
	public void Guard_throws_when_there_is_no_connection()
	{
		var exec = new CapturingExecuteRoot();
		new SqliteDdl(exec).Fts5Table("f", ["Text"]);

		var act = () => exec.Guards.Single()(null!, null!);

		act.Should().Throw<InvalidOperationException>().WithMessage("*Fts5Table(f)*");
	}

	// ...and on the engine it IS written for, the guard is a no-op and the statement runs.
	[Fact]
	public void Guard_passes_on_a_sqlite_connection()
	{
		var exec = new CapturingExecuteRoot();
		new SqliteDdl(exec).Fts5Table("f", ["Text"]);

		using var conn = new SqliteConnection("Data Source=:memory:");
		var act = () => exec.Guards.Single()(conn, null!);

		act.Should().NotThrow();
	}

	// The guard is queued BEFORE the statement it protects: FluentMigrator runs a migration's
	// expressions in the order Up() created them, so on a foreign engine the failure happens
	// before any DDL is issued.
	[Fact]
	public void Guard_is_queued_before_the_statement()
	{
		var exec = new CapturingExecuteRoot();
		new SqliteDdl(exec).Raw("rebuild: INSERT..SELECT has no typed form", "INSERT INTO b SELECT * FROM a;");

		exec.Calls.Should().Equal("guard", "sql");
	}

	// Every helper emits the SQLite construct it names — and NOT `IF NOT EXISTS`, which a
	// once-only migration cannot be protected by, only silenced by.
	[Fact]
	public void Helpers_emit_the_named_construct_and_never_if_not_exists()
	{
		var exec = new CapturingExecuteRoot();
		var ddl = new SqliteDdl(exec);

		ddl.PartialIndex("ux_e_active", "e", ["Store", "Key"], "ActiveTo IS NULL", unique: true);
		ddl.Fts5Table("search_fts", ["Scope", "Type", "Id", "Text"], ["Scope", "Type", "Id"]);
		ddl.Trigger("trg_e", "e", "AFTER INSERT", "INSERT INTO log(Id) VALUES (NEW.Id);");
		ddl.Raw("data move", "INSERT INTO b SELECT * FROM a;");

		exec.Sql[0].Should().Be("CREATE UNIQUE INDEX ux_e_active ON e (Store, Key) WHERE ActiveTo IS NULL;");
		exec.Sql[1].Should().Be(
			"CREATE VIRTUAL TABLE search_fts USING fts5(Scope UNINDEXED, Type UNINDEXED, Id UNINDEXED, Text, tokenize='unicode61');");
		exec.Sql[2].Should().Be("CREATE TRIGGER trg_e AFTER INSERT ON e FOR EACH ROW BEGIN INSERT INTO log(Id) VALUES (NEW.Id); END;");

		exec.Sql.Should().NotContain(s => s.Contains("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));
	}

	// Raw's `reason` is mandatory and does not evaporate: it rides along as a SQL comment and as
	// the expression's description in the migration log.
	[Fact]
	public void Raw_carries_its_reason_into_the_sql_and_the_log()
	{
		var exec = new CapturingExecuteRoot();
		new SqliteDdl(exec).Raw("table rebuild: SQLite cannot alter a PK in place", "INSERT INTO b SELECT * FROM a;");

		exec.Sql.Single().Should().StartWith("-- SqliteDdl.Raw: table rebuild: SQLite cannot alter a PK in place");
		exec.Descriptions.Single().Should().Contain("SQLite cannot alter a PK in place");
	}

	// ── fakes ──────────────────────────────────────────────────────────────────

	sealed class CapturingExecuteRoot : IExecuteExpressionRoot
	{
		public List<string> Calls { get; } = [];
		public List<string> Sql { get; } = [];
		public List<string> Descriptions { get; } = [];
		public List<Action<IDbConnection, IDbTransaction>> Guards { get; } = [];

		void IExecuteExpressionRoot.Sql(string sqlStatement)
		{
			Calls.Add("sql");
			Sql.Add(sqlStatement);
		}

		void IExecuteExpressionRoot.Sql(string sqlStatement, string description)
		{
			Calls.Add("sql");
			Sql.Add(sqlStatement);
			Descriptions.Add(description);
		}

		void IExecuteExpressionRoot.Script(string pathToSqlScript) => Calls.Add("script");
		void IExecuteExpressionRoot.Script(string pathToSqlScript, IDictionary<string, string> parameters) => Calls.Add("script");
		void IExecuteExpressionRoot.EmbeddedScript(string embeddedSqlScriptName) => Calls.Add("script");
		void IExecuteExpressionRoot.EmbeddedScript(string embeddedSqlScriptName, IDictionary<string, string> parameters) => Calls.Add("script");

		void IExecuteExpressionRoot.WithConnection(Action<IDbConnection, IDbTransaction> operation)
		{
			Calls.Add("guard");
			Guards.Add(operation);
		}
	}

	// Any IDbConnection that is not SQLite. Nothing on it is ever called: the guard looks at the
	// TYPE, and throws.
	sealed class NotSqliteConnection : IDbConnection
	{
#pragma warning disable CS8767 // IDbConnection.ConnectionString is oblivious; the setter is never called
		public string ConnectionString { get; set; } = "";
#pragma warning restore CS8767
		public int ConnectionTimeout => 0;
		public string Database => "";
		public ConnectionState State => ConnectionState.Open;
		public IDbTransaction BeginTransaction() => throw new NotSupportedException();
		public IDbTransaction BeginTransaction(IsolationLevel il) => throw new NotSupportedException();
		public void ChangeDatabase(string databaseName) => throw new NotSupportedException();
		public void Close() { }
		public IDbCommand CreateCommand() => throw new NotSupportedException();
		public void Open() { }
		public void Dispose() { }
	}
}
