namespace PetBox.Web.Rendering;

// Server-side markdown → sanitized HTML for the READ surfaces (node bodies, comment bodies,
// board-row previews). Rendering on the server emits real <article>/<p>/<ul>… in the initial
// response so Firefox's reader-view heuristic (isProbablyReaderable) can detect the article.
// The live edit-preview still renders client-side (ts/markdown.ts); this is read-only.
public interface IMarkdownRenderer
{
	// Parse `markdown` to HTML and sanitize it (parity with the client DOMPurify path). Safe to
	// emit via @Html.Raw. Returns "" for null/empty input. When `commitUrlTemplate` is a usable
	// template (non-empty, carrying a literal {sha}), standalone 7–40-hex commit hashes in plain
	// text runs are autolinked to the commit view. When `nodeRefs` maps a slug to a target, a
	// `[[slug]]` mention in a plain text run becomes a link to that node (link text = the bare
	// slug; an unmapped mention stays literal). Both transforms exclude code spans/blocks and
	// existing links. With neither context the output is byte-identical to the plain path.
	string RenderToHtml(string? markdown, string? commitUrlTemplate = null,
		IReadOnlyDictionary<string, NodeRefTarget>? nodeRefs = null);
}
