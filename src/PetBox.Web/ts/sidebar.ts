import { writeUiState } from "./ui-state";

// Sidebar pin toggle. "Pinned" keeps the sidebar open inline on wider viewports
// (the `md:drawer-open` class); "floating" turns it into a collapsible drawer
// reachable via the hamburger. On narrow viewports the sidebar is always a
// collapsible drawer regardless.
//
// State lives in the shared `petbox.ui` cookie (BrowserState.SidebarPinned, ui-state.ts) —
// window/device state, not a cross-device preference — so the SERVER renders the correct
// drawer-open class, aria-pressed and btn-active on the very first response (_Layout.cshtml /
// _AdminLayout.cshtml / _AccountLayout.cshtml / _SidebarPin.cshtml). This module therefore never
// applies the pinned state on load (that was the FOUC bug: server always printed drawer-open,
// this file stripped it after paint) — it only reacts to a click and writes the new value
// through the cookie helper so the NEXT server response already agrees.
const PINNED_CLASS = "drawer-open";

// Current state is read from the DOM the server just rendered — never from storage — so a
// toggle always flips whatever is actually showing, with no separate "what does storage say"
// source of truth to drift out of sync with it.
function isPinned(): boolean {
	return document.querySelector(".drawer")?.classList.contains(PINNED_CLASS) ?? true;
}

function apply(pinned: boolean): void {
	// Pinned: docked open inline at any width (the drawer-open class).
	// Unpinned: a floating drawer — collapse it (uncheck the toggle) so it hides;
	// the always-visible navbar hamburger reopens it as an overlay.
	// Selected by class, not id, so this works uniformly across every zone's
	// layout (/ui, /ui/admin, /ui/me) — each page renders exactly one drawer.
	document.querySelector(".drawer")?.classList.toggle(PINNED_CLASS, pinned);
	const toggle = document.querySelector<HTMLInputElement>(".drawer-toggle");
	if (toggle && !pinned) toggle.checked = false;
	document.querySelectorAll<HTMLElement>("[data-sidebar-pin]").forEach((btn) => {
		btn.setAttribute("aria-pressed", String(pinned));
		btn.classList.toggle("btn-active", pinned);
	});
}

// --- Tree expand memory: DELETED, not migrated (work sidebar-tree-cookie-dead) -----------------
// A previous version of this file wrote a SECOND cookie, `petbox.sidebar.tree`, with a JSON array
// of expanded node keys, and promised (in this comment) that the server would read it back to
// render `<details open>` with no reflow. Nothing ever read it: the server had no consumer, and
// the sidebar markup carried no `data-tree-key` for it to key off of — a half-built mechanism.
//
// Deleted rather than finished, because finishing it properly is a much bigger feature than "wire
// up a cookie read": the two disclosure trees that actually exist in the sidebar today (the Logs
// and Databases nodes, _Layout.cshtml) are htmx-lazy (`hx-trigger="toggle once"`) — pre-opening
// them from a cookie would render an empty "loading…" placeholder that never fetches (the toggle
// event that triggers the htmx GET only fires on a user-driven state change, not on the initial
// `open` attribute the server would have to print), which is a WORSE first paint than today's
// collapsed-by-default, not a fix. The third `<details>` in the sidebar (the Tasks node) already
// computes its open state from the active route (`IsActive(pTasks)`), so it needs no persistence
// at all. Reopening this — if a real use case shows up — belongs in `BrowserState`/`petbox.ui`
// (a `[BrowserState]` array property), never a second cookie.

function init(): void {
	// No apply(isPinned()) here on purpose: the server already rendered the correct class/aria
	// state for this request, and re-applying it after load is exactly the post-load correction
	// that caused the sidebar to visibly flicker on every board switch.
	document.addEventListener("click", (e) => {
		const btn = (e.target as HTMLElement | null)?.closest("[data-sidebar-pin]");
		if (!btn) return;
		const next = !isPinned();
		writeUiState({ sidebarPinned: next });
		apply(next);
	});
}

if (document.readyState === "loading") {
	document.addEventListener("DOMContentLoaded", init);
} else {
	init();
}
