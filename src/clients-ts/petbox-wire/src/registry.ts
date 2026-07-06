// Shared project resolver for the global agent-wiring kit.
//
// One global registry (`~/.petbox/projects.json`) maps a filesystem prefix to a PetBox
// project + the env var that holds its API key. The Claude Code user hooks and the global
// opencode plugin both run in EVERY project on the machine, so they resolve the active
// project by the current working directory (longest-prefix match) and no-op cleanly when
// the cwd is not registered.
//
// Plain TS for native node type-stripping: no enum/namespace/parameter-properties, type-only
// imports, zero deps.

import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const DEFAULT_BASE_URL = "https://petbox.3po.su";

// Per-project prompt-RAG config (step 1, client-side registry gate). Stored on the matched
// registry entry and read by the global UserPromptSubmit hook (prompt-rag.ts), which self-gates
// per project: `enabled:false` (or absent) → the hook is a silent no-op for that project. The
// tolerances are optional; the hook falls back to PROMPT_RAG_DEFAULTS when a field is absent.
export type PromptRagConfig = {
  enabled: boolean;
  cap?: number;
  requireHyphen?: boolean;
};

// Single source of truth for the prompt-RAG tolerance defaults, shared by wire.ts (what it writes
// on --prompt-rag) and prompt-rag.ts (what it falls back to when a field is missing from config).
export const PROMPT_RAG_DEFAULTS = { cap: 8, requireHyphen: true } as const;

export type RegistryEntry = {
  prefix: string;
  project: string;
  envVar: string;
  baseUrl?: string;
  promptRag?: PromptRagConfig;
};

export type ResolvedProject = {
  project: string;
  apiKey: string;
  baseUrl: string;
  envVar: string;
  promptRag?: PromptRagConfig;
};

export function registryPath(): string {
  return join(homedir(), ".petbox", "projects.json");
}

// Cross-platform key store written by wire.ts: ~/.petbox/keys.json is a flat JSON map
// { "<ENV_VAR>": "<key>" }. Read as a fallback when the env var is not set in the process
// (so a machine wired via `npx petbox-wire` works without a user-scope env var). Never throws.
function readKeyStore(envVar: string): string {
  try {
    const raw = readFileSync(join(homedir(), ".petbox", "keys.json"), "utf8");
    const parsed = JSON.parse(raw);
    const v = parsed && typeof parsed === "object" ? parsed[envVar] : undefined;
    return typeof v === "string" ? v : "";
  } catch {
    return "";
  }
}

// Normalize a path for prefix comparison: unify separators to "/", drop a trailing
// separator, and lowercase on Windows (case-insensitive filesystem).
function normalize(p: string): string {
  let n = String(p).replace(/[\\/]+/g, "/");
  if (n.length > 1 && n.endsWith("/")) n = n.slice(0, -1);
  if (process.platform === "win32") n = n.toLowerCase();
  return n;
}

// Segment-boundary prefix match: "d:/my/prj/yoba" must NOT match "d:/my/prj/yobapub".
// dir is a prefix of, or equal to, the entry path (so worktree subfolders are covered).
function isUnderPrefix(dir: string, prefix: string): boolean {
  if (dir === prefix) return true;
  return dir.startsWith(prefix + "/");
}

export function readRegistry(): RegistryEntry[] {
  try {
    const raw = readFileSync(registryPath(), "utf8");
    const parsed = JSON.parse(raw);
    const entries = parsed && Array.isArray(parsed.entries) ? parsed.entries : [];
    return entries.filter(
      (e: unknown): e is RegistryEntry =>
        !!e &&
        typeof (e as RegistryEntry).prefix === "string" &&
        typeof (e as RegistryEntry).project === "string" &&
        typeof (e as RegistryEntry).envVar === "string",
    );
  } catch {
    return [];
  }
}

// Resolve the active project for a directory. Returns null on ANY failure
// (no registry file, no match, empty env var) — never throws, because the hooks
// that call this run globally and must be a no-op outside registered projects.
export function resolveProject(dir: string): ResolvedProject | null {
  try {
    if (!dir || typeof dir !== "string") return null;
    const entries = readRegistry();
    if (entries.length === 0) return null;

    const nd = normalize(dir);
    let best: RegistryEntry | null = null;
    let bestLen = -1;
    for (const e of entries) {
      const np = normalize(e.prefix);
      if (isUnderPrefix(nd, np) && np.length > bestLen) {
        best = e;
        bestLen = np.length;
      }
    }
    if (!best) return null;

    // env var wins; fall back to ~/.petbox/keys.json (the wire.ts key store).
    const apiKey = process.env[best.envVar] || readKeyStore(best.envVar);
    if (!apiKey || apiKey.trim().length === 0) return null;

    const baseUrl = (best.baseUrl && best.baseUrl.trim()) || DEFAULT_BASE_URL;
    // Pass the matched entry's per-project prompt-RAG config through unchanged (absent = undefined,
    // back-compat). Only surface it when it is a real object, so a malformed value degrades to
    // "no config" (→ the hook self-gates off) rather than a truthy junk value.
    const promptRag =
      best.promptRag && typeof best.promptRag === "object" ? best.promptRag : undefined;
    return {
      project: best.project,
      apiKey,
      baseUrl: baseUrl.replace(/\/+$/, ""),
      envVar: best.envVar,
      promptRag,
    };
  } catch {
    return null;
  }
}
