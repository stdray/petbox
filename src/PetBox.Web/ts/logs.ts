// ---------- Local-time rendering ----------
// formatLocalTime/renderLocalTimes live in ./localTime (kept side-effect-free there so
// localTime.test.ts can import them under `node --test` without a DOM). Wiring below is the
// only part that touches `document`.
import { renderLocalTimes } from "./localTime";
import { writeUiState } from "./ui-state";

renderLocalTimes(document);
document.addEventListener("htmx:afterSwap", (event) => {
	const detail = (event as CustomEvent).detail as { target?: Element } | undefined;
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

// ---------- Hotkey toast ----------
function ensureToastRoot(): HTMLDivElement {
	const existing = document.getElementById("hotkey-toast-root") as HTMLDivElement | null;
	if (existing) return existing;
	const root = document.createElement("div");
	root.id = "hotkey-toast-root";
	root.className = "fixed bottom-4 right-4 z-50 flex flex-col gap-2 pointer-events-none";
	document.body.appendChild(root);
	return root;
}

function showHotkeyToast(combo: string, action: string): void {
	const root = ensureToastRoot();
	const toast = document.createElement("div");
	toast.className =
		"flex items-center gap-2 px-3 py-2 rounded-md bg-base-300 shadow-lg text-xs transition-opacity duration-150 opacity-0";
	const kbd = document.createElement("kbd");
	kbd.className = "kbd kbd-xs";
	kbd.textContent = combo;
	const text = document.createElement("span");
	text.textContent = action;
	toast.appendChild(kbd);
	toast.appendChild(text);
	root.appendChild(toast);
	requestAnimationFrame(() => toast.classList.remove("opacity-0"));
	setTimeout(() => {
		toast.classList.add("opacity-0");
		setTimeout(() => toast.remove(), 150);
	}, 900);
}

const PendingToastKey = "petbox.pendingToast";

function deferHotkeyToast(combo: string, action: string): void {
	try {
		sessionStorage.setItem(PendingToastKey, JSON.stringify({ combo, action, t: Date.now() }));
	} catch {
		showHotkeyToast(combo, action);
	}
}

(() => {
	try {
		const raw = sessionStorage.getItem(PendingToastKey);
		if (!raw) return;
		sessionStorage.removeItem(PendingToastKey);
		const data = JSON.parse(raw) as { combo: string; action: string; t: number };
		if (Date.now() - data.t < 5_000) showHotkeyToast(data.combo, data.action);
	} catch {
		// ignore
	}
})();

// ---------- Global focus shortcut: "/" jumps to KQL textarea ----------
document.addEventListener("keydown", (event) => {
	if (event.key !== "/") return;
	if (event.ctrlKey || event.metaKey || event.altKey) return;
	const active = document.activeElement;
	if (active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement) return;
	const textarea = document.getElementById("kql-textarea") as HTMLTextAreaElement | null;
	if (!textarea) return;
	event.preventDefault();
	textarea.focus();
	textarea.setSelectionRange(textarea.value.length, textarea.value.length);
	showHotkeyToast("/", "focus query");
});

// ---------- KQL completion ----------
document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const button = target?.closest(".kql-suggestion") as HTMLButtonElement | null;
	if (!button) return;

	const list = button.closest("[data-kql-completions]") as HTMLElement | null;
	const textarea = document.getElementById("kql-textarea") as HTMLTextAreaElement | null;
	if (!list || !textarea) return;

	const editStart = Number(list.dataset["editStart"] ?? "0");
	const editLength = Number(list.dataset["editLength"] ?? "0");
	const before = button.dataset["before"] ?? "";
	const after = button.dataset["after"] ?? "";

	const value = textarea.value;
	const left = value.substring(0, editStart);
	const right = value.substring(editStart + editLength);
	const prevChar = left.slice(-1);
	const needsLeadingSpace = editLength === 0 && prevChar !== "" && !/[\s(.]/.test(prevChar);
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
	if (!(target instanceof HTMLTextAreaElement) || target.id !== "kql-textarea") {
		if (event.key === "Escape") closeKqlPanel();
		return;
	}

	if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
		event.preventDefault();
		closeKqlPanel();
		const submit = target.form?.querySelector<HTMLButtonElement>('button[type="submit"]');
		if (submit) flashButton(submit);
		deferHotkeyToast(event.metaKey ? "⌘+Enter" : "Ctrl+Enter", "apply");
		target.form?.requestSubmit();
		return;
	}

	const items = Array.from(document.querySelectorAll<HTMLButtonElement>("#kql-completions .kql-suggestion"));

	if (event.key === "Escape") {
		closeKqlPanel();
		return;
	}

	if (items.length === 0) return;

	const current = items.findIndex((b) => b.dataset["kqlActive"] === "1");
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
	if (target.closest("#kql-completions") || target.closest("#kql-textarea")) return;
	closeKqlPanel();
});

