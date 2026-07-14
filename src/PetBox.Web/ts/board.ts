import { readUiState, writeUiState } from "./ui-state";

// Task board view interactivity — collapse / filter / active-only / sort. Imperative (mirrors
// config.ts), so it does NOT fight Alpine over display. A row is shown unless:
//   - active-only is on and the row is closed (and not kept visible for an open descendant),
//   - any of its part_of ancestors is collapsed,
//   - the filter bar excludes it (status / type / free text).
//
// board-view-cross-device / board-filters-server-state: view mode, tag-`by`, field selection,
// active-only, sort and the collapsed-node set are ALL now resolved and rendered SERVER-SIDE
// (TaskBoardModel — BoardViewPreferences/TasksActiveOnly/TasksSortBy/TasksSortDesc/
// CollapsedByBoard on BrowserState) before this script ever runs. This file therefore no longer
// seeds ANY of that state from localStorage on load — it reads the INITIAL value off the DOM the
// server just rendered (checkbox `checked`, select `value`, the sort arrow's glyph, each row's
// `data-collapsed`/inline `display`), the same "never re-apply what the server already got right"
// rule ts/sidebar.ts's isPinned() established. It only reacts to a user-driven change: an instant
// client-side re-filter/re-sort/re-collapse (unchanged from before — the interaction itself never
// needed a round trip) PLUS a write so the NEXT load (any device, for the DB-backed prefs; this
// board, for the cookie-backed collapse set) already agrees.
//   - active-only / sort are GLOBAL, cross-device [Setting] preferences -> a fire-and-forget POST
//     to /api/ui/board-filter-prefs (persistFilterPrefs).
//   - the collapsed-node set is per-(project,board) WINDOW state -> the shared petbox.ui cookie,
//     through ui-state.ts's readUiState/writeUiState (persistCollapsed) — never a raw
//     localStorage/sessionStorage/document.cookie call (ui-state-single-mechanism-guard).
//   - view mode / tag-`by` / field selection are written SERVER-SIDE, by TaskBoardModel itself,
//     the moment a `?view=`/`?fields=` navigation lands — nothing here to do for those at all.
//
// board-node-filter / board-sort: this wiring is SHARED by every view mode (tree/kanban/
// outline/table), not one mechanism per mode. Two conventions make that possible:
//   - a "row" is ANY `[data-node-id]` element anywhere on the page — filtering (show/hide) and
//     the status/type <select> population both operate over EVERY row on the page regardless of
//     which container it lives in, since exactly one view mode's content pane is ever rendered
//     per page load.
//   - a "sort scope" is any `[data-sort-scope]` container — reordering (reorderDom) runs
//     independently PER SCOPE, walking each scope's own rows depth-first via data-parent-id
//     (rows with no parent, or whose parent isn't in the same scope, sort as siblings at that
//     scope's root). Tree/outline render ONE scope (their DFS-flat list — no actual DOM nesting,
//     data-parent-id alone encodes it), so sort reorders sibling branches; table renders one
//     scope with flat (parent-less) rows, so sort reorders the whole row list; kanban renders
//     ONE scope PER COLUMN, so sort reorders cards within each column independently.

interface Row {
	el: HTMLElement;
	id: string;
	parent: string | null;
}

// board-sort-impl: the client sort toggle. `by` picks the comparison key (priority is the
// server's own default order — Priority then Key — so "priority asc" reproduces the untouched
// render); `desc` flips it.
export type SortKey = "priority" | "created" | "updated" | "title";
export interface SortPref {
	by: SortKey;
	desc: boolean;
}
const SORT_KEYS: readonly SortKey[] = ["priority", "created", "updated", "title"];

// Sort comparator over one row's data-* attrs for a given key. Priority/created/updated read as
// numbers (a missing/unparsable created|updated timestamp sorts as 0 — oldest — rather than
// throwing a row to a random position); title compares case-insensitively, falling back to the
// node key so two blank titles still order deterministically. Exported so the pure comparison
// logic is unit-testable without a DOM (node:test has no browser).
export function sortKeyValue(d: DOMStringMap, by: SortKey): number | string {
	switch (by) {
		case "priority":
			return Number(d["priority"] ?? "0");
		case "created":
		case "updated": {
			const t = Date.parse(d[by] ?? "");
			return Number.isNaN(t) ? 0 : t;
		}
		case "title":
			return (d["title"] || d["nodeKey"] || "").toLowerCase();
	}
}

