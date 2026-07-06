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
import { PROMPT_RAG_DEFAULTS, type PromptRagConfig, resolveProject } from "./registry.ts";

const FETCH_TIMEOUT_MS = 6000;
const MAX_CANDIDATES = PROMPT_RAG_DEFAULTS.cap; // default per-prompt lookup cap; tunable via per-project config
const REQUIRE_HYPHEN = PROMPT_RAG_DEFAULTS.requireHyphen; // default: identifiers must contain a hyphen

// Self-audit (best-effort). A dedicated petbox named log receives ONE record per ENABLED-project
// invocation so injection-rate (= injected / all enabled invocations) and precision can be
// measured. The write happens AFTER the pointer stdout, has its OWN short timeout, and swallows
// EVERY failure — so it can never change the injected output, the exit code, or hang the turn.
const AUDIT_LOG = "prompt-rag-audit";
const AUDIT_TIMEOUT_MS = 1500; // short + independent of FETCH_TIMEOUT_MS: bounds the worst-case tail
const AUDIT_SERVICE_KEY = "prompt-rag-hook"; // X-Service-Key emitter tag on the audit records

type HookInput = {
  cwd?: string;
  prompt?: unknown;
  prompt_text?: unknown;
  user_prompt?: unknown;
  // CC's UserPromptSubmit stdin carries a session marker; spelling has varied, accept both.
  session_id?: unknown;
  sessionId?: unknown;
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
// requireHyphen=true (default): a slug MUST contain a hyphen (skips ordinary prose words). The
// relaxed variant (requireHyphen=false) additionally accepts single-segment lowercase tokens, for
// projects whose node keys are single words — at the cost of more (still exact-join) lookups.
const SLUG_RE_HYPHEN = /\b[a-z][a-z0-9]*(?:-[a-z0-9]+)+\b/g;
const SLUG_RE_ANY = /\b[a-z][a-z0-9]*(?:-[a-z0-9]+)*\b/g;
const NODEID_RE = /\b[0-9a-f]{32}\b/g;

// Extract deduped identifier candidates from a prompt, capped at `cap`. Pure/deterministic.
// `cap` and `requireHyphen` come from per-project config (registry), defaulting to the module
// constants so existing callers keep the original behavior.
export function extractCandidates(
  prompt: string,
  cap: number = MAX_CANDIDATES,
  requireHyphen: boolean = REQUIRE_HYPHEN,
): string[] {
  if (typeof prompt !== "string" || prompt.length === 0) return [];
  const seen = new Set<string>();
  const out: string[] = [];
  const push = (tok: string) => {
    if (!seen.has(tok)) {
      seen.add(tok);
      out.push(tok);
    }
  };
  const slugRe = requireHyphen ? SLUG_RE_HYPHEN : SLUG_RE_ANY;
  // NodeIds first (most specific), then slugs. A 32-hex NodeId matches the hyphen-required slug RE
  // only if it contained a hyphen, which it can't; under the relaxed RE a NodeId would match, but
  // it is already deduped in via NODEID_RE first, so the two sets stay disjoint either way.
  for (const m of prompt.matchAll(NODEID_RE)) push(m[0]);
  for (const m of prompt.matchAll(slugRe)) push(m[0]);
  const n = Number.isFinite(cap) && cap > 0 ? Math.floor(cap) : MAX_CANDIDATES;
  return out.slice(0, n);
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
export type ExtractOpts = { cap?: number; requireHyphen?: boolean };

// The full result of a resolve pass: the rendered pointer text PLUS the raw signals the audit
// record needs (how many identifiers were extracted, which nodes matched). `text` is byte-for-byte
// what buildInjection returns, so callers that only want the injection are unaffected.
export type InjectionResult = {
  text: string;
  hits: TaskHit[];
  candidateCount: number;
};

export async function buildInjectionDetailed(
  prompt: string,
  resolve: Resolver,
  opts: ExtractOpts = {},
): Promise<InjectionResult> {
  const candidates = extractCandidates(prompt, opts.cap, opts.requireHyphen);
  if (candidates.length === 0) return { text: "", hits: [], candidateCount: 0 };
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
  return { text: renderInjection(hits), hits, candidateCount: candidates.length };
}

// Thin back-compat wrapper: the injection text only (unchanged signature/behavior).
export async function buildInjection(
  prompt: string,
  resolve: Resolver,
  opts: ExtractOpts = {},
): Promise<string> {
  return (await buildInjectionDetailed(prompt, resolve, opts)).text;
}

// Resolve the effective extraction tolerances from a project's prompt-RAG config, filling any
// unset field from the module defaults (the same defaults wire.ts writes on --prompt-rag).
export function tolerancesOf(promptRag?: PromptRagConfig): ExtractOpts {
  return {
    cap: promptRag?.cap ?? MAX_CANDIDATES,
    requireHyphen: promptRag?.requireHyphen ?? REQUIRE_HYPHEN,
  };
}

// Per-project gate + build in one testable step: when the project's prompt-RAG is absent/disabled,
// inject NOTHING ("") regardless of the prompt; when enabled, extract+resolve using the project's
// tolerances. This is exactly what the hook's main() does around the network call.
export async function buildInjectionForProject(
  prompt: string,
  promptRag: PromptRagConfig | undefined,
  resolve: Resolver,
): Promise<string> {
  if (!promptRag?.enabled) return "";
  return buildInjection(prompt, resolve, tolerancesOf(promptRag));
}

// ---- self-audit (best-effort) --------------------------------------------------------------

// The structured audit record — ONE per enabled-project invocation. `injected` and `matchCount`
// are redundant by construction (injected === matchCount > 0), but the eval reads both directly:
// injection-rate = count(injected=true) / count(all records); precision = sample `matched`, judge.
export type AuditRecord = {
  injected: boolean;
  candidateCount: number;
  matchCount: number;
  matched: string[]; // `board/key` of each injected node
  promptLen: number;
  sessionId?: string;
  timestamp: string; // ISO 8601
};

// PURE builder (no I/O): hook input + resolved nodes → the audit object. Unit-testable directly.
export function buildAuditRecord(
  prompt: string,
  candidateCount: number,
  hits: TaskHit[],
  sessionId?: string,
  now: Date = new Date(),
): AuditRecord {
  const rec: AuditRecord = {
    injected: hits.length > 0,
    candidateCount,
    matchCount: hits.length,
    matched: hits.map((h) => `${h.board}/${h.key}`),
    promptLen: typeof prompt === "string" ? prompt.length : 0,
    timestamp: now.toISOString(),
  };
  if (sessionId) rec.sessionId = sessionId;
  return rec;
}

// Serialize an audit record to ONE CLEF (Seq/Serilog compact) NDJSON line: `@t` timestamp (the one
// field the server's CLEF parser requires — CleFParser.cs:29), a rendered `@m` message, and every
// audit field as a top-level structured property (queryable via KQL). Pure/deterministic.
export function auditToClefLine(rec: AuditRecord): string {
  const clef: Record<string, unknown> = {
    "@t": rec.timestamp,
    "@m": `prompt-rag: injected=${rec.injected} candidates=${rec.candidateCount} matches=${rec.matchCount}`,
    "@l": "Information",
    injected: rec.injected,
    candidateCount: rec.candidateCount,
    matchCount: rec.matchCount,
    matched: rec.matched,
    promptLen: rec.promptLen,
  };
  if (rec.sessionId) clef.sessionId = rec.sessionId;
  return JSON.stringify(clef);
}

// Fire-and-forget audit POST to the path-based CLEF ingest route (POST
// /api/ingest/{project}/{log}/clef — LogApi.cs:30; reuses the hook's X-Api-Key, X-Service-Key tags
// the emitter). TOTAL: any failure (network, 404 missing log, 403 scope, timeout, 5xx) is swallowed
// and the response is never inspected — a SINGLE bounded attempt, so it can neither 404-loop nor
// hang. Callers MUST invoke this only AFTER writing the pointer stdout, so a failed/slow audit can
// never change the injected output or the exit code.
async function postAudit(
  target: { baseUrl: string; apiKey: string; project: string },
  logName: string,
  rec: AuditRecord,
): Promise<void> {
  try {
    const url = `${target.baseUrl}/api/ingest/${target.project}/${logName}/clef`;
    await fetch(url, {
      method: "POST",
      headers: {
        "X-Api-Key": target.apiKey,
        "X-Service-Key": AUDIT_SERVICE_KEY,
        "Content-Type": "application/vnd.serilog.clef",
      },
      body: auditToClefLine(rec),
      signal: AbortSignal.timeout(AUDIT_TIMEOUT_MS),
    });
  } catch {
    // best-effort: swallow EVERYTHING (a global prompt hook must never surface an audit failure).
  }
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

// Best-effort session marker from the hook stdin (undefined when absent) — a precision-audit
// grouping key, never required for the record to be valid.
function sessionIdOf(j: HookInput): string | undefined {
  for (const v of [j.session_id, j.sessionId]) {
    if (typeof v === "string" && v.length > 0) return v;
  }
  return undefined;
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

    // SECOND guard: per-project registry gate. The global hook fires for EVERY registered project;
    // prompt-RAG only runs where it was explicitly enabled (`wire <proj> --prompt-rag`). Absent or
    // disabled config → silent no-op: no output, no network. (step 1: client-side registry gate.)
    if (!resolved.promptRag?.enabled) return;

    // Per-project tolerances, defaulting to PROMPT_RAG_DEFAULTS when a field is unset in config.
    const opts = tolerancesOf(resolved.promptRag);

    const prompt = promptOf(j);
    const sessionId = sessionIdOf(j);
    const auditTarget = { baseUrl: resolved.baseUrl, apiKey: resolved.apiKey, project: resolved.project };

    // Cheap pre-check: no identifier-shaped tokens → nothing to inject and NO MCP call. This is an
    // enabled-project invocation nonetheless, so it still belongs in the denominator: emit a
    // zero-match audit (best-effort, after the — empty — stdout) then return. stdout stays empty,
    // byte-identical to today.
    if (extractCandidates(prompt, opts.cap, opts.requireHyphen).length === 0) {
      await postAudit(auditTarget, AUDIT_LOG, buildAuditRecord(prompt, 0, [], sessionId));
      return;
    }

    const client = await connectMcp({
      baseUrl: resolved.baseUrl,
      apiKey: resolved.apiKey,
      timeoutMs: FETCH_TIMEOUT_MS,
    });
    // petbox unreachable → no pointers, and the audit POST (same host) could only fail; skip it so
    // we don't add the audit timeout to a turn where petbox is already down. Silence, as today.
    if (!client) return;

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

    // Compute pointers → print to stdout FIRST → THEN best-effort audit. Order is load-bearing: the
    // audit must never precede or gate the injected output.
    const result = await buildInjectionDetailed(prompt, resolver, opts);
    if (result.text) process.stdout.write(result.text);
    await postAudit(
      auditTarget,
      AUDIT_LOG,
      buildAuditRecord(prompt, result.candidateCount, result.hits, sessionId),
    );
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
