// Per-type workflow (FSM) graph visualization — hand-rolled SVG, no diagram library. Renders a
// board block's state machine as a layered graph: statuses are nodes (open vs terminal styled
// apart), transitions are directed edges, gates are compact edge labels. The render function takes
// PURE JSON + a container and is Razor-agnostic on purpose — it is reused later as the methodology
// -editor live preview. `initWorkflowViz` wires the board / node-detail pages: it reads the JSON
// island the server embeds and opens a daisyUI modal for the type a "View workflow" trigger names.

export type StatusKind = "Open" | "TerminalOk" | "TerminalCancel";

export interface WorkflowStatusNode {
	readonly slug: string;
	readonly name: string;
	readonly kind: StatusKind;
}

export interface WorkflowEdge {
	readonly from: string;
	readonly to: string;
	readonly requiresApproval?: boolean;
	readonly requiresReason?: boolean;
	readonly preconditionArtifact?: string | null;
	// True when the transition declares a pre-transition checklist (convention gate).
	readonly checklist?: boolean;
}

// One state machine: the set of type slugs sharing it + its statuses and transitions. The pure
// contract the render consumes — no server coupling (a block IS a graph plus its type labels).
export interface WorkflowGraph {
	readonly types: ReadonlyArray<string>;
	readonly statuses: ReadonlyArray<WorkflowStatusNode>;
	readonly transitions: ReadonlyArray<WorkflowEdge>;
}

// The board's whole workflow surface as embedded by the server (BoardWorkflowView): one or more
// blocks (a preset kind has exactly one; a custom methodology kind may split types across several).
interface WorkflowDoc {
	readonly kind: string;
	readonly blocks: ReadonlyArray<WorkflowGraph>;
}

const SVG_NS = "http://www.w3.org/2000/svg";

// Geometry (px). Graphs are 5–7 nodes, so a fixed box + generous gaps keeps labels legible.
const NODE_W = 132;
const NODE_H = 40;
const COL_GAP = 78;
const ROW_GAP = 22;
const PAD = 20;
const CURVE = 20; // perpendicular bow for the two edges of a bidirectional pair, so they don't overlap

// Theme-aware colors: daisyUI oklch CSS variables (the same `oklch(var(--x))` form the app's CSS
// uses), so the graph tracks whatever theme is active without a JS theme probe.
const EDGE_COLOR = "oklch(var(--bc) / 0.45)";
const LABEL_BG = "oklch(var(--b1))";
const LABEL_FG = "oklch(var(--bc) / 0.8)";

interface NodeStyle {
	readonly fill: string;
	readonly stroke: string;
	readonly text: string;
}

// Open = neutral box; TerminalOk = success accent; TerminalCancel = a muted error accent.
function nodeStyle(kind: StatusKind): NodeStyle {
	switch (kind) {
		case "TerminalOk":
			return { fill: "oklch(var(--su) / 0.18)", stroke: "oklch(var(--su))", text: "oklch(var(--bc))" };
		case "TerminalCancel":
			return { fill: "oklch(var(--er) / 0.14)", stroke: "oklch(var(--er) / 0.6)", text: "oklch(var(--bc) / 0.75)" };
		default:
			return { fill: "oklch(var(--b2))", stroke: "oklch(var(--bc) / 0.35)", text: "oklch(var(--bc))" };
	}
}

function el<K extends keyof SVGElementTagNameMap>(
	tag: K,
	attrs: Record<string, string | number>,
): SVGElementTagNameMap[K] {
	const node = document.createElementNS(SVG_NS, tag);
	for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, String(v));
	return node;
}

// How far a column-spanning edge must bow off the straight line to clear the node(s) it would
// otherwise cross: enough to pass one node row (2-col skip) or arc over two (3+). 0 for adjacent
// columns (their straight line runs through the empty inter-column gap). Also sizes the vertical
// margin reserved so the outermost bows never clip the viewBox.
const bowDeviation = (span: number): number => (span < 2 ? 0 : span === 2 ? NODE_H / 2 + 14 : NODE_H + 8);

const norm = (s: string): string => s.toLowerCase();

const isTerminal = (s: WorkflowStatusNode): boolean => s.kind === "TerminalOk" || s.kind === "TerminalCancel";

function gateLabel(t: WorkflowEdge): string {
	const parts: string[] = [];
	if (t.requiresApproval) parts.push("approve");
	if (t.requiresReason) parts.push("reason");
	if (t.preconditionArtifact) parts.push(`artifact:${t.preconditionArtifact}`);
	if (t.checklist) parts.push("checklist");
	return parts.join(" ");
}

interface Placed {
	readonly node: WorkflowStatusNode;
	col: number;
	row: number;
	x: number;
	y: number;
}

interface Rect {
	readonly x: number;
	readonly y: number;
	readonly w: number;
	readonly h: number;
}

