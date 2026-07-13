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

// --- Tree expand memory -----------------------------------------------------
// Persist which project nodes are expanded. State lives in a COOKIE (not
// localStorage) so the server can read it and render `<details open>` up front —
// no post-load reflow / flicker (the JS no longer opens nodes after paint).
// Only nodes carrying a `data-tree-key` are tracked — the statically-rendered
// project <details>, NOT the htmx lazy log/db/table nodes; persisting those
// would fire a storm of lazy GETs on every page load.
const TREE_COOKIE = "petbox.sidebar.tree";

function readOpenSet(): Set<string> {
	const m = document.cookie.match(/(?:^|;\s*)petbox\.sidebar\.tree=([^;]*)/);
	if (!m) return new Set();
	try {
		return new Set(JSON.parse(decodeURIComponent(m[1] ?? "")) as string[]);
	} catch {
		return new Set();
	}
}

function persistOpenSet(set: Set<string>): void {
	const v = encodeURIComponent(JSON.stringify([...set]));
	document.cookie = `${TREE_COOKIE}=${v};path=/;max-age=31536000;samesite=lax`;
}

function initTreeMemory(): void {
	// Open state is rendered server-side from the cookie (no FOUC); here we only
	// keep the cookie in sync as the user expands/collapses project nodes.
	document.querySelectorAll<HTMLDetailsElement>("details[data-tree-key]").forEach((d) => {
		const key = d.getAttribute("data-tree-key");
		if (!key) return;
		d.addEventListener("toggle", () => {
			const set = readOpenSet();
			if (d.open) set.add(key);
			else set.delete(key);
			persistOpenSet(set);
		});
	});
}

function init(): void {
	// No apply(isPinned()) here on purpose: the server already rendered the correct class/aria
	// state for this request, and re-applying it after load is exactly the post-load correction
	// that caused the sidebar to visibly flicker on every board switch.
	initTreeMemory();
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
