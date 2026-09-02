// Third gap named by task kit-version-lands-everywhere-and-sweeps: publishing petbox-wire to
// npm is gated on a push of the `npm-wire` GIT TAG (.github/workflows/ci.yml, npm-wire-publish
// job) — a merge to `main` never triggers it. That gate was invisible: nothing anywhere told an
// operator "main moved on and the tag was never pushed", so `latest` on npm silently fell behind
// main indefinitely (measured live 2026-09-02: `latest` stayed at 0.1.0-ci.2174 for a full merge
// after main had already moved to a newer commit).
//
// This module answers ONE question, best-effort: is the npm `latest` dist-tag of petbox-wire
// built from a commit that is now BEHIND this checkout's `main`? It is deliberately narrow —
// npm's publish metadata is the only signal used (the `gitHead` npm stamps automatically at
// `npm publish` time from the git working tree, see build.cs's NpmWirePublish task), and the
// comparison only makes sense when the caller is actually inside a git checkout of this repo
// with a resolvable local `main` ref (a dev/CI machine's own clone — most `wire`/`update`/
// `apply` runs happen on machines with NO such checkout at all, and that is a clean skip, never
// an error).
//
// Every failure mode here is best-effort / silent-skip by design (same posture as
// resolveApplyDefinition's LKG fallback and skill-files.ts's probeWorkspace): no network, no
// git binary, not inside a git repo, no local `main` ref, npm registry unreachable/malformed —
// none of these are errors, they are simply "nothing to compare", because the overwhelming
// majority of callers (any user machine outside this monorepo) hit them on every single run.
//
// Plain TS for native node type-stripping: zero deps beyond node:child_process.

import { execFileSync } from "node:child_process";

export const NPM_WIRE_DRIFT_TIMEOUT_MS = 5000;
export const NPM_REGISTRY_PACKAGE_URL = "https://registry.npmjs.org/petbox-wire/latest";

export type NpmWireDriftResult =
  // Not applicable here — most callers, most of the time. `reason` is for a debug log line, not
  // shown to the user by default (doctor/status only print something when there IS a drift to
  // report — see their own callers).
  | { readonly status: "skipped"; readonly reason: string }
  | { readonly status: "in-sync"; readonly gitHead: string }
  // main has moved past what's published; publishedGitHead IS an ancestor of local main.
  | {
      readonly status: "ahead";
      readonly publishedGitHead: string;
      readonly publishedVersion: string;
      readonly localMainHead: string;
      readonly aheadBy: number;
    }
  // publishedGitHead is NOT an ancestor of local main (history rewrite, or main is behind a
  // published commit from a different branch) — reported as a fact, no commit count offered.
  | {
      readonly status: "diverged";
      readonly publishedGitHead: string;
      readonly publishedVersion: string;
      readonly localMainHead: string;
    };

