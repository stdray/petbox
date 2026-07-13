// Hand-written TS twin of PetBox.Core.Settings.BrowserState — the cookie branch of the combined
// UI-state record (see UiStateResolver.cs and _Layout.cshtml, which resolves it server-side
// BEFORE render, the same way ThemeHelper resolves data-theme). There is no C#<->TS codegen in
// this project; this file and BrowserState.cs are kept in sync by UiStateTypeSyncTests, which
// reflects over the C# record and parses this interface — add a field to BOTH sides together, or
// the test fails loudly.
//
// The whole app writes ONE cookie, `petbox.ui`, carrying a flat JSON object keyed by each
// [BrowserState] property's `Key` — never one cookie per feature (that grows every request's
// header on each new preference). `sidebarPinned` (work `sidebar-pin-server-state`) and
// `kqlPanelPinned` (work `kql-panel-pin-server-state`) are the first two fields; `collapsedByBoard`
// (work `board-filters-server-state`) is the third. View mode/tag-`by`/fields (work
// `board-view-cross-device`) and active-only/sort (work `board-filters-server-state`) are NOT here
// — they're cross-device preferences, so they live in the DB branch (BrowserState.cs's
// [Setting]-tagged properties), read/written through TaskBoardModel + the
// `/api/ui/board-filter-prefs` endpoint, never through this cookie. The dead `petbox.sidebar.tree`
// cookie sidebar.ts used to write was deleted, not migrated here (work `sidebar-tree-cookie-dead`)
// — nothing in the markup ever consumed it.
export interface BrowserState {
	sidebarPinned?: boolean;
	kqlPanelPinned?: boolean;
	// board-filters-server-state: which plan-node subtrees are collapsed, per (project,board) key
	// (literally "projectKey/board", the same composite key BoardViewPreferences uses server-side)
	// — a TOP-LEVEL cookie key holding the WHOLE map for every board the user has touched, since
	// MergeCookieValue only merges at the top level (one [BrowserState] property = one cookie key);
	// board.ts is responsible for reading the current map out, updating ONE board's entry, and
	// writing the whole map back (see persistCollapsed in board.ts) — the same read-modify-write
	// shape BoardViewPreferences uses on the DB side.
	collapsedByBoard?: Record<string, string[]>;
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

// Writes `patch` into the shared petbox.ui cookie, merged with whatever is already there.
export function writeUiState(patch: Partial<BrowserState>): void {
	const merged = mergeUiStateCookie(readRawCookie(), patch);
	document.cookie = `${COOKIE_NAME}=${encodeURIComponent(merged)};path=/;max-age=31536000;samesite=lax`;
}
