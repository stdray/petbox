// Claude Code UserPromptSubmit hook (global, OPT-IN) — safe prompt-context injection.
//
// Design contract (idea wire-loop-instrumentation, Нога B; spec prompt-context-injection):
// the owner has a NEGATIVE history with RAG-as-dump (fuzzy matches → context bloat → S/N
// collapse → rot). So this hook is the OPPOSITE of a push-dump. It injects ONLY on a confident,
// deterministic EXACT match and is otherwise SILENT — zero fixed per-turn noise:
//
//   1. Pointers, not bodies — it emits a task node's key/board/status/title (a heading + an
//      `expand:` command), never the body. The agent decides what to pull.
//   2. Deterministic exact-join, not semantics — it extracts identifier-shaped tokens from the
//      prompt (hyphenated node slugs, 32-hex NodeIds) with a regex and resolves each against the
//      project's task nodes via an EXACT `keys` lookup. No fuzzy/semantic/embedding matching.
//   3. If nothing matches confidently → it prints NOTHING and exits 0 (the prompt is unchanged).
//
// UserPromptSubmit contract (CC docs: hooks-guide.md / hooks.md): the hook reads a JSON object on
// stdin carrying the user's prompt + cwd; on exit 0, whatever it prints to STDOUT is added to the
// model's context for that turn (this event is one of the few where stdout becomes context). We
// use plain-stdout injection (same pattern as the SessionStart pull-memory hook). The prompt field
// name has varied across CC versions, so we read it defensively (`prompt` | `prompt_text` | …).
//
// Best-effort and TOTAL: any failure (not a registered project, petbox unreachable, bad stdin)
// degrades to silence and we ALWAYS exit 0 — a context hook must never break the user's prompt.
//
// Plain TS for native node type-stripping: no enum/namespace/parameter-properties, zero deps.

import { argv } from "node:process";
import { pathToFileURL } from "node:url";
import { connectMcp } from "./mcp-client.ts";
import { resolveProject } from "./registry.ts";

const FETCH_TIMEOUT_MS = 6000;
const MAX_CANDIDATES = 8; // bound the per-prompt lookups; extra identifiers are ignored

type HookInput = {
  cwd?: string;
  prompt?: unknown;
  prompt_text?: unknown;
  user_prompt?: unknown;
};

// A resolved task-node pointer — headings/keys only, never the body.
export type TaskHit = {
  key: string;
  board: string;
  status: string;
  type?: string;
  title: string;
  nodeId?: string;
};

// A token → node resolver. Returns the node for an EXACT slug/NodeId match, else null.
export type Resolver = (token: string) => Promise<TaskHit | null>;

// Identifier-shaped tokens we are willing to exact-join. Both are deliberately narrow so the
// lookup set stays small and confident; a token that isn't a real node simply misses (→ silence),
// so breadth costs at most a lookup, never noise.
//   - node slug: lowercase, MUST contain a hyphen (petbox slugs are multi-segment, e.g.
//     `prompt-rag-hook`). Requiring a hyphen skips ordinary prose words.
//   - NodeId: exactly 32 lowercase hex chars.
const SLUG_RE = /\b[a-z][a-z0-9]*(?:-[a-z0-9]+)+\b/g;
const NODEID_RE = /\b[0-9a-f]{32}\b/g;

// Extract deduped identifier candidates from a prompt, capped at MAX_CANDIDATES. Pure/deterministic.
export function extractCandidates(prompt: string): string[] {
  if (typeof prompt !== "string" || prompt.length === 0) return [];
  const seen = new Set<string>();
  const out: string[] = [];
  const push = (tok: string) => {
    if (!seen.has(tok)) {
      seen.add(tok);
      out.push(tok);
    }
  };
  // NodeIds first (most specific), then slugs. A 32-hex NodeId also matches SLUG_RE only if it
  // contained a hyphen, which it can't — so the two sets are disjoint.
  for (const m of prompt.matchAll(NODEID_RE)) push(m[0]);
  for (const m of prompt.matchAll(SLUG_RE)) push(m[0]);
  return out.slice(0, MAX_CANDIDATES);
}

function clip(s: string, n: number): string {
  const t = (s ?? "").replace(/\s+/g, " ").trim();
  return t.length > n ? t.slice(0, n - 1) + "…" : t;
}

