/**
 * Server-backed delegated runs — the half that makes a run steerable.
 *
 * WHY THIS EXISTS: `opencode run --standalone` owns a private server, so once the child starts
 * nothing outside it can reach the session. `opencode serve` exposes the same v2 API on a socket
 * we hold the URL (and the password) for, which is what turns "spawn and hope" into
 * steer / interrupt / collect-later.
 *
 * THE ROUTES ARE MEASURED, not guessed — this is opencode 2.3.2's own OpenAPI document
 * (`GET /openapi.json`, 142 paths, `Authorization: Basic base64("opencode:<OPENCODE_SERVER_PASSWORD>")`):
 *
 *   POST   /api/session                              {title?, agent?, model?}   -> {data:{id}}
 *   POST   /api/session/{id}/prompt                  {text, delivery:"steer"|"queue", resume?}
 *   POST   /api/session/{id}/interrupt               -> {interrupted:boolean}
 *   GET    /api/session/{id}/message?limit&order     -> {data:[…]} (type user|assistant|idle|…)
 *   PATCH  /api/session/{id}/inbox/{inboxID}         {delivery}  (re-aim a queued item)
 *   POST   /api/experimental/session/{id}/wait       -> 204 when the run settles
 *
 * `delivery: "steer"` on a running session is the steering primitive: measured live, a counting
 * run abandoned its task and answered the steer text on the next turn. `queue` admits the prompt
 * and returns immediately, which is why the first prompt uses it.
 *
 * No pi imports: the tools wire this in, and the pure parts (handle store, aggregation, error
 * mapping) are unit-tested without a server.
 */

import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import * as path from "node:path";

import { resolveOpencodeExecutable } from "./delegate.ts";

/** `provider/model` → `Model.Ref`. */
export function modelRef(model: string): { providerID: string; id: string; variant?: string } {
	const hash = model.indexOf("#");
	const base = hash >= 0 ? model.slice(0, hash) : model;
	const slash = base.indexOf("/");
	if (slash <= 0) throw new Error(`serve-run: model ${JSON.stringify(model)} is not "provider/model"`);
	const ref: { providerID: string; id: string; variant?: string } = {
		providerID: base.slice(0, slash),
		id: base.slice(slash + 1),
	};
	if (hash >= 0) ref.variant = model.slice(hash + 1);
	return ref;
}

/** Basic auth: `opencode:<OPENCODE_SERVER_PASSWORD>` (measured; Bearer is rejected). */
export function basicAuth(password: string): string {
	return `Basic ${Buffer.from(`opencode:${password}`).toString("base64")}`;
}

/** What the caller holds onto for a background run. */
export interface RunHandle {
	id: string;
	sessionId: string;
	url: string;
	/** Model the session was created with, `provider/id`. */
	model: string;
	agent?: string;
	createdAt: string;
	/** Route line the run reports in every interaction. */
	routeLine: string;
}

const handles = new Map<string, RunHandle>();

export function registerHandle(handle: RunHandle): RunHandle {
	handles.set(handle.id, handle);
	return handle;
}

export function getHandle(id: string): RunHandle | undefined {
	return handles.get(id);
}

export function listHandles(): RunHandle[] {
	return [...handles.values()];
}

export function forgetHandle(id: string): boolean {
	return handles.delete(id);
}

/** Test seam. */
export function resetHandles(): void {
	handles.clear();
}

export interface ServeProc {
	url: string;
	/** The password this server was started with (never logged, never on a handle). */
	password: string;
	/** Free the port and the process. Idempotent. */
	stop: () => void;
}

export interface StartServeOptions {
	/** The per-call config dir (role-derived, deny-by-default), as `OPENCODE_CONFIG_DIR`. */
	configDir: string;
	cwd?: string;
	timeoutMs?: number;
	env?: Record<string, string | undefined>;
}

/**
 * Start a private `opencode serve` and return its URL.
 *
 * Port 0 and the URL out of its own log line: the server picks the port, so there is no
 * probe-then-race window. The password is generated per server and never logged.
 */
