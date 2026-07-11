using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PetBox.Tests.Data.Schema;

// A NORMALIZED, human-readable dump of everything a SQLite file's schema actually contains:
// tables, columns, foreign keys, indexes (INCLUDING their partial WHERE predicate), triggers
// and virtual-table declarations. Golden-file tests diff this against a committed baseline, so
// any migration-body change that alters the resulting schema shows up as a reviewable diff.
//
// WHY NOT just diff sqlite_master.sql: that text is a verbatim echo of whatever DDL the
// migration happened to emit, so it flips on pure FORMATTING (line breaks, indentation) and on
// the raw-SQL -> typed-FluentMigrator-API conversion (the typed generator double-quotes every
// identifier and appends ASC to index columns; raw DDL does neither). A snapshot that flags
// those as schema changes is noise and gets ignored. So the snapshot is built from the PRAGMAs
// (structural facts, no formatting) and only falls back to sqlite_master for what the PRAGMAs
// cannot express: an index's partial predicate, trigger bodies, and CREATE VIRTUAL TABLE.
// Those few SQL fragments go through NormalizeSql (whitespace collapsed, identifier quoting
// stripped, keywords upper-cased, redundant ASC / IF NOT EXISTS dropped).
public static class SchemaSnapshot
{
	// FTS5 keeps its inverted index in shadow tables next to the virtual table. They are
	// derived, their layout is sqlite's business, and they only add noise to a review diff —
	// the virtual-table DECLARATION is the schema fact worth pinning.
	static readonly string[] Fts5ShadowSuffixes = ["_data", "_idx", "_content", "_docsize", "_config"];

