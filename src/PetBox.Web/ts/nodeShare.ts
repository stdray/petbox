// Node/comment share links (spec `node-share`, `node-share-scope`, `share-link-revocable`;
// cards `node-share-ui`, `node-share-scope-switch-orphans-live-links`).
//
// The same shape as the log-query share modal (logs.ts "Share modal" section) but a SEPARATE
// module rather than an extension of it: the two mint different things through different
// endpoints (POST /api/share = a KQL query; POST /api/share/node = a task node), share no state,
// and the node one carries a SCOPE the query one has no concept of. Only the revoke half is
// literally the same call (DELETE /api/share/{token}), and that is one fetch, not a reason to
// couple two modals into one file.
//
// What the link carries is chosen at MINT time, not at read time (spec node-share-scope):
//   body    — the node's own body only
//   full    — the node plus its whole discussion
//   comment — one single comment (opened from that comment's own row in the thread)
// The reader cannot widen it afterwards, so a different scope is a DIFFERENT token: the radios
// choose what the next mint will carry, they cannot re-point a token that already exists.
//
// TTL: deliberately NOT sent. The contract's `ttlMinutes` is optional and its absence means "no
// expiry" — a task link that dies on a timer is the wrong default for something pasted into an
// issue or a chat months before anyone follows it. Revocation, not expiry, is the withdrawal
// mechanism, which is why the Revoke control is not optional chrome here.
//
// MINTING IS AN EXPLICIT ACT, and the token lives ONLY in the DOM row that can revoke it.
// This module used to mint on `showModal()` and again on every scope change, keeping the newest
// token in one module-level variable. Because an indefinite capability token is addressable only
// BY VALUE — there is no "list share links" verb, by design (see the `share_revoke` MCP tool) —
// every token that variable was overwritten with stayed live, forever, with nothing left on the
// page able to name it. Opening the dialog to look at it was enough to leak one.
//
// Two invariants replace that, and both are structural rather than remembered:
//   1. nothing is minted except by pressing Create — opening the dialog and picking a scope have
//      no side effect at all;
//   2. a minted token's ONLY record anywhere on the client is `data-token` on its own row, which
//      carries that row's own Revoke button. A token cannot become unreachable while its row is
//      on screen, and the row is removed only after the server has confirmed the DELETE.
// So a second mint ADDS a row, it never displaces one. Auto-revoking the previous token would
// have been less code and is the wrong trade: it silently kills a link the user may already have
// pasted somewhere, and it makes the two scopes mutually exclusive when the whole point of
// offering both is that a body-only link and a full link can be handed to different people.
//
// The list is per PAGE, not per server: a reload empties it, and links minted before the reload
// become unreachable again. Closing that would need either a server-side "list my share links"
// verb (deliberately absent) or the tokens persisted in browser storage (a new at-rest copy of a
// live capability). Neither is this card's; the bounded residual is that a reload, not a click,
// is now the only way to lose sight of a link.

type ShareScope = "body" | "full" | "comment";

interface CreatedShare {
	id: string;
	// The server may answer with no expiry at all for an indefinite link — modelled as
	// optional/nullable rather than assumed to be a parseable date string.
	expiresAt?: string | null;
}

// What a freshly minted link needs in order to become a row. `token` + `project` are what REVOKE
// addresses (DELETE /api/share/{token} with {projectKey} in the body): the public URL alone would
// have to be re-parsed for the token, and the project is not in that URL at all.
interface MintedLink {
	key: string;
	url: string;
	expiresAt: string | null;
	token: string;
	project: string;
	scope: ShareScope;
}

// The only module state left is what the NEXT mint will ask for. Deliberately no `minted`
// variable: a token held here instead of on its row is a token one assignment away from being
// unrevocable, which is the whole defect this module was rewritten to remove.
let scope: ShareScope = "body";
let commentId = "";
let minting = false;

function modal(): HTMLDialogElement | null {
	return document.getElementById("node-share-modal") as HTMLDialogElement | null;
}

function el(id: string): HTMLElement | null {
	return document.getElementById(id);
}

