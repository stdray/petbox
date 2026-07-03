// History importer (spec: wiring-history-import) — backfill the PetBox session archive
// from the agents' LOCAL history, so a freshly connected project starts with a warm
// memory instead of an empty one:
//
//   node --experimental-strip-types import-sessions.ts [--agent claude|opencode|all]
//        [--project <key>] [--dry-run] [--since YYYY-MM-DD] [--limit N] [--force]
//
// Two invariants protect the archive:
//   native ids   — a session is pushed under the SAME id its agent's live hook/plugin
//                  uses (Claude: transcript uuid; opencode: ses_…), so an import lands
//                  in the same latest-snapshot row — duplicates are impossible;
//   upgrade-only — latest-snapshot is last-write-wins, so before pushing we compare the
//                  local message count against the server version (GET /api/sessions)
//                  and skip unless local is strictly larger (--force overrides) — a
//                  stale file read can never roll back a fresher snapshot.
//
// Only dialogue turns are sent (shared transcript.ts parsing — the same code the Stop
// hook runs); raw tool outputs never leave the machine. Server-side, the cursor-driven
// pipelines (digest, facts, patterns) backfill on their own after the push.

import { readdirSync, readFileSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { join, basename } from "node:path";
import { readRegistry, resolveProject, type ResolvedProject } from "./registry.ts";
import { buildMessages, readTranscriptCwd, type Msg } from "./transcript.ts";

const FETCH_TIMEOUT_MS = 30000;

type Args = {
  agent: "claude" | "opencode" | "all";
  project?: string;
  dryRun: boolean;
  since?: Date;
  limit?: number;
  force: boolean;
};

type Candidate = {
  agent: string;
  sessionId: string;
  updated: Date;
  load: () => Promise<Msg[]>;
};

function parseArgs(argv: string[]): Args {
  const a: Args = { agent: "all", dryRun: false, force: false };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--agent") a.agent = (argv[++i] as Args["agent"]) ?? "all";
    else if (arg === "--project") a.project = argv[++i];
    else if (arg === "--dry-run") a.dryRun = true;
    else if (arg === "--force") a.force = true;
    else if (arg === "--since") a.since = new Date(argv[++i] ?? "");
    else if (arg === "--limit") a.limit = parseInt(argv[++i] ?? "0", 10) || undefined;
    else throw new Error(`unknown argument: ${arg}`);
  }
  if (!["claude", "opencode", "all"].includes(a.agent)) throw new Error(`invalid --agent: ${a.agent}`);
  if (a.since && isNaN(a.since.getTime())) throw new Error("invalid --since (use YYYY-MM-DD)");
  return a;
}

// The target: --project finds the registry entry by project key; otherwise the cwd
// resolves like the hooks do.
function resolveTarget(projectKey?: string): ResolvedProject {
  if (projectKey) {
    const entry = readRegistry().find((e) => e.project === projectKey);
    if (!entry) throw new Error(`project '${projectKey}' not found in ~/.petbox/projects.json`);
    const apiKey = process.env[entry.envVar];
    if (!apiKey) throw new Error(`env var ${entry.envVar} is empty`);
    return {
      project: entry.project,
      apiKey,
      baseUrl: (entry.baseUrl ?? "https://petbox.3po.su").replace(/\/+$/, ""),
      envVar: entry.envVar,
    };
  }
  const resolved = resolveProject(process.cwd());
  if (!resolved) throw new Error("cwd is not a registered project (or its env var is empty); use --project");
  return resolved;
}

function listDirs(path: string): string[] {
  try {
    return readdirSync(path, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => d.name);
  } catch {
    return [];
  }
}

function listFiles(path: string, ext: string): string[] {
  try {
    return readdirSync(path).filter((f) => f.endsWith(ext));
  } catch {
    return [];
  }
}

function readJson(path: string): any | null {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch {
    return null;
  }
}

// Claude Code: ~/.claude/projects/<encoded-cwd>/*.jsonl. The encoding is lossy, so a
// transcript is attributed by the cwd recorded INSIDE it, resolved through the same
// registry matching the hooks use (worktrees and subfolders included).
async function claudeCandidates(target: ResolvedProject): Promise<Candidate[]> {
  const root = join(homedir(), ".claude", "projects");
  const out: Candidate[] = [];
  for (const dir of listDirs(root)) {
    for (const file of listFiles(join(root, dir), ".jsonl")) {
      const path = join(root, dir, file);
      const cwd = await readTranscriptCwd(path);
      if (!cwd) continue;
      const resolved = resolveProject(cwd);
      if (!resolved || resolved.project !== target.project) continue;
      out.push({
        agent: "claude-code",
        sessionId: basename(file, ".jsonl"),
        updated: statSync(path).mtime,
        load: () => buildMessages(path),
      });
    }
  }
  return out;
}