function highlightKqlItem(items: readonly HTMLButtonElement[], i: number): void {
	for (let idx = 0; idx < items.length; idx++) {
		const btn = items[idx];
		if (!btn) continue;
		if (idx === i) {
			btn.dataset["kqlActive"] = "1";
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
	const btn = target?.closest("[data-filter-field]") as HTMLButtonElement | null;
	if (!btn) return;
	event.stopPropagation();
	event.preventDefault();

	if (chipSubmitLock) return;

	const field = btn.dataset["filterField"] ?? "";
	const op = btn.dataset["filterOp"] ?? "eq";
	const value = btn.dataset["filterValue"] ?? "";
	if (!field || !value) return;

	const sym = op === "eq" ? "==" : op === "ne" ? "!=" : op === "ge" ? ">=" : op === "le" ? "<=" : "==";

	const textarea = document.getElementById("kql-textarea") as HTMLTextAreaElement | null;
	if (!textarea) return;

	const clause = `| where ${field} ${sym} ${value}`;
	const base = textarea.value.trim().length > 0 ? textarea.value.trimEnd() : "events";
	if (base.includes(clause)) return;
	textarea.value = `${base}\n${clause}`;
	chipSubmitLock = true;
	textarea.form?.requestSubmit();
});

// ---------- Pin search panel ----------
// State lives in the shared `petbox.ui` cookie (BrowserState.KqlPanelPinned, ui-state.ts) — window/
// device state, not a cross-device preference — so the SERVER renders the sticky/shadow-lg classes
// and aria-pressed on the very first response (Pages/Logs/Index.cshtml). This module therefore
// never applies the pinned state on load (that was the FOUC bug: localStorage was read and the
// classes were applied AFTER paint, work `kql-panel-pin-server-state`) — it only reacts to a click
// and writes the new value through the cookie helper so the NEXT server response already agrees.
// Mirrors ts/sidebar.ts's pin toggle exactly.

// Current state is read from the DOM the server just rendered — never from storage — so a toggle
// always flips whatever is actually showing.
function isKqlPinned(): boolean {
	return document.getElementById("kql-pin-toggle")?.getAttribute("aria-pressed") === "true";
}

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

document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const btn = target?.closest("#kql-pin-toggle") as HTMLButtonElement | null;
	if (!btn) return;
	event.preventDefault();
	const pinned = !isKqlPinned();
	writeUiState({ kqlPanelPinned: pinned });
	applyKqlPinState(pinned);
});

// ---------- Copy-to-clipboard ----------
document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const btn = target?.closest("[data-copy]") as HTMLButtonElement | null;
	if (!btn) return;
	event.stopPropagation();
	event.preventDefault();

	const text = btn.dataset["copy"] ?? "";
	void navigator.clipboard.writeText(text).then(() => {
		const original = btn.textContent;
		btn.textContent = "copied";
		btn.dataset["state"] = "copied";
		setTimeout(() => {
			btn.textContent = original;
			btn.removeAttribute("data-state");
		}, 1200);
	});
});

// ---------- Event permalink ----------
// The copied link carries only the row id (?event=): the server rebuilds the
// filter itself, so no query text appears in the URL.
document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	const btn = target?.closest("[data-event-link]") as HTMLButtonElement | null;
	if (!btn) return;
	event.stopPropagation();
	event.preventDefault();

	const url = `${window.location.origin}${window.location.pathname}?event=${btn.dataset["eventLink"]}`;
	void navigator.clipboard.writeText(url).then(() => {
		const original = btn.textContent;
		btn.textContent = "copied";
		btn.dataset["state"] = "copied";
		setTimeout(() => {
			btn.textContent = original;
			btn.removeAttribute("data-state");
		}, 1200);
	});
});

// Highlight and expand the row an ?event= permalink points at.
(() => {
	const id = new URLSearchParams(window.location.search).get("event");
	if (!id) return;
	const row = document.querySelector(`tr[data-event-id="${CSS.escape(id)}"]`);
	if (!row) return;
	row.classList.add("event-permalink-target");
	const details = row.nextElementSibling;
	if (details?.classList.contains("event-details")) details.classList.remove("hidden");
	row.scrollIntoView({ block: "center" });
})();

// ---------- Expandable event row ----------
document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	if (!target) return;
	if (target.closest("button, a, input, textarea, select, summary")) return;

	const row = target.closest("tr[data-event-id], tr.event-live") as HTMLTableRowElement | null;
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
	banner.innerHTML =
		'<td colspan="4" class="text-center py-2 text-sm font-semibold bg-primary/20 hover:bg-primary/30"><span data-testid="live-tail-count">0</span> new events — click to show</td>';
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
		// This insert bypasses htmx (it batches staged rows instead of swapping them one
		// at a time), so htmx:afterSwap never fires for these rows — localize the fragment
		// here, before its nodes move into tbody and it empties out.
		renderLocalTimes(frag);
		tbody.insertBefore(frag, banner.nextSibling);
	} finally {
		queueMicrotask(() => {
			liveTailFlushing = false;
		});
	}
	resetLiveTailStaging();
	window.scrollTo({ top: 0, behavior: "smooth" });
});

