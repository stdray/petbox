// Shared Claude Code transcript parsing — the ONE implementation both the Stop hook
// (push-session.ts) and the history importer (import-sessions.ts) use, so what a session
// "is" cannot drift between live pushes and imports (spec: wiring-single-source).
//
// A transcript is JSONL; we keep the user/assistant TEXT turns in order and exclude tool
// dumps, meta/sidechain entries and harness chrome — tool outputs can carry secrets and
// the server only wants the dialogue (spec: wiring-history-import).
//
// Plain TS for native node type-stripping: zero deps.

import { createReadStream, existsSync, readdirSync, readFileSync } from "node:fs";
import { basename, dirname, join } from "node:path";
import { createInterface } from "node:readline";

export type Msg = { role: string; content: string };

// Provenance of one subagent spawn — the FACT of what ran, not the machine's role→model
// INTENTION (roleBinding). `modelSource` distinguishes an explicit spawn-time override (the
// caller's tool_use passed a `model`) from the roster default (no `model` on the tool_use, so
// whatever the active profile bound for that role/agent applied). `actualModel` is present only
// when the harness's own data made it recoverable — never guessed, never backfilled from
// spawnModel or roleBinding (spec: subagent-run-provenance; incident 2026-07-12 — a worker's
// self-intro prose named a model it was NOT running on; prose is not telemetry).
export type SubagentRun = {
  readonly role: string;
  readonly modelSource: "override" | "roster";
  readonly spawnModel?: string;
  readonly actualModel?: string;
};

// Claude Code's subagent-spawn tool has been named "Task" (older CLI versions) and "Agent"
// (current); accept both so this doesn't silently go blind across a CLI upgrade.
const SPAWN_TOOL_NAMES = new Set(["Agent", "Task"]);

type ToolUseBlock = { type: "tool_use"; id: string; name: string; input?: unknown };

function isSpawnToolUse(c: unknown): c is ToolUseBlock {
  const b = c as ToolUseBlock | null;
  return (
    !!b &&
    b.type === "tool_use" &&
    typeof b.id === "string" &&
    b.id.length > 0 &&
    typeof b.name === "string" &&
    SPAWN_TOOL_NAMES.has(b.name)
  );
}

function nonEmptyString(v: unknown): string | undefined {
  return typeof v === "string" && v.trim().length > 0 ? v.trim() : undefined;
}

type SpawnDecl = { toolUseId: string; role: string; spawnModel?: string };

// Scan the MAIN transcript (top-level assistant turns only, never isSidechain — a subagent's
// own tool_use calls are not a new spawn of the outer session) for Agent/Task tool_use blocks.
// This is the declared/INTENDED half: subagent_type + whether `model` was passed at spawn.
async function collectMainTranscriptSpawns(transcriptPath: string): Promise<SpawnDecl[]> {
  const rl = createInterface({
    input: createReadStream(transcriptPath, { encoding: "utf8" }),
    crlfDelay: Infinity,
  });
  const spawns: SpawnDecl[] = [];
  for await (const line of rl) {
    if (!line || line.trim().length === 0) continue;
    let e: any;
    try {
      e = JSON.parse(line);
    } catch {
      continue;
    }
    if (e.type !== "assistant" || e.isSidechain) continue;
    if (!e.message || !Array.isArray(e.message.content)) continue;
    for (const c of e.message.content) {
      if (!isSpawnToolUse(c)) continue;
      const input = (c.input ?? {}) as Record<string, unknown>;
      const role = nonEmptyString(input.subagent_type);
      if (!role) continue; // malformed/unknown spawn shape — nothing trustworthy to record
      spawns.push({ toolUseId: c.id, role, spawnModel: nonEmptyString(input.model) });
    }
  }
  return spawns;
}

// Claude Code writes each subagent's OWN transcript to a sibling per-task file, NOT inline in
// the main JSONL as isSidechain entries (verified empirically 2026-07-12 against a live
// multi-subagent session: 0 isSidechain lines in the main file; every sidechain assistant turn,
// carrying `message.model`, lives only under <sessionDir>/subagents/agent-<id>.jsonl, with a
// sibling agent-<id>.meta.json carrying the `toolUseId` that correlates back to the spawning
// tool_use in the main transcript). That per-task file is exactly what gets cleaned up after —
// so this recovery is opportunistic: read what's still on disk at Stop-hook time, and represent
// "not there anymore" as simply absent (`actualModel` omitted), never as a guess.
function loadSubagentFileIndex(transcriptPath: string): Map<string, string> {
  const index = new Map<string, string>();
  try {
    const dir = dirname(transcriptPath);
    const sessionBase = basename(transcriptPath).replace(/\.jsonl$/i, "");
    const subagentsDir = join(dir, sessionBase, "subagents");
    if (!existsSync(subagentsDir)) return index;
    for (const f of readdirSync(subagentsDir)) {
      if (!f.endsWith(".meta.json")) continue;
      try {
        const meta = JSON.parse(readFileSync(join(subagentsDir, f), "utf8"));
        const toolUseId = nonEmptyString(meta?.toolUseId);
        if (!toolUseId) continue;
        index.set(toolUseId, join(subagentsDir, f.slice(0, -".meta.json".length) + ".jsonl"));
      } catch {
        continue; // unreadable/malformed meta — skip, don't fail the whole scan
      }
    }
  } catch {
    /* no subagents dir (cleaned up, or none spawned) — index stays empty */
  }
  return index;
}

