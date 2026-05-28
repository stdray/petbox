using System.Security.Cryptography;
using System.Text;

namespace YobaBox.Data.Schema;

// Canonicalizes SQL text so semantically-equivalent scripts hash identically.
// Used by SqliteHashingJournal to detect when a migration's content has changed
// while its name stayed the same.
//
// Normalization policy (in order):
// 1. Strip line comments (-- ...) and block comments (/* ... */).
// 2. Lowercase everything OUTSIDE single-quoted string literals. SQLite is
//    case-insensitive for keywords AND identifiers (case-preserving only in
//    display), so `CREATE TABLE Votes` == `create table votes` semantically.
// 3. Collapse all whitespace runs outside strings to a single space, and strip
//    whitespace adjacent to punctuation `(`, `)`, `,`, `;`.
// 4. Trim trailing semicolons + whitespace.
//
// Single-quoted string content is preserved EXACTLY (case, whitespace, escapes).
//
// Hand-rolled rather than using a SQL formatter library. We evaluated
// Hogimn.Sql.Formatter 2.0.4 with FormatConfig { Case = LOWER,
// SkipWhitespaceNearBlockParentheses = true }: it handles keyword case,
// whitespace collapse, and parenthesis spacing — but does NOT lowercase
// identifiers (`CREATE TABLE Votes` stays `Votes`, breaking SQLite's
// case-insensitive identifier semantics for hash purposes), preserves
// comments, and keeps trailing semicolons. We'd still need ~3 custom passes
// on top, giving us `formatter + post-process` (≈80 LOC) vs hand-rolled
// (~100 LOC). The hand-rolled version is deterministic, has no external
// version drift, and the contract is owned by tests in SqlNormalizerTests.
public static class SqlNormalizer
{
	public static string Normalize(string sql)
	{
		ArgumentNullException.ThrowIfNull(sql);

		// Pass 1: strip comments, lowercase outside strings, preserve string literals exactly.
		// Whitespace is preserved as-is here; pass 2 collapses it.
		var pass1 = new StringBuilder(sql.Length);
		var i = 0;
		while (i < sql.Length)
		{
			var ch = sql[i];

			if (ch == '\'')
			{
				pass1.Append(ch);
				i++;
				while (i < sql.Length)
				{
					var c = sql[i];
					pass1.Append(c);
					i++;
					if (c == '\'')
					{
						if (i < sql.Length && sql[i] == '\'')
						{
							pass1.Append(sql[i]);
							i++;
							continue;
						}
						break;
					}
				}
				continue;
			}

			if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
			{
				i += 2;
				while (i < sql.Length && sql[i] != '\n') i++;
				pass1.Append(' ');
				continue;
			}

			if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
			{
				i += 2;
				while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
				if (i + 1 < sql.Length) i += 2;
				pass1.Append(' ');
				continue;
			}

			pass1.Append(char.ToLowerInvariant(ch));
			i++;
		}

		// Pass 2: collapse whitespace; strip space adjacent to punctuation; preserve strings.
		var src = pass1.ToString();
		var pass2 = new StringBuilder(src.Length);
		var pendingSpace = false;
		i = 0;
		while (i < src.Length)
		{
			var ch = src[i];

			if (ch == '\'')
			{
				pass2.Append(ch);
				i++;
				while (i < src.Length)
				{
					var c = src[i];
					pass2.Append(c);
					i++;
					if (c == '\'')
					{
						if (i < src.Length && src[i] == '\'')
						{
							pass2.Append(src[i]);
							i++;
							continue;
						}
						break;
					}
				}
				pendingSpace = false;
				continue;
			}

			if (char.IsWhiteSpace(ch))
			{
				pendingSpace = pass2.Length > 0;
				i++;
				continue;
			}

			if (IsPunctuation(ch))
			{
				// Strip trailing space before punctuation.
				if (pass2.Length > 0 && pass2[^1] == ' ') pass2.Length--;
				pass2.Append(ch);
				pendingSpace = false;
				i++;
				continue;
			}

			if (pendingSpace && pass2.Length > 0 && !IsPunctuation(pass2[^1]))
			{
				pass2.Append(' ');
			}
			pass2.Append(ch);
			pendingSpace = false;
			i++;
		}

		return TrimTrailing(pass2.ToString());
	}

	public static string Hash(string sql)
	{
		var normalized = Normalize(sql);
		var bytes = Encoding.UTF8.GetBytes(normalized);
		var digest = SHA256.HashData(bytes);
		return Convert.ToHexString(digest).ToLowerInvariant();
	}

	static bool IsPunctuation(char c) => c is '(' or ')' or ',' or ';';

	static string TrimTrailing(string s)
	{
		var end = s.Length;
		while (end > 0)
		{
			var c = s[end - 1];
			if (c == ';' || char.IsWhiteSpace(c)) end--;
			else break;
		}
		return s[..end];
	}
}