function runGit(args: string[], cwd: string): string | null {
  try {
    return execFileSync("git", args, { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim();
  } catch {
    return null;
  }
}

/** Local `main` branch tip for `cwd`, or null when `cwd` is not inside a git working tree, git
 * is missing, or there is no local `main` ref (any of which is a clean skip upstream). */
function localMainHead(cwd: string): string | null {
  const inWorkTree = runGit(["rev-parse", "--is-inside-work-tree"], cwd);
  if (inWorkTree !== "true") return null;
  const head = runGit(["rev-parse", "--verify", "refs/heads/main"], cwd);
  return head && /^[0-9a-f]{40}$/i.test(head) ? head : null;
}

/** True iff `ancestor` is an ancestor of (or equal to) `descendant`, per `git merge-base
 * --is-ancestor`. Never throws — a missing SHA (e.g. main history was rewritten/shallow-cloned
 * away from the published commit) is reported by its exit code, which execFileSync turns into a
 * thrown error, caught here and folded into `false`. */
function isAncestor(ancestor: string, descendant: string, cwd: string): boolean {
  try {
    execFileSync("git", ["merge-base", "--is-ancestor", ancestor, descendant], {
      cwd,
      stdio: "ignore",
    });
    return true;
  } catch {
    return false;
  }
}

function commitsBetween(from: string, to: string, cwd: string): number | null {
  const out = runGit(["rev-list", "--count", `${from}..${to}`], cwd);
  if (out === null) return null;
  const n = Number.parseInt(out, 10);
  return Number.isFinite(n) ? n : null;
}

type NpmLatestManifest = { readonly version?: unknown; readonly gitHead?: unknown };

async function fetchNpmLatest(opts: {
  readonly timeoutMs?: number;
  readonly fetchImpl?: typeof fetch;
}): Promise<{ readonly version: string; readonly gitHead: string } | null> {
  const timeoutMs = typeof opts.timeoutMs === "number" && opts.timeoutMs > 0 ? opts.timeoutMs : NPM_WIRE_DRIFT_TIMEOUT_MS;
  const fetchFn = opts.fetchImpl ?? fetch;
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const resp = await fetchFn(NPM_REGISTRY_PACKAGE_URL, {
      method: "GET",
      headers: { Accept: "application/json", Connection: "close" },
      signal: ctrl.signal,
    });
    if (!resp.ok) return null;
    const body = (await resp.json().catch(() => null)) as NpmLatestManifest | null;
    const version = typeof body?.version === "string" ? body.version : null;
    const gitHead = typeof body?.gitHead === "string" ? body.gitHead : null;
    if (!version || !gitHead || !/^[0-9a-f]{40}$/i.test(gitHead)) return null;
    return { version, gitHead };
  } catch {
    return null; // network/timeout/parse failure — best-effort, never thrown upstream
  } finally {
    clearTimeout(timer);
  }
}

/**
 * Compare npm's published `latest` petbox-wire against this checkout's local `main` branch tip.
 * `cwd` is the directory to probe from (doctor/status pass their own resolved root — same
 * pattern as resolveApplyDefinition's `cwd`). Never throws.
 */
export async function checkNpmWireDrift(
  cwd: string,
  opts: { readonly timeoutMs?: number; readonly fetchImpl?: typeof fetch } = {},
): Promise<NpmWireDriftResult> {
  const mainHead = localMainHead(cwd);
  if (!mainHead) {
    return { status: "skipped", reason: `${cwd} is not inside a git checkout with a local 'main' ref` };
  }
  const latest = await fetchNpmLatest(opts);
  if (!latest) {
    return { status: "skipped", reason: "npm registry unreachable, or its response had no usable gitHead" };
  }
  if (latest.gitHead === mainHead) {
    return { status: "in-sync", gitHead: mainHead };
  }
  if (isAncestor(latest.gitHead, mainHead, cwd)) {
    const aheadBy = commitsBetween(latest.gitHead, mainHead, cwd) ?? -1;
    return {
      status: "ahead",
      publishedGitHead: latest.gitHead,
      publishedVersion: latest.version,
      localMainHead: mainHead,
      aheadBy,
    };
  }
  return {
    status: "diverged",
    publishedGitHead: latest.gitHead,
    publishedVersion: latest.version,
    localMainHead: mainHead,
  };
}

/** One human-readable line for doctor/status. Never silent about a real drift; "skipped" and
 * "in-sync" are the two boring, expected cases and get one short line each. */
export function formatNpmWireDrift(result: NpmWireDriftResult): string {
  switch (result.status) {
    case "skipped":
      return `npm-wire tag check: skipped (${result.reason})`;
    case "in-sync":
      return `npm-wire tag check: in sync — npm 'latest' is built from local main (${result.gitHead.slice(0, 12)})`;
    case "ahead":
      return (
        `npm-wire tag check: STALE — local main is ${result.aheadBy >= 0 ? result.aheadBy : "some number of"} ` +
        `commit(s) ahead of npm 'latest' (v${result.publishedVersion}, gitHead ` +
        `${result.publishedGitHead.slice(0, 12)}); the 'npm-wire' tag has not been pushed for this merge.`
      );
    case "diverged":
      return (
        `npm-wire tag check: npm 'latest' (v${result.publishedVersion}, gitHead ` +
        `${result.publishedGitHead.slice(0, 12)}) is not an ancestor of local main (${result.localMainHead.slice(0, 12)}) — ` +
        `history diverged; compare manually.`
      );
  }
}
