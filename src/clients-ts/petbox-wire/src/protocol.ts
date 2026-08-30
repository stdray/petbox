// Shared PetBox memory-protocol builder — the ONE implementation every SessionStart injector
// renders from (pull-memory.ts for Claude Code, opencode-plugin.ts for opencode,
// droid-pull-memory.ts for Factory Droid), so the protocol text can no longer drift between
// agents as hand-synced copies (spec: agent-wiring, wiring-single-source).
//
// The ONLY thing that varies per agent is how an MCP tool is named: pass a `tool` mapper that
// turns a bare verb (e.g. "memory_search") into that agent's fully-qualified tool name. Claude
// Code and Droid both expose petbox tools as `mcp__petbox__<verb>`; opencode as `petbox_<verb>`.
//
// Role-orchestration prescriptions (SPAWN by DEFAULT / fan-out / orchestrator mandate) are
// gated by harness capability spawn_subagents (definition-truthfulness / wiring-startup-symmetry).
// Memory protocol (search-before-rework, capture, tool names) is always emitted.
//
// The CC-only "resume/compact" suffix is opt-in via `opts.source` (Droid's SessionStart payload
// also carries a `source`, so it shares the same behavior). Any other source = no suffix.
//
// Plain TS for native node type-stripping: no enum/namespace/parameter-properties, zero deps.

import { DEFAULT_AGENT_DEFINITION, emittedRoleName, type AgentDefinition } from "./agent-definition.ts";
import { hasCapability } from "./harness-capabilities.ts";

export type ToolNamer = (verb: string) => string;

export type ProtocolOpts = {
  // The SessionStart `source` (Claude Code / Droid). "resume" | "compact" append a recall nudge;
  // anything else (or omitted) renders the base protocol only.
  source?: string;
  /**
   * Target harness id (claude-code | opencode | droid). Controls whether spawn/orchestrator
   * prescriptions are emitted. Injectors always pass it. Omitted / unknown → no spawn prose
   * (never invent a capability the harness does not declare).
   */
  harness?: string;
  /**
   * Portable agent definition to render the orchestrator self-intro notes from. Callers
   * resolve this the same way `apply` does — server fetch, then LKG cache, then the built-in
   * default (agent-def-fetch.ts's resolveAgentDefinitionWithLkg) — so the main-loop banner no
   * longer drifts from the per-role artifacts that already got server-authored notes. Omitted
   * → DEFAULT_AGENT_DEFINITION (never crash, never an empty banner).
   */
  definition?: AgentDefinition;
};

/** True when protocol may prescribe SPAWN workers / fan-out / orchestrator mandate. */
export function orchestrationPrescriptionsAllowed(harness: string | undefined): boolean {
  if (!harness) return false;
  return hasCapability(harness, "spawn_subagents");
}

function buildSelfIntro(allowSpawn: boolean, definition: AgentDefinition): string {
  if (allowSpawn) {
    const orch = definition.roles.find((r) => r.slug === "orchestrator");
    const notes =
      orch?.notes?.trim() ||
      "plan, decompose, delegate, review, triage. Prefer spawning workers over solo implementation.";
    // The literal spawn target string an orchestrator passes to Agent/Task — MUST be the same
    // computed, namespaced identity apply actually renders the worker role's file under
    // (emittedRoleName; chore: petbox-namespaced-agent-names). A hardcoded `worker` here would
    // be a THIRD source of truth that can drift from the emitted file/frontmatter name, which
    // is exactly the bug this rename set out to close.
    const workerRole = definition.roles.find((r) => r.slug === "worker");
    const workerName = emittedRoleName(workerRole ?? "worker");
    // The delegate-by-default rule, its threshold and the cost rationale behind it live ONLY in
    // the portable definition's orchestrator notes (point 3), rendered below — never restated
    // here. Two sources of prose about the same behavior is itself a defect (bug:
    // kit-prose-contradicts-server-definition; card: orchestrator-delegate-rule-portable-and-priced);
    // this banner line stays a pointer, not a second copy, so it can never drift from the notes.
    return `Your FIRST response MUST open with:
\`🧠 PetBox memory active\`
Then next line, your self-intro — exactly:
\`<your model name> · orchestrator\` — + one sentence naming your working rules (search-before-rework, capture-as-you-go, respect the gates).

Spawn as \`${workerName}\`. Delegation rule and cost rationale: Orchestrator notes, point 3.

Orchestrator notes (from definition): ${notes}`;
  }

  // No spawn_subagents: memory protocol still applies; do not force orchestrator-spawn mandate.
  return `Your FIRST response MUST open with:
\`🧠 PetBox memory active\`
Then next line, your self-intro — exactly:
\`<your model name> · main\` — + one sentence naming your working rules (search-before-rework, capture-as-you-go, respect the gates).

This harness does not declare spawn_subagents — do not assume subagent fan-out is available; work in the main session.`;
}