// First `message.model` recorded in a subagent's own transcript — the ACTUAL model it ran on.
async function readActualModel(subagentTranscriptPath: string): Promise<string | undefined> {
  if (!existsSync(subagentTranscriptPath)) return undefined;
  const rl = createInterface({
    input: createReadStream(subagentTranscriptPath, { encoding: "utf8" }),
    crlfDelay: Infinity,
  });
  for await (const line of rl) {
    if (!line || line.trim().length === 0) continue;
    let e: any;
    try {
      e = JSON.parse(line);
    } catch {
      continue;
    }
    if (e.type !== "assistant" || !e.message) continue;
    const model = nonEmptyString(e.message.model);
    if (model) {
      rl.close();
      return model;
    }
  }
  return undefined;
}

// Per-session subagent-run provenance for Claude Code (spec: subagent-run-provenance). Empty
// transcript / no spawns → [] (caller must treat that as "omit the meta key", not "empty array
// on the wire" — see push-session.ts).
export async function collectSubagentRuns(transcriptPath: string): Promise<SubagentRun[]> {
  const spawns = await collectMainTranscriptSpawns(transcriptPath);
  if (spawns.length === 0) return [];
  const fileIndex = loadSubagentFileIndex(transcriptPath);
  const runs: SubagentRun[] = [];
  for (const s of spawns) {
    const run: { role: string; modelSource: "override" | "roster"; spawnModel?: string; actualModel?: string } = {
      role: s.role,
      modelSource: s.spawnModel ? "override" : "roster",
    };
    if (s.spawnModel) run.spawnModel = s.spawnModel;
    const file = fileIndex.get(s.toolUseId);
    if (file) {
      const actual = await readActualModel(file);
      if (actual) run.actualModel = actual;
    }
    runs.push(run);
  }
  return runs;
}

export function extractText(message: unknown): string {
  const msg = message as { content?: unknown } | null;
  if (!msg || msg.content == null) return "";
  if (typeof msg.content === "string") return msg.content.trim();
  if (Array.isArray(msg.content)) {
    const parts = msg.content
      .filter((p: any) => p && p.type === "text" && typeof p.text === "string")
      .map((p: any) => p.text);
    return parts.join("\n").trim();
  }
  return "";
}

export function isExcluded(text: string): boolean {
  return (
    text.startsWith("<system-reminder") ||
    text.startsWith("<command-name>") ||
    text.startsWith("<local-command")
  );
}

// Collect the user/assistant text messages in transcript order. No rendering and no cap:
// the server needs the full, ordered transcript to assign stable per-message ordinals.
export async function buildMessages(transcriptPath: string): Promise<Msg[]> {
  const rl = createInterface({
    input: createReadStream(transcriptPath, { encoding: "utf8" }),
    crlfDelay: Infinity,
  });
  const msgs: Msg[] = [];
  for await (const line of rl) {
    if (!line || line.trim().length === 0) continue;
    let e: any;
    try {
      e = JSON.parse(line);
    } catch {
      continue;
    }
    if (e.type !== "user" && e.type !== "assistant") continue;
    if (e.isMeta || e.isSidechain) continue;
    const text = extractText(e.message);
    if (text.length === 0) continue;
    if (isExcluded(text)) continue;
    msgs.push({ role: e.type, content: text });
  }
  return msgs;
}

// The cwd a transcript was recorded in (the first entry that carries one). Lets the
// importer attribute a transcript to a registered project WITHOUT reversing Claude's
// lossy path-encoding of the directory name.
export async function readTranscriptCwd(transcriptPath: string, maxLines = 25): Promise<string | null> {
  const rl = createInterface({
    input: createReadStream(transcriptPath, { encoding: "utf8" }),
    crlfDelay: Infinity,
  });
  let seen = 0;
  for await (const line of rl) {
    if (++seen > maxLines) break;
    if (!line || line.trim().length === 0) continue;
    try {
      const e = JSON.parse(line);
      if (e && typeof e.cwd === "string" && e.cwd.length > 0) {
        rl.close();
        return e.cwd;
      }
    } catch {
      /* skip unparseable line */
    }
  }
  return null;
}
