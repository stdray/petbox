// Sidebar pin toggle. "Pinned" keeps the sidebar open inline on wider viewports
// (the `md:drawer-open` class); "floating" turns it into a collapsible drawer
// reachable via the hamburger. Choice persists in localStorage. On narrow
// viewports the sidebar is always a collapsible drawer regardless.
const KEY = "petbox.sidebar.pinned";
const PINNED_CLASS = "md:drawer-open";

function isPinned(): boolean {
	const v = localStorage.getItem(KEY);
	return v === null ? true : v === "1";
}

function apply(pinned: boolean): void {
	document.getElementById("app-drawer")?.classList.toggle(PINNED_CLASS, pinned);
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