const intersects = (a: Rect, b: Rect): boolean =>
	a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;

// Point on the quadratic P1→C→P2 at parameter t.
const qPoint = (
	p1: { x: number; y: number },
	c: { x: number; y: number },
	p2: { x: number; y: number },
	t: number,
) => ({
	x: (1 - t) * (1 - t) * p1.x + 2 * (1 - t) * t * c.x + t * t * p2.x,
	y: (1 - t) * (1 - t) * p1.y + 2 * (1 - t) * t * c.y + t * t * p2.y,
});

// Where a gate label may sit on its edge: the midpoint first, then sliding toward either
// end. A dense graph (free-transition presets, multi-row columns) drops midpoints inside
// node boxes and on sibling labels — each candidate is collision-checked against every
// node and every already-placed label, and the first clean spot wins (midpoint fallback
// when the whole edge is crowded). Order matters: near-middle candidates first, so labels
// stay visually attached to their edge's center when possible.
const LABEL_T_CANDIDATES = [0.5, 0.42, 0.58, 0.34, 0.66, 0.26, 0.74, 0.18, 0.82];

const LABEL_H = 15;

// Pick the label's center: the first collision-free candidate along the edge, clamped
// into the viewBox so a label near the border never clips.
function placeLabel(
	labelW: number,
	pointAt: (t: number) => { x: number; y: number },
	obstacles: ReadonlyArray<Rect>,
	placedLabels: ReadonlyArray<Rect>,
	viewWidth: number,
): { x: number; y: number } {
	const clampX = (x: number): number =>
		Math.min(Math.max(x, labelW / 2 + 2), Math.max(viewWidth - labelW / 2 - 2, labelW / 2 + 2));
	let fallback: { x: number; y: number } | null = null;
	for (const t of LABEL_T_CANDIDATES) {
		const p = pointAt(t);
		const center = { x: clampX(p.x), y: p.y };
		fallback ??= center;
		const rect: Rect = { x: center.x - labelW / 2, y: center.y - LABEL_H / 2, w: labelW, h: LABEL_H };
		const clear = !obstacles.some((o) => intersects(rect, o)) && !placedLabels.some((o) => intersects(rect, o));
		if (clear) return center;
	}
	return fallback ?? pointAt(0.5);
}

// The point where the ray from a node's center toward (tx,ty) meets that node's border — so an
// edge touches the box edge, not the center, and the arrowhead lands cleanly.
function borderPoint(cx: number, cy: number, tx: number, ty: number): { x: number; y: number } {
	const dx = tx - cx;
	const dy = ty - cy;
	if (dx === 0 && dy === 0) return { x: cx, y: cy };
	const scale = 1 / Math.max(Math.abs(dx) / (NODE_W / 2), Math.abs(dy) / (NODE_H / 2));
	return { x: cx + dx * scale, y: cy + dy * scale };
}