function setDisplay(target: Element | null, visible: boolean): void {
	if (target instanceof HTMLElement) target.style.display = visible ? "" : "none";
}

function setStatus(text: string): void {
	const status = el("node-share-status");
	if (status) status.textContent = text;
}

function setCreateEnabled(enabled: boolean): void {
	const btn = document.querySelector("[data-node-share-create]") as HTMLButtonElement | null;
	if (btn) btn.disabled = !enabled;
}

// The live links, newest first. Reading them off the DOM rather than off a parallel array is the
// point: the DOM IS the register of outstanding tokens, so the two cannot drift apart.
function linkRows(): HTMLElement[] {
	return Array.from(document.querySelectorAll<HTMLElement>("[data-node-share-link]"));
}

function scopeLabel(value: ShareScope): string {
	if (value === "comment") return "One comment";
	return value === "full" ? "Everything, including the discussion" : "Body only";
}

// The expiry line doubles as the honest statement of what an unbounded link means. An empty /
// absent expiresAt is NOT an error to hide — it is the default this UI asks for, so it gets a
// sentence saying the link stays live until it is revoked, not a blank strip.
function expiryText(expiresAt: string | null): string {
	return expiresAt
		? `Expires ${new Date(expiresAt).toLocaleString()}`
		: "Never expires — the link works until you revoke it.";
}

// The placeholder is BOTH the "nothing minted yet" state and a disabled preview of the controls a
// link will get, which is why it is dropped for good on the first mint rather than hidden: a
// second, permanently disabled Copy/Revoke pair sitting under real ones is dead chrome, and a
// disabled Revoke is the one control that must never be ambiguous here.
function syncEmptyState(): void {
	const has = linkRows().length > 0;
	if (has) document.querySelector("[data-node-share-placeholder]")?.remove();
	const placeholderGone = document.querySelector("[data-node-share-placeholder]") === null;
	setDisplay(document.querySelector("[data-node-share-empty]"), !has && placeholderGone);
}

function addRow(link: MintedLink): void {
	const box = el("node-share-links");
	const template = document.querySelector("[data-node-share-row-template]") as HTMLTemplateElement | null;
	const row = template?.content.firstElementChild?.cloneNode(true) as HTMLElement | null;
	if (!box || !row) return;

	// The token exists nowhere else on the client. Everything the Revoke call needs travels with
	// the button that offers it.
	row.dataset["token"] = link.token;
	row.dataset["project"] = link.project;
	row.dataset["key"] = link.key;

	const input = row.querySelector<HTMLInputElement>("[data-node-share-url]");
	if (input) input.value = link.url;
	const meta = row.querySelector<HTMLElement>("[data-node-share-meta]");
	if (meta) meta.textContent = `${scopeLabel(link.scope)} — ${expiryText(link.expiresAt)}`;

	box.prepend(row);
	syncEmptyState();
}

function selectedScope(): ShareScope {
	const checked = document.querySelector(
		"[data-testid='node-share-scope-choice'] input[name='node-share-scope']:checked",
	) as HTMLInputElement | null;
	return checked?.value === "full" ? "full" : "body";
}

async function createShareLink(): Promise<void> {
	const dialog = modal();
	if (!dialog || minting) return;

	const project = dialog.dataset["project"] ?? "";
	const board = dialog.dataset["board"] ?? "";
	const nodeId = dialog.dataset["nodeId"] ?? "";

	// Pressing Create again for the SAME request would mint a second secret granting exactly what
	// the first already grants — two things to revoke instead of one, for no gain. Nothing is
	// hidden by declining: the existing row is on screen, with its own Revoke.
	const key = `${project} ${board} ${nodeId} ${scope} ${commentId}`;
	if (linkRows().some((row) => row.dataset["key"] === key)) {
		setStatus("A link for this scope already exists below.");
		return;
	}

	minting = true;
	setCreateEnabled(false);
	setStatus("Creating link…");

	// `ttlMinutes` is omitted on purpose (see the header note) — indefinite until revoked.
	const body: Record<string, string> = { projectKey: project, board, nodeId, scope };
	if (scope === "comment") body["commentId"] = commentId;

	try {
		const resp = await fetch("/api/share/node", {
			method: "POST",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify(body),
		});
		if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
		const created = (await resp.json()) as CreatedShare;
		addRow({
			key,
			url: `${window.location.origin}/ui/share/node/${created.id}`,
			expiresAt: created.expiresAt ?? null,
			token: created.id,
			project,
			scope,
		});
		setStatus("");
	} catch {
		setStatus("Failed to create the share link.");
	} finally {
		minting = false;
		setCreateEnabled(true);
	}
}

