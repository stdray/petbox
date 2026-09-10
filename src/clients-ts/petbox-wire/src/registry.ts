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

import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { petboxDir, petboxKeysJsonPath } from "./petbox-dir.ts";
import { wireLog } from "./wire-log.ts";

// Class A vs Class Б (bug: wire-silent-failures-invisible): a MISSING registry/keys-store file
// is the overwhelmingly common case (any machine before its first `petbox-wire` run, or any
// project directory these global hooks run in that was simply never wired) — that is legitimate
// silence, not a breakage, and must never leave a trace. A PRESENT file that fails to parse is a
// different animal (disk corruption, a hand-edit gone wrong, a partial write) — the previous
// code folded both into one catch-and-return-empty, so "porcha реестра" and "project not
// registered" (card item 4) looked identical from every hook's behavior. `isEnoent` tells them
// apart; only the non-ENOENT branch calls wireLog.
function isEnoent(e: unknown): boolean {
  return typeof e === "object" && e !== null && (e as NodeJS.ErrnoException).code === "ENOENT";
}

const DEFAULT_BASE_URL = "https://petbox.3po.su";

// A registry entry may carry extra keys written by older kits (e.g. the removed `promptRag` gate) —
// they are simply ignored here and dropped the next time wire.ts upserts the entry.
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

export function registryPath(homeDir: string = homedir()): string {
  return join(petboxDir(homeDir), "projects.json");
}

// keys-json-supports-env-var-references: a stored value may be a LITERAL key (old behavior,
// unchanged byte-for-byte) or a reference to another environment variable, written as $VAR or
// ${VAR}. The reference form means "read VAR from the environment right now" — it carries no
// key material of its own, so a keys.json file full of references is nothing worth stealing
// (obs probe-agent-reads-owner-keys-json-bypassing-env-strip), and there is no second copy of
// the secret left to go stale (the whole point: env-var rotation now reaches every consumer with
// zero edits to this file). Anchored on the WHOLE value — "prefix$VARsuffix" is a literal, not a
// partial reference; that keeps a key that legitimately starts with "$" (unlikely, but not this
// module's business to forbid) from being misparsed.
const ENV_REF_RE = /^\$\{([A-Za-z_][A-Za-z0-9_]*)\}$|^\$([A-Za-z_][A-Za-z0-9_]*)$/;

/** `$VAR` / `${VAR}` → `VAR`; anything else (including "") → null (a literal). */
export function parseEnvRef(raw: string): string | null {
  const m = ENV_REF_RE.exec(raw);
  if (!m) return null;
  return m[1] ?? m[2] ?? null;
}

/** The reference form bootstrap writes for a key it obtained from a live env var. */
export function toEnvRef(envVar: string): string {
  return "${" + envVar + "}";
}

// Thrown by readKeyStore (never by inspectKeyStoreEntry) when a keys.json value is a reference
// and the referenced variable is NOT set in this process's environment. Deliberately loud and
// synchronous, on the resolution path itself, so a caller cannot forward "" or the literal
// "${VAR}" text into a request — that literal-string-in-a-header shape is exactly the qwen-code
// issue #11499 defect (a placeholder reached the network and came back as an unexplained 401).
// Decision (card keys-json-supports-env-var-references, item 4): resolution is NEVER lazy —
// this must surface before the first network call of the run that needs the key, not at the
// moment some provider call finally touches it.
export class UnresolvedEnvRefError extends Error {
  readonly envVar: string;
  readonly refVar: string;
  readonly project: string;

  constructor(envVar: string, refVar: string, project: string) {
    super(
      `~/.petbox/keys.json: "${envVar}" (project "${project}") is a reference to ${refVar}, but ` +
        `${refVar} is not set in this process's environment. Set ${refVar} and retry — no ` +
        `request was sent.`,
    );
    this.name = "UnresolvedEnvRefError";
    this.envVar = envVar;
    this.refVar = refVar;
    this.project = project;
  }
}

export type KeyStoreEntry =
  | { kind: "absent" }
  | { kind: "literal"; value: string }
  | { kind: "reference"; refVar: string; resolved: string | null }; // resolved: null = unresolved right now

