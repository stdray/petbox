// The project-scope `<project>/.qwen/settings.json` writer — BOTH keys the kit owns in that file:
// `mcpServers.petbox` and `skills.directories`.
//
// WHY THIS MODULE EXISTS (card wire-qwen-project-settings-mcp-and-skills, caught live 09.09.2026
// in D:\my\prj\petsonde). Two gaps of the same file:
//
//   1. MCP. `writeProjectFiles` (wire.ts) already merged `mcpServers.petbox` here — but it runs
//      ONLY on a full `wire`. `apply` never touched the file, so a project wired by an older kit
//      and kept current with `apply` never grew a `.qwen` directory at all: qwen then fell back to
//      `.mcp.json` (claude-code's format, no env-var resolution) and reported `needs
//      authentication` with zero tools. The key was fine; the file simply did not exist.
//   2. Skills. `SKILL_SURFACES` (skill-files.ts) writes `.claude/skills` and `.factory/skills`
//      and nothing for qwen. qwen reads FOREIGN skill roots through `skills.directories`, so the
//      fix is a POINTER at the `.claude/skills` this project already has — not a third copy of
//      every skill body on disk.
//
// TWO MEASURED TRAPS, both from running qwen 0.23.2's real SkillManager (reconnaissance on the
// card, not re-derived here):
//
//   - A RELATIVE entry in `skills.directories` is resolved against the RUNTIME's `process.cwd()`,
//     not against the project the settings file belongs to. Only an absolute path means what it
//     looks like it means, so `mergeQwenProjectSettings` THROWS on a relative `skillsDir` rather
//     than writing a pointer that would silently resolve somewhere else.
//   - Every directory named in `skills.directories` is loaded at qwen's `user` LEVEL. Writing the
//     key into `~/.qwen/settings.json` would therefore spill ONE project's skills into EVERY
//     project on the machine. Hence: this module writes a PROJECT path and is called with one;
//     the user-scope qwen settings (wire.ts's installGlobalHooks) must never gain this key, which
//     qwen-user-settings-no-skills.test.ts pins.
//
// Merge, never clobber — the same rule (and the same failure this project already paid for:
// commit 1655231a, `.mcp.json`/`opencode.json` regenerated whole) applies here: qwen writes
// `$version` into this file itself, and a person may hold their own servers and their own skill
// directories in it. Only the two keys above are ever touched; `skills.directories` is a UNION
// (an entry already present is not duplicated), so the write is idempotent byte for byte.
//
// Plain TS for native node type-stripping: zero deps.

import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, isAbsolute, resolve } from "node:path";

/** What one merge did to the file. Mirrors apply-write.ts's vocabulary so the caller can map it
 * onto the apply ledger without a translation table. */
export type QwenProjectSettingsOutcome = {
  readonly reason: "new" | "own" | "unchanged";
  /** The `petbox` MCP entry existed with DIFFERENT content and was overwritten (same
   * name-conflict condition wire.ts's mergeMcpServer warns about for the other four sites). */
  readonly mcpNameConflict: boolean;
  /** Non-empty when `skills` or `skills.directories` held a shape this merge refuses to
   * reinterpret (e.g. `"skills": true`). The MCP half still lands; the pointer does not. */
  readonly skillsWarning?: string | undefined;
};

export type QwenProjectSettingsInput = {
  /** Absolute path of the PROJECT's `.qwen/settings.json`. Never the user-scope one. */
  readonly settingsPath: string;
  /** Absolute path of the skills root to point qwen at (this project's `.claude/skills`). */
  readonly skillsDir: string;
  /** The `mcpServers.petbox` value — built by qwen-mcp-entry.ts, the single source of truth. */
  readonly mcpEntry: unknown;
  /** Compute the outcome without touching the filesystem (apply's `--dry-run`). */
  readonly dryRun?: boolean | undefined;
};

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

/** Same tolerant read wire.ts uses for every other MCP config: an absent or unreadable file is an
 * empty object. Kept deliberately identical so this file is not the one site with its own rules. */