// Render the injection block from resolved hits. Returns "" when there are none (→ inject nothing).
// Pointers only: one line per node = a heading (board/key + title + status) plus the exact command
// to expand it. Never the body.
export function renderInjection(hits: TaskHit[], toolPrefix = "mcp__petbox__"): string {
  if (hits.length === 0) return "";
  const lines = hits.map((h) => {
    const t = h.type ? ` ${h.type}` : "";
    return `- ${h.board}/${h.key} — "${clip(h.title, 120)}" [${h.status}${t}] · expand: ${toolPrefix}tasks_node_get(board="${h.board}", node="${h.key}")`;
  });
  return (
    "PetBox exact-match pointers (deterministic: these identifiers from your prompt are real task nodes). " +
    "Pointers only — pull the full body of whichever is relevant, ignore the rest:\n" +
    lines.join("\n")
  );
}

// Orchestrate: extract candidates → resolve each exactly → dedupe by node → render. Pure w.r.t.
// the injected `resolve` fn, so it is unit-testable without any network. Never throws.
export async function buildInjection(prompt: string, resolve: Resolver): Promise<string> {
  const candidates = extractCandidates(prompt);
  if (candidates.length === 0) return "";
  const hits: TaskHit[] = [];
  const seenNodes = new Set<string>();
  for (const tok of candidates) {
    let hit: TaskHit | null = null;
    try {
      hit = await resolve(tok);
    } catch {
      hit = null; // a single lookup failure must not abort the rest
    }
    if (!hit) continue;
    const dedupeKey = hit.nodeId || `${hit.board}/${hit.key}`;
    if (seenNodes.has(dedupeKey)) continue;
    seenNodes.add(dedupeKey);
    hits.push(hit);
  }
  return renderInjection(hits);
}

// Pull the first node out of a tasks_search structuredContent payload ({ nodes: [...] }), mapped
// to a TaskHit — or null when the shape is unexpected/empty.
function firstNode(structured: unknown): TaskHit | null {
  const nodes = (structured as any)?.nodes;
  if (!Array.isArray(nodes) || nodes.length === 0) return null;
  const n = nodes[0];
  if (!n || typeof n.key !== "string" || typeof n.board !== "string") return null;
  return {
    key: n.key,
    board: n.board,
    status: typeof n.status === "string" ? n.status : "?",
    type: typeof n.type === "string" ? n.type : undefined,
    title: typeof n.title === "string" ? n.title : n.key,
    nodeId: typeof n.nodeId === "string" ? n.nodeId : undefined,
  };
}

function readStdin(): Promise<string> {
  return new Promise((resolve) => {
    let buf = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (c) => (buf += c));
    process.stdin.on("end", () => resolve(buf));
    process.stdin.on("error", () => resolve(buf));
  });
}

function promptOf(j: HookInput): string {
  // CC's prompt field name has varied across versions; accept the known spellings.
  for (const v of [j.prompt, j.prompt_text, j.user_prompt]) {
    if (typeof v === "string" && v.length > 0) return v;
  }
  return "";
}

async function main(): Promise<void> {
  try {
    const raw = await readStdin();
    let j: HookInput;
    try {
      j = JSON.parse(raw);
    } catch {
      return; // no parseable stdin → nothing to do
    }

    // FIRST guard: not a registered project → silent no-op (mirrors push-session.ts).
    const resolved = resolveProject(typeof j.cwd === "string" ? j.cwd : "");
    if (!resolved) return;

    const prompt = promptOf(j);
    // Cheap pre-check: no identifier-shaped tokens → emit nothing WITHOUT any network call.
    if (extractCandidates(prompt).length === 0) return;

    const client = await connectMcp({
      baseUrl: resolved.baseUrl,
      apiKey: resolved.apiKey,
      timeoutMs: FETCH_TIMEOUT_MS,
    });
    if (!client) return; // petbox unreachable → silence

    const resolver: Resolver = async (token) => {
      // Exact-join: `keys` resolves a slug OR a 32-hex NodeId to a single node (terminal nodes
      // included), and returns a tool error on a miss/ambiguous ref → connectMcp maps that to null.
      const sc = await client.call("tasks_search", {
        projectKey: resolved.project,
        keys: [token],
        bodyLen: 0,
      });
      return firstNode(sc);
    };

    const out = await buildInjection(prompt, resolver);
    if (out) process.stdout.write(out);
  } catch {
    // best-effort: never break the user's prompt
  }
}

// Run main() ONLY when executed directly as the hook (node prompt-rag.ts) — never when the module
// is imported (e.g. by prompt-rag.test.ts). Importing must not start main(), which blocks forever
// on stdin. (wire.ts documents the same import-safety hazard for its own top-level main().)
if (argv[1] && import.meta.url === pathToFileURL(argv[1]).href) {
  main().finally(() => process.exit(0));
}
