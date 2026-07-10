// Shared PetBox memory-protocol builder — the ONE implementation every SessionStart injector
// renders from (pull-memory.ts for Claude Code, opencode-plugin.ts for opencode,
// droid-pull-memory.ts for Factory Droid), so the protocol text can no longer drift between
// agents as hand-synced copies (spec: agent-wiring, wiring-single-source).
//
// The ONLY thing that varies per agent is how an MCP tool is named: pass a `tool` mapper that
// turns a bare verb (e.g. "memory_search") into that agent's fully-qualified tool name. Claude
// Code and Droid both expose petbox tools as `mcp__petbox__<verb>`; opencode as `petbox_<verb>`.
//
// The CC-only "resume/compact" suffix is opt-in via `opts.source` (Droid's SessionStart payload
// also carries a `source`, so it shares the same behavior). Any other source = no suffix.
//
// Plain TS for native node type-stripping: no enum/namespace/parameter-properties, zero deps.

export type ToolNamer = (verb: string) => string;

export type ProtocolOpts = {
  // The SessionStart `source` (Claude Code / Droid). "resume" | "compact" append a recall nudge;
  // anything else (or omitted) renders the base protocol only.
  source?: string;
};

// Build the memory-protocol block for a project. `tool` maps a bare verb to the agent's
// fully-qualified MCP tool name so the SAME text renders correct tool names everywhere.
export function buildProtocol(project: string, tool: ToolNamer, opts?: ProtocolOpts): string {
  const memorySearch = tool("memory_search");
  const memoryGet = tool("memory_get");
  const sessionSearch = tool("session_search");
  const sessionGet = tool("session_get");
  const memoryRemember = tool("memory_remember");
  const memoryUpsert = tool("memory_upsert");
  const tasksUpsert = tool("tasks_upsert");
  const tasksMethodologyGuide = tool("tasks_methodology_guide");
  const tasksMethodologyGet = tool("tasks_methodology_get");
  const tasksWorkflow = tool("tasks_workflow");

  let out = `## PetBox memory

This project is wired to PetBox (project \`${project}\`) over the \`petbox\` MCP.

Your FIRST response MUST open with:
\`🧠 PetBox memory active\`
Then next line, your self-intro — exactly:
\`<your model name> · orchestrator\` — + one sentence naming your working rules (search-before-rework, capture-as-you-go, respect the gates). Banner reaches only main loop; role always \`orchestrator\`. When spawning workers, write their self-intro into the brief (they never see this).

**Orchestrate — delegate by DEFAULT.** SPAWN workers for anything beyond a trivial edit — implementation, research, review, multi-file. Fan-out is default; solo is exception to justify. If several calls deep implementing, stop and delegate. (No subagent → inline is fine.) Spawn as \`worker\`; lead with worker preamble.

PetBox remembers curated facts AND full session history. Start from SEARCH, not assumption.

**Rule — search before rework:** before re-deriving, re-investigating, re-deciding anything about this project's past, run \`${memorySearch}\` FIRST — redoing remembered work is the failure this protocol prevents. Before storing a fact, search for an existing entry and edit it (duplicates poison recall).

**Entry points:**

- **Facts — \`${memorySearch}\`**: \`q\` of confident words (ANDed, prefix-match, stemmed). \`bodyLen\` for snippets. No \`scope\` cascades project⊕workspace, all stores incl. \`autocaptured\` (per-hit label). No \`q\` = listing. Full body: \`${memoryGet}\`.
- **Conversations — \`${sessionSearch}\`**: for HOW something was decided, error text, or detail a fact wouldn't carry — two-stage session-archive search; each hit carries the message ordinal → \`${sessionGet}\` for verbatim source.

**Capture-as-you-go** — don't wait. After a decision, fix, pattern, or preference: \`${memoryRemember}\` (\`text\` = learning; \`type\` = User|Feedback|Project|Reference; \`scope\` = workspace for cross-project/user facts, else omit). Curated/temporal edits: \`${memoryUpsert}\`.

**Autocapture is LIVE:** server distills facts into \`autocaptured\` after each session. So: (1) don't re-store autocaptured entries — promotion is owner's call; (2) end-of-session sweep = INSURANCE: before stopping, store 1-3 must-not-wait learnings (decision+why, root cause, gotcha) and 0-2 friction notes (what got in the way, what looked stale). Skip narration, skip anything derivable from code/git.

**Process defects are findings, not obstacles:** never silently work around a process/doc defect or doc-vs-reality contradiction — file it on THIS project's methodology (do not invent board/type/status). Read process via \`${tasksMethodologyGuide}\` / \`${tasksMethodologyGet}\`; legal types/statuses for a board via \`${tasksWorkflow}\`; then \`${tasksUpsert}\` with those values. Process criticism is welcome, never scope creep.`;

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
