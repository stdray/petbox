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

export type RegistryEntry = {
  prefix: string;
  project: string;
  envVar: string;
  baseUrl?: string;
};

export type ResolvedProject = {
  project: string;
  apiKey: string;
  baseUrl: string;
  envVar: string;
};

export function registryPath(): string {
  return join(homedir(), ".petbox", "projects.json");
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

    const apiKey = process.env[best.envVar];
    if (!apiKey || apiKey.trim().length === 0) return null;

    const baseUrl = (best.baseUrl && best.baseUrl.trim()) || DEFAULT_BASE_URL;
    return { project: best.project, apiKey, baseUrl: baseUrl.replace(/\/+$/, ""), envVar: best.envVar };
  } catch {
    return null;
  }
}