// The cursor of the NEWEST row currently on screen, in the same (TimestampMs, Id) key — and the same
// 16-byte big-endian base64 encoding — the server pages the table by (LogCursor.cs). The table renders
// newest-first, so that is the topmost row carrying a data-event-id (the banner row has none). Sending
// it as ?since= is what closes the gap between the moment the table was rendered and the moment the
// EventSource opens: without it the stream starts "now" and everything ingested in between is lost.
// Nothing on screen → no cursor → the server starts at the log's tip, i.e. only new events.
function encodeLogCursor(timestampMs: number, id: number): string {
	const bytes = new Uint8Array(16);
	const view = new DataView(bytes.buffer);
	view.setBigInt64(0, BigInt(timestampMs));
	view.setBigInt64(8, BigInt(id));
	return btoa(String.fromCharCode(...bytes));
}

function newestRenderedCursor(): string | null {
	const row = document.querySelector<HTMLElement>("#events-body tr[data-event-id]");
	const id = Number(row?.dataset["eventId"]);
	const iso = row?.querySelector("time[datetime]")?.getAttribute("datetime");
	if (!row || !iso || !Number.isFinite(id)) return null;
	const ms = Date.parse(iso);
	if (!Number.isFinite(ms)) return null;
	return encodeLogCursor(ms, id);
}

// Live-tail toggle → SSE connect/disconnect
document.addEventListener("change", (event) => {
	const target = event.target as HTMLInputElement | null;
	if (target?.id !== "live-tail-toggle") return;

	const project = target.dataset["project"];
	const log = target.dataset["log"];
	const kql = target.dataset["kql"] ?? "";
	const tbody = document.getElementById("events-body");
	if (!project || !log || !tbody?.parentElement) return;

	const formField = document.getElementById("live-tail-form-field") as HTMLInputElement | null;
	if (formField) formField.disabled = !target.checked;

	const containerId = "live-tail-sse";
	document.getElementById(containerId)?.remove();
	resetLiveTailStaging();

	if (!target.checked) return;

	const since = newestRenderedCursor();
	const sinceParam = since ? `&since=${encodeURIComponent(since)}` : "";
	const url = `/api/logs/${encodeURIComponent(project)}/${encodeURIComponent(log)}/live-tail?kql=${encodeURIComponent(kql)}${sinceParam}`;
	const container = document.createElement("div");
	container.id = containerId;
	container.setAttribute("hx-ext", "sse");
	container.setAttribute("sse-connect", url);
	container.setAttribute("sse-retry", "3000");
	container.innerHTML = '<div sse-swap="event" hx-target="#events-body" hx-swap="afterbegin"></div>';
	tbody.parentElement.parentElement?.insertBefore(container, tbody.parentElement);
	// htmx must be told about a container that was never in the initial DOM, or hx-ext/sse-connect on
	// it are never processed. window.htmx is published by ts/htmx-global.ts (the ESM htmx build does
	// not set it itself) — that module is also what lets the SSE extension register at all.
	window.htmx.process(container);
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
// The link must not expose the KQL text: the query is stored server-side
// (POST /api/share) and the URL carries only the opaque token.
let shareLinkCache: { key: string; url: string; expiresAt: string } | null = null;

async function createShareLink(project: string, log: string, kql: string): Promise<void> {
	const urlInput = document.getElementById("share-url") as HTMLInputElement | null;
	const expiryEl = document.getElementById("share-expiry");
	if (!urlInput) return;

	const cacheKey = `${project} ${log} ${kql}`;
	if (shareLinkCache?.key === cacheKey) {
		urlInput.value = shareLinkCache.url;
		if (expiryEl) expiryEl.textContent = `Expires ${new Date(shareLinkCache.expiresAt).toLocaleString()}`;
		return;
	}

	urlInput.value = "";
	urlInput.placeholder = "Creating link…";
	if (expiryEl) expiryEl.textContent = "";
	try {
		const resp = await fetch("/api/share", {
			method: "POST",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify({ projectKey: project, logName: log, kql, ttlMinutes: 0 }),
		});
		if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
		const created = (await resp.json()) as { id: string; expiresAt: string };
		const url = `${window.location.origin}/ui/share/${created.id}`;
		shareLinkCache = { key: cacheKey, url, expiresAt: created.expiresAt };
		urlInput.value = url;
		if (expiryEl) expiryEl.textContent = `Expires ${new Date(created.expiresAt).toLocaleString()}`;
	} catch {
		urlInput.placeholder = "Failed to create share link";
		if (expiryEl) expiryEl.textContent = "";
	}
}

document.addEventListener("click", (event) => {
	const target = event.target as HTMLElement | null;
	if (!target) return;

	const openBtn = target.closest("[data-share-modal-open]") as HTMLElement | null;
	if (openBtn) {
		const modal = document.getElementById("share-modal") as HTMLDialogElement | null;
		modal?.showModal();
		void createShareLink(
			openBtn.dataset["project"] ?? "",
			openBtn.dataset["log"] ?? "",
			openBtn.dataset["kql"] ?? "events",
		);
		return;
	}

	if (target.closest("[data-share-copy]")) {
		const url = (document.getElementById("share-url") as HTMLInputElement | null)?.value ?? "";
		if (url) void navigator.clipboard.writeText(url);
		return;
	}
});
