/**
 * child-session-dir — persisted subagent sessions are written to `<project session dir>/_children/`
 * instead of beside the user's own conversations.
 *
 * Why: with `rememberAgents` on (the default since @tintinweb/pi-subagents 0.22), every top-level
 * spawn writes a real session file into pi's normal store. pi's listing reads that directory ONE
 * level deep (`listSessionsFromDir`, pi dist/core/session-manager.js — `readdir` + a `.jsonl`
 * filter, no recursion; `listAll` walks exactly one level of project directories), so every
 * `scout#1b8c729` shows up in `/resume` and in any other browser of that directory as a
 * conversation the user started. Observed here: 117 of the files in the petbox project directory
 * carry a `parentSession` header.
 *
 * Why a runtime redirect and not a patched `dist/agent-runner.js`: upstream ships a release every
 * ~3 days (7 in the 30 days before 2026-11-17). The line that decides the directory,
 * `SessionManager.create(effectiveCwd, configuredSessionDir ?? defaultSessionDir, {…})`, was
 * byte-identical in all eight published versions 0.22.0…0.26.0 while the file hash changed in five
 * of seven upgrades — so a hash-gated patcher would have declined to patch almost every release.
 * `SessionManager.create` is a writable static on a non-frozen public export, and pi-subagents
 * looks it up at call time (`dist/agent-runner.js:1007` in 0.25.0), so redirecting it from an
 * extension loaded at startup reaches every spawn without touching `node_modules`.
 *
 * Which calls are children, and nothing else: `SessionManager.create(cwd, dir, { parentSession })`
 * is the shape pi-subagents uses for a spawn — its `opts.parentSession` is the spawning session's
 * file. pi's own forking goes through the INSTANCE method `newSession({ parentSession })`, which
 * keeps the current directory, so a `/fork` or `/branch` is untouched by this patch.
 *
 * Two pass-throughs, both deliberate:
 *   - `sessionDir` given ⇒ someone configured it (an agent's `session_dir:` frontmatter, or the
 *     global `sessionDir` setting / `PI_CODING_AGENT_SESSION_DIR`). pi-subagents passes
 *     `configuredSessionDir ?? defaultSessionDir` and the latter is undefined unless a global
 *     setting exists, so a defined value means an explicit choice we do not overrule.
 *   - parent already under `_children` ⇒ a nested spawn stays beside its own parent rather than
 *     creating `_children/_children`.
 *
 * Trade-off accepted: the redirect is invisible when it stops firing (children simply reappear
 * beside conversations — which is exactly the symptom the owner watches for), and an opt-in
 * per-parent layout upstream (#402) would make this file unnecessary.
 */

import { SessionManager } from "@earendil-works/pi-coding-agent";
import { basename, dirname, join } from "node:path";

const CHILD_DIR = "_children";

const MARKER = Symbol.for("petbox:child-session-dir");

type Marked = { [MARKER]?: true };

function installChildSessionDir(): void {
	const current = SessionManager.create;
	if ((current as unknown as Marked)[MARKER] === true) return;

	const original = current;
	const patched: typeof SessionManager.create = (cwd, sessionDir, options) => {
		const parentFile = options?.parentSession;
		if (!parentFile || sessionDir !== undefined) return original(cwd, sessionDir, options);

		const parentDir = dirname(parentFile);
		if (basename(parentDir) === CHILD_DIR) return original(cwd, sessionDir, options);

		return original(cwd, join(parentDir, CHILD_DIR), options);
	};

	Object.defineProperty(patched, MARKER, { value: true });
	SessionManager.create = patched;
}

// Load-time, not `session_start`: pi imports every extension module during startup, and a spawn
// can only happen once a turn runs, so the redirect is in place before the first child exists.
// Doing it in a handler would work too, but this way the patch does not depend on event plumbing.
installChildSessionDir();

export default function childSessionDir(): void {
	// No registrations: this extension is a redirect, not a tool or a prompt section.
}
