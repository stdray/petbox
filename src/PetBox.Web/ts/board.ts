// Task board view interactivity — collapse / filter / active-only / sort. Imperative (mirrors
// config.ts), so it does NOT fight Alpine over display. A row is shown unless:
//   - active-only is on and the row is closed (and not kept visible for an open descendant),
//   - any of its part_of ancestors is collapsed,
//   - the filter bar excludes it (status / type / free text).
// Persisted bits (active-only, collapsed set) live in localStorage, keyed board-independently.
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

const LS_ACTIVE = "tasksActiveOnly";
const LS_COLLAPSED = "tasksCollapsed";
const LS_SORT = "tasksSort";
const LS_VIEW_PREFIX = "tasksView:";
// board-view-fields: the SAME localStorage-by-board-key mechanism as LS_VIEW_PREFIX above (spec's
// explicit ask — no second persistence mechanism), just its own key prefix + its own csv shape
// (BoardFieldConfig.ToCsv()/FromKeys, not JSON — the value is already just a flat key list).
const LS_FIELDS_PREFIX = "tasksFields:";

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

// board-view-persistence: the view mode a board opens in is remembered per BOARD (not
// globally, unlike active-only/collapsed/sort above) — different boards have different
// methodology defaultViews (kanban/outline/table/tree), so one global choice would fight
// the per-kind default. Stored under `tasksView:<projectKey>/<board>` (the same key
// TaskBoard.cshtml embeds in [data-testid='board-view-meta'] data-board-key).
export interface BoardViewPref {
	mode: string;
	by?: string;
}

export function parseViewPref(raw: string | null): BoardViewPref | null {
	if (!raw) return null;
	try {
		const p = JSON.parse(raw) as Partial<BoardViewPref>;
		if (p && typeof p.mode === "string" && p.mode.length > 0) {
			// exactOptionalPropertyTypes: only include `by` when it's a real value — an
			// explicit `by: undefined` is a type error under that flag.
			return typeof p.by === "string" && p.by.length > 0 ? { mode: p.mode, by: p.by } : { mode: p.mode };
		}
	} catch {
		// malformed localStorage value — treat as absent
	}
	return null;
}

// board-view-persistence: whether a saved localStorage pref is STALE relative to what the
// server actually resolved/rendered this load (data-resolved-view / data-resolved-by) — the
// pure decision behind initBoardViewPersistence's reconcile-on-load redirect, factored out so
// the `by` comparison is testable without a DOM (mirrors parseViewPref/compareSortValues
// above). Comparing `mode` alone let a saved {mode:"tags", by:"area"} silently lose its `by`
// whenever the server's own by-less resolution already landed on "tags" (e.g. a methodology
// defaultView of "tags"): the mode matched, so no redirect fired and the page rendered the
// by-less tags degradation instead of the saved grouping. Absent `by` on either side reads as
// "" so a mode-only pref against a mode-only resolution still compares equal.
export function viewPrefNeedsReconcile(
	saved: BoardViewPref | null,
	resolvedMode: string | undefined,
	resolvedBy: string | undefined,
): boolean {
	if (!saved) return false;
	return saved.mode !== resolvedMode || (saved.by ?? "") !== (resolvedBy ?? "");
}

// board-view-fields: a saved csv and a freshly-resolved one are the SAME preference regardless of
// key order or incidental duplicates — the dialog emits BoardFieldNames.Options order, the server
// emits BoardFieldConfig.Keys() order (same order, in practice), but comparing as sets rather than
// strings keeps this robust to either changing independently.
function normalizeFieldsCsv(csv: string): string {
	return Array.from(
		new Set(
			csv
				.split(",")
				.map((s) => s.trim())
				.filter(Boolean),
		),
	)
		.sort()
		.join(",");
}

// board-view-fields: whether a saved fields csv differs from what the server actually resolved/
// rendered this load (`data-resolved-fields`) — the fields counterpart of viewPrefNeedsReconcile
// just above. `saved === null` means "no preference saved" (never reconcile, use the server
// default); an empty STRING is a real, deliberately-empty saved selection and DOES reconcile
// against a non-empty default.
export function fieldsPrefNeedsReconcile(saved: string | null, resolvedFields: string | undefined): boolean {
	if (saved === null) return false;
	return normalizeFieldsCsv(saved) !== normalizeFieldsCsv(resolvedFields ?? "");
}

