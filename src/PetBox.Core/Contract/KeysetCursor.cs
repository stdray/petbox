using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PetBox.Core.Contract;

// The pagination token of a LISTING read, and its wire form (spec surface-economy /
// bounded-result-sets — the other half of ResponseBudget, which lives next door).
//
// ResponseBudget answers "how much of the answer fits in one response"; it does NOT answer
// "how do I get the rest". Without a resume token a corpus that outgrows the ~30k budget has
// an UNREACHABLE TAIL: the budget is a constant, the board is not, and there is nothing to
// ask the tail with. This type is that missing half — the position a caller hands back to
// continue where the previous page stopped.
//
// KEYSET, NOT OFFSET. An offset ("skip the first 40") is wrong under concurrent writes: a row
// inserted before the boundary silently re-serves a row, a row deleted before it silently
// swallows one, and neither is visible to the caller. A keyset token names the LAST ROW THAT
// WAS ACTUALLY EMITTED — (sort-key value, key, board) — inside a TOTAL order, so "after this
// row" means the same thing no matter what happened elsewhere in the list. Precedent in this
// codebase: LogCursor (PetBox.Log.Core/Query/LogCursor.cs) pages the log the same way, on its
// own total key (TimestampMs, Id).
//
// STRICT ON DECODE — deliberately unlike LogCursor. LogCursor decodes leniently (garbage =
// "no cursor") because its other consumer is an SSE reconnect, where a bad Last-Event-ID must
// degrade to "from now on" instead of killing the stream. A tool call has no such excuse: a
// caller that pages with a token from a DIFFERENT query is mid-enumeration, and silently
// restarting them at the top of some other ordering hands back a page that looks valid and is
// not. So a malformed token, and a token whose Fingerprint does not match the query being
// served, are ERRORS with instructive text. The caller is told to restart WITHOUT the cursor —
// an explicit act — rather than being restarted behind their back.
//
// FINGERPRINT. Everything that decides WHICH rows are selected and in WHAT order — the sort
// axis, its direction, and every filter predicate — is hashed into the token (FingerprintOf).
// Paging is only coherent within one such query; changing a filter mid-walk produces a
// different fingerprint and therefore an error rather than two half-lists stapled together.
// Knobs that change only how a row is RENDERED (bodyLen, url inclusion) or how many rows one
// page carries (limit) are deliberately NOT in the fingerprint: they change the page, not the
// sequence, and a caller may vary them between pages.
//
// KNOWN, ACCEPTED ANOMALY. A node whose SORT KEY changes between two pages (its priority is
// edited, say) can be skipped or served twice — it moved across the boundary the token names.
// Fixing that needs an as-of snapshot read of the whole board, which is out of proportion to
// the anomaly; the tools that expose a cursor document it instead.
//
// ORDER HASH. The fingerprint answers "is this the same QUERY"; `OrderHash` answers the question that
// turned out to be different — "is this the same ORDER". They come apart whenever a ranked result is
// REBUILT rather than reused: identical data, identical filters, identical fingerprint, but a rerank
// route that was down when page 1 was built and up when page 2 was (or the reverse, which is the
// commoner shape — a degraded pool is not cached, so page 2 is always a rebuild). RRF ordering and
// cross-encoder ordering are simply different lists, and an identity seek into the wrong one skips and
// duplicates rows without a single error. Data-version stamps cannot see this: nothing was written.
// Nor can they see an embed cascade answering with a different model between pages, or the async vector
// worker draining the index mid-walk (a write to the vector index is not a write to the entity table).
//
// So the token carries a hash of the ORDER it was issued against, and every page re-checks it. Against
// a cached pool it matches for free; against a rebuilt one it matches only if the rebuild genuinely
// reproduced the list. Everything else is a loud, honest refusal. This generalizes the scheme session
// search already used — the only surface that was closed on purpose rather than by luck.
// DATA STAMP. `Fingerprint` answers "is this the same QUESTION" — the caller's own arguments: the
// selection and ordering inputs THEY supplied. It used to also carry the DATA VERSION of the container(s)
// a query read (memory_search's `containerStamps`, tasks_search's `dataVersion`), which meant a write
// landing between two pages of an UNCHANGED query surfaced as "this cursor was issued for a DIFFERENT
// query" — true by the letter (the fingerprint moved) and false by the sense a caller reads it in: they
// changed nothing, and the advice that follows ("keep the query identical while paging") is a caller
// contribution they had not withheld. Measured on the live server 2026-08-28 (card
// cursor-refusal-blames-caller-for-data-shift): a chasing write moved memory's and tasks' fingerprints
// with the order hash untouched, so the honest "pool expired" refusal (work/rerank-route-nondeterministic-
// order) was unreachable behind this one on any actively-written project. `DataStamp` is the fix: it moves
// the data version OUT of `Fingerprint` and into its own field, checked by its own `AssertDataStamp`, so a
// data shift gets a diagnosis that actually names what changed instead of impersonating a caller mistake.
public readonly record struct KeysetCursor(
	string Fingerprint, string SortValue, string Key, string Board, string OrderHash = "", string DataStamp = "")
{
	// Bumped only if the payload's SHAPE changes; an old token then fails decode loudly
	// (see the strictness note above) instead of being misread as the new shape.
	// 2: added `o` (OrderHash) — a v1 token carries no order commitment, so it must not be honoured.
	// 3: added `d` (DataStamp), moved OUT of `f` (see the DATA STAMP note above). A v2 token has the data
	// version baked INSIDE its fingerprint instead of carrying it separately, so there is no `d` to compare
	// it against under the new, args-only fingerprint scheme — exactly the v1→v2 situation repeated: it
	// "carries no [...] commitment, so it must not be honoured", and is refused as an old format rather
	// than silently misread. The cost is small and one-shot: a pool survives a deploy on disk (up to ~15
	// minutes, SearchPoolCache.DefaultTtl), so only a walk whose token was minted before THIS deploy pays a
	// restart, once.
	const int FormatVersion = 3;

	// The token's payload. Short names because the encoded form travels in every page request
	// and back in every response — a token is overhead, not content.
	// internal, not private: System.Text.Json's reflection path constructs this through its
	// primary constructor, and a private nested type is needlessly close to that edge.
	internal sealed record Payload(
		[property: JsonPropertyName("v")] int Version,
		[property: JsonPropertyName("f")] string Fingerprint,
		[property: JsonPropertyName("s")] string SortValue,
		[property: JsonPropertyName("k")] string Key,
		[property: JsonPropertyName("b")] string Board,
		[property: JsonPropertyName("o")] string? OrderHash = null,
		[property: JsonPropertyName("d")] string? DataStamp = null);

	static readonly JsonSerializerOptions TokenJson = new(JsonSerializerDefaults.Web);

	// base64 of the JSON payload — OPAQUE by contract: the fields above are an implementation
	// detail a caller must not parse, construct or edit. Base64 (not raw JSON) is what makes
	// that stick: it is one line, safe in a JSON string argument, and visibly not a structure
	// to reach into.
	public string Encode() =>
		Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
			new Payload(FormatVersion, Fingerprint, SortValue, Key, Board, OrderHash, DataStamp), TokenJson));

	// The ORDER a pool was in when a token was issued, as one hash: each row's address and score, in
	// order. Address AND score, because either moving is a different list to page — a reordering shows
	// up in the sequence, and a re-scoring (RRF ~1/60-scale vs a cross-encoder's) shows up in the values
	// even when the sequence happens to survive. "R" round-trips the double exactly, so a pool rebuilt
	// identically hashes identically and this never fires on a walk that was actually fine.
	public static string OrderHashOf(IEnumerable<(string Address, double Score)> ordered) =>
		FingerprintOf([.. ordered.Select(r => r.Address + "=" + r.Score.ToString("R", System.Globalization.CultureInfo.InvariantCulture))]);

	// The order check, as its own refusal with its own words. Deliberately NOT folded into the
	// fingerprint: a fingerprint mismatch means the caller changed the QUESTION and the advice is "keep
	// your arguments identical", which would be actively misleading here — the caller did everything
	// right and the SERVER's ranking moved under them. Saying "this row is gone" (the old comparison-path
	// message) was the same lie from the other end: the row is present, it is the ordering that is not.
	public void AssertPoolOrder(string expectedOrderHash, string subject)
	{
		if (string.Equals(OrderHash, expectedOrderHash, StringComparison.Ordinal)) return;

		throw new ArgumentException(
			$"{subject}: the ranked order this cursor was issued against no longer exists — the result was "
			+ "rebuilt and came out ranked DIFFERENTLY (a rerank route recovered or went down between "
			+ "pages, or the vector index moved), so continuing would splice two orderings and silently "
			+ "skip or repeat rows. Your arguments are fine and nothing was written; the ranking simply "
			+ "changed underneath. Drop the cursor and start the query over.");
	}

	// THE DATA COMMITMENT — its own refusal, its own words, for the same reason AssertPoolOrder got one:
	// folding this into the fingerprint made a defect indistinguishable from a caller mistake. A write
	// between two pages of the SAME query moves the data a walk is reading; that is real and MUST end the
	// walk (splicing two states together silently skips or repeats rows — the refusal is the feature, see
	// SearchPoolCache's own note on this), but it is not the caller having "changed the query", and telling
	// them to "keep the query identical" is advice they already followed. Card:
	// cursor-refusal-blames-caller-for-data-shift.
	public void AssertDataStamp(string expectedDataStamp, string subject)
	{
		if (string.Equals(DataStamp, expectedDataStamp, StringComparison.Ordinal)) return;

		throw new ArgumentException(
			$"{subject}: the DATA this cursor was reading has changed since the page it came from — "
			+ "something was written to the project between pages, so continuing would splice two states "
			+ "together and silently skip or repeat rows. Your arguments are fine and the query itself did "
			+ "not change; the underlying data did. Drop the cursor and start the query over.");
	}

	// THE POOL COMMITMENT — the refusal that had to exist once the order stopped being reproducible.
	//
	// MEASURED, not assumed (work/rerank-route-nondeterministic-order): the cross-encoder that ranks a
	// pool is NOT bit-reproducible. On the local route, 8 close paraphrases of ~50-60 tokens came back in
	// 3 different orders across 10 identical calls (9 of 10 differed from the first); on a cloud route the
	// sequence held but the scores moved by up to 3e-3. Both break an order hash, and neither is a defect
	// in this codebase: GPU reduction kernels are not batch-invariant, so the shape of the batch a request
	// lands in decides the last bits of the score. There is no rounding that separates that noise from
	// signal either — the reranker's own gaps between ADJACENT candidates are SMALLER than its own jitter.
	//
	// So a reranked order is not a function of the query and the data; it is a property of ONE PASS. The
	// cursor is therefore bound to the MATERIALIZED POOL that pass produced, and when that pool is gone
	// the walk is over — not because anything is broken, but because nothing can promise to rebuild the
	// list the caller was walking. `poolRebuiltByRerank` is exactly that state: the order in hand came out
	// of a FRESH cross-encoder pass instead of out of the stored pool.
	//
	// WHY IT IS SCOPED TO A RERANK PASS and not to every cache miss. An RRF order (Speed's ChosenRrf, an
	// outage's DegradedRrf) IS reproducible — it is arithmetic over the same index — and AssertPoolOrder
	// below proves reproduction exactly and for free. Refusing those too would end paging for every
	// deployment with no rerank route, and for the whole length of any rerank outage, in exchange for
	// nothing: this codebase's standing rule is that a rerank outage must never take search down.
	//
	// AssertPoolOrder stays, behind this, as the SECOND ECHELON. It no longer decides the common case (a
	// live pool always matches it; a dead one is refused here first), but it costs nothing and still
	// catches what this cannot see — an order that moved with the pool still in hand.
	public static void AssertPoolAlive(bool poolRebuiltByRerank, string subject)
	{
		if (!poolRebuiltByRerank) return;

		throw new ArgumentException(
			$"{subject}: the ranked POOL this cursor was walking is gone — it expired (a walk keeps its pool "
			+ "for about 15 minutes of idleness) or was never kept, so the rows had to be ranked again from "
			+ "scratch. A second cross-encoder pass does not reproduce the first one document-for-document, so "
			+ "continuing would splice two orderings and silently skip or repeat rows. Your arguments are fine, "
			+ "nothing was written and nothing is broken; the walk simply outlived its pool. Drop the cursor and "
			+ "start the query over.");
	}

	// Decode a token issued for the query identified by `expectedFingerprint`. Throws
	// ArgumentException — never returns null, never falls back to "start from the top" — for a
	// token that is malformed, of an unknown format version, or issued for a DIFFERENT query.
	// `subject` names the tool in the error text so the message reads as advice, not a stack
	// trace ("tasks_search: cursor …").
	public static KeysetCursor Decode(string? cursor, string expectedFingerprint, string subject)
	{
		if (string.IsNullOrWhiteSpace(cursor))
			throw new ArgumentException($"{subject}: cursor is empty — omit it to start from the first page");

		Payload? p;
		try
		{
			p = JsonSerializer.Deserialize<Payload>(Convert.FromBase64String(cursor), TokenJson);
		}
		catch (Exception e) when (e is FormatException or JsonException)
		{
			throw new ArgumentException(
				$"{subject}: cursor is not a pagination token issued by this tool — a cursor is OPAQUE, "
				+ "pass back the `nextCursor` from the previous page verbatim, or omit it to start over");
		}

		if (p is null || p.Version != FormatVersion || p.Fingerprint.Length == 0)
			throw new ArgumentException(
				$"{subject}: cursor is malformed or from an older token format — omit it to start over");

		// The whole point of the strict decode: a token from another query is refused, not
		// silently honoured against the wrong ordering.
		if (!string.Equals(p.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
			throw new ArgumentException(
				$"{subject}: this cursor was issued for a DIFFERENT query — the sort axis or a filter "
				+ "changed since the page it came from, so continuing would splice two orderings. Keep "
				+ "the query identical while paging, or drop the cursor to start the new query over.");

		return new KeysetCursor(p.Fingerprint, p.SortValue, p.Key, p.Board, p.OrderHash ?? "", p.DataStamp ?? "");
	}

	// STRUCTURAL decode WITHOUT the fingerprint check, plus the check as a separate step. For consumers
	// whose expected fingerprint is not knowable until the read has already run — session_search stamps
	// its token with the DISCOVERY ORDER itself, which only exists after discovery — so Decode's
	// all-at-once contract cannot be satisfied before the position is needed.
	//
	// This does NOT weaken the guarantee, and it must not be used as if it did: the token's POSITION is
	// read here, but it is still worthless until AssertFingerprint has run, and the consumer must call it
	// before returning ANY row. The split moves the check later in the call, never out of it. Every other
	// refusal (empty, not base64, wrong format version, no fingerprint) still fires here, up front.
	public static KeysetCursor Peek(string? cursor, string subject)
	{
		if (string.IsNullOrWhiteSpace(cursor))
			throw new ArgumentException($"{subject}: cursor is empty — omit it to start from the first page");

		Payload? p;
		try
		{
			p = JsonSerializer.Deserialize<Payload>(Convert.FromBase64String(cursor), TokenJson);
		}
		catch (Exception e) when (e is FormatException or JsonException)
		{
			throw new ArgumentException(
				$"{subject}: cursor is not a pagination token issued by this tool — a cursor is OPAQUE, "
				+ "pass back the `nextCursor` from the previous page verbatim, or omit it to start over");
		}

		if (p is null || p.Version != FormatVersion || p.Fingerprint.Length == 0)
			throw new ArgumentException(
				$"{subject}: cursor is malformed or from an older token format — omit it to start over");

		return new KeysetCursor(p.Fingerprint, p.SortValue, p.Key, p.Board, p.OrderHash ?? "", p.DataStamp ?? "");
	}

	// The other half of Peek: the SAME refusal Decode makes, as its own step. Word-for-word identical
	// message, because it is word-for-word the same failure — a caller must not be able to tell which
	// verb validated their token.
	public void AssertFingerprint(string expectedFingerprint, string subject)
	{
		if (!string.Equals(Fingerprint, expectedFingerprint, StringComparison.Ordinal))
			throw new ArgumentException(
				$"{subject}: this cursor was issued for a DIFFERENT query — the sort axis or a filter "
				+ "changed since the page it came from, so continuing would splice two orderings. Keep "
				+ "the query identical while paging, or drop the cursor to start the new query over.");
	}

	// The query identity a token is bound to: an order-sensitive hash of the caller-supplied
	// parts that decide selection + ordering. Null and "" stay distinguishable (an absent filter
	// is not an empty one — null becomes RS) and the parts join on US: two control characters no
	// slug, board name or sort axis can contain, so no part can impersonate a boundary.
	public static string FingerprintOf(params string?[] parts)
	{
		var joined = string.Join('\u001f', parts.Select(p => p ?? "\u001e"));
		// Truncated to 10 bytes / 20 hex chars: this is a collision-shaped equality check on
		// caller-supplied text, not a security boundary, and the token rides every request.
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)).AsSpan(0, 10)).ToLowerInvariant();
	}

	// The rows of `ordered` that come strictly AFTER this cursor's position, where `ordered` is
	// already sorted by (sort value, key, board) — the exact order the caller paged last time.
	//
	// Two ways to find the boundary, in this order:
	//  1. IDENTITY, **and only while the row has not MOVED**. If the row named by the token is still in
	//     the list AND its sort value still matches the one the token recorded, resume right after it.
	//     That is exact and immune to any modelling error in the comparison below.
	//
	//     The sort-value guard is not paranoia — without it this path silently eats the tail of a walk
	//     on any VOLATILE axis. Sessions sort by `Updated`, which every write moves: let the boundary
	//     row receive a message between pages and it jumps to the far end of the list, and "resume after
	//     it, wherever it now sits" resumes after its NEW position — skipping every unvisited row in
	//     between, with nothing on the wire to say so. The documented anomaly below promises that ONE
	//     row may be missed or repeated; losing the middle of the list is a different thing entirely, so
	//     a row that moved is handed to the comparison path instead, which resumes at the POSITION the
	//     token described rather than at wherever the row wandered to.
	//  2. COMPARISON. If that row is gone (deleted, renamed, filtered out since) or has MOVED along the
	//     sort axis, fall back to the keyset predicate — skip while the row is not after the token.
	//     Deleting the boundary row must not restart the walk.
	//
	// `sortComparison` compares two canonical sort-value strings for the axis in play; the
	// caller supplies it because only the caller knows whether "12" is a number, a title or a
	// timestamp. `desc` inverts the PRIMARY key only — key/board tie-breakers are ascending in
	// both directions, matching how the sorts are actually built.
	public static IReadOnlyList<T> Advance<T>(
		IReadOnlyList<T> ordered,
		KeysetCursor cursor,
		Func<T, (string Sort, string Key, string Board)> keyOf,
		Comparison<string> sortComparison,
		bool desc,
		string subject)
	{
		for (var i = 0; i < ordered.Count; i++)
		{
			var (sort, key, board) = keyOf(ordered[i]);
			if (string.Equals(key, cursor.Key, StringComparison.Ordinal)
				&& string.Equals(board, cursor.Board, StringComparison.Ordinal))
			{
				// Same row, same place on the axis → resume after it. If its sort value MOVED, this is no
				// longer the position the token described, so fall through to the comparison path (which
				// resumes at the token's POSITION) instead of skipping everything the row jumped over.
				if (!string.Equals(sort, cursor.SortValue, StringComparison.Ordinal)) break;
				return i + 1 >= ordered.Count ? [] : ordered.Skip(i + 1).ToList();
			}
		}

		try
		{
			return ordered.SkipWhile(r => !IsAfter(keyOf(r), cursor, sortComparison, desc)).ToList();
		}
		catch (Exception e) when (e is FormatException or OverflowException)
		{
			// Only reachable from a forged token that somehow carries a matching fingerprint —
			// still an explicit refusal rather than a 500 out of a comparison delegate.
			throw new ArgumentException(
				$"{subject}: cursor carries a position that does not belong to this sort axis — omit it to start over");
		}
	}

	static bool IsAfter(
		(string Sort, string Key, string Board) row, KeysetCursor cursor, Comparison<string> sortComparison, bool desc)
	{
		var cmp = sortComparison(row.Sort, cursor.SortValue);
		if (desc) cmp = -cmp;
		if (cmp != 0) return cmp > 0;
		cmp = string.CompareOrdinal(row.Key, cursor.Key);
		if (cmp != 0) return cmp > 0;
		return string.CompareOrdinal(row.Board, cursor.Board) > 0;
	}
}
