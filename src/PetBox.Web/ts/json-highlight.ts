// Elementary, self-contained JSON syntax highlighting for the methodology editor's
// textareas (any `textarea[data-json-highlight]`): the classic transparent-textarea-over-
// highlighted-<pre> overlay. The textarea keeps ALL behavior (typing, caret, selection,
// undo, form post); the <pre> mirror behind it renders the same text tokenized into
// colored spans and follows the textarea's scroll. Zero dependencies; colors are daisyUI
// theme variables (see .json-tok-* in app.css), so the highlight tracks the active theme.

// One pass over the text: strings (key when followed by a colon), numbers, keywords.
// Everything unmatched (punctuation, whitespace) stays default-colored text.
const TOKEN = /("(?:\\.|[^"\\])*")(\s*:)?|\b(?:true|false|null)\b|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/g;

function renderTokens(code: HTMLElement, text: string): void {
	code.replaceChildren();
	let last = 0;
	for (const m of text.matchAll(TOKEN)) {
		const idx = m.index;
		const full = m[0] ?? "";
		const str = m[1];
		const colon = m[2];
		if (idx > last) code.appendChild(document.createTextNode(text.slice(last, idx)));
		const span = document.createElement("span");
		if (str !== undefined) {
			span.className = colon !== undefined ? "json-tok-key" : "json-tok-str";
			span.textContent = str;
			code.appendChild(span);
			if (colon !== undefined) code.appendChild(document.createTextNode(colon));
		} else {
			span.className = /^[-\d]/.test(full) ? "json-tok-num" : "json-tok-kw";
			span.textContent = full;
			code.appendChild(span);
		}
		last = idx + full.length;
	}
	if (last < text.length) code.appendChild(document.createTextNode(text.slice(last)));
}

// Mirror the metrics that decide glyph positions, so overlay text sits exactly under the
// textarea's. Copied from the computed style (robust against utility-class changes).
const MIRRORED = [
	"fontFamily",
	"fontSize",
	"fontWeight",
	"lineHeight",
	"letterSpacing",
	"paddingTop",
	"paddingRight",
	"paddingBottom",
	"paddingLeft",
	"borderTopWidth",
	"borderRightWidth",
	"borderBottomWidth",
	"borderLeftWidth",
] as const;

function attach(textarea: HTMLTextAreaElement): void {
	const wrapper = document.createElement("div");
	wrapper.className = "json-hl-wrap";
	const pre = document.createElement("pre");
	pre.className = "json-hl-mirror";
	pre.setAttribute("aria-hidden", "true");
	pre.dataset["testid"] = "json-highlight-mirror";
	const code = document.createElement("code");
	pre.appendChild(code);

	const cs = getComputedStyle(textarea);
	for (const prop of MIRRORED) pre.style[prop] = cs[prop];

	textarea.parentElement?.insertBefore(wrapper, textarea);
	wrapper.appendChild(pre);
	wrapper.appendChild(textarea);
	textarea.classList.add("json-hl-input");

	// A trailing newline keeps the mirror's scroll height in step with the textarea's,
	// so the last line never drifts while scrolling.
	const sync = (): void => renderTokens(code, `${textarea.value}\n`);
	const scroll = (): void => {
		pre.scrollTop = textarea.scrollTop;
		pre.scrollLeft = textarea.scrollLeft;
	};
	textarea.addEventListener("input", () => {
		sync();
		scroll();
	});
	textarea.addEventListener("scroll", scroll);
	sync();
}

export function initJsonHighlight(): void {
	for (const t of Array.from(document.querySelectorAll<HTMLTextAreaElement>("textarea[data-json-highlight]")))
		attach(t);
}
