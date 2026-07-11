using System.Data;
using System.Globalization;
using FluentMigrator;
using FluentMigrator.Builders.Execute;
using Microsoft.Data.Sqlite;

namespace PetBox.Core.Data;

// SQLITE-SPECIFIC DDL, DECLARED BY NAME.
//
// The rule (owner, 2026-07-11): raw SQL inside an honest migration is not the problem. The
// problems are (a) `IF NOT EXISTS`, which silences schema drift instead of surfacing it, and
// (b) database-specific DDL smuggled in as anonymous `Execute.Sql("...")` blobs, where nothing
// in the call says "this only works on SQLite" and nothing fails if it ever runs elsewhere.
//
// So: everything FluentMigrator's typed API CAN express (Create.Table / Create.Index /
// Alter.Table / Delete.Table) MUST be written with the typed API. The handful of things it
// cannot express go through the NAMED helpers below — the call site reads as a declaration of
// intent ("a PARTIAL index", "an FTS5 table"), and every one of them is guarded: if the
// migration is ever run against a non-SQLite engine it FAILS, loudly, naming the operation and
// why it is SQLite-specific.
//
// WHAT THE TYPED API CANNOT EXPRESS (checked empirically):
//   * PARTIAL/filtered indexes (`WHERE ActiveTo IS NULL`) — the backbone of the temporal model.
//     `.Filter()` lives ONLY in FluentMigrator.Extensions.SqlServer and on SQLite it SILENTLY
//     drops the WHERE, turning a partial unique index into a total one. That package must NOT be
//     added to this solution: without it, `.Filter()` does not even compile — which is the only
//     thing standing between us and a silently broken temporal invariant.  -> PartialIndex()
//   * FTS5 `CREATE VIRTUAL TABLE ... USING fts5(...)`.                     -> Fts5Table()
//   * Triggers.                                                            -> Trigger()
//   * `INSERT..SELECT` (Insert.IntoTable takes literal rows only), and therefore the SQLite
//     table-rebuild idiom (create-new + copy + DROP + RENAME).             -> Raw(reason, sql)
//
// WHY THE GUARD IS NOT `IfDatabase("sqlite")`: IfDatabase SKIPS the expression on another
// engine. Silent skipping is the worst possible outcome here — the schema would come out
// INCOMPLETE (no partial unique index, no FTS table) without a single signal. The guard below
// is an expression of its own, queued immediately BEFORE the DDL, that inspects the connection
// the runner hands it and THROWS when it is not a SQLite connection.
//
// WHAT IS NOT HERE, ON PURPOSE: no `IF NOT EXISTS`. A migration runs exactly once, gated by
// VersionInfo; a tolerant CREATE cannot protect it, it can only swallow a divergence. The one
// legal way to "adopt" an object that a pre-migration runtime already created is the typed
// `Schema.Table(x).Exists()` guard — see PetBox.Sessions M007_SearchCursorTables for the single
// place where that is justified, and why it is not a licence.
public sealed class SqliteDdl
{
	readonly IExecuteExpressionRoot _execute;

	internal SqliteDdl(IExecuteExpressionRoot execute) => _execute = execute;

	// A PARTIAL (filtered) index: `CREATE [UNIQUE] INDEX name ON table (cols) WHERE predicate`.
	// The predicate is the point — `unique: true` + `where: "ActiveTo IS NULL"` is how the
	// temporal model says "at most ONE active revision per key". Dropping the WHERE would not be
	// a formatting difference; it would forbid history.
	public void PartialIndex(string name, string table, IReadOnlyList<string> columns, string where, bool unique = false)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(table);
		ArgumentException.ThrowIfNullOrWhiteSpace(where);
		if (columns is not { Count: > 0 }) throw new ArgumentException("a partial index needs at least one column", nameof(columns));

