using System.Data;
using FluentMigrator;
using PetBox.Core.Auth;

namespace PetBox.Core.Data.Migrations;

// Clears the retired `agents:read` / `agents:write` tokens out of ApiKeys.Scopes — the DATA tail of
// work agent-defs-server-teardown (work retire-agents-scopes-from-live-keys).
//
// WHY THERE IS ANYTHING LEFT TO DO. M054 dropped `agent_definitions` and the same change removed
// both scopes from `ApiKeyScopes.All`. That retires the tokens from the CATALOG; it does not touch
// the strings already stored on issued keys. Measured on production 2026-09-06 via `apikey_list`
// across every project: 17 keys in 13 projects still carry one or both — $system x3, kek-devices x3,
// and one each in animemov, express, infra, one-c, petsonde, pochtar, seller, smoke,
// tarantool-deploy, xdc, yobapub. Most were minted by the Connect page back when AgentDefaultScopes
// still included `agents:read`.
//
// WHAT ACTUALLY BREAKS — it is not access control. `ApiKeyScopes.Granted` compares tokens exactly,
// so an unknown token grants nothing and denies nothing; every one of those keys works. Two other
// surfaces do break:
//
//   * `AgentKeyAdminService` REFUSES a scope set containing an unknown token ("Unknown scopes:
//     agents:read") on MCP `apikey_update` / PATCH. `whoami` hands an agent the RAW stored split, so
//     the natural script — read whoami.scopes, send it back with one entry added — is refused by a
//     token the caller never chose.
//   * `_AgentKeysTable.cshtml` renders the raw column as badges, so the admin table shows a
//     `agents:read` badge for which the editor below it renders no checkbox (it renders from
//     `AllScopes`). The row looks editable and is not.
//
// ── THE THREE RULES THIS MIGRATION HOLDS ──────────────────────────────────────────────────────
//
// 1. A ROW WITH NOTHING RETIRED IS NOT WRITTEN AT ALL. Not rewritten identically — not written. The
//    canonical stored form is comma-joined (every writer is `string.Join(',', ...)`), but
//    `ApiKeyScopes.Validate` also accepts space- and semicolon-separated input, so a hand-set row
//    could legally hold "data:read logs:query". Re-joining every row would silently normalize rows
//    this card was never about. Only a row that genuinely loses a token is rebuilt, and rebuilding
//    it does normalize its separators — which is correct: it is being rewritten anyway, and the
//    comma form is what every other writer produces.
//
// 2. ORDER AND MULTIPLICITY OF THE SURVIVORS ARE PRESERVED EXACTLY. The retired tokens sit in the
//    MIDDLE of the real strings ("...agent:heartbeat,agents:read,agents:write,admin:provision") and
//    at the START of one ("agents:read,agents:write,config:read,..."), so a filter that reordered or
//    de-duplicated would be quietly rewriting grants. De-duplication is deliberately NOT done here:
//    every write path already applies `.Distinct()`, so a duplicate in the column is an anomaly to
//    leave visible, not a second thing for this migration to fix.
//
// 3. A ROW IS NEVER EMPTIED. If every token on a key is retired, the row is SKIPPED and keeps its
//    dead tokens. That looks like giving up on the card's own goal, so it is the one decision here
//    that needs the reason spelled out:
//
//      - An empty `Scopes` is NOT equivalent to "only unknown scopes" further down the stack.
//        `McpToolScopeFilter` reads `granted.Count == 0` as "no claim -> show all" — the branch that
//        exists for cookie identities, which carry no scopes claim at all. A key stored with "" is
//        indistinguishable from one there, so emptying such a row would flip its `tools/list` from
//        "filtered down to nothing" to "the entire tool catalog". No access widens (`ModuleMcp`'s
//        AssertScope still denies every one of those calls), but a data migration must not change
//        what a key OBSERVES.
//      - The product refuses to create this state deliberately: `AgentKeyAdminService.UpdateAsync`
//        answers "Select at least one scope — a key with no scopes can do nothing." A migration
//        should not manufacture a state the write path rejects.
//      - Inventing a replacement scope so the row stays non-empty would be this migration GRANTING
//        something nobody asked for, which is strictly worse than leaving a dead token.
//      - And it is free: of the 17 keys measured above, ZERO have only retired tokens. Every one is
//        a mix. This branch is a guard against a database this census did not see, not a case that
//        drops rows on the ground today.
//
// TOKENIZATION IS THE CATALOG'S, NOT A LOCAL PARSER. `ApiKeyScopes.Split` is the one reading of a
// stored scope string (spec access-permission-uniform) — separators `, ` `;`, empty entries removed,
// entries trimmed. Matching is `ApiKeyScopes.Comparer` (Ordinal), for the reason the catalog gives at
// length: a case-insensitive match here would recognize a token the catalog does not, which is the
// disagreement that spec closed. So "Agents:Read" is NOT removed — and cannot exist anyway, because
// `Validate` checks the same Ordinal set at mint time, so no writer could ever have stored it.
//
// The two literals below are spelled out rather than referenced: the constants are gone from
// `ApiKeyScopes`, and a migration is history — it must keep meaning the same thing when the catalog
// moves again.
//
// Forward-only: Down does not put the tokens back (there is nothing left that would honour them).
[Migration(55, "Strip retired agents:read/agents:write tokens from issued ApiKeys.Scopes")]
public sealed class M055_RetireAgentScopesFromKeys : Migration
{
	internal static readonly string[] RetiredScopes = ["agents:read", "agents:write"];