// Runs on EVERY board page load (every view mode alike — unlike initBoardPage below, which
// wires the filter/sort/collapse interactivity and bails when no view mode's filter bar
// rendered, e.g. an empty board). Three jobs, all keyed off the SAME per-board localStorage
// namespace (board-view-fields: "тот же механизм, что и режим просмотра" — one storage
// mechanism, two key prefixes):
//   1. Reconcile view: if the URL has no explicit `?view=`, and a saved pref differs from what
//      the server actually resolved/rendered (`data-resolved-view`), redirect to the saved mode.
//   2. Reconcile fields: same idea for `?fields=`/`fieldsSet` against `data-resolved-fields` — a
//      saved field selection applies REGARDLESS of which view mode is active ("выбор действует в
//      любом режиме просмотра"), so this fires independently of the view reconcile above and both
//      land in ONE redirect (never two competing ones — see the shared `qs`/`redirect` below).
//      This runs at normal deferred module-script timing (after the body is parsed, same as
//      every other persisted board pref in this file) rather than an early <head> script, so a
//      returning user with a non-default saved mode/fields may see one flash of the server-
//      resolved view before the redirect — the same tradeoff already accepted for the
//      active-only/collapsed/sort prefs above, kept deliberately rather than adding a second
//      inline-<script> exception to the "no inline JS in .cshtml" rule (the one that exists,
//      _ThemeScript, is reserved for the light/dark FOUC case).
//   3. Save: clicking a view-switch link (`[data-view-link]`) persists its mode (+ optional
//      `by`) before the browser navigates — a plain `<a href>` click, not intercepted. Submitting
//      the fields dialog's form persists the checked set the same way, before its own (also
//      un-intercepted) GET submit.
export function initBoardViewPersistence(): void {
	const meta = document.querySelector<HTMLElement>("[data-testid='board-view-meta']");
	if (!meta) return;
	const boardKey = meta.dataset["boardKey"];
	if (!boardKey) return;
	const viewStorageKey = LS_VIEW_PREFIX + boardKey;
	const fieldsStorageKey = LS_FIELDS_PREFIX + boardKey;

	const params = new URLSearchParams(window.location.search);
	const qs = new URLSearchParams(params);
	let redirect = false;

	if (!params.has("view")) {
		const saved = parseViewPref(localStorage.getItem(viewStorageKey));
		if (saved && viewPrefNeedsReconcile(saved, meta.dataset["resolvedView"], meta.dataset["resolvedBy"])) {
			qs.set("view", saved.mode);
			if (saved.by) qs.set("by", saved.by);
			else qs.delete("by");
			redirect = true;
		}
	}
	if (!params.has("fieldsSet")) {
		const savedFields = localStorage.getItem(fieldsStorageKey);
		if (savedFields !== null && fieldsPrefNeedsReconcile(savedFields, meta.dataset["resolvedFields"])) {
			qs.delete("fields");
			for (const key of normalizeFieldsCsv(savedFields).split(",").filter(Boolean)) qs.append("fields", key);
			qs.set("fieldsSet", "1");
			redirect = true;
		}
	}
	if (redirect) {
		window.location.replace(`${window.location.pathname}?${qs.toString()}`);
		return; // navigating away — no point wiring click/submit handlers on this (stale) page
	}

	for (const link of Array.from(document.querySelectorAll<HTMLAnchorElement>("[data-view-link]"))) {
		link.addEventListener("click", () => {
			const mode = link.dataset["viewMode"];
			if (!mode) return;
			const by = link.dataset["viewBy"];
			try {
				localStorage.setItem(viewStorageKey, JSON.stringify(by ? { mode, by } : { mode }));
			} catch {
				// storage unavailable (private mode / quota) — the click still navigates normally
			}
		});
	}

	const fieldsForm = document.querySelector<HTMLFormElement>("[data-testid='fields-form']");
	fieldsForm?.addEventListener("submit", () => {
		const checked = Array.from(fieldsForm.querySelectorAll<HTMLInputElement>("input[name='fields']:checked")).map(
			(el) => el.value,
		);
		try {
			localStorage.setItem(fieldsStorageKey, checked.join(","));
		} catch {
			// storage unavailable (private mode / quota) — the submit still navigates normally
		}
	});
}

// board-view-fields: the properties dialog's open/close — same daisyUI `.modal-open` toggle
// pattern as initWorkflowViz's "View workflow" modal (ts/workflow-viz.ts), independent of
// initBoardViewPersistence above so it still wires up on a page with the dialog but (hypothetically)
// no board-view-meta.
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
