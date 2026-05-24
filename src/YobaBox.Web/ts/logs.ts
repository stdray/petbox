// ---------- Local-time rendering ----------
function formatLocalTime(iso: string): string {
	const d = new Date(iso);
	if (Number.isNaN(d.getTime())) return iso;
	const pad = (n: number, w = 2) => String(n).padStart(w, "0");
	return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

function renderLocalTimes(root: ParentNode): void {
	for (const el of root.querySelectorAll<HTMLElement>(
		"time.local-time[datetime]",
	)) {
		const iso = el.getAttribute("datetime");
		if (iso) el.textContent = formatLocalTime(iso);
	}
}

renderLocalTimes(document);
document.addEventListener("htmx:afterSwap", (event) => {
	const detail = (event as CustomEvent).detail as
		| { target?: Element }
		| undefined;
	renderLocalTimes(detail?.target ?? document);
});

// ---------- Button press flash ----------
function flashButton(el: HTMLElement): void {
	el.classList.remove("btn-flash");
	void el.offsetWidth;
	el.classList.add("btn-flash");
	setTimeout(() => el.classList.remove("btn-flash"), 500);
}

document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const btn = target?.closest<HTMLElement>(".btn");
	if (btn) flashButton(btn);
});

// ---------- Global focus shortcut: "/" jumps to KQL textarea ----------
document.addEventListener("keydown", (event) => {
	if (event.key !== "/") return;
	if (event.ctrlKey || event.metaKey || event.altKey) return;
	const active = document.activeElement;
	if (
		active instanceof HTMLInputElement ||
		active instanceof HTMLTextAreaElement
	)
		return;
	const textarea = document.getElementById(
		"kql-textarea",
	) as HTMLTextAreaElement | null;
	if (!textarea) return;
	event.preventDefault();
	textarea.focus();
	textarea.setSelectionRange(textarea.value.length, textarea.value.length);
});

// ---------- KQL completion ----------
document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const button = target?.closest(".kql-suggestion") as HTMLButtonElement | null;
	if (!button) return;

	const list = button.closest("[data-kql-completions]") as HTMLElement | null;
	const textarea = document.getElementById(
		"kql-textarea",
	) as HTMLTextAreaElement | null;
	if (!list || !textarea) return;

	const editStart = Number(list.dataset.editStart ?? "0");
	const editLength = Number(list.dataset.editLength ?? "0");
	const before = button.dataset.before ?? "";
	const after = button.dataset.after ?? "";

	const value = textarea.value;
	const left = value.substring(0, editStart);
	const right = value.substring(editStart + editLength);
	const prevChar = left.slice(-1);
	const needsLeadingSpace =
		editLength === 0 && prevChar !== "" && !/[\s(.]/.test(prevChar);
	const prefix = needsLeadingSpace ? ` ${before}` : before;
	textarea.value = left + prefix + after + right;

	const caret = left.length + prefix.length;
	textarea.setSelectionRange(caret, caret);
	textarea.focus();

	const panel = document.getElementById("kql-completions");
	if (panel) panel.innerHTML = "";

	if (prefix.endsWith(".")) {
		textarea.dispatchEvent(new Event("keyup", { bubbles: true }));
	}
});

document.addEventListener("keydown", (event) => {
	const target = event.target as HTMLElement | null;
	if (
		!(target instanceof HTMLTextAreaElement) ||
		target.id !== "kql-textarea"
	) {
		if (event.key === "Escape") closeKqlPanel();
		return;
	}

	if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
		event.preventDefault();
		closeKqlPanel();
		const submit = target.form?.querySelector<HTMLButtonElement>(
			'button[type="submit"]',
		);
		if (submit) flashButton(submit);
		target.form?.requestSubmit();
		return;
	}

	const items = Array.from(
		document.querySelectorAll<HTMLButtonElement>(
			"#kql-completions .kql-suggestion",
		),
	);

	if (event.key === "Escape") {
		closeKqlPanel();
		return;
	}

	if (items.length === 0) return;

	const current = items.findIndex((b) => b.dataset.kqlActive === "1");
	const cols = countKqlCols(items);
	const n = items.length;
	const start = current < 0 ? 0 : current;

	if (event.key === "ArrowRight") {
		event.preventDefault();
		highlightKqlItem(items, (start + 1) % n);
	} else if (event.key === "ArrowLeft") {
		event.preventDefault();
		highlightKqlItem(items, (start - 1 + n) % n);
	} else if (event.key === "ArrowDown") {
		event.preventDefault();
		highlightKqlItem(items, current < 0 ? 0 : (current + cols) % n);
	} else if (event.key === "ArrowUp") {
		event.preventDefault();
		highlightKqlItem(items, current < 0 ? n - 1 : (current - cols + n) % n);
	} else if (event.key === "Enter" && current >= 0) {
		event.preventDefault();
		items[current]?.click();
	}
});

function countKqlCols(items: readonly HTMLButtonElement[]): number {
	if (items.length < 2) return 1;
	const topRow = items[0]?.offsetTop ?? 0;
	let cols = 0;
	for (const item of items) {
		if (item.offsetTop === topRow) cols++;
		else break;
	}
	return cols || 1;
}

