// Single source of truth for this kit's per-user `~/.petbox` directory (card
// wire-home-path-centralize). Every home-rooted subpath in the kit composes off petboxDir()
// (directly, or via one of the derivatives below), so a future decision about the on-disk
// layout is a one-function edit, not a grep-and-fix across a dozen files. This card does NOT
// move the directory itself (no XDG, no `~/.config/petbox`) — see role-scope.ts/wire.ts for the
// unrelated per-machine POLICY (roleScope etc.); this module is only about WHERE the directory
// lives.
//
// `homeDir` is injectable (tests only; every real caller uses the default) — the same pattern
// already used by registry.ts/roles.ts/role-scope.ts/wire-log.ts, kept here for consistency.
//
// NOT this module's job (two deliberate carve-outs, do not fold them in):
//   - Hooks running FROM the stable kit mirror (~/.petbox/wire/) resolve their OWN location via
//     import.meta.dirname, not via a homedir()-relative path this kit builds — a different
//     mechanism entirely (see wire.ts's HERE vs STABLE).
//   - posix-env.ts's generated shell source line (`[ -f "$HOME/.petbox/env.sh" ] && …`) is POSIX
//     shell TEXT interpreted by the target login shell via its own $HOME, not a Node path this
//     function produces — left as a literal there, see the comment at its call site.
//
// Plain TS for native node type-stripping: zero deps.

import { homedir } from "node:os";
import { join } from "node:path";

/** The directory name itself — exported ONLY for definition-source.ts's LAYER_DIR_SEGMENTS,
 * which needs the bare segment (not a joined path) to build both the home- and project-rooted
 * agent-layer directories from one list. Every other caller should use petboxDir() or a
 * derivative below instead of this constant. */
export const PETBOX_DIRNAME = ".petbox";

/** Root of this kit's per-user state: `~/.petbox` (or the injected `homeDir` in tests). */
export function petboxDir(homeDir: string = homedir()): string {
  return join(homeDir, PETBOX_DIRNAME);
}

/** Offline canon cache: `~/.petbox/cache` (canon.ts). */
export function petboxCacheDir(homeDir: string = homedir()): string {
  return join(petboxDir(homeDir), "cache");
}

/** Stable kit mirror global hooks/plugins link at: `~/.petbox/wire` (wire.ts's STABLE). */
export function petboxWireMirrorDir(homeDir: string = homedir()): string {
  return join(petboxDir(homeDir), "wire");
}

/** Cross-platform key store: `~/.petbox/keys.json` — a flat JSON map { "<ENV_VAR>": "<key>" }. */
export function petboxKeysJsonPath(homeDir: string = homedir()): string {
  return join(petboxDir(homeDir), "keys.json");
}
