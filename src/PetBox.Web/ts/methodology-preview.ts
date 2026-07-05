// Methodology-editor FSM preview: renders every kind/workflow-block of the definition the
// admin editor page embeds as a JSON island, reusing the SVG workflow-graph renderer the
// per-type board modal uses (workflow-viz.ts renderWorkflow — its pure, Razor-agnostic
// contract). Inline, no modal: the editor wants all graphs visible at once.

import { type WorkflowGraph, renderWorkflow } from "./workflow-viz";

// One definition kind's workflow surface (the WorkflowGraphJson.SerializeMany element —
// the same {kind, blocks} doc shape the board modal reads, arrayed per kind).
// `effectNotes` carries the kind's cross-board transition effects as pre-phrased server
// sentences (MethodologyGuide phrasing) — automation with no edge to draw.
interface PreviewDoc {
	readonly kind: string;
	readonly blocks: ReadonlyArray<WorkflowGraph>;
	readonly effectNotes?: ReadonlyArray<string>;
}

export function initMethodologyPreview(): void {
	const island = document.querySelector<HTMLScriptElement>("[data-testid='methodology-preview-data']");
	const host = document.querySelector<HTMLElement>("[data-testid='methodology-preview']");
	if (!island || !host) return;

	let docs: ReadonlyArray<PreviewDoc>;
	try {
		docs = JSON.parse(island.textContent ?? "[]") as ReadonlyArray<PreviewDoc>;
	} catch {
		return;
	}

	host.replaceChildren();
	for (const doc of docs) {
		const heading = document.createElement("h3");
		heading.className = "font-mono font-semibold text-sm mt-4";
		heading.setAttribute("data-testid", "methodology-preview-kind");
		heading.textContent = doc.kind;
		host.appendChild(heading);

		for (const block of doc.blocks ?? []) {
			const label = document.createElement("div");
			label.className = "text-xs opacity-60 font-mono mt-1";
			label.textContent = block.types.join(" · ");
			host.appendChild(label);

			const graph = document.createElement("div");
			graph.className = "overflow-x-auto border border-base-300 rounded-lg bg-base-100 p-2 mt-1";
			graph.setAttribute("data-testid", "methodology-preview-graph");
			host.appendChild(graph);
			renderWorkflow(graph, block);
		}

		// Kind-level transition effects: cross-board automation with no edge on the graph —
		// listed as prose under the kind's blocks (server-phrased, one sentence per effect).
		const notes = doc.effectNotes ?? [];
		if (notes.length > 0) {
			const effects = document.createElement("div");
			effects.className = "text-xs opacity-80 mt-1";
			effects.setAttribute("data-testid", "methodology-preview-effects");
			const title = document.createElement("span");
			title.className = "font-semibold";
			title.textContent = "Effects: ";
			effects.appendChild(title);
			const list = document.createElement("ul");
			list.className = "list-disc pl-5";
			for (const note of notes) {
				const li = document.createElement("li");
				li.textContent = note;
				list.appendChild(li);
			}
			effects.appendChild(list);
			host.appendChild(effects);
		}
	}
}