// Render `graph` into `container` as an SVG FSM. Layout is layered: BFS distance from the initial
// status (statuses[0]) sets each open status's column; terminal statuses are pulled to a final
// column so "done/cancelled" read as the right edge of the flow.
export function renderWorkflow(container: HTMLElement, graph: WorkflowGraph): void {
	container.replaceChildren();
	const first = graph.statuses[0];
	if (!first) return;
	const initial = norm(first.slug);

	// Forward adjacency + BFS depth from the initial status.
	const adj = new Map<string, string[]>();
	for (const t of graph.transitions) {
		const from = norm(t.from);
		const list = adj.get(from) ?? [];
		list.push(norm(t.to));
		adj.set(from, list);
	}
	const depth = new Map<string, number>([[initial, 0]]);
	const queue = [initial];
	for (let head = 0; head < queue.length; head++) {
		const cur = queue[head];
		if (cur === undefined) break;
		const d = depth.get(cur) ?? 0;
		for (const next of adj.get(cur) ?? []) {
			if (!depth.has(next)) {
				depth.set(next, d + 1);
				queue.push(next);
			}
		}
	}

	// Terminal statuses to the last layer (one past the deepest open status).
	let openMax = 0;
	for (const s of graph.statuses) {
		if (!isTerminal(s)) openMax = Math.max(openMax, depth.get(norm(s.slug)) ?? 0);
	}
	const layerOf = new Map<string, number>();
	for (const s of graph.statuses) {
		const slug = norm(s.slug);
		layerOf.set(slug, isTerminal(s) ? openMax + 1 : (depth.get(slug) ?? 0));
	}

	// Distinct layers → contiguous column indices (collapses any gap the terminal pull left).
	const columnLayers = [...new Set(layerOf.values())].sort((a, b) => a - b);
	const colIndex = new Map<number, number>();
	columnLayers.forEach((l, i) => colIndex.set(l, i));

	// Place each status into its column, stacking in declaration order.
	const perCol = new Map<number, number>();
	const placed = new Map<string, Placed>();
	for (const s of graph.statuses) {
		const slug = norm(s.slug);
		const col = colIndex.get(layerOf.get(slug) ?? 0) ?? 0;
		const row = perCol.get(col) ?? 0;
		perCol.set(col, row + 1);
		placed.set(slug, { node: s, col, row, x: 0, y: 0 });
	}

	let maxRows = 0;
	for (const c of perCol.values()) maxRows = Math.max(maxRows, c);
	const contentH = maxRows * NODE_H + Math.max(0, maxRows - 1) * ROW_GAP;
	const colStride = NODE_W + COL_GAP;
	const rowStride = NODE_H + ROW_GAP;

	// Reserve top/bottom margin for the deepest edge bow (a spanning edge can arc above the top
	// row or below the bottom one), so nothing routes outside the viewBox.
	let maxSpan = 0;
	for (const t of graph.transitions) {
		const a = placed.get(norm(t.from));
		const b = placed.get(norm(t.to));
		if (a && b) maxSpan = Math.max(maxSpan, Math.abs(b.col - a.col));
	}
	const vMargin = bowDeviation(maxSpan);

	for (const p of placed.values()) {
		const colSize = perCol.get(p.col) ?? 1;
		const colH = colSize * NODE_H + Math.max(0, colSize - 1) * ROW_GAP;
		p.x = PAD + p.col * colStride;
		p.y = PAD + vMargin + (contentH - colH) / 2 + p.row * rowStride; // vertically center each column
	}

	const numCols = columnLayers.length;
	const width = PAD * 2 + numCols * NODE_W + Math.max(0, numCols - 1) * COL_GAP;
	const height = PAD * 2 + 2 * vMargin + contentH;

	const svg = el("svg", { viewBox: `0 0 ${width} ${height}`, width, height });
	svg.setAttribute("role", "img");
	svg.setAttribute("aria-label", `Workflow graph: ${graph.statuses.length} statuses`);
	svg.setAttribute("style", "max-width:100%;height:auto;");
	svg.dataset["testid"] = "workflow-svg";

	const defs = el("defs", {});
	const marker = el("marker", {
		id: "wf-arrow",
		viewBox: "0 0 10 10",
		refX: 9,
		refY: 5,
		markerWidth: 7,
		markerHeight: 7,
		orient: "auto-start-reverse",
	});
	marker.appendChild(el("path", { d: "M0,0 L10,5 L0,10 z", fill: EDGE_COLOR }));
	defs.appendChild(marker);
	svg.appendChild(defs);

	const edgeLayer = el("g", {});
	const labelLayer = el("g", {});
	const nodeLayer = el("g", {});

	// Node boxes are obstacles for gate labels; labels also avoid one another.
	const nodeRects: Rect[] = [...placed.values()].map((p) => ({ x: p.x, y: p.y, w: NODE_W, h: NODE_H }));
	const labelRects: Rect[] = [];

	const pairSet = new Set(graph.transitions.map((t) => `${norm(t.from)}|${norm(t.to)}`));
	for (const t of graph.transitions) {
		const a = placed.get(norm(t.from));
		const b = placed.get(norm(t.to));
		if (!a || !b) continue;
		const acx = a.x + NODE_W / 2;
		const acy = a.y + NODE_H / 2;
		const bcx = b.x + NODE_W / 2;
		const bcy = b.y + NODE_H / 2;
		const p1 = borderPoint(acx, acy, bcx, bcy);
		const p2 = borderPoint(bcx, bcy, acx, acy);

		// Both directions between the same pair bow apart via opposite perpendicular offsets;
		// a stable sign (slug order) keeps the split deterministic.
		const twoWay = pairSet.has(`${norm(t.to)}|${norm(t.from)}`);
		let off = twoWay ? (norm(t.from) < norm(t.to) ? CURVE : -CURVE) : 0;
		// An edge that skips a column would otherwise run straight through the node(s) in between
		// (and drop its gate label on top of one). Bow it clear: a quadratic deviates by off/2 at its
		// midpoint, so off = 2·bowDeviation. Arc toward the target's side (compared by VISUAL y —
		// columns are centered independently, so row indices don't map to height); level → downward.
		const dev = bowDeviation(Math.abs(b.col - a.col));
		if (dev > 0) {
			const dir = Math.abs(bcy - acy) < 1 ? 1 : Math.sign(bcy - acy);
			off += dir * 2 * dev;
		}
		const mx = (p1.x + p2.x) / 2;
		const my = (p1.y + p2.y) / 2;
		const vx = p2.x - p1.x;
		const vy = p2.y - p1.y;
		const len = Math.hypot(vx, vy) || 1;
		const cx = mx + (-vy / len) * off;
		const cy = my + (vx / len) * off;

		edgeLayer.appendChild(
			el("path", {
				d: `M ${p1.x} ${p1.y} Q ${cx} ${cy} ${p2.x} ${p2.y}`,
				fill: "none",
				stroke: EDGE_COLOR,
				"stroke-width": 1.5,
				"marker-end": "url(#wf-arrow)",
			}),
		);

		const label = gateLabel(t);
		if (label) {
			// Slide along the curve to the first spot that overlaps no node and no earlier
			// label (dense graphs drop midpoints inside node boxes), clamped into the view.
			const w = label.length * 5.9 + 8;
			const spot = placeLabel(w, (tt) => qPoint(p1, { x: cx, y: cy }, p2, tt), nodeRects, labelRects, width);
			labelRects.push({ x: spot.x - w / 2, y: spot.y - LABEL_H / 2, w, h: LABEL_H });
			labelLayer.appendChild(
				el("rect", { x: spot.x - w / 2, y: spot.y - LABEL_H / 2, width: w, height: LABEL_H, rx: 3, fill: LABEL_BG }),
			);
			const text = el("text", { x: spot.x, y: spot.y + 3.5, "text-anchor": "middle", "font-size": 10, fill: LABEL_FG });
			text.textContent = label;
			labelLayer.appendChild(text);
		}
	}

	for (const p of placed.values()) {
		const style = nodeStyle(p.node.kind);
		nodeLayer.appendChild(
			el("rect", {
				x: p.x,
				y: p.y,
				width: NODE_W,
				height: NODE_H,
				rx: 7,
				fill: style.fill,
				stroke: style.stroke,
				"stroke-width": 1.5,
			}),
		);
		const name = el("text", {
			x: p.x + NODE_W / 2,
			y: p.y + NODE_H / 2 + 4,
			"text-anchor": "middle",
			"font-size": 12,
			fill: style.text,
		});
		name.textContent = p.node.name;
		nodeLayer.appendChild(name);
		if (norm(p.node.slug) === initial) {
			// Initial-status marker, tucked top-left so it doesn't collide with the name.
			const badge = el("text", {
				x: p.x + 10,
				y: p.y + 13,
				"text-anchor": "middle",
				"font-size": 10,
				fill: style.text,
			});
			badge.textContent = "▶";
			nodeLayer.appendChild(badge);
		}
	}

	// Nodes over edges; gate labels on top of everything so they stay readable.
	svg.appendChild(edgeLayer);
	svg.appendChild(nodeLayer);
	svg.appendChild(labelLayer);
	container.appendChild(svg);
}