export function startServe(options: StartServeOptions): Promise<ServeProc> {
	return new Promise((resolve, reject) => {
		const password = randomBytes(16).toString("hex");
		const child = spawn(resolveOpencodeExecutable(), ["serve", "--hostname", "127.0.0.1", "--port", "0"], {
			cwd: options.cwd,
			env: {
				...(options.env ?? process.env),
				OPENCODE_CONFIG_DIR: options.configDir,
				OPENCODE_SERVER_PASSWORD: password,
			},
			stdio: ["ignore", "pipe", "pipe"],
			windowsHide: true,
			shell: false,
		});

		let stderr = "";
		let settled = false;
		const timer = setTimeout(() => {
			if (settled) return;
			settled = true;
			child.kill();
			reject(new Error(`serve-run: opencode serve did not report a URL within ${options.timeoutMs ?? 30_000} ms`));
		}, options.timeoutMs ?? 30_000);

		const stop = () => {
			try {
				child.kill();
			} catch {
				// already gone
			}
		};

		child.stdout?.setEncoding("utf8");
		child.stdout?.on("data", (chunk: string) => {
			if (settled) return;
			const match = /server listening on (http:\/\/[^\s]+)/i.exec(chunk);
			if (!match) return;
			settled = true;
			clearTimeout(timer);
			resolve({ url: match[1].replace(/\/$/, ""), password, stop });
		});
		child.stderr?.setEncoding("utf8");
		child.stderr?.on("data", (chunk: string) => {
			stderr += chunk;
			if (stderr.length > 4_000) stderr = stderr.slice(-2_000);
		});
		child.on("error", (err: Error) => {
			if (settled) return;
			settled = true;
			clearTimeout(timer);
			reject(new Error(`serve-run: could not start opencode serve: ${err.message}`));
		});
		child.on("close", (code) => {
			if (settled) return;
			settled = true;
			clearTimeout(timer);
			reject(new Error(`serve-run: opencode serve exited with ${code}: ${stderr.trim().slice(-400)}`));
		});
	});
}

export interface ApiReply<T = unknown> {
	status: number;
	ok: boolean;
	body: T | undefined;
	error?: string;
}

/** One JSON call against the server. Never throws; a failure is data. */
export async function apiCall<T = unknown>(
	url: string,
	password: string,
	method: string,
	route: string,
	body?: unknown,
	signal?: AbortSignal,
): Promise<ApiReply<T>> {
	try {
		const res = await fetch(`${url}${route}`, {
			method,
			headers: { Authorization: basicAuth(password), "Content-Type": "application/json" },
			...(body === undefined ? {} : { body: JSON.stringify(body) }),
			...(signal ? { signal } : {}),
		});
		const text = await res.text();
		let parsed: unknown;
		try {
			parsed = text.length > 0 ? JSON.parse(text) : undefined;
		} catch {
			parsed = undefined;
		}
		const reply: ApiReply<T> = { status: res.status, ok: res.ok, body: parsed as T | undefined };
		if (!res.ok) reply.error = apiError(parsed, res.status);
		return reply;
	} catch (err) {
		return { status: 0, ok: false, body: undefined, error: `transport: ${(err as Error).message}` };
	}
}

/** A server error body is `{_tag, message}`; anything else falls back to the status. */
export function apiError(parsed: unknown, status: number): string {
	const body = parsed as { _tag?: unknown; message?: unknown; error?: unknown } | undefined;
	if (body && typeof body.message === "string") {
		const tag = typeof body._tag === "string" ? `${body._tag}: ` : "";
		return `${tag}${body.message}`;
	}
	if (body && typeof body.error === "string") return body.error;
	return `HTTP ${status}`;
}

// --- messages -------------------------------------------------------------------------------

export interface ServeContentPart {
	type?: string;
	text?: string;
	tool?: string;
	state?: { status?: string; output?: unknown; title?: string };
}

export interface ServeMessage {
	type?: string;
	id?: string;
	role?: string;
	outcome?: string;
	model?: { providerID?: string; id?: string; variant?: string };
	agent?: string;
	content?: ServeContentPart[];
	text?: string;
	cost?: number;
	tokens?: { input?: number; output?: number; reasoning?: number; cache?: { read?: number; write?: number } };
	error?: unknown;
}

