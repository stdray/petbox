// Sidebar pin toggle. "Pinned" keeps the sidebar open inline on wider viewports
// (the `md:drawer-open` class); "floating" turns it into a collapsible drawer
// reachable via the hamburger. Choice persists in localStorage. On narrow
// viewports the sidebar is always a collapsible drawer regardless.
const KEY = "petbox.sidebar.pinned";
// Pinned keeps the sidebar docked open at ALL widths (not just >= md), so
// shrinking the window doesn't hide it. Unpinned = collapsible overlay.
const PINNED_CLASS = "drawer-open";

function isPinned(): boolean {
	const v = localStorage.getItem(KEY);
	return v === null ? true : v === "1";
}

function apply(pinned: boolean): void {
	// Pinned: docked open inline at any width (the drawer-open class).
	// Unpinned: a floating drawer — collapse it (uncheck the toggle) so it hides;
	// the always-visible navbar hamburger reopens it as an overlay.
	document.getElementById("app-drawer")?.classList.toggle(PINNED_CLASS, pinned);
	const toggle = document.getElementById("sidebar-toggle") as HTMLInputElement | null;
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
	apply(isPinned());
	initTreeMemory();
	document.addEventListener("click", (e) => {
		const btn = (e.target as HTMLElement | null)?.closest("[data-sidebar-pin]");
		if (!btn) return;
		const next = !isPinned();
		localStorage.setItem(KEY, next ? "1" : "0");
		apply(next);
	});
}

if (document.readyState === "loading") {
	document.addEventListener("DOMContentLoaded", init);
} else {
	init();
}
