// Config page interactivity: secret reveal (10s window), clipboard copy.

const REVEAL_WINDOW_MS = 10_000;

function getAntiforgeryToken(): string {
	const el = document.getElementById("__requestVerificationToken") as HTMLInputElement | null;
	return el?.value ?? "";
}

function getWorkspaceKey(): string {
	const meta = document.querySelector('meta[name="config-workspace-key"]') as HTMLMetaElement | null;
	return meta?.content ?? "$system";
}

async function revealSecret(bindingId: string): Promise<void> {
	const cellSpan = document.querySelector<HTMLElement>(
		`[data-testid="config-secret-masked"][data-binding-id="${bindingId}"]`,
	);
	if (!cellSpan) return;

	const original = cellSpan.textContent ?? "••••••";
	const button = document.querySelector<HTMLButtonElement>(
		`[data-testid="config-secret-reveal"][data-binding-id="${bindingId}"]`,
	);
	if (button) button.disabled = true;

	try {
		const ws = getWorkspaceKey();
		// Path is /ui/{ws}/config — NOT /ui/config/{ws} (that hit the project route and 200'd, never revealing).
		const url = `/ui/${encodeURIComponent(ws)}/config?handler=Reveal&id=${encodeURIComponent(bindingId)}`;
		const resp = await fetch(url, {
			method: "POST",
			headers: {
				RequestVerificationToken: getAntiforgeryToken(),
				"X-Requested-With": "fetch",
			},
		});

		if (!resp.ok) {
			cellSpan.textContent = `error ${resp.status}`;
			setTimeout(() => {
				cellSpan.textContent = original;
				if (button) button.disabled = false;
			}, 2_000);
			return;
		}

		const body = (await resp.json()) as { plaintext?: string };
		const plain = body.plaintext ?? "(empty)";
		cellSpan.textContent = plain;
		cellSpan.dataset["revealed"] = "true";

		setTimeout(() => {
			cellSpan.textContent = original;
			cellSpan.dataset["revealed"] = "false";
			if (button) button.disabled = false;
		}, REVEAL_WINDOW_MS);
	} catch (_err) {
		cellSpan.textContent = "(error)";
		setTimeout(() => {
			cellSpan.textContent = original;
			if (button) button.disabled = false;
		}, 2_000);
	}
}

async function copyToClipboard(value: string, trigger: HTMLElement): Promise<void> {
	try {
		await navigator.clipboard.writeText(value);
		const label = trigger.textContent ?? "";
		trigger.textContent = "✓";
		setTimeout(() => {
			trigger.textContent = label;
		}, 1_000);
	} catch {
		trigger.textContent = "✗";
	}
}

export function initConfigPage(): void {
	document.addEventListener("click", (evt) => {
		const target = evt.target as HTMLElement | null;
		if (!target) return;

		const revealBtn = target.closest<HTMLElement>('[data-testid="config-secret-reveal"]');
		if (revealBtn) {
			const id = revealBtn.dataset["bindingId"];
			if (id) {
				evt.preventDefault();
				void revealSecret(id);
			}
			return;
		}

		const copyTarget = target.closest<HTMLElement>("[data-copy]");
		if (copyTarget) {
			const value = copyTarget.dataset["copy"] ?? "";
			evt.preventDefault();
			void copyToClipboard(value, copyTarget);
		}
	});
}
