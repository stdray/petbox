// Node/comment share links (spec `node-share`, `node-share-scope`; card `node-share-ui`).
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
// The reader cannot widen it afterwards, so the scope radios are re-minting controls: changing
// one creates a DIFFERENT token, it does not re-point the one already shown.
//
// TTL: deliberately NOT sent. The contract's `ttlMinutes` is optional and its absence means "no
// expiry" — a task link that dies on a timer is the wrong default for something pasted into an
// issue or a chat months before anyone follows it. Revocation, not expiry, is the withdrawal
// mechanism, which is why the Revoke control is not optional chrome here.

type ShareScope = "body" | "full" | "comment";

interface CreatedShare {
	id: string;
	// The server may answer with no expiry at all for an indefinite link — modelled as
	// optional/nullable rather than assumed to be a parseable date string.
	expiresAt?: string | null;
}

// `token` + `project` are what REVOKE addresses (DELETE /api/share/{token} with {projectKey} in
// the body): the public URL alone would have to be re-parsed for the token, and the project is
// not in that URL at all. Same reasoning as logs.ts's shareLinkCache.
interface MintedLink {
	key: string;
	url: string;
	expiresAt: string | null;
	token: string;
	project: string;
}

let minted: MintedLink | null = null;
let scope: ShareScope = "body";
let commentId = "";

function modal(): HTMLDialogElement | null {
	return document.getElementById("node-share-modal") as HTMLDialogElement | null;
}

function el(id: string): HTMLElement | null {
	return document.getElementById(id);
}

function urlInput(): HTMLInputElement | null {
	return el("node-share-url") as HTMLInputElement | null;
}

function setDisplay(target: Element | null, visible: boolean): void {
	if (target instanceof HTMLElement) target.style.display = visible ? "" : "none";
}

function setRevokeEnabled(enabled: boolean): void {
	const btn = document.querySelector("[data-node-share-revoke]") as HTMLButtonElement | null;
	if (btn) btn.disabled = !enabled;
}

function setRevokeStatus(text: string): void {
	const status = el("node-share-revoke-status");
	if (status) status.textContent = text;
}

// The expiry line doubles as the honest statement of what an unbounded link means. An empty /
// absent expiresAt is NOT an error to hide — it is the default this UI asks for, so it gets a
// sentence saying the link stays live until it is revoked, not a blank strip.
function setExpiry(expiresAt: string | null): void {
	const line = el("node-share-expiry");
	if (!line) return;
	line.textContent = expiresAt
		? `Expires ${new Date(expiresAt).toLocaleString()}`
		: "Never expires — the link works until you revoke it.";
}

function clearExpiry(): void {
	const line = el("node-share-expiry");
	if (line) line.textContent = "";
}

function selectedScope(): ShareScope {
	const checked = document.querySelector(
		"[data-testid='node-share-scope-choice'] input[name='node-share-scope']:checked",
	) as HTMLInputElement | null;
	return checked?.value === "full" ? "full" : "body";
}

async function createShareLink(): Promise<void> {
	const dialog = modal();
	const input = urlInput();
	if (!dialog || !input) return;

	const project = dialog.dataset["project"] ?? "";
	const board = dialog.dataset["board"] ?? "";
	const nodeId = dialog.dataset["nodeId"] ?? "";

	// The cache key is the REQUEST, not the token: flipping body→full→body must hand back the
	// first `body` link rather than minting a third one, but flipping to `full` must never reuse
	// the `body` token (a wider scope is a different secret).
	const cacheKey = `${project} ${board} ${nodeId} ${scope} ${commentId}`;
	if (minted?.key === cacheKey) {
		input.value = minted.url;
		setExpiry(minted.expiresAt);
		setRevokeEnabled(true);
		setRevokeStatus("");
		return;
	}

	input.value = "";
	input.placeholder = "Creating link…";
	clearExpiry();
	setRevokeEnabled(false);
	setRevokeStatus("");

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
		const url = `${window.location.origin}/ui/share/node/${created.id}`;
		minted = {
			key: cacheKey,
			url,
			expiresAt: created.expiresAt ?? null,
			token: created.id,
			project,
		};
		input.value = url;
		setExpiry(minted.expiresAt);
		setRevokeEnabled(true);
	} catch {
		input.placeholder = "Failed to create share link";
		clearExpiry();
		setRevokeEnabled(false);
	}
}

// Revoke = DELETE /api/share/{token}. The link is withdrawn server-side, so there is nothing to
// "un-revoke" — hence the confirm. The mint cache is dropped on success: the key is the REQUEST
// (project+board+node+scope), not the token, so keeping it would hand the user back the URL of a
// token that no longer exists the next time they open the same scope.
async function revokeShareLink(): Promise<void> {
	const link = minted;
	if (!link) return;
	if (!window.confirm("Revoke this share link? Anyone holding it loses access immediately.")) return;

	const input = urlInput();
	setRevokeEnabled(false);
	setRevokeStatus("Revoking…");
	try {
		const resp = await fetch(`/api/share/${encodeURIComponent(link.token)}`, {
			method: "DELETE",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify({ projectKey: link.project }),
		});
		if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
		minted = null;
		if (input) {
			input.value = "";
			input.placeholder = "Link revoked";
		}
		clearExpiry();
		setRevokeStatus("Revoked — this link no longer works.");
	} catch {
		setRevokeStatus("Failed to revoke the link.");
		setRevokeEnabled(true);
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

	dialog.showModal();
	void createShareLink();
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

		if (target.closest("[data-node-share-copy]")) {
			const url = urlInput()?.value ?? "";
			if (url) void navigator.clipboard.writeText(url);
			return;
		}

		if (target.closest("[data-node-share-revoke]")) {
			void revokeShareLink();
		}
	});

	// Changing the scope MINTS A NEW LINK — it does not re-scope the one on screen (the scope is
	// baked into the token server-side). Wired on the radios themselves rather than on the click
	// handler above so keyboard arrow-key selection counts too.
	const choice = document.querySelector("[data-testid='node-share-scope-choice']");
	choice?.addEventListener("change", () => {
		if (scope === "comment") return; // the comment flow hides this chooser entirely
		scope = selectedScope();
		void createShareLink();
	});
}