export interface ServeAggregate {
	/** Assistant text, in order. */
	text: string;
	/** Last observed `idle` outcome (`succeeded`, `error`, …). */
	outcome?: string;
	error?: string;
	usage: { input: number; output: number; reasoning: number; cacheRead: number; cacheWrite: number; cost: number };
	steps: number;
	/** Provider/model the server says answered — not the request. */
	model?: string;
	/** Tool calls seen in the transcript, `tool:status`. */
	toolCalls: string[];
}

/** Fold v2 session messages into the same shape the NDJSON runner produces. */
export function aggregateServeMessages(messages: ServeMessage[]): ServeAggregate {
	let text = "";
	let outcome: string | undefined;
	let error: string | undefined;
	let model: string | undefined;
	const usage = { input: 0, output: 0, reasoning: 0, cacheRead: 0, cacheWrite: 0, cost: 0 };
	let steps = 0;
	const toolCalls: string[] = [];

	for (const message of messages) {
		if (!message || typeof message !== "object") continue;
		if (message.type === "assistant") {
			steps += 1;
			if (message.model?.providerID && message.model.id) {
				model = `${message.model.providerID}/${message.model.id}`;
			}
			for (const part of message.content ?? []) {
				if (part?.type === "text" && typeof part.text === "string") text += part.text;
				if (part?.type === "tool" && typeof part.tool === "string") {
					toolCalls.push(`${part.tool}:${part.state?.status ?? "unknown"}`);
				}
			}
			usage.input += message.tokens?.input ?? 0;
			usage.output += message.tokens?.output ?? 0;
			usage.reasoning += message.tokens?.reasoning ?? 0;
			usage.cacheRead += message.tokens?.cache?.read ?? 0;
			usage.cacheWrite += message.tokens?.cache?.write ?? 0;
			usage.cost += message.cost ?? 0;
			if (message.error !== undefined && message.error !== null) error = describeError(message.error);
		}
		if (message.type === "idle") {
			outcome = message.outcome;
			if (message.outcome && message.outcome !== "succeeded" && !error) error = `idle outcome: ${message.outcome}`;
		}
	}

	const aggregate: ServeAggregate = { text, usage, steps, toolCalls };
	if (outcome !== undefined) aggregate.outcome = outcome;
	if (error !== undefined) aggregate.error = error;
	if (model !== undefined) aggregate.model = model;
	return aggregate;
}

function describeError(value: unknown): string {
	if (typeof value === "string") return value;
	if (value && typeof value === "object") {
		const body = value as { message?: unknown; _tag?: unknown };
		if (typeof body.message === "string") return body.message;
		if (typeof body._tag === "string") return body._tag;
	}
	return JSON.stringify(value);
}

/** Messages, oldest first, as the API returns them (it paginates newest-first by default). */
export async function readMessages(
	url: string,
	password: string,
	sessionId: string,
	limit = 200,
): Promise<ServeMessage[]> {
	const reply = await apiCall<{ data?: ServeMessage[] }>(
		url,
		password,
		"GET",
		`/api/session/${sessionId}/message?limit=${limit}&order=desc`,
	);
	return (reply.body?.data ?? []).slice().reverse();
}

/** True when the transcript carries an `idle` message — the server's settle marker. */
export function isSettled(messages: ServeMessage[]): boolean {
	return messages.some((message) => message.type === "idle");
}

/** Create a session. The model is pinned so the server cannot fall back to its default. */
export async function createSession(
	url: string,
	password: string,
	options: { model: string; agent?: string; title?: string },
): Promise<{ sessionId?: string; error?: string }> {
	const reply = await apiCall<{ data?: { id?: string } }>(url, password, "POST", "/api/session", {
		...(options.title ? { title: options.title } : {}),
		...(options.agent ? { agent: options.agent } : {}),
		model: modelRef(options.model),
	});
	if (!reply.ok) return { error: reply.error ?? `HTTP ${reply.status}` };
	const sessionId = reply.body?.data?.id;
	return sessionId ? { sessionId } : { error: "session created without an id" };
}