		Emit(
			operation: $"PartialIndex({name})",
			why: "a partial index's WHERE predicate has no typed FluentMigrator form (.Filter() is SqlServer-only and is silently dropped on other engines)",
			sql: $"CREATE {(unique ? "UNIQUE " : "")}INDEX {name} ON {table} ({string.Join(", ", columns)}) WHERE {where};");
	}

	// An FTS5 full-text index: `CREATE VIRTUAL TABLE name USING fts5(cols..., tokenize='...')`.
	// `unindexed` names the columns carried for addressing only (stored, not tokenised) — in our
	// search tables the entity address (Scope, Type, Id); column ORDER follows `columns`.
	public void Fts5Table(string name, IReadOnlyList<string> columns, IReadOnlyList<string>? unindexed = null, string tokenize = "unicode61")
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(tokenize);
		if (columns is not { Count: > 0 }) throw new ArgumentException("an FTS5 table needs at least one column", nameof(columns));

		var skip = unindexed is null
			? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			: new HashSet<string>(unindexed, StringComparer.OrdinalIgnoreCase);
		var unknown = skip.Except(columns, StringComparer.OrdinalIgnoreCase).ToList();
		if (unknown.Count > 0)
			throw new ArgumentException($"unindexed column(s) not in the column list: {string.Join(", ", unknown)}", nameof(unindexed));

		var decl = columns.Select(c => skip.Contains(c) ? c + " UNINDEXED" : c);

		Emit(
			operation: $"Fts5Table({name})",
			why: "FTS5 is a SQLite virtual-table module; CREATE VIRTUAL TABLE has no typed FluentMigrator form",
			sql: $"CREATE VIRTUAL TABLE {name} USING fts5({string.Join(", ", decl)}, tokenize='{tokenize}');");
	}

	// A trigger: `CREATE TRIGGER name <when> ON table [WHEN cond] BEGIN body END`.
	// `when` is the event clause as SQLite spells it — e.g. "AFTER INSERT", "AFTER UPDATE OF Col",
	// "BEFORE DELETE"; `body` is the statement list inside BEGIN..END.
	public void Trigger(string name, string table, string when, string body, string? condition = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(table);
		ArgumentException.ThrowIfNullOrWhiteSpace(when);
		ArgumentException.ThrowIfNullOrWhiteSpace(body);

		var cond = string.IsNullOrWhiteSpace(condition) ? "" : $" WHEN {condition}";
		var stmts = body.Trim().EndsWith(';') ? body.Trim() : body.Trim() + ";";

		Emit(
			operation: $"Trigger({name})",
			why: "triggers have no typed FluentMigrator form, and their body is SQLite SQL",
			// No `FOR EACH ROW`: SQLite has no other kind of trigger, so the clause adds nothing but a
			// word — and that word would land in sqlite_master, diverging the stored DDL of every
			// trigger we rewrite from the one already on disk.
			sql: $"CREATE TRIGGER {name} {when} ON {table}{cond} BEGIN {stmts} END;");
	}

	// The escape hatch for what the three named helpers do not cover — an `INSERT..SELECT` data
	// move, the create-new/copy/DROP/RENAME table rebuild SQLite needs to change a primary key.
	// `reason` is MANDATORY: it says why raw SQL was unavoidable, it is emitted as a comment in
	// front of the statement and passed to the runner as the expression's description, so it
	// shows up in the migration log instead of evaporating.
	public void Raw(string reason, string sql)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		ArgumentException.ThrowIfNullOrWhiteSpace(sql);

		Emit(
			operation: "Raw",
			why: reason,
			sql: $"-- SqliteDdl.Raw: {reason}\n{sql.Trim()}",
			description: $"SqliteDdl.Raw: {reason}");
	}

	// Queues TWO expressions, in order: the engine guard, then the statement. FluentMigrator
	// executes a migration's expressions in the order Up() produced them, so a non-SQLite run
	// blows up on the guard and never reaches the SQL.
	void Emit(string operation, string why, string sql, string? description = null)
	{
		_execute.WithConnection((conn, _) => Require(conn, operation, why));
		_execute.Sql(sql, description ?? $"SqliteDdl.{operation}");
	}

	// THE GUARD. Called with the very connection the runner is migrating; anything that is not a
	// SQLite connection (or no connection at all — a connectionless/script-generating processor
	// cannot be verified) is a hard stop. Deliberately NOT a silent skip: a missing partial index
	// or FTS table that nobody is told about is worse than a failed migration.
	// `internal` (+ InternalsVisibleTo PetBox.Tests) so the test suite can drive this exact code
	// path with a non-SQLite connection, without needing another database engine installed.
	internal static void Require(IDbConnection? conn, string operation, string why)
	{
		// An exact type test, not "the type name contains sqlite": the whole value of this guard is
		// that it cannot be fooled. Microsoft.Data.Sqlite is the one SQLite provider in the
		// solution (AddSQLite() in MigrationRunner hands the processor exactly this).
		if (conn is SqliteConnection) return;

		var actual = conn?.GetType().FullName;
		throw new InvalidOperationException(string.Format(
			CultureInfo.InvariantCulture,
			"SqliteDdl.{0} is SQLite-specific DDL and was asked to run against {1}. " +
			"It is SQLite-specific because: {2}. Refusing to continue: skipping it silently " +
			"(what IfDatabase(\"sqlite\") would do) would leave the schema INCOMPLETE with no signal. " +
			"Port this migration to the target engine, or keep the tier on SQLite.",
			operation,
			actual is null ? "a processor with no connection (preview/script generation?)" : actual,
			why));
	}
}

// The base class a migration inherits to get `SqliteDdl`.
//
// WHY A BASE CLASS AND NOT AN EXTENSION METHOD: the pieces the helpers need — `Execute` (and
// `IfDatabase`, `Schema`) — are PROTECTED members of FluentMigrator's `Migration`. Protected
// means "visible to subclasses", not "visible to extension methods", so no `this Migration`
// extension can reach them. Inheriting is the only way to hand the helper the expression roots,
// and it doubles as documentation: the class declaration itself says this migration is written
// for SQLite.
public abstract class SqliteMigration : Migration
{
	// New per call: an expression root is a thin builder over the migration's current context,
	// and Up()/Down() are invoked with a fresh context each time.
	protected SqliteDdl SqliteDdl => new(Execute);
}