// Wire the "View workflow" modal on the board / node pages: read the embedded JSON island, and on a
// click of any [data-workflow-type] trigger open the daisyUI modal for the block whose types cover
// that type (falling back to the first block when the type is empty or unmatched).
export function initWorkflowViz(): void {
	const island = document.querySelector<HTMLScriptElement>("[data-testid='workflow-data']");
	const modal = document.querySelector<HTMLElement>("[data-testid='workflow-modal']");
	if (!island || !modal) return;

	let doc: WorkflowDoc;
	try {
		doc = JSON.parse(island.textContent ?? "{}") as WorkflowDoc;
	} catch {
		return;
	}
	const blocks = doc.blocks ?? [];
	const firstBlock = blocks[0];
	if (!firstBlock) return;

	const graphEl = modal.querySelector<HTMLElement>("[data-testid='workflow-graph']");
	const titleEl = modal.querySelector<HTMLElement>("[data-testid='workflow-modal-title']");
	const closeBtn = modal.querySelector<HTMLElement>("[data-testid='workflow-modal-close']");
	const backdrop = modal.querySelector<HTMLElement>("[data-testid='workflow-modal-backdrop']");
	if (!graphEl || !titleEl) return;

	const findBlock = (type: string): WorkflowGraph => {
		const key = type.toLowerCase();
		return blocks.find((b) => b.types.some((tp) => tp.toLowerCase() === key)) ?? firstBlock;
	};

	const close = (): void => {
		modal.classList.remove("modal-open");
		graphEl.replaceChildren();
	};

	const open = (type: string): void => {
		const block = findBlock(type);
		titleEl.textContent = `View workflow: ${block.types.join(" · ")}`;
		renderWorkflow(graphEl, block);
		modal.classList.add("modal-open");
	};

	document.addEventListener("click", (evt) => {
		const target = evt.target;
		if (!(target instanceof Element)) return;
		const trigger = target.closest<HTMLElement>("[data-workflow-type]");
		if (!trigger) return;
		evt.preventDefault();
		open(trigger.dataset["workflowType"] ?? "");
	});

	closeBtn?.addEventListener("click", close);
	backdrop?.addEventListener("click", close);
	document.addEventListener("keydown", (evt) => {
		if (evt.key === "Escape" && modal.classList.contains("modal-open")) close();
	});
}