/** Admit a prompt. `steer` on a running session is the steering primitive. */
export async function promptSession(
	url: string,
	password: string,
	sessionId: string,
	text: string,
	delivery: "steer" | "queue" = "queue",
	options: { resume?: boolean } = {},
): Promise<{ messageId?: string; error?: string }> {
	const reply = await apiCall<{ data?: { id?: string } }>(url, password, "POST", `/api/session/${sessionId}/prompt`, {
		text,
		delivery,
		...(options.resume === undefined ? {} : { resume: options.resume }),
	});
	if (!reply.ok) return { error: reply.error ?? `HTTP ${reply.status}` };
	return { messageId: reply.body?.data?.id };
}

/** Interrupt. `interrupted: false` means nothing was running — report that verbatim. */
export async function interruptSession(
	url: string,
	password: string,
	sessionId: string,
): Promise<{ interrupted: boolean; error?: string }> {
	const reply = await apiCall<{ interrupted?: boolean }>(url, password, "POST", `/api/session/${sessionId}/interrupt`);
	if (!reply.ok) return { interrupted: false, error: reply.error ?? `HTTP ${reply.status}` };
	return { interrupted: reply.body?.interrupted === true };
}

/** Poll until an `idle` message appears or the budget runs out. */
export async function waitForSettle(
	url: string,
	password: string,
	sessionId: string,
	timeoutMs: number,
	pollMs = 1_000,
): Promise<{ settled: boolean; messages: ServeMessage[] }> {
	const deadline = Date.now() + timeoutMs;
	let messages = await readMessages(url, password, sessionId);
	while (!isSettled(messages) && Date.now() < deadline) {
		await new Promise((resolve) => setTimeout(resolve, pollMs));
		messages = await readMessages(url, password, sessionId);
	}
	return { settled: isSettled(messages), messages };
}

/** A fresh temp dir holding nothing but the caller's config — the server's config root. */
export function tempConfigDir(prefix = "pi-opencode-serve-"): string {
	return mkdtempSync(path.join(tmpdir(), prefix));
}

/** Servers stay alive past the tool call that started them; the stop function lives here. */
const servers = new Map<string, () => void>();

export function rememberServer(handleId: string, stop: () => void): void {
	servers.set(handleId, stop);
}

/** Stop the server behind a handle, if it is still around. Idempotent. */
export function stopServer(handleId: string): boolean {
	const stop = servers.get(handleId);
	if (!stop) return false;
	servers.delete(handleId);
	stop();
	return true;
}

export function stopAllServers(): number {
	let stopped = 0;
	for (const id of [...servers.keys()]) if (stopServer(id)) stopped += 1;
	return stopped;
}

export interface SteerOutcome {
	delivered: boolean;
	/** True only when an active execution was actually stopped on the way in. */
	interrupted: boolean;
	/** One line for the caller: what happened, and what it means. */
	note: string;
	messageId?: string;
	error?: string;
}

/** Injection seam for the idle-vs-running branch, so it is unit-testable without a server. */
export interface SteerDeps {
	interrupt: (url: string, password: string, sessionId: string) => Promise<{ interrupted: boolean; error?: string }>;
	prompt: (
		url: string,
		password: string,
		sessionId: string,
		text: string,
		delivery: "steer" | "queue",
		options?: { resume?: boolean },
	) => Promise<{ messageId?: string; error?: string }>;
	waitForIdle: (url: string, password: string, sessionId: string, timeoutMs: number) => Promise<boolean>;
}

/** Grace for the interrupted step to reach `idle` before the new prompt is admitted. */
export const HARD_STEER_SETTLE_MS = 10_000;