// Shared file-read/parse step behind both inspectKeyStoreEntry and readKeyStore — same Class A
// (ENOENT, silent) vs Class Б (corrupt JSON, traced) split as readRegistry below.
function readKeysJsonFile(homeDir: string): Record<string, unknown> | null {
  const path = petboxKeysJsonPath(homeDir);
  let raw: string;
  try {
    raw = readFileSync(path, "utf8");
  } catch (e) {
    if (!isEnoent(e)) {
      wireLog("registry", `keys.json at ${path} unreadable — ${e instanceof Error ? e.message : String(e)}`, homeDir);
    }
    return null;
  }
  try {
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === "object" ? parsed : null;
  } catch (e) {
    // File exists but is not valid JSON — corruption, not "no keys written yet" (Class Б).
    wireLog("registry", `keys.json at ${path} is not valid JSON — ${e instanceof Error ? e.message : String(e)}`, homeDir);
    return null;
  }
}

// Classifies envVar's keys.json entry WITHOUT throwing — used by doctor/drift, which must
// always finish and report rather than crash on a reference that happens not to resolve in
// doctor's own process. `resolved: null` on a "reference" row means "not resolvable right now",
// which is a fact to REPORT, not a reason to abort.
export function inspectKeyStoreEntry(envVar: string, homeDir: string = homedir()): KeyStoreEntry {
  const store = readKeysJsonFile(homeDir);
  const v = store ? store[envVar] : undefined;
  if (typeof v !== "string" || v === "") return { kind: "absent" };
  const refVar = parseEnvRef(v);
  if (refVar === null) return { kind: "literal", value: v };
  const envValue = process.env[refVar];
  return { kind: "reference", refVar, resolved: envValue && envValue.trim() ? envValue : null };
}

