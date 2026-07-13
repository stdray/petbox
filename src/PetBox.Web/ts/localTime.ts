// Local-time rendering for every `<time class="local-time" datetime="…">` element in the
// UI (board cards, comments, logs, traces, dashboard, share links, …).
//
// Millisecond precision is noise for a human-readable timestamp — dropped by default (board
// cards, comments, memory, sessions, dashboard). It is DATA, not noise, wherever sub-second
// ordering or span duration matters — log rows and trace rows — so those opt in per-element
// with the `data-ms` attribute on the `<time>` tag (logs-keep-millis). This must stay a
// per-element opt-in, not a blanket constant here or a list of page paths: a prior fix
// (petbox-date-format-ms-noise) dropped ms globally through this one shared helper and took
// milliseconds out of logs/traces too, where they are load-bearing (event ordering within a
// second, span durations) — the next log-ish page must default to "with ms" by marking its
// `<time>` elements, not by this file knowing which pages are log pages.
export function formatLocalTime(iso: string, withMs: boolean): string {
	const d = new Date(iso);
	if (Number.isNaN(d.getTime())) return iso;
	const pad = (n: number, w = 2) => String(n).padStart(w, "0");
	const base = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
	return withMs ? `${base}.${pad(d.getMilliseconds(), 3)}` : base;
}

export function renderLocalTimes(root: ParentNode): void {
	const list = root.querySelectorAll<HTMLElement>("time.local-time[datetime]");
	list.forEach((el) => {
		const iso = el.getAttribute("datetime");
		if (iso) el.textContent = formatLocalTime(iso, el.hasAttribute("data-ms"));
	});
}
