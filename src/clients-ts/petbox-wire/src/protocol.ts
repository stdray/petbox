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

  let out = `## PetBox memory

This project is wired to PetBox (project \`${project}\`) over the connected \`petbox\` MCP.

In your FIRST response this session, open with exactly this line (so it's visible the protocol is active):
\`🧠 PetBox memory active\`

PetBox remembers a LOT about this project — the curated facts AND the full session history (imported + auto-pushed). Start reasoning about anything past from a SEARCH, not from assumption.

**Rule — search before you (re)work:** before re-deriving, re-investigating, or re-deciding ANYTHING about this project's past, run \`${memorySearch}\` FIRST — redoing work the project already remembers is the failure mode this protocol exists to prevent. And before you store a new fact, \`${memorySearch}\` for an existing one and edit that instead (duplicates poison recall).

The entry point has two legs:

- **Facts — \`${memorySearch}\`**: a \`q\` of a few words you are confident appear (tokens are ANDed, prefix-matched; wordforms stem), pass \`bodyLen\` (e.g. 240) for cheap snippets. With no \`scope\` it cascades project ⊕ workspace and EVERY store — curated notes and the machine-distilled \`autocaptured\` quarantine alike (the store label in each hit tells you which). Without \`q\` it's a plain listing (freshest first). Pull a full body with \`${memoryGet}\`.
- **Past conversations — \`${sessionSearch}\`**: when you need HOW something was decided, an error text, or any detail a fact wouldn't carry — two-stage search over the whole session archive; every hit carries the message ordinal, so \`${sessionGet}\` jumps to the verbatim source.

As you work, **capture** incrementally (don't wait for session end): after a decision, a fixed bug, a discovered pattern, or a stated preference, store a concise fact via \`${memoryRemember}\` (\`text\` = the learning; \`type\` = User|Feedback|Project|Reference; \`scope\` = workspace for facts that span projects or are about the user, else omit for this project). Curated/temporal edits go through \`${memoryUpsert}\`.

**Background autocapture is LIVE:** after a session settles (~minutes), the server distills durable facts and recurring behavior patterns from it into the \`autocaptured\` store on its own. So: (1) don't re-store what memory_search already shows as autocaptured — promote-worthy entries are the owner's call; (2) the **end-of-session sweep** is now an INSURANCE pass, not the only capture: before you stop, store the 1-3 learnings that must not wait for background distillation (a key decision + why, a root cause, a gotcha) — and also record 0-2 process-friction observations (what got in the way, what you had to work around, what looked stale) — skip narration and anything derivable from code/git.

**Process defects are findings, not obstacles:** never silently work around a process/doc defect or a contradiction between a document and reality — file an intake issue on the project's \`intake\` board (\`${tasksUpsert}\` type:"issue" status:"reported") instead of swallowing it. Criticism of the process is explicitly welcome and is never scope creep.`;

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