// Cross-platform key store written by wire.ts: ~/.petbox/keys.json is a flat JSON map
// { "<ENV_VAR>": "<key-or-reference>" }. Read as a fallback when the env var is not set in the
// process (so a machine wired via `npx petbox-wire` works without a user-scope env var).
// `homeDir` is injectable (tests only; every real caller uses the default) so the Class A/Б
// split above is unit-testable without touching the real ~/.petbox.
// Throws UnresolvedEnvRefError — and ONLY that — when the entry is a reference the current
// environment cannot satisfy; `project` is cosmetic (goes in that message only, default is a
// placeholder for call sites that don't have one handy). Every other outcome (absent, literal,
// resolved reference) returns a plain string and never throws.
export function readKeyStore(envVar: string, homeDir: string = homedir(), project = "(unknown project)"): string {
  const entry = inspectKeyStoreEntry(envVar, homeDir);
  if (entry.kind === "absent") return "";
  if (entry.kind === "literal") return entry.value;
  if (entry.resolved === null) throw new UnresolvedEnvRefError(envVar, entry.refVar, project);
  return entry.resolved;
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

// `homeDir` is injectable (tests only) — see readKeyStore's comment above.
export function readRegistry(homeDir: string = homedir()): RegistryEntry[] {
  const path = registryPath(homeDir);
  let raw: string;
  try {
    raw = readFileSync(path, "utf8");
  } catch (e) {
    // Class A: no registry file yet — every unwired machine/project hits this constantly.
    if (!isEnoent(e)) {
      wireLog("registry", `projects.json at ${path} unreadable — ${e instanceof Error ? e.message : String(e)}`, homeDir);
    }
    return [];
  }
  try {
    const parsed = JSON.parse(raw);
    const entries = parsed && Array.isArray(parsed.entries) ? parsed.entries : [];
    return entries.filter(
      (e: unknown): e is RegistryEntry =>
        !!e &&
        typeof (e as RegistryEntry).prefix === "string" &&
        typeof (e as RegistryEntry).project === "string" &&
        typeof (e as RegistryEntry).envVar === "string",
    );
  } catch (e) {
    // File exists but is not valid JSON — this IS the "порча реестра" the card calls out
    // (item 4): distinct from "project not registered", which is Class A and never gets here.
    wireLog("registry", `projects.json at ${path} is not valid JSON — ${e instanceof Error ? e.message : String(e)}`, homeDir);
    return [];
  }
}

// Resolve the active project for a directory. Returns null on ANY failure
// (no registry file, no match, empty env var) — never throws, because the hooks
// that call this run globally and must be a no-op outside registered projects.
// readRegistry/readKeyStore already classify their own failures (Class A silent vs Class Б
// traced); this outer catch only guards against a genuinely unexpected bug in the match logic
// below (e.g. a malformed dir argument) — if that ever fires, it is unambiguously Class Б.
//
// ONE deliberate exception to "never throws": UnresolvedEnvRefError (keys.json holds a
// $VAR/${VAR} reference this process's environment cannot satisfy). That is NOT the same
// silence as "project not registered" — it means the project IS wired and a key SHOULD exist,
// so folding it into `null` would send a caller straight at the provider with no key at all
// (silent) or let a stale/placeholder value slip through. It is logged here (so it always
// shows up in `doctor`'s wire.log trace tail even if a caller swallows the throw) and then
// rethrown — every caller on a network path is expected to let it propagate and fail before
// that call, per the card's "resolution is never lazy" decision.
// `homeDir` is injectable (tests only; every real caller uses the default homedir()).
export function resolveProject(dir: string, homeDir: string = homedir()): ResolvedProject | null {
  try {
    if (!dir || typeof dir !== "string") return null;
    const entries = readRegistry(homeDir);
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

    // env var wins; fall back to ~/.petbox/keys.json (the wire.ts key store — literal or
    // reference; readKeyStore throws UnresolvedEnvRefError for an unresolvable reference).
    const apiKey = process.env[best.envVar] || readKeyStore(best.envVar, homeDir, best.project);
    if (!apiKey || apiKey.trim().length === 0) return null;

    const baseUrl = (best.baseUrl && best.baseUrl.trim()) || DEFAULT_BASE_URL;
    return {
      project: best.project,
      apiKey,
      baseUrl: baseUrl.replace(/\/+$/, ""),
      envVar: best.envVar,
    };
  } catch (e) {
    if (e instanceof UnresolvedEnvRefError) {
      wireLog("registry", `resolveProject(dir=${dir}) — ${e.message}`, homeDir);
      throw e;
    }
    wireLog("registry", `resolveProject(dir=${dir}) unexpected failure — ${e instanceof Error ? e.message : String(e)}`, homeDir);
    return null;
  }
}

// --- keys.json drift detection (card keys-json-doctor-drift-check) -----------------------------
//
// Measured 2026-09-09 (see work/keys-env-refresh-path's comment): ~/.petbox/keys.json is written
// ONLY by a full `wire` run; a plain env-var rotation never reaches it, so the file can go stale
// indefinitely. That alone is mostly harmless because resolveProject above is env-first (line 152)
// — but `doctor` resolves a key the SAME env-first way, so a healthy `doctor` run proves nothing
// about whether the FILE (what a process with no env var, e.g. a fresh sandboxed child, actually
// gets) is in sync. This section exists to compare the two paths directly, bypassing the env-first
// fallback, so drift is caught instead of surfacing later as a silent 401 in some other process.
//
// Hard rule: never read, log, or return key material — only per-envVar hash equality. A 12-hex-char
// SHA-256 prefix is enough to prove/disprove equality without ever reconstructing the value.

export type KeyDrift = {
  readonly envVar: string;
  readonly kind: "missing-in-file" | "differs";
};

function shortHash(value: string): string {
  return createHash("sha256").update(value, "utf8").digest("hex").slice(0, 12);
}

// Only envVars actually SET in this process are checked: when the env var is unset, the file is
// the only source there is (that's the fallback working as designed), so there is nothing to
// diff. The failure mode this exists to catch — a process where the env var is missing gets a
// stale file value — can only be OBSERVED from a different process that does have the env var,
// which is exactly the case `doctor` runs in.
//
// Three states now, not two (card keys-json-supports-env-var-references, item 5): a reference
// entry is skipped here on purpose, not compared by hash — a reference has no copy to go stale,
// so "drift" does not apply to it BY CONSTRUCTION, and hashing the literal text "${VAR}" against
// the live env value (the pre-fix behavior) would have manufactured a permanent false "differs"
// on every reference-based entry, on every run. That was the concrete bug this fixes: `doctor`
// must not lie on references. Uses inspectKeyStoreEntry (never throws) rather than readKeyStore,
// since a stray unresolved reference elsewhere in the file must never abort this scan.
export function detectKeysStoreDrift(homeDir: string = homedir()): KeyDrift[] {
  const entries = readRegistry(homeDir);
  const envVars = [...new Set(entries.map((e) => e.envVar))];
  const drifts: KeyDrift[] = [];
  for (const envVar of envVars) {
    const envValue = process.env[envVar];
    if (!envValue || envValue.trim().length === 0) continue;
    const entry = inspectKeyStoreEntry(envVar, homeDir);
    if (entry.kind === "reference") continue; // no second copy → no drift possible
    if (entry.kind === "absent") {
      drifts.push({ envVar, kind: "missing-in-file" });
    } else if (shortHash(envValue) !== shortHash(entry.value)) {
      drifts.push({ envVar, kind: "differs" });
    }
  }
  return drifts;
}

// Names the variable and the kind of divergence — never the value or any part of it.
export function formatKeyDrift(d: KeyDrift): string {
  return d.kind === "missing-in-file"
    ? `${d.envVar} — set in the environment but never written to keys.json`
    : `${d.envVar} — environment value differs from keys.json (file is stale)`;
}
