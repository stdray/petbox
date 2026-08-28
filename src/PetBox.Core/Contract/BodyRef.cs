using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PetBox.Core.Contract;

// spec/no-retransmission-of-existing-content, work/write-body-by-reference: "Запись НЕ ДОЛЖНА
// требовать повторной передачи содержимого, которое уже имеется."
//
// `fragment` (FragmentPatch, spec/write-cost-follows-change) made the cost of an EDIT follow the
// size of the change. It does nothing for the other half of the problem: a body that ALREADY EXISTS
// as a file — a log, a diff, a command's output, a subagent's report — still has to be retyped into
// a JSON argument, which means it passes through the model's OUTPUT budget a second time (or, for a
// body the model never authored, a first time it should never have paid at all). And it passes
// through it in JSON string form, where a non-ASCII character an MCP client escapes as \uXXXX costs
// six output characters instead of one — the truncated-call/"could not be parsed as JSON" failure
// class the tool descriptions already warn about and that the warning demonstrably does not prevent.
//
// THE MECHANISM: the file is uploaded over REST (POST /api/blobs/{projectKey}, raw body — NOT a
// JSON argument, or the escaping problem returns by construction), the server answers with an
// opaque reference, and the write verb carries only that reference. The body never enters the
// JSON-RPC call at all.
//
// THE SHAPE, decided on this card rather than left open:
//
//   * ONE-SHOT. A blob is consumed by the write that references it and is then deleted. An
//     unconsumed blob expires after Ttl and is pruned in the background. This is a TRANSPORT, not
//     an attachment store: long-lived blobs would need listing, deletion, and per-project quotas,
//     none of which the spec asks for.
//   * TENANT = the project the blob was uploaded into. Any key authorized for that project may
//     reference it (ProjectScope decides, exactly as everywhere else), which is what makes the
//     fan-out case work: one agent uploads, another references. A key that is not authorized gets
//     "no such reference" — the same answer as a genuinely absent one, so this cannot become a
//     cross-tenant existence oracle (the posture IShareLinkDirectory and MemoryApi already take).
//   * ONE REFERENCE PER ITEM, not per call: N nodes of one tasks_upsert may each name a different
//     blob.
//   * `body` and `bodyRef` together are REFUSED, never silently precedence-ordered — the same rule,
//     through the same conflicts[] channel, as `body` vs `fragment`.
public static class BodyRefs
{
	// 10 MB. Deliberately UNRELATED to the canon write budget (10k chars): that budget governs a
	// STORE whose whole value is staying a compact index, this governs a TRANSPORT whose job is to
	// carry a log or a diff that nobody claims is compact. Conflating the two would cap the
	// transport at a size that makes it pointless for the population it exists to serve.
	public const long MaxBytes = 10L * 1024 * 1024;

	// How long an UNCONSUMED blob survives. A blob is uploaded moments before the write that
	// references it; a day is generous for a retry loop and short enough that the table cannot
	// silently become storage.
	public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

	const string Prefix = "blob-";

	// 128 bits of CSPRNG. The tenant check is the real gate — this is defence in depth, so that a
	// reference leaked into a transcript is not by itself a guessable neighbour of another one.
	static readonly Regex Shape = new("^blob-[0-9a-f]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static string NewReference() =>
		Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

	public static bool IsWellFormed(string? reference) =>
		reference is not null && Shape.IsMatch(reference);

	// The refusals. Kept here, next to each other, so every verb words them identically — the same
	// reason FragmentPatch.BodyAndFragment lives beside FragmentPatch.Apply.

	// Mirrors FragmentPatch.BodyAndFragment word for word in structure: two answers to "what is the
	// new text", and honouring either over the other would be a guess.
	public const string BodyAndBodyRef =
		"'body' and 'bodyRef' are mutually exclusive — 'body' carries the text inline, 'bodyRef' " +
		"names an uploaded blob that becomes the text; send one";

	public const string FragmentAndBodyRef =
		"'fragment' and 'bodyRef' are mutually exclusive — 'fragment' patches the current text, " +
		"'bodyRef' replaces it with an uploaded blob; send one";

	public static string Malformed(string reference) =>
		$"'bodyRef' value '{reference}' is not a blob reference — upload the file to " +
		"POST /api/blobs/{projectKey} and send the `ref` it returns verbatim";

	// ONE message for "never existed", "already consumed", "expired" and "belongs to a project you
	// may not read". Distinguishing them would disclose the existence of another tenant's blob, and
	// the caller's remedy is identical in all four cases: upload again.
	public static string Unresolvable(string reference) =>
		$"'bodyRef' {reference} is not available — a blob is ONE-SHOT (consumed by the write that " +
		$"references it), expires after {Ttl.TotalHours:0} hours, and is readable only by a key " +
		"authorized for the project it was uploaded into. Upload the file again and use the new ref.";
}

// A `bodyRef` as it reaches a write service: ALREADY looked up, by the transport layer, against the
// CALLER's authority — not the write target's.
//
// That split is deliberate and it is the reason this record exists at all instead of the services
// taking a raw reference string and a store. `memory_upsert` with `scope: workspace` writes into a
// container ($workspace / $ws-<key>) that is not a project the caller's key is claimed on, while the
// blob lives in the caller's OWN project. A service resolving the reference against its write target
// would look the blob up in the wrong tenant and refuse every legitimate workspace write. So the
// lookup happens where the ClaimsPrincipal is (the MCP tool layer), and what reaches the service is
// the VERDICT.
//
// `Text` and `Error` are exclusive: exactly one is non-null.
public sealed record BodyRefResolution(string Reference, string? Text, string? Error)
{
	public static BodyRefResolution Resolved(string reference, string text) => new(reference, text, null);

	public static BodyRefResolution Failed(string reference, string error) => new(reference, null, error);
}