function readSettings(path: string): { readonly data: Record<string, unknown>; readonly before: string | null } {
  let before: string | null;
  try {
    before = readFileSync(path, "utf8");
  } catch {
    return { data: {}, before: null };
  }
  try {
    const parsed: unknown = JSON.parse(before);
    return { data: isPlainObject(parsed) ? parsed : {}, before };
  } catch {
    return { data: {}, before };
  }
}

/**
 * Two absolute paths naming the same directory? `resolve` normalizes separators and `.`/`..`;
 * Windows paths are additionally compared case-insensitively, which is the platform's own rule.
 * A RELATIVE existing entry is never compared at all — `resolve` would anchor it to this
 * process's cwd, which is precisely the runtime trap this module exists to avoid, and answering
 * "does that equal ours?" from the wrong anchor is worse than answering "no".
 */
function sameDirectory(a: string, b: string): boolean {
  if (!isAbsolute(a) || !isAbsolute(b)) return false;
  const ra = resolve(a);
  const rb = resolve(b);
  return process.platform === "win32" ? ra.toLowerCase() === rb.toLowerCase() : ra === rb;
}

/**
 * Merge the kit's two owned keys into the project's `.qwen/settings.json`, preserving every other
 * key, every other MCP server and every other skills directory.
 *
 * Throws when `skillsDir` is not absolute: a relative pointer is not a weaker version of this
 * feature, it is a different (and silently wrong) one — see the header.
 */
export function mergeQwenProjectSettings(input: QwenProjectSettingsInput): QwenProjectSettingsOutcome {
  if (!isAbsolute(input.skillsDir)) {
    throw new Error(
      `qwen skills.directories entry must be an ABSOLUTE path (qwen resolves a relative one ` +
        `against the runtime's cwd, not against the project) — got "${input.skillsDir}".`,
    );
  }

  const { data, before } = readSettings(input.settingsPath);

  // ---- mcpServers.petbox ----
  if (!isPlainObject(data["mcpServers"])) data["mcpServers"] = {};
  const servers = data["mcpServers"];
  if (!isPlainObject(servers)) throw new Error("unreachable: mcpServers was just normalized to an object");
  const existingEntry = servers["petbox"];
  const mcpNameConflict =
    existingEntry !== undefined && JSON.stringify(existingEntry) !== JSON.stringify(input.mcpEntry);
  servers["petbox"] = input.mcpEntry;

  // ---- skills.directories ----
  let skillsWarning: string | undefined;
  if (data["skills"] !== undefined && !isPlainObject(data["skills"])) {
    skillsWarning =
      `"skills" is not an object (${JSON.stringify(data["skills"])}) — left untouched, so qwen ` +
      `will not see this project's skills. Remove or fix that key and re-run.`;
  } else {
    if (data["skills"] === undefined) data["skills"] = {};
    const skills = data["skills"];
    if (!isPlainObject(skills)) throw new Error("unreachable: skills was just normalized to an object");
    const existingDirs = skills["directories"];
    if (existingDirs !== undefined && !Array.isArray(existingDirs)) {
      skillsWarning =
        `"skills.directories" is not an array (${JSON.stringify(existingDirs)}) — left untouched, ` +
        `so qwen will not see this project's skills. Remove or fix that key and re-run.`;
    } else {
      const current: unknown[] = Array.isArray(existingDirs) ? [...existingDirs] : [];
      const already = current.some((d) => typeof d === "string" && sameDirectory(d, input.skillsDir));
      if (!already) current.push(input.skillsDir);
      skills["directories"] = current;
    }
  }

  const text = JSON.stringify(data, null, 2) + "\n";
  if (before !== null && before === text) return { reason: "unchanged", mcpNameConflict, skillsWarning };
  if (!input.dryRun) {
    mkdirSync(dirname(input.settingsPath), { recursive: true });
    writeFileSync(input.settingsPath, text, "utf8");
  }
  return { reason: before === null ? "new" : "own", mcpNameConflict, skillsWarning };
}
