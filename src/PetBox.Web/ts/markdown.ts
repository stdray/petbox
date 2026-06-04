// Client-side markdown rendering for plan-node bodies (board rows + the node detail page).
// The body is server-rendered as PLAIN TEXT inside a [data-md] element (so it's XSS-safe before
// we touch it); we parse it to HTML with marked and run it through DOMPurify before injecting.
// Storage/format is unchanged — bodies are already markdown, this is purely a view concern.

import DOMPurify from "dompurify";
import { marked } from "marked";

export function renderMarkdown(src: string): string {
	const html = marked.parse(src, { gfm: true, breaks: true }) as string;
	return DOMPurify.sanitize(html);
}

// Hydrate every [data-md] element under `root`: take its textContent as the raw markdown and
// replace it with rendered + sanitized HTML. Idempotent via a data-md-done flag so a second
// call (e.g. after an htmx swap) doesn't double-render.
export function hydrateMarkdown(root: ParentNode = document): void {
	for (const el of Array.from(root.querySelectorAll<HTMLElement>("[data-md]:not([data-md-done])"))) {
		const raw = el.textContent ?? "";
		el.innerHTML = renderMarkdown(raw);
		el.dataset["mdDone"] = "1";
	}
}
