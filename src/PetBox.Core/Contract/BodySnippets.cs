namespace PetBox.Core.Contract;

// Response-body shaping helpers (spec bodylen-uniform-contract) — the cut-with-ellipsis
// one-liner every body-carrying surface used to carry its own private copy of
// (`ModuleMcp.Body`, `MemoryService.SnippetBody`, `TasksService.SnippetBody`). Lives in Core
// because the lower layers (Memory, Tasks) could not reuse the Web-side original without an
// upside-down Web←Memory dependency; Core is referenced by all of them.
public static class BodySnippets
{
	// The first `len` chars of `body`, with "…" appended when the body was actually cut.
	// `body` no shorter than `len` comes back untouched (no ellipsis on an exact fit).
	public static string TruncateWithEllipsis(string body, int len) =>
		body.Length <= len ? body : string.Concat(body.AsSpan(0, len), "…");
}