// Revoke = DELETE /api/share/{token}. The link is withdrawn server-side, so there is nothing to
// "un-revoke" — hence the confirm.
async function revokeShareLink(row: HTMLElement): Promise<void> {
	const token = row.dataset["token"] ?? "";
	const project = row.dataset["project"] ?? "";
	if (!token) return;
	if (!window.confirm("Revoke this share link? Anyone holding it loses access immediately.")) return;

	const button = row.querySelector<HTMLButtonElement>("[data-node-share-revoke]");
	if (button) button.disabled = true;
	setStatus("Revoking…");
	try {
		const resp = await fetch(`/api/share/${encodeURIComponent(token)}`, {
			method: "DELETE",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify({ projectKey: project }),
		});
		if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
		// Only now. The row is the token's only record, so dropping it optimistically on a DELETE
		// that then failed would take a STILL-LIVE token off screen — the exact class of loss this
		// module exists to prevent.
		row.remove();
		syncEmptyState();
		setStatus("Revoked — that link no longer works.");
	} catch {
		setStatus("Failed to revoke the link.");
		if (button) button.disabled = false;
	}
}

function openFrom(opener: HTMLElement): void {
	const dialog = modal();
	if (!dialog) return; // no modal on this page (e.g. the public share page) → the button is inert

	const requested = opener.dataset["scope"];
	if (requested === "comment") {
		scope = "comment";
		commentId = opener.dataset["commentId"] ?? "";
	} else {
		scope = selectedScope();
		commentId = "";
	}

	// The scope radios are meaningless for a single comment (the comment IS the scope), so the
	// chooser is hidden rather than shown with a third, differently-shaped option.
	setDisplay(document.querySelector("[data-testid='node-share-scope-choice']"), scope !== "comment");
	const title = el("node-share-title");
	if (title) title.textContent = scope === "comment" ? "Share this comment" : "Share this node";
	setStatus("");

	// Opening MINTS NOTHING. Rows already on screen are links minted earlier on this page and
	// still live, so they stay — each with the Revoke button that is the only way to withdraw it.
	dialog.showModal();
}

export function initNodeShare(): void {
	document.addEventListener("click", (event) => {
		const target = event.target;
		if (!(target instanceof HTMLElement)) return;

		const opener = target.closest<HTMLElement>("[data-node-share-open]");
		if (opener) {
			openFrom(opener);
			return;
		}

		if (target.closest("[data-node-share-create]")) {
			void createShareLink();
			return;
		}

		const copy = target.closest<HTMLElement>("[data-node-share-copy]");
		if (copy) {
			const url =
				copy.closest("[data-node-share-link]")?.querySelector<HTMLInputElement>("[data-node-share-url]")?.value ?? "";
			if (url) void navigator.clipboard.writeText(url);
			return;
		}

		const revoke = target.closest<HTMLElement>("[data-node-share-revoke]");
		const row = revoke?.closest<HTMLElement>("[data-node-share-link]");
		if (row) void revokeShareLink(row);
	});

	// Changing the scope chooses what the NEXT Create will mint. It issues nothing by itself —
	// that is the card: a token nobody asked for is a token nobody remembers to revoke. Wired on
	// the radios themselves rather than on the click handler above so keyboard arrow-key selection
	// counts too.
	const choice = document.querySelector("[data-testid='node-share-scope-choice']");
	choice?.addEventListener("change", () => {
		if (scope === "comment") return; // the comment flow hides this chooser entirely
		scope = selectedScope();
		setStatus("");
	});
}
