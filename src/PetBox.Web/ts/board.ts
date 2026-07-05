// Task board view interactivity — collapse / filter / active-only. Imperative (mirrors
// config.ts), so it does NOT fight Alpine over display: the board renders a FLAT DFS list of
// <li data-node-id …> and we toggle each row's `hidden`. A row is shown unless:
//   - active-only is on and the row is closed (and not kept visible for an open descendant),
//   - any of its part_of ancestors is collapsed,
//   - the filter bar excludes it (status / type / free text).
// Persisted bits (active-only, collapsed set) live in localStorage, keyed board-independently.

const LS_ACTIVE = "tasksActiveOnly";
const LS_COLLAPSED = "tasksCollapsed";
const LS_SORT = "tasksSort";

interface Row {
	el: HTMLElement;
	id: string;
	parent: string | null;
}

// board-sort-impl: the client sort toggle. `by` picks the comparison key (priority is the
// server's own default order — Priority then Key — so "priority asc" reproduces the untouched
// render); `desc` flips it. Persisted verbatim to localStorage so a reload keeps the choice.
export type SortKey = "priority" | "created" | "updated" | "title";
export interface SortPref {
	by: SortKey;
	desc: boolean;
}
const SORT_KEYS: readonly SortKey[] = ["priority", "created", "updated", "title"];

export function parseSortPref(raw: string | null): SortPref {
	try {
		const p = raw ? (JSON.parse(raw) as Partial<SortPref>) : null;
		if (p && typeof p.by === "string" && (SORT_KEYS as readonly string[]).includes(p.by)) {
			return { by: p.by as SortKey, desc: p.desc === true };
		}
	} catch {
		// malformed localStorage value — fall through to the default
	}
	return { by: "priority", desc: false };
}

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

export function initBoardPage(): void {
	const boardEl = document.querySelector<HTMLElement>("[data-testid='board-nodes']");
	if (!boardEl) return;
	const board: HTMLElement = boardEl; // narrowed once, non-null everywhere below (incl. nested closures)

	const rows: Row[] = Array.from(board.querySelectorAll<HTMLElement>("[data-node-id]")).map((el) => ({
		el,
		id: el.dataset["nodeId"] ?? "",
		parent: el.dataset["parentId"] || null,
	}));
	const parentOf = new Map<string, string>();
	for (const r of rows) if (r.parent) parentOf.set(r.id, r.parent);

	// Sibling groups for the sort toggle — a root is any row whose parent isn't ALSO a row on
	// this board (no parent, or a parent filtered out of this read). Sorting only ever reorders
	// within a sibling group, then re-walks depth-first, so the part_of nesting/indentation
	// stays intact (board-sort-impl keeps finding D11's server-side invariant on the client).
	const idSet = new Set(rows.map((r) => r.id));
	const childrenOf = new Map<string, Row[]>();
	const roots: Row[] = [];
	for (const r of rows) {
		if (r.parent && idSet.has(r.parent)) {
			const kids = childrenOf.get(r.parent) ?? [];
			kids.push(r);
			childrenOf.set(r.parent, kids);
		} else {
			roots.push(r);
		}
	}

	let activeOnly = JSON.parse(localStorage.getItem(LS_ACTIVE) ?? "true") as boolean;
	const collapsed = new Set<string>(JSON.parse(localStorage.getItem(LS_COLLAPSED) ?? "[]") as string[]);
	let sortPref = parseSortPref(localStorage.getItem(LS_SORT));

	const elText = document.querySelector<HTMLInputElement>("[data-testid='board-filter-text']");
	const elStatus = document.querySelector<HTMLSelectElement>("[data-testid='board-filter-status']");
	const elType = document.querySelector<HTMLSelectElement>("[data-testid='board-filter-type']");
	const elActive = document.querySelector<HTMLInputElement>("[data-testid='active-only-toggle']");
	const elSortBy = document.querySelector<HTMLSelectElement>("[data-testid='board-sort-by']");
	const elSortDir = document.querySelector<HTMLButtonElement>("[data-testid='board-sort-dir']");
	if (elActive) elActive.checked = activeOnly;
	if (elSortBy) elSortBy.value = sortPref.by;
	if (elSortDir) elSortDir.textContent = sortPref.desc ? "↓" : "↑";

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

	// Depth-first re-append in sort order: each sibling group is sorted by the current
	// SortPref, then its subtree is walked before moving to the next sibling — exactly the
	// server's own DFS shape, just with a chosen comparison key instead of the fixed
	// priority-then-key one. `appendChild` on an already-attached node MOVES it, so one pass
	// over the whole tree reorders every row without detach/reattach churn.
	function reorderDom(): void {
		const cmp = (a: Row, b: Row): number => {
			const v = compareSortValues(sortKeyValue(a.el.dataset, sortPref.by), sortKeyValue(b.el.dataset, sortPref.by));
			return sortPref.desc ? -v : v;
		};
		function walk(list: Row[]): void {
			for (const r of [...list].sort(cmp)) {
				board.appendChild(r.el);
				const kids = childrenOf.get(r.id);
				if (kids) walk(kids);
			}
		}
		walk(roots);
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

	board.addEventListener("click", (evt) => {
		const toggle = (evt.target as HTMLElement).closest<HTMLElement>("[data-collapse-toggle]");
		if (!toggle) return;
		evt.preventDefault();
		const id = toggle.closest<HTMLElement>("[data-node-id]")?.dataset["nodeId"];
		if (!id) return;
		if (collapsed.has(id)) collapsed.delete(id);
		else collapsed.add(id);
		localStorage.setItem(LS_COLLAPSED, JSON.stringify(Array.from(collapsed)));
		apply();
	});

	elText?.addEventListener("input", apply);
	elStatus?.addEventListener("change", apply);
	elType?.addEventListener("change", apply);
	elActive?.addEventListener("change", () => {
		activeOnly = elActive.checked;
		localStorage.setItem(LS_ACTIVE, JSON.stringify(activeOnly));
		apply();
	});
	elSortBy?.addEventListener("change", () => {
		sortPref = { ...sortPref, by: (elSortBy.value as SortKey) || "priority" };
		localStorage.setItem(LS_SORT, JSON.stringify(sortPref));
		reorderDom();
	});
	elSortDir?.addEventListener("click", () => {
		sortPref = { ...sortPref, desc: !sortPref.desc };
		elSortDir.textContent = sortPref.desc ? "↓" : "↑";
		localStorage.setItem(LS_SORT, JSON.stringify(sortPref));
		reorderDom();
	});

	reorderDom();
	apply();
}
