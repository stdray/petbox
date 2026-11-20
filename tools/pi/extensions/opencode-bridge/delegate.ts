/**
 * Running the `opencode` CLI as a child process.
 *
 * No pi imports here on purpose: this module is the whole external contract and
 * is exercised by tests without a pi host.
 *
 * The contract, as measured against opencode 2.3.2 on Windows:
 * - every flag comes AFTER the `run` subcommand — before it, the CLI prints help;
 * - `-m` is mandatory in practice: without it the config default resolves to an
 *   `opencode-go/*` model and the run dies with `403 … subscription required`;
 * - the child's stdin must be closed, otherwise `run` reads stdin to EOF and hangs;
 * - `OPENCODE_CONFIG_DIR` makes opencode load ONLY that directory's config, which
 *   is how the caller keeps the repo's MCP servers out of the child;
 * - stdout is NDJSON, one event per line.
 */

import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import * as path from "node:path";

import { aggregate, parseEventLine } from "./parse.ts";
import type { OpencodeErrorInfo, OpencodeEvent, RunResult } from "./types.ts";

/** Model used when the caller passes none. Free tier, so `usage.cost === 0`. */
export const DEFAULT_MODEL = "opencode/deepseek-v4.1-flash-free";

/** Wall-clock budget for one `runOpencode()` call when the caller sets none. */
export const DEFAULT_TIMEOUT_MS = 300_000;

/** Longest text tail kept for the model-facing result (64 KB). */
export const DEFAULT_MAX_TEXT_CHARS = 64 * 1024;

/** How long to wait for `close` after the kill signal before settling anyway. */
export const KILL_GRACE_MS = 3_000;

/**
 * Argv budget for the prompt.
 *
 * The prompt rides the command line (stdin is closed on purpose), and Windows caps a
 * command line at 32 767 characters — past that `CreateProcess` fails with an opaque error.
 * Counting CHARACTERS is not enough: Node escapes each `"` and `\` as `\\` and `\"`, so a
 * quote-heavy prompt of ~16 000 characters already costs ~32 000 on the wire. The guard
 * therefore counts the ESCAPED size plus the flags, model, session id and separator
 * (`ARGV_FIXED_OVERHEAD_CHARS`).
 */
export const MAX_ARGV_CHARS = 32_000;

/** Flags, model, session id, separator — the non-prompt part of the command line. */
export const ARGV_FIXED_OVERHEAD_CHARS = 400;

/** What the prompt costs on the command line once Node escapes it for Windows. */
export function estimateArgvCost(prompt: string): number {
	let escaped = ARGV_FIXED_OVERHEAD_CHARS;
	for (const ch of prompt) escaped += ch === '"' || ch === "\\" ? 2 : 1;
	return escaped;
}

/** What the child is allowed to do. Everything not named here is denied. */
export interface ChildPolicy {
	/** Let the child edit and write files (opencode's `edit`, plus `write`). */
	allowWrite?: boolean;
	/** Let the child run shell commands. */
	allowShell?: boolean;
}

/**
 * Config written into the per-call `OPENCODE_CONFIG_DIR`.
 *
 * Two deliberate properties, both measured against opencode 2.3.2:
 *
 * - **No `mcp` key.** opencode loads ONLY this directory's config, so the child never
 *   sees the repo's `.opencode/opencode.json` and its production PetBox MCP server.
 * - **An explicit deny policy.** `--auto` approves anything the config does not deny, so
 *   a config without permissions means an unattended child that can write files and run
 *   shell in the session cwd. `"*": "deny"` plus the read-only allows makes the default
 *   a read-only agent: measured, the child reports "this session only exposes read-only
 *   tools", and no file is written even when `--auto` is passed. The catch-all is FIRST
 *   and later specific rules override it — that order is what was measured.
 *
 * `write` is normalised to `edit` by opencode (`debug config` shows one `edit` action per
 * key), but both are emitted so neither name silently falls back to a deny.
 */
export function buildChildConfig(model: string, policy: ChildPolicy = {}): Record<string, unknown> {
	const allow = (on: boolean) => (on ? "allow" : "deny");
	return {
		$schema: "https://opencode.ai/config.json",
		model,
		permission: {
			"*": "deny",
			read: "allow",
			glob: "allow",
			grep: "allow",
			websearch: "allow",
			edit: allow(Boolean(policy.allowWrite)),
			write: allow(Boolean(policy.allowWrite)),
			shell: allow(Boolean(policy.allowShell)),
			subagent: "deny",
		},
	};
}