export function compareSortValues(a: number | string, b: number | string): number {
	if (typeof a === "string" || typeof b === "string") return String(a).localeCompare(String(b));
	return a - b;
}

// board-filters-server-state: fire-and-forget persistence for the two GLOBAL, cross-device
// [Setting] preferences (active-only, sort) — DisableAntiforgery() on the endpoint (matches
// WorkspaceSwitchEndpoint/ProjectSwitchEndpoint's own POST-without-a-form-token precedent), so no
// token needs threading through here. The client already applied the change instantly (apply()/
// reorderDom() below); this call only makes sure the NEXT load — this board or any other, this
// device or another — already agrees.
function persistFilterPrefs(patch: { activeOnly?: boolean; sortBy?: SortKey; sortDesc?: boolean }): void {
	const body = new URLSearchParams();
	if (patch.activeOnly !== undefined) body.set("activeOnly", String(patch.activeOnly));
	if (patch.sortBy !== undefined) body.set("sortBy", patch.sortBy);
	if (patch.sortDesc !== undefined) body.set("sortDesc", String(patch.sortDesc));
	void fetch("/api/ui/board-filter-prefs", {
		method: "POST",
		body,
		credentials: "same-origin",
		headers: { "Content-Type": "application/x-www-form-urlencoded" },
	});
}

// board-filters-server-state: the collapsed-node set is per-(project,board) WINDOW state, so it
// rides the shared petbox.ui cookie (BrowserState.CollapsedByBoard) rather than a POST — read the
// WHOLE map back out, replace just THIS board's entry, write the whole map back (MergeCookieValue
// only merges at the top-level cookie KEY, not inside a Dictionary value it carries — same
// read-modify-write shape TaskBoardModel uses server-side for BoardViewPreferences).
function persistCollapsed(boardKey: string, collapsed: ReadonlySet<string>): void {
	const current = readUiState().collapsedByBoard ?? {};
	writeUiState({ collapsedByBoard: { ...current, [boardKey]: Array.from(collapsed) } });
}

// board-view-fields: the properties dialog's open/close — same daisyUI `.modal-open` toggle
// pattern as initWorkflowViz's "View workflow" modal (ts/workflow-viz.ts).
export function initBoardFieldsDialog(): void {
	const modal = document.querySelector<HTMLElement>("[data-testid='fields-modal']");
	const toggle = document.querySelector<HTMLElement>("[data-testid='board-fields-toggle']");
	if (!modal || !toggle) return;
	const closeBtn = modal.querySelector<HTMLElement>("[data-testid='fields-modal-close']");
	const backdrop = modal.querySelector<HTMLElement>("[data-testid='fields-modal-backdrop']");

	const close = (): void => modal.classList.remove("modal-open");
	toggle.addEventListener("click", () => modal.classList.add("modal-open"));
	closeBtn?.addEventListener("click", close);
	backdrop?.addEventListener("click", close);
	document.addEventListener("keydown", (evt) => {
		if (evt.key === "Escape" && modal.classList.contains("modal-open")) close();
	});
}

// kanban-column-picker: the column (status) visibility dialog's open/close — same daisyUI
// `.modal-open` toggle pattern as initBoardFieldsDialog just above (and initWorkflowViz's "View
// workflow" modal), just against the columns-* testids instead of fields-*. Kanban-only: the
// modal/toggle simply aren't in the DOM on any other view mode, so this is a no-op there.
export function initBoardColumnsDialog(): void {
	const modal = document.querySelector<HTMLElement>("[data-testid='columns-modal']");
	const toggle = document.querySelector<HTMLElement>("[data-testid='columns-toggle']");
	if (!modal || !toggle) return;
	const closeBtn = modal.querySelector<HTMLElement>("[data-testid='columns-modal-close']");
	const backdrop = modal.querySelector<HTMLElement>("[data-testid='columns-modal-backdrop']");

	const close = (): void => modal.classList.remove("modal-open");
	toggle.addEventListener("click", () => modal.classList.add("modal-open"));
	closeBtn?.addEventListener("click", close);
	backdrop?.addEventListener("click", close);
	document.addEventListener("keydown", (evt) => {
		if (evt.key === "Escape" && modal.classList.contains("modal-open")) close();
	});
}

// One independent reorder scope (board-sort): a `[data-sort-scope]` container plus the
// roots/childrenOf sibling structure computed from ONLY the rows inside it. Kanban renders one
// of these per column; tree/outline/table render exactly one for the whole pane.
interface SortScope {
	el: HTMLElement;
	roots: Row[];
	childrenOf: Map<string, Row[]>;
}

