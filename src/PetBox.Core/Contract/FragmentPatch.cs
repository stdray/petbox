namespace PetBox.Core.Contract;

// spec/write-cost-follows-change, work/write-fragment-patch: "Стоимость записи ДОЛЖНА следовать
// объёму изменения, а не размеру узла."
//
// A full-replace write makes the cost of an edit proportional to the SIZE OF THE NODE, not the
// size of the change: fixing one paragraph of a long body costs a complete re-emit of the body.
// A fragment patch is the point fix — the caller sends only {old, new} and the server does the
// substitution against the row it is already reading during the merge.
//
// SEMANTICS (deliberate, and each one is a refusal rather than a guess):
//
//   * UNIQUENESS IS MANDATORY. An `old` that occurs more than once is REFUSED, never resolved to
//     "the first match". This is the central requirement of the card, and it is not a nicety: the
//     caller cannot see which occurrence the server would pick, so a first-match rule silently
//     edits a place the caller never looked at. The same rule the `Edit` tool enforces.
//   * ZERO MATCHES IS REFUSED. `old` not being there means the body is not what the caller read —
//     the same class of failure as a stale version watermark, and it gets the same treatment
//     (refuse, explain, let the caller re-read) rather than a no-op that reports success.
//   * ALL-OR-NOTHING over the list. Edits apply IN ORDER to a running buffer, so a later edit sees
//     an earlier edit's result; but the buffer is only handed back when every edit succeeded. A
//     list that fails at edit #3 leaves the stored body untouched, not two-thirds patched.
//   * `new` MUST BE PRESENT (`""` is how you delete text). The MCP unknown-parameter filter walks
//     the top level and ONE hop into batch items — it cannot see inside `fragment[]`, which is a
//     second hop. So a caller who typos `nw:"..."` gets an item whose `new` is null. Were null
//     read as "", that typo would silently DELETE the matched text — a lost mutation of exactly
//     the kind the filter exists to prevent. Requiring `new` turns that typo into a refusal.
//
// Ordinal comparison throughout: bodies are markdown, `old` is a literal slice the caller copied
// out of one, and a culture-sensitive match could equate text the caller can see is different.
public readonly record struct FragmentEdit(string? Old, string? New);

// The verdict of resolving a fragment list against a body: either the patched text, or the reason
// the write must be refused. Never both, and never a partially-applied body.
public readonly record struct FragmentPatchResult(bool Ok, string Body, string? Error)
{
	public static FragmentPatchResult Success(string body) => new(true, body, null);
	public static FragmentPatchResult Failure(string error) => new(false, string.Empty, error);
}

public static class FragmentPatch
{
	// The one refusal every caller shares: `body` and `fragment` are two different answers to
	// "what is the new text", and honouring either one over the other would be a guess. Kept here
	// so all three verbs word it identically.
	public const string BodyAndFragment =
		"'body' and 'fragment' are mutually exclusive — 'body' replaces the whole text, 'fragment' " +
		"patches part of it; send one";

	// Resolve `edits` against `current`, in order. `current` is the body of the row the caller's
	// baseline refers to — i.e. the SAME row the surrounding merge inherits every other omitted
	// field from, so the substitution is consistent with the rest of the write by construction.
	public static FragmentPatchResult Apply(string? current, IReadOnlyList<FragmentEdit>? edits)
	{
		if (edits is not { Count: > 0 })
			return FragmentPatchResult.Failure("'fragment' is empty — send at least one {old, new} edit");

		var body = current ?? string.Empty;
		for (var i = 0; i < edits.Count; i++)
		{
			var (old, replacement) = edits[i];
			// Position is part of every message: in a multi-edit list "no match" without an index
			// tells the caller a fragment failed but not WHICH, and the list is the whole point.
			var at = edits.Count == 1 ? "'fragment'" : $"'fragment[{i}]'";

			if (string.IsNullOrEmpty(old))
				return FragmentPatchResult.Failure($"{at}: 'old' is required and must be non-empty");
			// See the header: null `new` is a typo signal, not a deletion request.
			if (replacement is null)
				return FragmentPatchResult.Failure(
					$"{at}: 'new' is required (send \"\" to delete the matched text)");

			var count = Occurrences(body, old);
			if (count == 0)
				return FragmentPatchResult.Failure(
					$"{at}: 'old' does not occur in the current text — it has moved since you read it; " +
					"re-read the body and rebase your edit");
			if (count > 1)
				return FragmentPatchResult.Failure(
					$"{at}: 'old' occurs {count} times — a fragment must match EXACTLY once; " +
					"extend it with surrounding text until it is unique");

			var idx = body.IndexOf(old, StringComparison.Ordinal);
			body = string.Concat(body.AsSpan(0, idx), replacement, body.AsSpan(idx + old.Length));
		}

		return FragmentPatchResult.Success(body);
	}

	// NON-OVERLAPPING occurrence count. Advancing by needle.Length (not by 1) is what makes
	// "aaaa" contain TWO "aa", not three: the caller reasons about slices of text it could point
	// at, and two overlapping matches are not two places to edit. Counting overlaps instead would
	// refuse writes that are genuinely unique-by-slice.
	//
	// Counted to the END even though ONE extra match is already enough to refuse: the refusal
	// message quotes this number, and stopping at 2 would tell a caller with five matches that it
	// has two — a specific, checkable claim that happens to be false. The scan is a single pass
	// over a body that the same call is about to rewrite wholesale anyway.
	static int Occurrences(string haystack, string needle)
	{
		var count = 0;
		var from = 0;
		while (from <= haystack.Length - needle.Length)
		{
			var idx = haystack.IndexOf(needle, from, StringComparison.Ordinal);
			if (idx < 0) break;
			count++;
			from = idx + needle.Length;
		}
		return count;
	}
}
