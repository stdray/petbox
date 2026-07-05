// Client-side markdown rendering for the live edit-preview (ts/nodeEdit.ts). Read surfaces
// (node/comment bodies, board-row previews) are rendered server-side — real markup already in
// the initial DOM (PetBox.Web.Rendering.MarkdownRenderer) — so browser reader-view can detect
// them; this renderer only serves the write/preview tab while the user is still typing.

import DOMPurify from "dompurify";
import { marked } from "marked";

export function renderMarkdown(src: string): string {
	const html = marked.parse(src, { gfm: true, breaks: true }) as string;
	return DOMPurify.sanitize(html);
}
