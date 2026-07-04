namespace PetBox.Web.Rendering;

// Server-side markdown → sanitized HTML for the READ surfaces (node bodies, comment bodies,
// board-row previews). Rendering on the server emits real <article>/<p>/<ul>… in the initial
// response so Firefox's reader-view heuristic (isProbablyReaderable) can detect the article.
// The live edit-preview still renders client-side (ts/markdown.ts); this is read-only.
public interface IMarkdownRenderer
{
	// Parse `markdown` to HTML and sanitize it (parity with the client DOMPurify path). Safe to
	// emit via @Html.Raw. Returns "" for null/empty input.
	string RenderToHtml(string? markdown);
}