// Build the memory-protocol block for a project. `tool` maps a bare verb to the agent's
// fully-qualified MCP tool name so the SAME text renders correct tool names everywhere.
// Spawn/orchestrator prescriptions only when opts.harness has spawn_subagents.
export function buildProtocol(project: string, tool: ToolNamer, opts?: ProtocolOpts): string {
  const memorySearch = tool("memory_search");
  const memoryGet = tool("memory_get");
  const sessionSearch = tool("session_search");
  const sessionGet = tool("session_get");
  const memoryRemember = tool("memory_remember");
  const memoryUpsert = tool("memory_upsert");
  const tasksSearch = tool("tasks_search");
  const tasksUpsert = tool("tasks_upsert");
  const tasksMethodologyGuide = tool("tasks_methodology_guide");
  const tasksWorkflow = tool("tasks_workflow");
  const tasksObservationPromote = tool("tasks_observation_promote");

  const allowSpawn = orchestrationPrescriptionsAllowed(opts?.harness);
  const definition = opts?.definition ?? DEFAULT_AGENT_DEFINITION;
  const intro = buildSelfIntro(allowSpawn, definition);

  let out = `## PetBox memory

This project is wired to PetBox (project \`${project}\`) over the \`petbox\` MCP.

${intro}

**Rule — search before rework:** before re-deriving, re-investigating or re-deciding anything about this project's past, run \`${memorySearch}\` / \`${tasksSearch}\` FIRST. Before storing a fact, search for an existing entry and edit it — duplicates poison recall.

**Entry points:**

- **Facts — \`${memorySearch}\`** (no \`scope\` cascades project⊕workspace, all stores incl. \`autocaptured\`); full body: \`${memoryGet}\`.
- **Conversations — \`${sessionSearch}\`**: HOW something was decided, error text, detail a fact wouldn't carry; each hit carries the message ordinal → \`${sessionGet}\`.
- **Canon** (curated project rules, hot gotchas, open threads): inlined below as \`## PetBox memory canon\` ONLY when this session's banner fits its size budget. No canon section below? Pull it yourself, first thing: \`${memoryGet}\` (store \`canon\`, key \`index\`; no scope = cascades project+workspace) — you MUST pass \`usageSource:"machine"\` on this call: it is an automated protocol-mandated pull, not a deliberate search, and omitting the argument silently mis-records it as deliberate.

**Capture-as-you-go** — after a decision, fix, pattern or preference: \`${memoryRemember}\` (\`type\` = User|Feedback|Project|Reference; \`scope\` = workspace for cross-project/user facts). Curated/temporal edits: \`${memoryUpsert}\`. The server also autocaptures after each session — don't re-store autocaptured entries; before stopping, store 1-3 must-not-wait learnings.

**Process defects are findings, not obstacles:** never silently work around a process/doc defect or doc-vs-reality contradiction — file it via \`${tasksMethodologyGuide}\` → \`${tasksWorkflow}\` → \`${tasksUpsert}\` (do not invent board/type/status).

**Defect-like findings are observations, not memory facts:** something broken, behaving unexpectedly, or contradicting docs is a node on the built-in \`observations\` board (\`${tasksSearch}\`/\`${tasksUpsert}\`), not a memory entry — the session extractor also files these automatically. To turn a real one into work or an idea: \`${tasksObservationPromote}\`.`;

  const source = opts?.source;
  if (source === "resume" || source === "compact") {
    out += `\n\nSession ${source} — also recall recent session/decision memories to pick up where you left off.`;
  }
  return out;
}

// The Claude-Code / Droid tool namer: petbox tools are exposed as `mcp__petbox__<verb>`.
export const mcpPetboxTool: ToolNamer = (verb) => `mcp__petbox__${verb}`;

// The opencode tool namer: petbox tools are exposed as `petbox_<verb>`.
export const opencodePetboxTool: ToolNamer = (verb) => `petbox_${verb}`;

// Factory Droid exposes MCP tools as `<server>___<tool>` (triple underscore) — observed
// live in exec mode (session 7be9f6c2: `mcp__petbox__*` answers "not permitted in exec
// mode"); the docs' `mcp__<server>__<tool>` form did not match the shipped CLI.
export const droidPetboxTool: ToolNamer = (verb) => `petbox___${verb}`;
