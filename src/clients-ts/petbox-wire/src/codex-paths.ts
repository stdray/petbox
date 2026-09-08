// Codex CLI home-directory resolution — the one place `petbox-wire` decides where
// `$CODEX_HOME` is, so the global-install step (wire.ts's installGlobalHooks) and the codex
// hook entry points (codex-pull-memory.ts / codex-push-session.ts, indirectly, via the same
// registry-independent path convention codex itself uses) agree.
//
// Mirrors codex's own resolution (codex-spec.md §1, utils/home-dir/src/lib.rs:13-63): an
// explicit `CODEX_HOME` env var wins outright (codex hard-errors if it is set but the directory
// does not exist — this kit does not replicate that hard error, it only reads the value: a
// missing directory is created by mkdirSync at write time, same as every other kit-managed dir);
// otherwise the default is `~/.codex`.
//
// KNOWN GAP (see role-scope.ts's userAgentFilesDir "codex" case for the fuller note): only THIS
// module's callers (wire.ts's global config.toml/hooks.json/model-catalog install) honor
// `CODEX_HOME`. Role-file rendering under `--roles=user` still assumes the default `~/.codex`
// via role-scope.ts's homeDir-relative convention — a real inconsistency this task left
// unresolved rather than restructure that convention machine-wide.
//
// Plain TS for native node type-stripping: zero deps.

import { homedir } from "node:os";
import { join } from "node:path";

export function codexHomeDir(homeDir: string = homedir()): string {
  const envHome = process.env["CODEX_HOME"];
  if (envHome && envHome.trim()) return envHome.trim();
  return join(homeDir, ".codex");
}
