// ---------- Local-time rendering ----------
function formatLocalTime(iso: string): string {
	const d = new Date(iso);
	if (Number.isNaN(d.getTime())) return iso;
	const pad = (n: number, w = 2) => String(n).padStart(w, "0");
	return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

function renderLocalTimes(root: ParentNode): void {
	for (const el of root.querySelectorAll<HTMLElement>("time[datetime]")) {
		const iso = el.getAttribute("datetime");
		if (iso) el.textContent = formatLocalTime(iso);
	}
}

renderLocalTimes(document);
document.addEventListener("htmx:afterSwap", (event) => {
	const detail = (event as CustomEvent).detail as { target?: Element } | undefined;
	renderLocalTimes(detail?.target ?? document);
});

// ---------- Expandable event row ----------
document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	if (!target) return;
	if (target.closest("button, a, input, textarea, select, summary")) return;

	const row = target.closest("tr[data-event-id]") as HTMLTableRowElement | null;
	if (!row) return;

	const details = row.nextElementSibling as HTMLElement | null;
	if (details?.classList.contains("event-details")) {
		details.classList.toggle("hidden");
	}
});

// ---------- Filter chips ----------
document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const btn = target?.closest("[data-filter-field]") as HTMLButtonElement | null;
	if (!btn) return;
	event.stopPropagation();
	event.preventDefault();

	const field = btn.dataset.filterField ?? "";
	const op = btn.dataset.filterOp ?? "eq";
	const value = btn.dataset.filterValue ?? "";
	if (!field || !value) return;

	const sym = op === "eq" ? "==" : op === "ne" ? "!=" : "==";

	const textarea = document.getElementById("kql-textarea") as HTMLTextAreaElement | null;
	if (!textarea) return;

	const clause = `| where ${field} ${sym} ${value}`;
	const base = textarea.value.trim().length > 0 ? textarea.value.trimEnd() : "events";
	if (base.includes(clause)) return;
	textarea.value = `${base}\n${clause}`;
	textarea.form?.requestSubmit();
});