/** `--auto` is only meaningful once something is actually approved. */
export function needsAutoApprove(policy: ChildPolicy): boolean {
	return Boolean(policy.allowWrite || policy.allowShell);
}

/** Longest stderr tail kept for diagnostics. */
export const DEFAULT_MAX_STDERR_CHARS = 8 * 1024;

/** A single NDJSON line longer than this is discarded rather than buffered. */
export const DEFAULT_MAX_LINE_CHARS = 1024 * 1024;

export interface InvocationOptions {
	prompt: string;
	/** `--agent <role>`; omitted when undefined. */
	agent?: string;
	/** `provider/model`, optionally already carrying `#variant`. */
	model?: string;
	/** Appended as `#variant` unless `model` already has one. */
	variant?: string;
	/** `-s <sessionID>`: resume that session. */
	session?: string;
	/** Working directory for the child; also pinned into `PWD` (see `buildInvocation`). */
	cwd?: string;
	/** `--auto`: auto-approve permissions the child's config does not deny. */
	auto?: boolean;
	/** Passed to the child as `OPENCODE_CONFIG_DIR`. */
	configDir?: string;
	/** Extra child env, layered over `baseEnv`. */
	env?: Record<string, string | undefined>;
	/** Base env, defaults to `process.env`. */
	baseEnv?: Record<string, string | undefined>;
	/** Override the resolved executable (tests, unusual installs). */
	executable?: string;
}

export interface Invocation {
	command: string;
	args: string[];
	env: Record<string, string | undefined>;
}

let cachedExecutable: string | undefined;

/**
 * The executable to spawn.
 *
 * On Windows `spawn()` does not apply PATHEXT, and a `.cmd` shim is not an
 * executable image, so a bare `"opencode"` fails with ENOENT even though
 * `opencode` is on PATH. The npm/bun-style shim is therefore read and the
 * `.exe` it launches is used directly — that also keeps the prompt out of any
 * shell (no quoting hazard from `&`, `|`, `^` or quotes in the prompt).
 */
export function resolveOpencodeExecutable(): string {
	if (cachedExecutable) return cachedExecutable;
	if (process.platform !== "win32") {
		cachedExecutable = "opencode";
		return cachedExecutable;
	}

	const dirs = (process.env.PATH ?? "").split(path.delimiter).filter(Boolean);
	for (const dir of dirs) {
		const direct = path.join(dir, "opencode.exe");
		if (existsSync(direct)) {
			cachedExecutable = direct;
			return cachedExecutable;
		}
		for (const shimName of ["opencode.cmd", "opencode.bat"]) {
			const shim = path.join(dir, shimName);
			if (!existsSync(shim)) continue;
			const target = exeFromShim(shim);
			if (target) {
				cachedExecutable = target;
				return cachedExecutable;
			}
		}
	}

	throw new Error(
		"opencode-bridge: could not find an opencode executable on PATH " +
			'(no `opencode.exe`, and no `opencode.cmd`/`.bat` shim pointing at a .exe). ' +
			"Pass `executable` in the tool call or install opencode so that a shim is on PATH.",
	);
}

