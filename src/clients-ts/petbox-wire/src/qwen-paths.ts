// Qwen Code home-directory resolution — the one place `petbox-wire` decides where
// `$QWEN_HOME` is, so the global-install step (wire.ts's installGlobalHooks) agrees with what
// Qwen Code itself resolves for `~/.qwen/settings.json`.
//
// Mirrors Qwen's own resolution (qwen-spec.md §1/§4, `Storage.getGlobalQwenDir()`,
// core/src/config/storage.ts:193-199 in the clone at D:\my\prj\_analysis\repos\qwen-code): an
// explicit `QWEN_HOME` env var wins outright (Qwen resolves it through `Storage.resolvePath`,
// which this kit does not replicate beyond a trim — a missing directory is created by
// mkdirSync at write time, same as every other kit-managed dir); otherwise the default is
// `~/.qwen`.
//
// NOTE — `QWEN_RUNTIME_DIR` is a DIFFERENT env var that only redirects Qwen's runtime/transcript
// base dir (`Storage.getRuntimeBaseDir()`, storage.ts:169-188 — priority: pinned context >
// QWEN_RUNTIME_DIR > ... > getGlobalQwenDir()), not the settings/hooks home this module
// resolves. `settings.json`, `mcp-oauth-tokens.json`, hooks, etc. all read `getGlobalQwenDir()`,
// which honors ONLY `QWEN_HOME` (storage.ts:193-199) — so this module deliberately does not
// look at `QWEN_RUNTIME_DIR` at all; a hook payload's own `transcript_path` (spec §4) is how the
// kit's hooks find a transcript, never a path this module reconstructs.
//
// KNOWN GAP (same shape as codex-paths.ts's own note): only THIS module's callers (wire.ts's
// global settings.json/hooks/provider install) honor `QWEN_HOME`. Role-file rendering under
// `--roles=user` still assumes the default `~/.qwen` via role-scope.ts's homeDir-relative
// convention — a real inconsistency this task left unresolved, same as codex's.
//
// Plain TS for native node type-stripping: zero deps.

import { homedir } from "node:os";
import { join } from "node:path";

export function qwenHomeDir(homeDir: string = homedir()): string {
  const envHome = process.env["QWEN_HOME"];
  if (envHome && envHome.trim()) return envHome.trim();
  return join(homeDir, ".qwen");
}
