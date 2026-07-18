// Comment thread interactivity (comments-ui-edit): the per-comment reply/edit toggles under
// board nodes and the node detail page. Imperative (mirrors nodeEdit.ts / board.ts — no
// inline JS in Razor): the reply/edit forms are server-rendered hidden (inline style) and
// shown by toggling that style. There can be MANY thread containers on the board page (one
// per node card) and MANY comments per thread, so this delegates ONE click listener per
// thread container instead of binding a handler per button.
export function initCommentThreads(): void {
	const threads = document.querySelectorAll<HTMLElement>("[data-testid='node-comments']");
	for (const root of Array.from(threads)) wireThread(root);

	// comment-permalink-anchor: the anchor scroll itself is native (id="comment-{id}" +
	// `<a href="#comment-{id}">`, no JS needed) — this only adds the brief flash so the
	// target comment is obvious, not just scrolled-to. Runs on initial load AND on
	// "hashchange" so clicking a comment's own timestamp permalink (same page, hash-only
	// navigation — no reload, so initCommentThreads doesn't re-run) re-flashes it too,
	// mirroring GitHub's click-timestamp-to-highlight idiom.
	flashCommentFromHash();
	window.addEventListener("hashchange", flashCommentFromHash);
}

const COMMENT_HASH = /^#comment-(.+)$/;
const FLASH_CLASS = "comment-permalink-flash";

function flashCommentFromHash(): void {
	const id = COMMENT_HASH.exec(location.hash)?.[1];
	if (!id) return;
	const target = document.getElementById(`comment-${id}`);
	if (!(target instanceof HTMLElement)) return;

	// Restart the animation even if the same hash fires twice in a row (e.g. clicking a
	// permalink whose hash already matches the current one doesn't fire "hashchange" at
	// all, but re-clicking a DIFFERENT comment's link right after does, and re-adding the
	// class mid-animation would otherwise be a no-op).
	target.classList.remove(FLASH_CLASS);
	void target.offsetWidth; // force reflow so the removal above takes effect first
	target.classList.add(FLASH_CLASS);
}

function setDisplay(el: Element | null, visible: boolean): void {
	if (el instanceof HTMLElement) el.style.display = visible ? "" : "none";
}

function isHidden(el: Element | null): boolean {
	return el instanceof HTMLElement && el.style.display === "none";
}

function wireThread(root: HTMLElement): void {
	root.addEventListener("click", (evt) => {
		const target = evt.target;
		if (!(target instanceof HTMLElement)) return;

		const replyToggle = target.closest<HTMLElement>("[data-testid='comment-reply-toggle']");
		if (replyToggle) {
			const comment = replyToggle.closest<HTMLElement>("[data-testid='comment']");
			const form = comment?.querySelector("[data-testid='comment-reply-form']") ?? null;
			setDisplay(form, isHidden(form));
			return;
		}

		const replyCancel = target.closest<HTMLElement>("[data-testid='comment-reply-cancel']");
		if (replyCancel) {
			const form = replyCancel.closest<HTMLFormElement>("[data-testid='comment-reply-form']");
			if (form) {
				form.reset();
				setDisplay(form, false);
			}
			return;
		}

		const editToggle = target.closest<HTMLElement>("[data-testid='comment-edit-toggle']");
		if (editToggle) {
			const comment = editToggle.closest<HTMLElement>("[data-testid='comment']");
			const form = comment?.querySelector("[data-testid='comment-edit-form']") ?? null;
			// reader-view-body-and-comments round 2: the comment-read-body WRAPPER div was
			// removed from _CommentThread.cshtml (it added an ancestor level that kept
			// Readability's candidate climb from reaching the shared reader-view root) — the
			// md-body div (data-testid="comment-body") is now toggled directly; same element's
			// role, one fewer wrapper.
			const readBody = comment?.querySelector("[data-testid='comment-body']") ?? null;
			const opening = isHidden(form);
			setDisplay(form, opening);
			setDisplay(readBody, !opening);
			return;
		}

		const editCancel = target.closest<HTMLElement>("[data-testid='comment-edit-cancel']");
		if (editCancel) {
			const comment = editCancel.closest<HTMLElement>("[data-testid='comment']");
			const form = comment?.querySelector("[data-testid='comment-edit-form']") ?? null;
			const readBody = comment?.querySelector("[data-testid='comment-body']") ?? null; // see editToggle above
			setDisplay(form, false);
			setDisplay(readBody, true);
		}
	});
}