// opencode: ~/.local/share/opencode/storage —
//   session/<projectHash>/<ses>.json  {id, directory, time.updated}
//   message/<ses>/<msg>.json          {id, role, time.created}
//   part/<msg>/<prt>.json             {type:"text", text}
// A session is attributed by its recorded `directory`; content = the text parts of each
// message, in message-time order — the same dialogue-only shape the live plugin pushes.
function opencodeCandidates(target: ResolvedProject): Candidate[] {
  const storage = join(homedir(), ".local", "share", "opencode", "storage");
  const out: Candidate[] = [];
  for (const hash of listDirs(join(storage, "session"))) {
    for (const file of listFiles(join(storage, "session", hash), ".json")) {
      const info = readJson(join(storage, "session", hash, file));
      if (!info || typeof info.id !== "string" || typeof info.directory !== "string") continue;
      const resolved = resolveProject(info.directory);
      if (!resolved || resolved.project !== target.project) continue;
      const sessionId = info.id;
      out.push({
        agent: "opencode",
        sessionId,
        updated: new Date(info.time?.updated ?? info.time?.created ?? 0),
        load: async () => {
          const msgDir = join(storage, "message", sessionId);
          const metas = listFiles(msgDir, ".json")
            .map((f) => readJson(join(msgDir, f)))
            .filter((m) => m && typeof m.role === "string")
            .sort((a, b) => (a.time?.created ?? 0) - (b.time?.created ?? 0) || String(a.id).localeCompare(String(b.id)));
          const msgs: Msg[] = [];
          for (const meta of metas) {
            const partDir = join(storage, "part", meta.id);
            const text = listFiles(partDir, ".json")
              .sort()
              .map((f) => readJson(join(partDir, f)))
              .filter((p) => p && p.type === "text" && typeof p.text === "string")
              .map((p) => p.text)
              .join("\n")
              .trim();
            if (text.length === 0) continue;
            msgs.push({ role: meta.role, content: text });
          }
          return msgs;
        },
      });
    }
  }
  return out;
}

async function serverVersions(target: ResolvedProject): Promise<Map<string, number>> {
  const res = await fetch(`${target.baseUrl}/api/sessions/${target.project}`, {
    headers: { "X-Api-Key": target.apiKey },
    signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
  });
  if (!res.ok) throw new Error(`GET /api/sessions/${target.project} → ${res.status}`);
  const body = (await res.json()) as { sessions?: { sessionId: string; version: number }[] };
  return new Map((body.sessions ?? []).map((s) => [s.sessionId, s.version]));
}

async function push(target: ResolvedProject, c: Candidate, msgs: Msg[]): Promise<void> {
  const body = msgs.map((m) => JSON.stringify(m)).join("\n");
  const uri = `${target.baseUrl}/api/sessions/${target.project}/${encodeURIComponent(c.sessionId)}?agent=${encodeURIComponent(c.agent)}`;
  const res = await fetch(uri, {
    method: "POST",
    headers: { "X-Api-Key": target.apiKey, "Content-Type": "application/x-ndjson; charset=utf-8" },
    body,
    signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
  });
  if (!res.ok) throw new Error(`POST ${c.sessionId} → ${res.status}`);
}

async function main(): Promise<void> {
  const args = parseArgs(process.argv.slice(2));
  const target = resolveTarget(args.project);
  console.log(`importing into '${target.project}' @ ${target.baseUrl} (agent: ${args.agent}${args.dryRun ? ", DRY-RUN" : ""})`);

  let candidates: Candidate[] = [];
  if (args.agent === "claude" || args.agent === "all") candidates.push(...(await claudeCandidates(target)));
  if (args.agent === "opencode" || args.agent === "all") candidates.push(...opencodeCandidates(target));
  candidates.sort((a, b) => a.updated.getTime() - b.updated.getTime());

  const filtered = args.since ? candidates.filter((c) => c.updated >= args.since!) : candidates;
  const skippedFiltered = candidates.length - filtered.length;
  const capped = args.limit ? filtered.slice(0, args.limit) : filtered;
  const skippedLimit = filtered.length - capped.length;

  const versions = await serverVersions(target);
  let pushed = 0, upToDate = 0, empty = 0, failed = 0, totalMsgs = 0;
  for (const c of capped) {
    try {
      const msgs = await c.load();
      if (msgs.length === 0) {
        empty++;
        continue;
      }
      const server = versions.get(c.sessionId) ?? 0;
      if (!args.force && msgs.length <= server) {
        upToDate++; // upgrade-only: never roll a fresher snapshot back
        continue;
      }
      if (args.dryRun) {
        console.log(`  would push ${c.agent} ${c.sessionId}: ${msgs.length} msgs (server: ${server})`);
        pushed++;
        totalMsgs += msgs.length;
        continue;
      }
      await push(target, c, msgs);
      pushed++;
      totalMsgs += msgs.length;
      console.log(`  pushed ${c.agent} ${c.sessionId}: ${msgs.length} msgs (server was ${server})`);
    } catch (e) {
      failed++;
      console.error(`  FAILED ${c.sessionId}: ${e instanceof Error ? e.message : e}`);
    }
  }

  console.log(
    `done: ${pushed} pushed (${totalMsgs} msgs), ${upToDate} up-to-date, ${empty} empty, ` +
      `${skippedFiltered} before --since, ${skippedLimit} over --limit, ${failed} failed`,
  );
  if (pushed > 0 && !args.dryRun)
    console.log("note: server pipelines (digest/facts/patterns) will backfill in the background over the next minutes.");
  process.exit(failed > 0 ? 1 : 0);
}

main().catch((e) => {
  console.error(e instanceof Error ? e.message : e);
  process.exit(1);
});