function closeKqlPanel(): void {
	const panel = document.getElementById("kql-completions");
	if (panel) panel.innerHTML = "";
}

document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	if (!target) return;
	if (target.closest("#kql-completions") || target.closest("#kql-textarea"))
		return;
	closeKqlPanel();
});

function highlightKqlItem(
	items: readonly HTMLButtonElement[],
	i: number,
): void {
	for (let idx = 0; idx < items.length; idx++) {
		const btn = items[idx];
		if (!btn) continue;
		if (idx === i) {
			btn.dataset.kqlActive = "1";
			btn.classList.add("bg-primary", "text-primary-content");
			btn.scrollIntoView({ block: "nearest" });
		} else {
			btn.removeAttribute("data-kql-active");
			btn.classList.remove("bg-primary", "text-primary-content");
		}
	}
}

// ---------- Hover filter chips ----------
let chipSubmitLock = false;

window.addEventListener("pageshow", () => {
	chipSubmitLock = false;
});

document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const btn = target?.closest(
		"[data-filter-field]",
	) as HTMLButtonElement | null;
	if (!btn) return;
	event.stopPropagation();
	event.preventDefault();

	if (chipSubmitLock) return;

	const field = btn.dataset.filterField ?? "";
	const op = btn.dataset.filterOp ?? "eq";
	const value = btn.dataset.filterValue ?? "";
	if (!field || !value) return;

	const sym =
		op === "eq"
			? "=="
			: op === "ne"
				? "!="
				: op === "ge"
					? ">="
					: op === "le"
						? "<="
						: "==";

	const textarea = document.getElementById(
		"kql-textarea",
	) as HTMLTextAreaElement | null;
	if (!textarea) return;

	const clause = `| where ${field} ${sym} ${value}`;
	const base =
		textarea.value.trim().length > 0 ? textarea.value.trimEnd() : "events";
	if (base.includes(clause)) return;
	textarea.value = `${base}\n${clause}`;
	chipSubmitLock = true;
	textarea.form?.requestSubmit();
});

// ---------- Pin search panel ----------
const KqlPinKey = "yobabox.kqlPanelPinned";

function applyKqlPinState(pinned: boolean): void {
	const form = document.getElementById("kql-search-form");
	const toggle = document.getElementById("kql-pin-toggle");
	if (!form) return;
	form.classList.toggle("sticky", pinned);
	form.classList.toggle("top-0", pinned);
	form.classList.toggle("z-20", pinned);
	form.classList.toggle("shadow-lg", pinned);
	if (toggle) {
		toggle.setAttribute("aria-pressed", pinned ? "true" : "false");
		toggle.classList.toggle("btn-active", pinned);
		toggle.textContent = pinned ? "Unpin" : "Pin";
	}
}

(() => {
	try {
		if (localStorage.getItem(KqlPinKey) === "1") applyKqlPinState(true);
	} catch {
		// localStorage unavailable — start unpinned.
	}
})();

document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const btn = target?.closest("#kql-pin-toggle") as HTMLButtonElement | null;
	if (!btn) return;
	event.preventDefault();
	const pinned = btn.getAttribute("aria-pressed") !== "true";
	try {
		localStorage.setItem(KqlPinKey, pinned ? "1" : "0");
	} catch {
		// localStorage unavailable — apply state for this session only.
	}
	applyKqlPinState(pinned);
});

// ---------- Copy-to-clipboard ----------
document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const btn = target?.closest("[data-copy]") as HTMLButtonElement | null;
	if (!btn) return;
	event.stopPropagation();
	event.preventDefault();

	const text = btn.dataset.copy ?? "";
	void navigator.clipboard.writeText(text).then(() => {
		const original = btn.textContent;
		btn.textContent = "copied";
		btn.dataset.state = "copied";
		setTimeout(() => {
			btn.textContent = original;
			btn.removeAttribute("data-state");
		}, 1200);
	});
});

// ---------- Expandable event row ----------
document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	if (!target) return;
	if (target.closest("button, a, input, textarea, select, summary")) return;

	const row = target.closest(
		"tr[data-event-id], tr.event-live",
	) as HTMLTableRowElement | null;
	if (!row) return;

	const details = row.nextElementSibling as HTMLElement | null;
	if (details?.classList.contains("event-details")) {
		details.classList.toggle("hidden");
	}
});

// ---------- Live-tail SSE ----------
function liveTailIsActive(): boolean {
	const toggle = document.getElementById("live-tail-toggle") as HTMLInputElement | null;
	return toggle?.checked === true;
}

let stagedPayloads: string[] = [];
let liveTailFlushing = false;

function isEventsBodyTopVisible(): boolean {
	const body = document.getElementById("events-body");
	if (!body) return true;
	return body.getBoundingClientRect().top >= -40;
}