/**
 * Steer a session, softly or hard.
 *
 * SOFT (`delivery: "steer"`) hands the message to the running agent at its next turn boundary.
 * Measured on opencode 2.3.2: a run told to count to 400 finished the count and only then
 * appended the steer's answer — delivered, but the task was NOT abandoned.
 *
 * HARD interrupts the active step, waits for the session to go idle, and then submits the
 * message as a fresh prompt on the SAME session. Measured: an interrupt lands mid-stream
 * (`interrupted: true`, `error: Step interrupted`, a 900-count run stopped at 281), which is
 * what "change the task now" actually needs.
 *
 * An already-idle session is not an error for a hard steer: there is nothing to interrupt, the
 * message is simply submitted, and the caller is told so without `interrupted` noise.
 */
export async function steerSession(
	target: { url: string; password: string; sessionId: string },
	message: string,
	hard: boolean,
	deps: Partial<SteerDeps> = {},
): Promise<SteerOutcome> {
	const interrupt = deps.interrupt ?? interruptSession;
	const prompt = deps.prompt ?? promptSession;
	const waitForIdle =
		deps.waitForIdle ??
		(async (url, password, sessionId, timeoutMs) => {
			const { settled } = await waitForSettle(url, password, sessionId, timeoutMs);
			return settled;
		});

	if (!hard) {
		const reply = await prompt(target.url, target.password, target.sessionId, message, "steer");
		if (reply.error) return { delivered: false, interrupted: false, note: "soft steer refused", error: reply.error };
		return {
			delivered: true,
			interrupted: false,
			note: "soft steer: delivered to the running turn boundary — the current task may finish first",
			...(reply.messageId ? { messageId: reply.messageId } : {}),
		};
	}

	const stop = await interrupt(target.url, target.password, target.sessionId);
	if (stop.error) return { delivered: false, interrupted: false, note: "hard steer: interrupt failed", error: stop.error };

	const interrupted = stop.interrupted;
	if (interrupted) {
		// The step is stopping, but it owns the session until it reports idle; admitting the new
		// prompt before that would queue behind a step we just killed.
		await waitForIdle(target.url, target.password, target.sessionId, HARD_STEER_SETTLE_MS);
	}

	// `resume: true` is what restarts the agent loop after an interrupt: measured, a plain queued
	// prompt behind an interrupted step is admitted and never runs.
	const reply = await prompt(target.url, target.password, target.sessionId, message, "queue", { resume: true });
	if (reply.error) return { delivered: false, interrupted, note: "hard steer: prompt refused", error: reply.error };
	return {
		delivered: true,
		interrupted,
		note: interrupted
			? "hard steer: running step interrupted, message submitted as the next prompt"
			: "hard steer: session was already idle, message submitted",
		...(reply.messageId ? { messageId: reply.messageId } : {}),
	};
}

/** Index of the last `user` message, or -1. The current exchange starts right after it. */
export function lastUserIndex(messages: ServeMessage[]): number {
	for (let i = messages.length - 1; i >= 0; i -= 1) if (messages[i]?.type === "user") return i;
	return -1;
}

/**
 * The messages of the CURRENT exchange only.
 *
 * A session outlives one prompt — a hard steer leaves an interrupted step behind, and a soft
 * steer leaves the turn it nudged. Reporting the whole transcript would answer with old text
 * and old errors (measured: `error: Step interrupted` from a step the caller already replaced).
 */
export function currentExchange(messages: ServeMessage[]): ServeMessage[] {
	const index = lastUserIndex(messages);
	return index >= 0 ? messages.slice(index + 1) : messages;
}

/** Settled means: an `idle` AFTER the last prompt, not one left over from a previous turn. */
export function isSettledSinceLastPrompt(messages: ServeMessage[]): boolean {
	return currentExchange(messages).some((message) => message.type === "idle");
}

export async function waitForSettleAfterPrompt(
	url: string,
	password: string,
	sessionId: string,
	timeoutMs: number,
	pollMs = 1_000,
): Promise<{ settled: boolean; messages: ServeMessage[] }> {
	const deadline = Date.now() + timeoutMs;
	let messages = await readMessages(url, password, sessionId);
	while (!isSettledSinceLastPrompt(messages) && Date.now() < deadline) {
		await new Promise((resolve) => setTimeout(resolve, pollMs));
		messages = await readMessages(url, password, sessionId);
	}
	return { settled: isSettledSinceLastPrompt(messages), messages };
}