/** Recover the `.exe` a `%dp0%`-style shim launches. Undefined when it cannot. */
function exeFromShim(shimPath: string): string | undefined {
	let body: string;
	try {
		body = readFileSync(shimPath, "utf8");
	} catch {
		return undefined;
	}
	const dir = path.dirname(shimPath) + path.sep;
	const expanded = body.replace(/%~?dp0~?%?/gi, dir).replace(/%~dp0/gi, dir);
	const quoted = expanded.match(/["']([^"'\r\n]+\.exe)["']/i);
	const bare = quoted ?? expanded.match(/([A-Za-z]:[\\/][^\r\n"'*?]*\.exe)/i);
	if (!bare) return undefined;
	const candidate = path.normalize(bare[1].replace(/\\\\/g, "\\"));
	return existsSync(candidate) ? candidate : undefined;
}

/** Model argument as `provider/model` or `provider/model#variant`. */
export function modelArgument(model?: string, variant?: string): string {
	const base = (model?.trim() || DEFAULT_MODEL).trim();
	if (!variant) return base;
	return base.includes("#") ? base : `${base}#${variant}`;
}

/**
 * Build `{command, args, env}` for one run. Pure apart from reading
 * `process.env` (and the PATH, when resolving the executable) — no spawning.
 */
export function buildInvocation(opts: InvocationOptions): Invocation {
	const argvCost = estimateArgvCost(opts.prompt);
	if (argvCost > MAX_ARGV_CHARS) {
		throw new Error(
			`opencode-bridge: prompt is ${opts.prompt.length} characters but costs ${argvCost} on the ` +
				`command line once escaped, over the ${MAX_ARGV_CHARS}-character argv budget ` +
				"(Windows CreateProcess allows 32 767; quotes and backslashes double when escaped, so a " +
				"quote-heavy prompt can blow the limit long before its character count does). The prompt " +
				"has to travel on the command line — the child's stdin is closed on purpose, so `opencode " +
				"run` cannot block on it. Shorten the prompt, strip embedded files, split the task, or run " +
				"the opencode agent directly with the prompt supplied another way.",
		);
	}
	const command = opts.executable ?? resolveOpencodeExecutable();
	const args: string[] = [
		"run",
		"--standalone",
		"--archived",
		"--format",
		"json",
		"-m",
		modelArgument(opts.model, opts.variant),
	];

	if (opts.agent) args.push("--agent", opts.agent);
	if (opts.session) args.push("-s", opts.session);
	if (opts.auto) args.push("--auto");
	// `--` ends flag parsing, so a prompt starting with `-` stays a message.
	args.push("--", opts.prompt);

	const env: Record<string, string | undefined> = {
		...(opts.baseEnv ?? process.env),
		...opts.env,
	};
	if (opts.configDir) env.OPENCODE_CONFIG_DIR = opts.configDir;
	// opencode takes its working directory from `PWD` when that variable is set, NOT from
	// the spawn cwd. Measured: spawn cwd = A, PWD = B, and every child tool ran in B. A pi
	// session started from a shell inherits that shell's `PWD`, so a caller that passes a
	// different `cwd` would otherwise have the child work in the wrong directory.
	if (opts.cwd) env.PWD = opts.cwd;

	return { command, args, env };
}

export interface RunOptions extends InvocationOptions {
	timeoutMs?: number;
	signal?: AbortSignal;
	maxTextChars?: number;
	maxStderrChars?: number;
	maxLineChars?: number;
	/** Called for every parsed event, in arrival order (progress reporting). */
	onEvent?: (event: OpencodeEvent) => void;
}

/** How a run ended, as the tool-facing caller needs to know it. */
export interface ResultVerdict {
	/** Must be reported as a failed tool call. */
	failed: boolean;
	/** Terminal-format drift: exit 0 and no answer, on a stream we could not fully parse. */
	drift: boolean;
	/** Human-readable notes, in report order. */
	reasons: string[];
}

/**
 * Decide whether a finished run counts as success, and what to report.
 *
 * The drift rule exists because a silent format change is otherwise indistinguishable
 * from a quiet agent: an `opencode` upgrade that renames events yields `exit 0`, zero
 * steps, no text and a pile of unparsable lines — which used to come back as a *successful*
 * empty answer. Exit 0 with no text and any signal that we lost the format is a failure.
 */
export function classifyResult(
	result: Pick<
		RunResult,
		"exitCode" | "text" | "steps" | "malformedLines" | "unknownEvents" | "error" | "timedOut" | "aborted"
	>,
): ResultVerdict {
	const reasons: string[] = [];
	if (result.error) reasons.push(`error: ${result.error.message}`);
	if (result.timedOut) reasons.push("timed out — the child was killed");
	if (result.aborted) reasons.push("aborted");
	if (result.exitCode !== null && result.exitCode !== 0) reasons.push(`child exited with ${result.exitCode}`);
	if (result.malformedLines > 0) {
		reasons.push(`${result.malformedLines} stdout line(s) were not valid events`);
	}
	if (result.unknownEvents > 0) {
		reasons.push(`${result.unknownEvents} event(s) had a type this bridge does not model (format drift?)`);
	}

	const failedOnProcess =
		Boolean(result.error) || result.timedOut || result.aborted || (result.exitCode ?? 0) !== 0;
	const drift =
		result.exitCode === 0 &&
		result.text.trim().length === 0 &&
		(result.steps === 0 || result.malformedLines > 0 || result.unknownEvents > 0);
	if (drift) {
		reasons.unshift(
			`opencode exited 0 but produced no answer text (steps ${result.steps}, ` +
				`${result.malformedLines} unparsable line(s), ${result.unknownEvents} unknown event(s)) — ` +
				"the CLI output format probably changed",
		);
	}

	return { failed: failedOnProcess || drift, drift, reasons };
}

/** Shown when a run finished cleanly and the agent simply said nothing. */
export const EMPTY_ANSWER_PLACEHOLDER = "(the opencode agent produced no text)";

/**
 * The body of the tool result's text.
 *
 * A placeholder must never stand in for a failed run: the parent model reports it as the
 * answer ("the opencode agent produced no text") next to the real error, which reads like
 * a result. On failure the reasons alone speak — the caller's error text IS the message.
 */
export function resultBody(answer: string, verdict: ResultVerdict): string {
	const trimmed = answer.trim();
	if (trimmed.length > 0) return trimmed;
	return verdict.failed ? "" : EMPTY_ANSWER_PLACEHOLDER;
}

function tail(text: string, maxChars: number): { text: string; truncated: boolean } {
	if (text.length <= maxChars) return { text, truncated: false };
	return { text: text.slice(text.length - maxChars), truncated: true };
}

/** Kill the child and everything it started (opencode's `--standalone` server). */
function killTree(pid: number | undefined) {
	if (pid === undefined) return;
	if (process.platform === "win32") {
		try {
			const killer = spawn("taskkill", ["/pid", String(pid), "/T", "/F"], {
				stdio: "ignore",
				windowsHide: true,
			});
			killer.on("error", () => {});
		} catch {
			// fall through to the plain kill below
		}
		return;
	}
	try {
		process.kill(-pid, "SIGKILL");
	} catch {
		// not a process-group leader; the caller's child.kill() covers it
	}
}

/**
 * Spawn one `opencode run`, stream its events, and resolve with the aggregate.
 *
 * Process-level failures — spawn error, timeout, abort, non-zero exit — come back as data
 * (`exitCode: null`, `timedOut`/`aborted`, `error`) rather than exceptions, because the
 * caller turns them into a tool result.
 *
 * It DOES reject for one class of failure: when the invocation itself cannot be built — no
 * `opencode` executable on PATH, or a prompt past the argv limit. That happens before any
 * process exists, so there is nothing to aggregate, and swallowing it into `exitCode: null`
 * would hide the reason.
 */
export function runOpencode(opts: RunOptions): Promise<RunResult> {
	let invocation: Invocation;
	try {
		invocation = buildInvocation(opts);
	} catch (err) {
		return Promise.reject(err instanceof Error ? err : new Error(String(err)));
	}
	return startRun(opts, invocation);
}

function startRun(opts: RunOptions, invocation: Invocation): Promise<RunResult> {
	const timeoutMs = opts.timeoutMs ?? DEFAULT_TIMEOUT_MS;
	const maxTextChars = opts.maxTextChars ?? DEFAULT_MAX_TEXT_CHARS;
	const maxStderrChars = opts.maxStderrChars ?? DEFAULT_MAX_STDERR_CHARS;
	const maxLineChars = opts.maxLineChars ?? DEFAULT_MAX_LINE_CHARS;
	const startedAt = Date.now();

	const events: OpencodeEvent[] = [];
	let malformedLines = 0;
	let unknownEvents = 0;

	if (opts.signal?.aborted) {
		return Promise.resolve({
			...aggregate([]),
			error: { type: "aborted", message: "aborted before the child was started" },
			exitCode: null,
			killedBy: null,
			stderrTail: "",
			timedOut: false,
			aborted: true,
			durationMs: 0,
			malformedLines,
			unknownEvents,
			textTruncated: false,
			executable: invocation.command,
		});
	}

	return new Promise<RunResult>((resolvePromise) => {
		let settled = false;
		let graceTimer: ReturnType<typeof setTimeout> | undefined;
		let timedOut = false;
		let aborted = false;
		let spawnError: OpencodeErrorInfo | undefined;
		let stderrTail = "";
		let lineBuf = "";
		let discardingLine = false;

		const child = spawn(invocation.command, invocation.args, {
			cwd: opts.cwd,
			env: invocation.env,
			// stdin MUST be closed: `run` otherwise reads it to EOF and hangs.
			stdio: ["ignore", "pipe", "pipe"],
			windowsHide: true,
			shell: false,
			// POSIX only: a process-group leader, so `killTree` can take the `--standalone`
			// server down with the CLI. On Windows `taskkill /T /F` does the same job and
			// `detached` would only open a console window.
			detached: process.platform !== "win32",
		});

		/**
		 * Settle anyway if the child ignores the kill.
		 *
		 * A signal is a request, not a state change: a wedged child (or a failed `taskkill`)
		 * would otherwise leave this promise pending forever and the tool call hanging.
		 */
		const armGrace = () => {
			if (settled || graceTimer) return;
			graceTimer = setTimeout(() => {
				finish(null, "unsettled");
			}, KILL_GRACE_MS);
			if (typeof graceTimer.unref === "function") graceTimer.unref();
		};

		const onAbort = () => {
			if (settled) return;
			aborted = true;
			killTree(child.pid);
			child.kill();
			armGrace();
		};
		opts.signal?.addEventListener("abort", onAbort, { once: true });

		const timer = setTimeout(() => {
			if (settled) return;
			timedOut = true;
			killTree(child.pid);
			child.kill();
			armGrace();
		}, timeoutMs);
		if (typeof timer.unref === "function") timer.unref();

		const handleLine = (line: string) => {
			if (discardingLine) {
				discardingLine = false;
				return;
			}
			const parsed = parseEventLine(line);
			if (!parsed.ok) {
				if (parsed.error !== "empty") malformedLines += 1;
				return;
			}
			if (!parsed.known) unknownEvents += 1;
			events.push(parsed.event);
			try {
				opts.onEvent?.(parsed.event);
			} catch {
				// a progress callback must not be able to break the run
			}
		};

		child.stdout?.setEncoding("utf8");
		child.stdout?.on("data", (chunk: string) => {
			lineBuf += chunk;
			let newline = lineBuf.indexOf("\n");
			while (newline >= 0) {
				handleLine(lineBuf.slice(0, newline));
				lineBuf = lineBuf.slice(newline + 1);
				newline = lineBuf.indexOf("\n");
			}
			if (lineBuf.length > maxLineChars) {
				lineBuf = "";
				discardingLine = true;
				malformedLines += 1;
			}
		});

		child.stderr?.setEncoding("utf8");
		child.stderr?.on("data", (chunk: string) => {
			stderrTail += chunk;
			if (stderrTail.length > maxStderrChars * 4) {
				stderrTail = stderrTail.slice(stderrTail.length - maxStderrChars);
			}
		});

		const finish = (exitCode: number | null, killedBy: string | null) => {
			if (settled) return;
			settled = true;
			clearTimeout(timer);
			if (graceTimer) clearTimeout(graceTimer);
			opts.signal?.removeEventListener("abort", onAbort);
			if (lineBuf.trim().length > 0) handleLine(lineBuf);
			lineBuf = "";

			const agg = aggregate(events);
			const capped = tail(agg.text, maxTextChars);
			let error = agg.error;
			if (!error && spawnError) error = spawnError;

			const result: RunResult = {
				...agg,
				text: capped.text,
				exitCode,
				killedBy,
				stderrTail: tail(stderrTail, maxStderrChars).text,
				timedOut,
				aborted,
				durationMs: Date.now() - startedAt,
				malformedLines,
				unknownEvents,
				textTruncated: capped.truncated,
				executable: invocation.command,
			};
			if (error) result.error = error;
			else delete (result as { error?: unknown }).error;
			resolvePromise(result);
		};

		child.on("error", (err: Error) => {
			spawnError = { type: "spawn", message: err.message };
			finish(null, null);
		});
		child.on("close", (code: number | null, signal: string | null) => {
			finish(code, signal);
		});
	});
}
