// Persist last-selected workspace in localStorage. On page load:
// - sync current cookie value → localStorage
// - if cookie is missing but localStorage has a remembered value, POST to
//   /api/ui/workspace to restore the cookie, then reload.

const STORAGE_KEY = "petbox.lastWorkspace";

function readCookie(name: string): string | null {
	const target = `${name}=`;
	for (const chunk of document.cookie.split(";")) {
		const trimmed = chunk.trim();
		if (trimmed.startsWith(target)) return decodeURIComponent(trimmed.slice(target.length));
	}
	return null;
}

function getAntiforgeryToken(): string {
	const input = document.querySelector<HTMLInputElement>('input[name="__RequestVerificationToken"]');
	return input?.value ?? "";
}

export function initWorkspacePersistence(): void {
	const metaCurrent = document.querySelector<HTMLMetaElement>('meta[name="yb-current-ws"]');
	const currentFromPage = metaCurrent?.content ?? null;
	const cookie = readCookie("yb_ws");

	if (cookie) {
		try {
			localStorage.setItem(STORAGE_KEY, cookie);
		} catch {
			/* private mode */
		}
		return;
	}

	if (currentFromPage) {
		try {
			localStorage.setItem(STORAGE_KEY, currentFromPage);
		} catch {
			/* private mode */
		}
	}

	let remembered: string | null = null;
	try {
		remembered = localStorage.getItem(STORAGE_KEY);
	} catch {
		remembered = null;
	}
	if (!remembered || remembered === currentFromPage) return;

	const token = getAntiforgeryToken();
	if (!token) return;

	const body = new URLSearchParams();
	body.set("ws", remembered);
	body.set("returnUrl", window.location.pathname + window.location.search);
	body.set("__RequestVerificationToken", token);

	void fetch("/api/ui/workspace", {
		method: "POST",
		body,
		headers: { "Content-Type": "application/x-www-form-urlencoded" },
		credentials: "same-origin",
		redirect: "manual",
	}).then(() => {
		window.location.reload();
	});
}