function ensureLiveTailBanner(): HTMLTableRowElement {
	let banner = document.getElementById("live-tail-banner") as HTMLTableRowElement | null;
	if (banner) return banner;
	const tbody = document.getElementById("events-body");
	if (!tbody) throw new Error("no events-body");
	banner = document.createElement("tr");
	banner.id = "live-tail-banner";
	banner.className = "hidden cursor-pointer";
	banner.setAttribute("data-testid", "live-tail-banner");
	banner.innerHTML = '<td colspan="4" class="text-center py-2 text-sm font-semibold bg-primary/20 hover:bg-primary/30"><span data-testid="live-tail-count">0</span> new events — click to show</td>';
	tbody.insertBefore(banner, tbody.firstChild);
	return banner;
}

function updateBannerCount(): void {
	const banner = document.getElementById("live-tail-banner");
	const countEl = banner?.querySelector<HTMLElement>('[data-testid="live-tail-count"]');
	if (!banner || !countEl) return;
	countEl.textContent = String(stagedPayloads.length);
	banner.classList.toggle("hidden", stagedPayloads.length === 0);
}

function resetLiveTailStaging(): void {
	stagedPayloads = [];
	updateBannerCount();
}

// Intercept SSE before htmx swaps
document.addEventListener("htmx:sseBeforeMessage", ((event: CustomEvent) => {
	const elt = event.detail.elt as HTMLElement;
	if (!elt.closest("#live-tail-sse")) return;
	if (!liveTailIsActive()) return;
	if (liveTailFlushing) return;

	if (isEventsBodyTopVisible()) return;

	event.preventDefault();
	stagedPayloads.push(String(event.detail.data));
	ensureLiveTailBanner();
	updateBannerCount();
}) as EventListener);

// Click banner → flush all staged events
document.addEventListener("click", (event) => {
	const banner = (event.target as HTMLElement)?.closest("#live-tail-banner");
	if (!banner) return;
	if (stagedPayloads.length === 0) return;

	const tbody = document.getElementById("events-body");
	if (!tbody) return;

	liveTailFlushing = true;
	try {
		const tmp = document.createElement("tbody");
		tmp.innerHTML = stagedPayloads.join("");
		const frag = document.createDocumentFragment();
		while (tmp.firstChild) frag.appendChild(tmp.firstChild);
		tbody.insertBefore(frag, banner.nextSibling);
	} finally {
		queueMicrotask(() => { liveTailFlushing = false; });
	}
	resetLiveTailStaging();
	window.scrollTo({ top: 0, behavior: "smooth" });
});

// Live-tail toggle → SSE connect/disconnect
document.addEventListener("change", (event) => {
	const target = event.target as HTMLInputElement | null;
	if (target?.id !== "live-tail-toggle") return;

	const project = target.dataset["project"];
	const kql = target.dataset["kql"] ?? "";
	const tbody = document.getElementById("events-body");
	if (!project || !tbody?.parentElement) return;

	const formField = document.getElementById("live-tail-form-field") as HTMLInputElement | null;
	if (formField) formField.disabled = !target.checked;

	const containerId = "live-tail-sse";
	document.getElementById(containerId)?.remove();
	resetLiveTailStaging();

	if (!target.checked) return;

	const url = `/api/live-tail?project=${encodeURIComponent(project)}&kql=${encodeURIComponent(kql)}`;
	const container = document.createElement("div");
	container.id = containerId;
	container.setAttribute("hx-ext", "sse");
	container.setAttribute("sse-connect", url);
	container.setAttribute("sse-retry", "3000");
	container.innerHTML = '<div sse-swap="event" hx-target="#events-body" hx-swap="afterbegin"></div>';
	tbody.parentElement.parentElement?.insertBefore(container, tbody.parentElement);
	(window as any).htmx?.process(container);
});

// Reconnect live-tail on page reload
(() => {
	const params = new URLSearchParams(window.location.search);
	if (params.get("liveTail") !== "1") return;
	const toggle = document.getElementById("live-tail-toggle") as HTMLInputElement | null;
	if (!toggle || toggle.checked) return;
	toggle.checked = true;
	toggle.dispatchEvent(new Event("change", { bubbles: true }));
})();

// ---------- Share modal ----------
document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	if (!target) return;

	const openBtn = target.closest("[data-share-modal-open]") as HTMLElement | null;
	if (openBtn) {
		const modal = document.getElementById("share-modal") as HTMLDialogElement | null;
		modal?.showModal();
		return;
	}

	if (target.closest("[data-share-copy]")) {
		const url = (document.getElementById("share-url") as HTMLInputElement | null)?.value ?? "";
		void navigator.clipboard.writeText(url);
		return;
	}
});

// Pre-fill share URL when modal opens
document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const btn = target?.closest("[data-share-modal-open]") as HTMLElement | null;
	if (!btn) return;
	const project = btn.dataset["project"] ?? "";
	const kql = btn.dataset["kql"] ?? "events";
	const urlInput = document.getElementById("share-url") as HTMLInputElement | null;
	if (urlInput) {
		urlInput.value = `${window.location.origin}/logs?project=${encodeURIComponent(project)}&kql=${encodeURIComponent(kql)}`;
	}
});