function buildScope(el: HTMLElement, rows: readonly Row[]): SortScope {
	const scopeRows = rows.filter((r) => el.contains(r.el));
	// Sibling groups for the sort toggle — a root is any row whose parent isn't ALSO a row in
	// THIS scope (no parent, or a parent outside the scope / filtered out). Sorting only ever
	// reorders within a sibling group, then re-walks depth-first, so the part_of nesting/
	// indentation stays intact (board-sort-impl keeps finding D11's server-side invariant on the
	// client). Rows with no data-parent-id (table rows, kanban cards) are all roots, so a scope
	// with no nesting just sorts flat — table's "sort by column", kanban's "sort within column".
	const idSet = new Set(scopeRows.map((r) => r.id));
	const childrenOf = new Map<string, Row[]>();
	const roots: Row[] = [];
	for (const r of scopeRows) {
		if (r.parent && idSet.has(r.parent)) {
			const kids = childrenOf.get(r.parent) ?? [];
			kids.push(r);
			childrenOf.set(r.parent, kids);
		} else {
			roots.push(r);
		}
	}
	return { el, roots, childrenOf };
}

export function initBoardPage(): void {
	// board-node-filter / board-sort: gate on the filter bar rather than a specific mode's own
	// container — it's the ONE thing every non-empty view mode renders (tree/kanban/outline/
	// table alike), so this single check covers all of them instead of one per mode.
	if (!document.querySelector("[data-testid='board-filter']")) return;

	// Rows: every `[data-node-id]` element anywhere on the page. Exactly one view mode's content
	// pane is ever rendered per page load, so this needs no scoping to a single container.
	const rows: Row[] = Array.from(document.querySelectorAll<HTMLElement>("[data-node-id]")).map((el) => ({
		el,
		id: el.dataset["nodeId"] ?? "",
		parent: el.dataset["parentId"] || null,
	}));
	const parentOf = new Map<string, string>();
	for (const r of rows) if (r.parent) parentOf.set(r.id, r.parent);

	const scopes: SortScope[] = Array.from(document.querySelectorAll<HTMLElement>("[data-sort-scope]")).map((el) =>
		buildScope(el, rows),
	);

	const elText = document.querySelector<HTMLInputElement>("[data-testid='board-filter-text']");
	const elStatus = document.querySelector<HTMLSelectElement>("[data-testid='board-filter-status']");
	const elType = document.querySelector<HTMLSelectElement>("[data-testid='board-filter-type']");
	const elActive = document.querySelector<HTMLInputElement>("[data-testid='active-only-toggle']");
	const elSortBy = document.querySelector<HTMLSelectElement>("[data-testid='board-sort-by']");
	const elSortDir = document.querySelector<HTMLButtonElement>("[data-testid='board-sort-dir']");
	const boardKey = document.querySelector<HTMLElement>("[data-testid='board-view-meta']")?.dataset["boardKey"];

	// Initial state comes from what the SERVER already rendered — checkbox `checked`, select
	// `value`, the arrow glyph — never storage, so a toggle always starts from what's actually on
	// screen (mirrors ts/sidebar.ts's isPinned() reading the DOM instead of a cookie/localStorage).
	let activeOnly = elActive?.checked ?? true;
	const initialSortBy = (elSortBy?.value ?? "") as SortKey;
	let sortPref: SortPref = {
		by: SORT_KEYS.includes(initialSortBy) ? initialSortBy : "priority",
		desc: elSortDir?.textContent === "↓",
	};
	// Collapsed set seeded from the server-rendered data-collapsed marker on each collapse toggle
	// (tree only — kanban/table never render one) — needed so a FUTURE click computes the right
	// next state and the right cookie write; the rows' CURRENT hidden/caret state is already
	// correct in the markup and is deliberately not recomputed here.
	const collapsed = new Set<string>(
		Array.from(document.querySelectorAll<HTMLElement>("[data-collapse-toggle][data-collapsed='true']"))
			.map((el) => el.closest<HTMLElement>("[data-node-id]")?.dataset["nodeId"])
			.filter((id): id is string => !!id),
	);

	// Fill the status/type selects from the rows actually on the board ("" = any).
	function fillSelect(sel: HTMLSelectElement | null, attr: string): void {
		if (!sel) return;
		const vals = new Set<string>();
		for (const r of rows) {
			const v = r.el.dataset[attr];
			if (v) vals.add(v);
		}
		for (const v of Array.from(vals).sort()) {
			const opt = document.createElement("option");
			opt.value = v;
			opt.textContent = v;
			sel.appendChild(opt);
		}
	}
	fillSelect(elStatus, "status");
	fillSelect(elType, "type");

	function hiddenByCollapse(id: string): boolean {
		let cur = parentOf.get(id);
		let guard = 0;
		while (cur && guard++ < 1000) {
			if (collapsed.has(cur)) return true;
			cur = parentOf.get(cur);
		}
		return false;
	}

	// Depth-first re-append in sort order, independently PER SCOPE: each sibling group is sorted
	// by the current SortPref, then its subtree is walked before moving to the next sibling —
	// exactly the server's own DFS shape, just with a chosen comparison key instead of the fixed
	// priority-then-key one. `appendChild` on an already-attached node MOVES it, so one pass over
	// each scope reorders every row in it without detach/reattach churn. A kanban board has one
	// scope per column, so a card only ever moves within its own column, never across.
	function reorderDom(): void {
		const cmp = (a: Row, b: Row): number => {
			const v = compareSortValues(sortKeyValue(a.el.dataset, sortPref.by), sortKeyValue(b.el.dataset, sortPref.by));
			return sortPref.desc ? -v : v;
		};
		for (const scope of scopes) {
			function walk(list: Row[]): void {
				for (const r of [...list].sort(cmp)) {
					scope.el.appendChild(r.el);
					const kids = scope.childrenOf.get(r.id);
					if (kids) walk(kids);
				}
			}
			walk(scope.roots);
		}
	}

	function apply(): void {
		const q = (elText?.value ?? "").trim().toLowerCase();
		const fs = elStatus?.value ?? "";
		const ft = elType?.value ?? "";
		for (const { el, id } of rows) {
			const d = el.dataset;
			let show = true;
			if (activeOnly && d["closed"] === "true" && d["keepVisible"] !== "true") show = false;
			else if (hiddenByCollapse(id)) show = false;
			else if (fs && d["status"] !== fs) show = false;
			else if (ft && d["type"] !== ft) show = false;
			else if (q && !(d["search"] ?? "").includes(q)) show = false;
			// Inline display, NOT the `hidden` attribute: the <li> carries daisyUI's `.card`
			// (display:flex), an author rule that beats the UA `[hidden]{display:none}` — so
			// el.hidden wouldn't actually hide anything. Inline style wins the cascade.
			el.style.display = show ? "" : "none";

			const caret = el.querySelector<HTMLElement>("[data-collapse-toggle]");
			if (caret) caret.textContent = collapsed.has(id) ? "▸" : "▾";
		}
	}

	// No apply()/reorderDom() call here on init, on purpose: the server already rendered the
	// correct hidden/sort-order state for THIS request (TaskBoardModel.ActiveOnly/SortBy/SortDesc/
	// CollapsedNodeIds), so recomputing now would be exactly the post-load correction that used to
	// cause the reflow (board-filters-server-state / the same fix ts/sidebar.ts already applied
	// for the pin toggle). apply()/reorderDom() only run below, reactively, in response to an
	// actual user-driven change.

	// Collapse toggle (tree/outline only — kanban/table cards carry no [data-collapse-toggle]):
	// delegated on the document since rows no longer live under one single container.
	document.addEventListener("click", (evt) => {
		const toggle = (evt.target as HTMLElement).closest<HTMLElement>("[data-collapse-toggle]");
		if (!toggle) return;
		evt.preventDefault();
		const id = toggle.closest<HTMLElement>("[data-node-id]")?.dataset["nodeId"];
		if (!id) return;
		if (collapsed.has(id)) collapsed.delete(id);
		else collapsed.add(id);
		apply();
		if (boardKey) persistCollapsed(boardKey, collapsed);
	});

	elText?.addEventListener("input", apply);
	elStatus?.addEventListener("change", apply);
	elType?.addEventListener("change", apply);
	elActive?.addEventListener("change", () => {
		activeOnly = elActive.checked;
		apply();
		persistFilterPrefs({ activeOnly });
	});
	elSortBy?.addEventListener("change", () => {
		sortPref = { ...sortPref, by: (elSortBy.value as SortKey) || "priority" };
		reorderDom();
		persistFilterPrefs({ sortBy: sortPref.by });
	});
	elSortDir?.addEventListener("click", () => {
		sortPref = { ...sortPref, desc: !sortPref.desc };
		elSortDir.textContent = sortPref.desc ? "↓" : "↑";
		reorderDom();
		persistFilterPrefs({ sortDesc: sortPref.desc });
	});
}
