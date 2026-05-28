using System.Security.Cryptography;
using System.Text;
using SqlParser;
using SqlParser.Dialects;

namespace PetBox.Data.Schema;

// Canonicalizes SQL text so semantically-equivalent scripts hash identically.
// Used by SqliteHashingJournal to detect when a migration's content has changed
// while its name stayed the same.
//
// Implementation: parse SQL to AST via SqlParserCS (SQLite dialect), then call
// .ToSql() on each statement. The AST roundtrip naturally drops comments,
// normalizes whitespace, uppercases keywords, removes trailing semicolons, and
// canonicalizes punctuation. Statement boundaries are joined with "; ".
//
// String literal content is preserved exactly. Identifier case is preserved
// as-typed (`Votes` and `votes` will hash differently). SQLite treats them as
// the same identifier semantically, but in practice pets ship consistent SQL
// so this hasn't been worth implementing as an AST visitor pass.
//
// Limitations:
// - PRAGMA statements don't parse with this dialect — pet must keep PRAGMAs
//   out of migration scripts (they belong at DataDb creation time anyway).
// - Truly empty input returns a stable hash (no parsing attempted).
public static class SqlNormalizer
{
	static readonly Dialect Dialect = new SQLiteDialect();

	public static string Normalize(string sql)
	{
		ArgumentNullException.ThrowIfNull(sql);

		if (string.IsNullOrWhiteSpace(sql))
			return string.Empty;

		// SqlParser.Parser holds per-call lexer state and is not safe to share
		// across threads — instantiate fresh each call.
		var statements = new Parser().ParseSql(sql, Dialect);
		if (statements.Count == 0) return string.Empty;

		return string.Join("; ", statements.Select(s => s.ToSql()));
	}

	public static string Hash(string sql)
	{
		var normalized = Normalize(sql);
		var bytes = Encoding.UTF8.GetBytes(normalized);
		var digest = SHA256.HashData(bytes);
		return Convert.ToHexString(digest).ToLowerInvariant();
	}
}
