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

function init(): void {
	apply(isPinned());
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