	static readonly HashSet<string> Retired = new(RetiredScopes, ApiKeyScopes.Comparer);

	public override void Up() => Execute.WithConnection((conn, tx) =>
	{
		// Read fully, THEN write. The rewrites are buffered rather than applied inside the reader
		// loop because an UPDATE issued on a connection with an open reader is a different (and
		// provider-dependent) contract than the plain sequential one this needs.
		var rewrites = new List<(string Key, string Scopes)>();

		using (var read = conn.CreateCommand())
		{
			read.Transaction = tx;
			read.CommandText = "SELECT \"Key\", \"Scopes\" FROM \"ApiKeys\";";
			using var reader = read.ExecuteReader();
			while (reader.Read())
			{
				var raw = reader.IsDBNull(1) ? null : reader.GetString(1);
				if (Rewrite(raw) is { } cleaned)
					rewrites.Add((reader.GetString(0), cleaned));
			}
		}

		// An empty list is the expected outcome on a database that never held these tokens (a fresh
		// install runs this migration too) — nothing to do is not a failure.
		foreach (var (key, scopes) in rewrites)
		{
			using var write = conn.CreateCommand();
			write.Transaction = tx;
			write.CommandText = "UPDATE \"ApiKeys\" SET \"Scopes\" = @scopes WHERE \"Key\" = @key;";
			Bind(write, "@scopes", scopes);
			Bind(write, "@key", key);
			write.ExecuteNonQuery();
		}
	});

	public override void Down() { } // forward-only

	// The whole decision, in one place and unit-testable: the new column value, or null for
	// "leave this row exactly as it is". Both skip cases return null so that a row which needs no
	// change is never written — see rules 1 and 3 above.
	internal static string? Rewrite(string? raw)
	{
		var tokens = ApiKeyScopes.Split(raw);
		var kept = tokens.Where(t => !Retired.Contains(t)).ToArray();

		if (kept.Length == tokens.Length) return null; // nothing retired here
		if (kept.Length == 0) return null;             // would empty the key

		return string.Join(',', kept);
	}

	static void Bind(IDbCommand cmd, string name, string value)
	{
		var p = cmd.CreateParameter();
		p.ParameterName = name;
		p.Value = value;
		cmd.Parameters.Add(p);
	}
}
