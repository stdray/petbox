// Task board view interactivity — collapse / filter / active-only. Imperative (mirrors
// config.ts), so it does NOT fight Alpine over display: the board renders a FLAT DFS list of
// <li data-node-id …> and we toggle each row's `hidden`. A row is shown unless:
//   - active-only is on and the row is closed (and not kept visible for an open descendant),
//   - any of its part_of ancestors is collapsed,
//   - the filter bar excludes it (status / type / free text).
// Persisted bits (active-only, collapsed set) live in localStorage, keyed board-independently.

const LS_ACTIVE = "tasksActiveOnly";
const LS_COLLAPSED = "tasksCollapsed";

interface Row {
	el: HTMLElement;
	id: string;
	parent: string | null;
}

export function initBoardPage(): void {
	const board = document.querySelector<HTMLElement>("[data-testid='board-nodes']");
	if (!board) return;

	const rows: Row[] = Array.from(board.querySelectorAll<HTMLElement>("[data-node-id]")).map((el) => ({
		el,
		id: el.dataset["nodeId"] ?? "",
		parent: el.dataset["parentId"] || null,
	}));
	const parentOf = new Map<string, string>();
	for (const r of rows) if (r.parent) parentOf.set(r.id, r.parent);

	let activeOnly = JSON.parse(localStorage.getItem(LS_ACTIVE) ?? "true") as boolean;
	const collapsed = new Set<string>(JSON.parse(localStorage.getItem(LS_COLLAPSED) ?? "[]") as string[]);

	const elText = document.querySelector<HTMLInputElement>("[data-testid='board-filter-text']");
	const elStatus = document.querySelector<HTMLSelectElement>("[data-testid='board-filter-status']");
	const elType = document.querySelector<HTMLSelectElement>("[data-testid='board-filter-type']");
	const elActive = document.querySelector<HTMLInputElement>("[data-testid='active-only-toggle']");
	if (elActive) elActive.checked = activeOnly;

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

	apply();
}