	public static string Capture(string connectionString)
	{
		using var conn = new SqliteConnection(connectionString);
		conn.Open();

		var objects = ReadMaster(conn);
		var virtualTables = objects
			.Where(o => o.Type == "table" && o.Sql is not null &&
				o.Sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
			.Select(o => o.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var shadows = virtualTables
			.SelectMany(v => Fts5ShadowSuffixes.Select(s => v + s))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var tables = objects
			.Where(o => o.Type == "table")
			.Select(o => o.Name)
			.Where(n => !n.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) // internal (sqlite_sequence/stat*)
			.Where(n => !virtualTables.Contains(n) && !shadows.Contains(n))
			.OrderBy(n => n, StringComparer.Ordinal)
			.ToList();

		var indexSql = objects
			.Where(o => o.Type == "index" && o.Sql is not null)
			.ToDictionary(o => o.Name, o => o.Sql!, StringComparer.OrdinalIgnoreCase);

		var sb = new StringBuilder();
		foreach (var table in tables)
		{
			sb.Append("TABLE ").Append(table).Append('\n');
			foreach (var line in Columns(conn, table)) sb.Append("  ").Append(line).Append('\n');
			foreach (var line in ForeignKeys(conn, table)) sb.Append("  ").Append(line).Append('\n');
			foreach (var line in Indexes(conn, table, indexSql)) sb.Append("  ").Append(line).Append('\n');
			foreach (var line in Triggers(objects, table)) sb.Append("  ").Append(line).Append('\n');
			sb.Append('\n');
		}

		foreach (var name in virtualTables.OrderBy(n => n, StringComparer.Ordinal))
		{
			var sql = objects.First(o => o.Type == "table" && o.Name == name).Sql!;
			sb.Append("VIRTUAL TABLE ").Append(name).Append('\n');
			sb.Append("  DECL ").Append(NormalizeSql(sql)).Append('\n');
			foreach (var line in Triggers(objects, name)) sb.Append("  ").Append(line).Append('\n');
			sb.Append('\n');
		}

		return sb.ToString().TrimEnd('\n') + "\n";
	}

	// ── the structural facts, straight from the PRAGMAs ────────────────────────

	// COL <name> <declared-type> <NULL|NOT NULL> [DEFAULT <expr>] [PK<position>]
	// Sorted by name: column ORDER in the table is not a schema fact worth pinning (an
	// ALTER TABLE ADD COLUMN appends, a table rebuild may reorder), but the position within
	// the PRIMARY KEY is, and it is carried explicitly by PK<n>.
	static IEnumerable<string> Columns(SqliteConnection conn, string table)
	{
		var rows = Query(conn, $"PRAGMA table_info({Quote(table)});");
		return rows
			.Select(r =>
			{
				var name = Str(r["name"]);
				var type = Str(r["type"]);
				var notNull = Num(r["notnull"]) != 0;
				var dflt = r["dflt_value"] is DBNull or null ? null : Str(r["dflt_value"]);
				var pk = Num(r["pk"]);
				var sb = new StringBuilder("COL ").Append(name)
					.Append(' ').Append(string.IsNullOrEmpty(type) ? "(no-type)" : type.ToUpperInvariant())
					.Append(notNull ? " NOT NULL" : " NULL");
				if (dflt is not null) sb.Append(" DEFAULT ").Append(NormalizeSql(dflt));
				if (pk > 0) sb.Append(" PK").Append(pk.ToString(CultureInfo.InvariantCulture));
				return sb.ToString();
			})
			.OrderBy(s => s, StringComparer.Ordinal);
	}

	// FK (<from-cols>) -> <target>(<to-cols>) ON DELETE <a> ON UPDATE <a>
	// Columns of one composite FK are joined in `seq` order (significant); the FK lines
	// themselves are sorted (their `id` is an arbitrary sqlite counter).
	static IEnumerable<string> ForeignKeys(SqliteConnection conn, string table)
	{
		var rows = Query(conn, $"PRAGMA foreign_key_list({Quote(table)});");
		return rows
			.GroupBy(r => Num(r["id"]))
			.Select(g =>
			{
				var ordered = g.OrderBy(r => Num(r["seq"])).ToList();
				var from = string.Join(", ", ordered.Select(r => Str(r["from"])));
				// A FK may omit the parent columns (implicitly the parent's PK) — then `to` is NULL.
				var to = string.Join(", ", ordered.Select(r => r["to"] is DBNull or null ? "(pk)" : Str(r["to"])));
				var first = ordered[0];
				return $"FK ({from}) -> {Str(first["table"])}({to})" +
					$" ON DELETE {Str(first["on_delete"]).ToUpperInvariant()}" +
					$" ON UPDATE {Str(first["on_update"]).ToUpperInvariant()}";
			})
			.OrderBy(s => s, StringComparer.Ordinal);
	}

	// INDEX <name> [UNIQUE] origin=<c|u|pk> (<cols in key order>) [WHERE <predicate>]
	//
	// Columns come from index_xinfo (not index_info) so DESC and a non-default COLLATE are
	// captured, and the trailing rowid columns (key=0) are dropped. Sort direction/collation
	// being structural here is also what immunizes the snapshot against the typed API's habit
	// of spelling out `"Col" ASC` where raw DDL writes `Col`.
	//
	// The partial predicate is the one thing PRAGMAs do not expose — and partial indexes are
	// the backbone of the temporal model (`WHERE ActiveTo IS NULL` = "at most one ACTIVE
	// revision per key"), so a snapshot without them would miss the invariant that matters
	// most. It is parsed out of sqlite_master.sql: everything after the column-list's matching
	// close-paren.
	static IEnumerable<string> Indexes(SqliteConnection conn, string table, IReadOnlyDictionary<string, string> indexSql)
	{
		var list = Query(conn, $"PRAGMA index_list({Quote(table)});");
		return list
			.Select(r =>
			{
				var name = Str(r["name"]);
				var unique = Num(r["unique"]) != 0;
				var origin = Str(r["origin"]); // c = CREATE INDEX, u = UNIQUE constraint, pk = PRIMARY KEY
				var cols = Query(conn, $"PRAGMA index_xinfo({Quote(name)});")
					.Where(x => Num(x["key"]) == 1)
					.OrderBy(x => Num(x["seqno"]))
					.Select(x =>
					{
						var col = x["name"] is DBNull or null ? "(expr)" : Str(x["name"]);
						if (Num(x["desc"]) != 0) col += " DESC";
						var coll = Str(x["coll"]);
						if (!string.Equals(coll, "BINARY", StringComparison.OrdinalIgnoreCase))
							col += " COLLATE " + coll.ToUpperInvariant();
						return col;
					});
				var line = new StringBuilder("INDEX ").Append(name)
					.Append(unique ? " UNIQUE" : "")
					.Append(" origin=").Append(origin)
					.Append(" (").Append(string.Join(", ", cols)).Append(')');
				if (Num(r["partial"]) != 0 && indexSql.TryGetValue(name, out var sql))
				{
					var where = PartialPredicate(sql);
					if (where is not null) line.Append(" WHERE ").Append(where);
				}
				return line.ToString();
			})
			.OrderBy(s => s, StringComparer.Ordinal);
	}

	// TRIGGER <name> <normalized CREATE TRIGGER ...>
	static IEnumerable<string> Triggers(IReadOnlyList<MasterRow> objects, string table) =>
		objects
			.Where(o => o.Type == "trigger" && string.Equals(o.TableName, table, StringComparison.OrdinalIgnoreCase))
			.OrderBy(o => o.Name, StringComparer.Ordinal)
			.Select(o => $"TRIGGER {o.Name} {NormalizeSql(o.Sql ?? "")}");

	// ── normalization ──────────────────────────────────────────────────────────

	// Everything after the column list of `CREATE [UNIQUE] INDEX x ON t (...) <rest>`, i.e. the
	// `WHERE <predicate>`. Scans to the paren matching the FIRST '(' so a predicate that itself
	// contains parentheses survives intact.
	internal static string? PartialPredicate(string sql)
	{
		var open = sql.IndexOf('(', StringComparison.Ordinal);
		if (open < 0) return null;
		var depth = 0;
		for (var i = open; i < sql.Length; i++)
		{
			if (sql[i] == '(') depth++;
			else if (sql[i] == ')' && --depth == 0)
			{
				var rest = sql[(i + 1)..].Trim().TrimEnd(';').Trim();
				if (!rest.StartsWith("WHERE", StringComparison.OrdinalIgnoreCase)) return null;
				return NormalizeSql(rest[5..]);
			}
		}
		return null;
	}

	static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
	{
		"CREATE", "TABLE", "VIRTUAL", "TRIGGER", "INDEX", "UNIQUE", "VIEW", "USING", "AS",
		"SELECT", "DISTINCT", "FROM", "WHERE", "GROUP", "ORDER", "BY", "HAVING", "LIMIT", "DESC",
		"INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "REPLACE", "OR", "AND", "NOT",
		"NULL", "IS", "IN", "EXISTS", "CASE", "WHEN", "THEN", "ELSE", "END", "BEGIN",
		"BEFORE", "AFTER", "INSTEAD", "OF", "ON", "FOR", "EACH", "ROW", "IGNORE", "ABORT",
		"FAIL", "ROLLBACK", "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "DEFAULT", "CHECK",
		"CONSTRAINT", "CASCADE", "RESTRICT", "ACTION", "NO", "COLLATE", "JOIN", "LEFT", "INNER",
		"OUTER", "UNION", "ALL", "LIKE", "GLOB", "BETWEEN", "WITHOUT", "ROWID", "STRICT",
	};

	static readonly Regex Word = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
	static readonly Regex QuotedIdent = new("\"([A-Za-z_][A-Za-z0-9_]*)\"|`([A-Za-z_][A-Za-z0-9_]*)`|\\[([A-Za-z_][A-Za-z0-9_]*)]", RegexOptions.Compiled);
	static readonly Regex StringLiteral = new("'[^']*'", RegexOptions.Compiled);
	static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);

	// Makes a SQL fragment independent of how it was WRITTEN:
	//   * whitespace/newlines collapsed to single spaces, `( x , y )` tightened to `(x, y)`;
	//   * identifier quoting stripped ("Key" / `Key` / [Key] -> Key) — the typed FluentMigrator
	//     generator quotes everything, raw DDL does not;
	//   * redundant `ASC` dropped (ASC is SQLite's default, so this is semantics-preserving)
	//     and `IF NOT EXISTS` dropped (an emit-style choice, not a schema fact);
	//   * keywords upper-cased, everything else left verbatim.
	// String literals are masked out first, so nothing inside 'quoted text' is rewritten.
	internal static string NormalizeSql(string sql)
	{
		var literals = new List<string>();
		var masked = StringLiteral.Replace(sql, m =>
		{
			literals.Add(m.Value);
			return $"\x01{literals.Count - 1}\x01";
		});

		masked = QuotedIdent.Replace(masked, m => m.Groups[1].Success ? m.Groups[1].Value
			: m.Groups[2].Success ? m.Groups[2].Value
			: m.Groups[3].Value);

		masked = Word.Replace(masked, m => Keywords.Contains(m.Value) ? m.Value.ToUpperInvariant() : m.Value);
		masked = Regex.Replace(masked, @"\bIF\s+NOT\s+EXISTS\b", "", RegexOptions.IgnoreCase);
		masked = Regex.Replace(masked, @"\bASC\b", "", RegexOptions.IgnoreCase);

		masked = Ws.Replace(masked, " ");
		masked = masked.Replace(" ,", ",", StringComparison.Ordinal)
			.Replace("( ", "(", StringComparison.Ordinal)
			.Replace(" )", ")", StringComparison.Ordinal)
			.Replace(";", "; ", StringComparison.Ordinal);
		masked = Ws.Replace(masked, " ").Trim().TrimEnd(';').Trim();

		return Regex.Replace(masked, "\x01(\\d+)\x01", m => literals[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);
	}

	// ── plumbing ───────────────────────────────────────────────────────────────

	sealed record MasterRow(string Type, string Name, string TableName, string? Sql);

	static IReadOnlyList<MasterRow> ReadMaster(SqliteConnection conn)
	{
		var rows = new List<MasterRow>();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master;";
		using var r = cmd.ExecuteReader();
		while (r.Read())
			rows.Add(new MasterRow(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
		return rows;
	}

	static List<Dictionary<string, object>> Query(SqliteConnection conn, string sql)
	{
		var rows = new List<Dictionary<string, object>>();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		using var r = cmd.ExecuteReader();
		while (r.Read())
		{
			var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
			for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.GetValue(i);
			rows.Add(row);
		}
		return rows;
	}

	static string Quote(string ident) => "'" + ident.Replace("'", "''", StringComparison.Ordinal) + "'";
	static string Str(object? v) => v is null or DBNull ? "" : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
	static long Num(object? v) => v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
}
