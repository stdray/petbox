// Read-only git classification of the paths this kit manages inside a project
// (card: normalize-all-environments-to-default, item 3 + item 5).
//
// The measurement that made this necessary: the eight registered projects had EIGHT different git
// policies for the identical generated files — `one-c` had 25 of them committed, `kek-devices` had
// 6 tracked + 15 ignored + 4 untracked, `infra` and `petsonde` had 25 untracked with no
// `.gitignore` at all, and three more had them ignored. None of that was visible from any command:
// `status` looked at skill CONTENT and never at git. A default nobody can measure is not a default.
//
// Strictly read-only, and it must stay that way: this runs against seven working directories that
// belong to other people. It shells out to `git` with explicit `-C <dir>` (never a chdir), only
// ever runs `ls-files` and `check-ignore`, and returns "unknown" for every path the moment
// anything goes wrong — a project that is not a git repository at all is a normal answer here,
// not an error.
//
// Plain TS for native node type-stripping: zero deps beyond node:child_process.

import { execFileSync } from "node:child_process";
import { existsSync } from "node:fs";
import { join } from "node:path";

export type PathGitState =
  /** Committed / staged — `git ls-files` lists it. This is what the single policy removes. */
  | "tracked"
  /** Matched by a `.gitignore` rule — the target state for a managed path. */
  | "ignored"
  /** Present on disk, neither tracked nor ignored — it shows up in every `git status`. */
  | "untracked"
  /** Nothing on disk at that path. */
  | "absent"
  /** Not a git working tree, or git could not answer. Never guessed at. */
  | "unknown";

export type GitStateReport = {
  readonly repo: boolean;
  /** One entry per requested relative path, in the requested order. */
  readonly states: readonly { readonly path: string; readonly state: PathGitState }[];
  readonly tracked: number;
  readonly ignored: number;
  readonly untracked: number;
  readonly absent: number;
};

function git(dir: string, args: readonly string[], input?: string): string | null {
  try {
    return execFileSync("git", ["-C", dir, ...args], {
      encoding: "utf8",
      stdio: ["pipe", "pipe", "ignore"],
      ...(input !== undefined ? { input } : {}),
    });
  } catch (e) {
    // check-ignore exits 1 when NOTHING matched — a legitimate answer, not a failure, and its
    // stdout still carries whatever did match (nothing). Only a missing/failed git is a null.
    const status = (e as { status?: number }).status;
    const stdout = (e as { stdout?: string }).stdout;
    if (typeof status === "number" && status === 1 && typeof stdout === "string") return stdout;
    return null;
  }
}

/**
 * Classify each relative path under `dir`. Two git invocations total, whatever the path count:
 * one `ls-files` for the tracked set, one `check-ignore --stdin` for the ignored set. Paths are
 * matched by their POSIX-normalized spelling, which is what git prints on every platform.
 */
export function classifyManagedPaths(dir: string, relPaths: readonly string[]): GitStateReport {
  const norm = (p: string) => p.replace(/\\/g, "/").replace(/\/+$/, "");
  const wanted = relPaths.map(norm);

  const inside = git(dir, ["rev-parse", "--is-inside-work-tree"]);
  if (inside === null || inside.trim() !== "true") {
    return {
      repo: false,
      states: wanted.map((p) => ({ path: p, state: "unknown" as const })),
      tracked: 0,
      ignored: 0,
      untracked: 0,
      absent: 0,
    };
  }

  const tracked = new Set<string>();
  // `ls-files -- <pathspec>` with a DIRECTORY pathspec lists every tracked file under it, so a
  // managed directory counts as tracked when anything inside it is.
  const lsOut = git(dir, ["ls-files", "-z", "--", ...wanted]);
  if (lsOut !== null) {
    for (const f of lsOut.split("\0")) {
      const file = norm(f);
      if (!file) continue;
      for (const w of wanted) {
        if (file === w || file.startsWith(w + "/")) tracked.add(w);
      }
    }
  }

  const ignored = new Set<string>();
  // check-ignore answers per-path; --stdin keeps it to one process. `--no-index` is deliberately
  // NOT passed: a tracked path is not ignored, and git's own precedence is the answer we want.
  const ciOut = git(dir, ["check-ignore", "--stdin"], wanted.join("\n") + "\n");
  if (ciOut !== null) {
    for (const line of ciOut.split(/\r?\n/)) {
      const p = norm(line.trim());
      if (p) ignored.add(p);
    }
  }

  const states = wanted.map((p) => {
    let state: PathGitState;
    if (tracked.has(p)) state = "tracked";
    else if (ignored.has(p)) state = "ignored";
    else if (existsSync(join(dir, p))) state = "untracked";
    else state = "absent";
    return { path: p, state };
  });

  return {
    repo: true,
    states,
    tracked: states.filter((s) => s.state === "tracked").length,
    ignored: states.filter((s) => s.state === "ignored").length,
    untracked: states.filter((s) => s.state === "untracked").length,
    absent: states.filter((s) => s.state === "absent").length,
  };
}

export function formatGitState(report: GitStateReport): string {
  if (!report.repo) return "git: not a repository (managed paths unclassified)";
  const dirty = report.tracked > 0 || report.untracked > 0;
  return (
    `git: tracked=${report.tracked} ignored=${report.ignored} untracked=${report.untracked} ` +
    `absent=${report.absent}` +
    (dirty
      ? ` — NOT on the single policy (managed paths must be ignored, never committed or loose)`
      : ` — on the single policy`)
  );
}
