using System.Data;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.Sqlite;

namespace PetBox.Data.Schema;

// Extends DbUp's SqliteTableJournal with a Hash column on the journal table
// (__SchemaVersions by default). Whenever DbUp records a successful migration,
// SqlNormalizer.Hash(script.Contents) is stored alongside the script name. The
// SchemaRunner uses that hash for the pre-check: same name + same hash = no-op,
// same name + different hash = 409 Conflict.
//
// We override three protected members:
//   - CreateSchemaTableSql: adds `Hash TEXT NOT NULL` column to the DDL.
//   - GetInsertJournalEntrySql: includes @hash in the INSERT parameter list.
//   - GetInsertScriptCommand: binds the @hash value from the script contents.
public sealed class SqliteHashingJournal : SqliteTableJournal
{
	public SqliteHashingJournal(Func<IConnectionManager> connectionManager, Func<IUpgradeLog> logger, string table)
		: base(connectionManager, logger, table)
	{
	}

	protected override string CreateSchemaTableSql(string quotedPrimaryKeyName) =>
		$@"CREATE TABLE {FqSchemaTableName} (
			SchemaVersionID INTEGER CONSTRAINT {quotedPrimaryKeyName} PRIMARY KEY AUTOINCREMENT NOT NULL,
			ScriptName TEXT NOT NULL,
			Applied DATETIME NOT NULL,
			Hash TEXT NOT NULL
		)";

	protected override string GetInsertJournalEntrySql(string scriptName, string applied) =>
		$"INSERT INTO {FqSchemaTableName} (ScriptName, Applied, Hash) VALUES ({scriptName}, {applied}, @hash)";

	protected override IDbCommand GetInsertScriptCommand(Func<IDbCommand> dbCommandFactory, SqlScript script)
	{
		var cmd = base.GetInsertScriptCommand(dbCommandFactory, script);
		var hashParam = cmd.CreateParameter();
		hashParam.ParameterName = "hash";
		hashParam.DbType = DbType.String;
		hashParam.Value = SqlNormalizer.Hash(script.Contents);
		cmd.Parameters.Add(hashParam);
		return cmd;
	}
}
