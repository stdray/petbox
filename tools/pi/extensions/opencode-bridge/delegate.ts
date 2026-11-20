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
 * Never rejects: a spawn failure, a timeout and an abort all come back as data
 * (`exitCode: null`, `timedOut`/`aborted`, `error`), because the caller turns
 * them into a tool result rather than an exception.
 */
export function runOpencode(opts: RunOptions): Promise<RunResult> {
	const invocation = buildInvocation(opts);
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
		});

		const onAbort = () => {
			if (settled) return;
			aborted = true;
			killTree(child.pid);
			child.kill();
		};
		opts.signal?.addEventListener("abort", onAbort, { once: true });

		const timer = setTimeout(() => {
			if (settled) return;
			timedOut = true;
			killTree(child.pid);
			child.kill();
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
