// Hand-written TS twin of PetBox.Core.Settings.BrowserState — the cookie branch of the combined
// UI-state record (see UiStateResolver.cs and _Layout.cshtml, which resolves it server-side
// BEFORE render, the same way ThemeHelper resolves data-theme). There is no C#<->TS codegen in
// this project; this file and BrowserState.cs are kept in sync by UiStateTypeSyncTests, which
// reflects over the C# record and parses this interface — add a field to BOTH sides together, or
// the test fails loudly.
//
// The whole app writes ONE cookie, `petbox.ui`, carrying a flat JSON object keyed by each
// [BrowserState] property's `Key` — never one cookie per feature (that grows every request's
// header on each new preference). `sidebarPinned` is the first field (work
// `sidebar-pin-server-state`); the remaining follow-ups (board view, board filters, kql panel
// pin, dead-tree cookie) each add their own key here and to BrowserState.cs, and read/write it
// through readUiState/writeUiState below instead of inventing their own cookie-merge logic.
export interface BrowserState {
	sidebarPinned?: boolean;
}

const COOKIE_NAME = "petbox.ui";

// Pure parse, exported for unit testing without a DOM. A missing cookie, malformed JSON, or a
// JSON value that isn't a plain object all read as `{}` rather than throwing — the mechanism's
// anonymous/first-visit/stale-cookie codepath must never fail loudly on the client either.
export function parseUiStateCookie(raw: string | null): Partial<BrowserState> {
	if (!raw) return {};
	try {
		const parsed: unknown = JSON.parse(raw);
		return parsed !== null && typeof parsed === "object" && !Array.isArray(parsed)
			? (parsed as Partial<BrowserState>)
			: {};
	} catch {
		return {};
	}
}

// Pure merge, exported for unit testing. Folds `patch` onto whatever the existing cookie already
// holds so writing one feature's key never clobbers another's — the reason there is ONE cookie,
// not N. A malformed existing cookie is treated as empty, not fatal (symmetric with parse above).
export function mergeUiStateCookie(existingRaw: string | null, patch: Partial<BrowserState>): string {
	const current = parseUiStateCookie(existingRaw);
	return JSON.stringify({ ...current, ...patch });
}

function readRawCookie(): string | null {
	const m = document.cookie.match(/(?:^|;\s*)petbox\.ui=([^;]*)/);
	return m ? decodeURIComponent(m[1] ?? "") : null;
}

export function readUiState(): Partial<BrowserState> {
	return parseUiStateCookie(readRawCookie());
}

// Writes `patch` into the shared petbox.ui cookie, merged with whatever is already there. Matches
// the attributes sidebar.ts already uses for its own cookie (petbox.sidebar.tree) so the two
// cookies behave identically to callers.
export function writeUiState(patch: Partial<BrowserState>): void {
	const merged = mergeUiStateCookie(readRawCookie(), patch);
	document.cookie = `${COOKIE_NAME}=${encodeURIComponent(merged)};path=/;max-age=31536000;samesite=lax`;
}
