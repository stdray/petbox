/**
 * Steering tools: `steer_delegate`, `abort_delegate`, `delegate_result`, plus the background
 * starter that `delegate({background: true})` calls.
 *
 * The split from `delegate-tool.ts` is deliberate: this file owns everything that needs a
 * REACHABLE session (a live `opencode serve` and its URL), while `delegate-tool.ts` owns the
 * routing decision. A background run is where the two meet: it routes, then keeps the leg's
 * session addressable.
 *
 * Every tool here answers with the same one-line contract as `delegate`:
 *   route <leg> · <role> · <provider>/<model> · <effort> · <reason>
 */

import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import * as path from "node:path";
import { Type } from "typebox";

import { buildChildConfig, DEFAULT_TIMEOUT_MS } from "./delegate.ts";
import { routeLine, type RouteDecision } from "./route.ts";
import { loadRole, planRoleRun, type RoleDefinition } from "./role-config.ts";
import {
	aggregateServeMessages,
	createSession,
	forgetHandle,
	getHandle,
	interruptSession,
	promptSession,
	readMessages,
	registerHandle,
	rememberServer,
	startServe,
	stopServer,
	currentExchange,
	steerSession,
	waitForSettleAfterPrompt,
	type RunHandle,
	type ServeAggregate,
} from "./serve-run.ts";

const OPENCODE_FALLBACK_MODEL = "opencode/hy4-flash-free";

/** Config the server runs with — the same role-derived, deny-by-default shape as the CLI path. */
async function makeConfigDir(role: RoleDefinition | undefined, model: string, allowWrite: boolean, allowShell: boolean): Promise<string> {
	const dir = await mkdtemp(path.join(tmpdir(), "pi-opencode-serve-cfg-"));
	const config = role
		? planRoleRun(role, { model, allowWrite, allowShell, defaultModel: OPENCODE_FALLBACK_MODEL }).config
		: buildChildConfig(model, { allowWrite, allowShell });
	await writeFile(path.join(dir, "opencode.json"), `${JSON.stringify(config, null, 2)}\n`, "utf8");
	return dir;
}

export interface BackgroundStart {
	handle: RunHandle;
	route: RouteDecision;
	configDir: string;
}

/**
 * Start a steerable run and return its handle immediately.
 *
 * Always the opencode leg: a background run is steerable only because a server we hold the URL
 * for owns the session, and the pi leg's sessions are inside this process.
 */
export async function startBackgroundRun(params: {
	prompt: string;
	role?: RoleDefinition;
	model: string;
	effort?: string;
	cwd: string;
	allowWrite?: boolean;
	allowShell?: boolean;
}): Promise<BackgroundStart> {
	const configDir = await makeConfigDir(params.role, params.model, params.allowWrite === true, params.allowShell === true);
	const server = await startServe({ configDir, cwd: params.cwd });
	const route: RouteDecision = {
		target: "opencode",
		provider: params.model.split("/")[0] ?? "",
		model: params.model.split("/").slice(1).join("/"),
		...(params.effort ? { effort: params.effort } : {}),
		reason: "explicit",
		evidence: "background: true — only the server leg exposes a session that can be steered or interrupted",
		decidedAt: new Date().toISOString(),
		legIndex: 1,
	};

	const created = await createSession(server.url, server.password, {
		model: params.model,
		...(params.role ? { agent: params.role.name } : {}),
		title: `delegate ${params.role?.name ?? "default"}`,
	});
	if (!created.sessionId) {
		server.stop();
		await rm(configDir, { recursive: true, force: true }).catch(() => {});
		throw new Error(`delegate: could not create a session on ${server.url}: ${created.error}`);
	}

	const admitted = await promptSession(server.url, server.password, created.sessionId, params.prompt, "queue");
	if (admitted.error) {
		await interruptSession(server.url, server.password, created.sessionId);
		server.stop();
		await rm(configDir, { recursive: true, force: true }).catch(() => {});
		throw new Error(`delegate: the session refused the prompt: ${admitted.error}`);
	}

	const handle = registerHandle({
		id: `run_${created.sessionId.slice(4, 12)}`,
		sessionId: created.sessionId,
		url: server.url,
		model: params.model,
		...(params.role ? { agent: params.role.name } : {}),
		createdAt: new Date().toISOString(),
		routeLine: routeLine(route, params.role?.name),
	});
	rememberServer(handle.id, server.stop);
	rememberPassword(handle.id, server.password);
	return { handle, route, configDir };
}

/** Shared shape: the line first, then whatever the caller asked for. */
function lines(handle: RunHandle, extra: string[] = []): string {
	return [handle.routeLine, ...extra].join("\n");
}

export function registerSteerTools(pi: ExtensionAPI): void {
	pi.registerTool({
		name: "steer_delegate",
		label: "Steer delegate",
		description:
			"Send a correction into a RUNNING delegated run (started with delegate({background: true})). " +
			"Default (soft) hands the message to the agent at its next turn boundary — measured, a run told to count " +
			"to 400 finished the count before answering, so the current task is NOT abandoned. hard: true interrupts " +
			"the running step and resubmits the message as the next prompt on the same session, which is what " +
			"'change the task now' needs. Use abort_delegate to stop a run and delegate_result to collect it.",
		promptSnippet: "Send a mid-run correction to a background delegated run",
		promptGuidelines: [
			"Use steer_delegate when a background delegate run is going the wrong way; it delivers the message into the running session rather than starting a new one.",
			"steer_delegate's first line is the run's route line — quote it when reporting which run was steered.",
		],
		parameters: Type.Object({
			handle: Type.String({ description: "Handle returned by delegate({background: true})." }),
			message: Type.String({ description: "The correction, delivered into the running session." }),
			hard: Type.Optional(
				Type.Boolean({
					description:
						"Interrupt the running step first, then submit the message as the next prompt (the task is " +
						"abandoned immediately). Absent/false keeps the soft behaviour: the agent sees the message at its " +
						"next turn boundary and may finish the current task first.",
				}),
			),
		}),
		async execute(_id, params) {
			const handle = getHandle(params.handle);
			if (!handle) throw new Error(`steer_delegate: unknown handle ${JSON.stringify(params.handle)}`);
			if (!params.message.trim()) throw new Error("steer_delegate: message must not be empty");
			// Both paths go through the same session and the same aggregation: a hard steer is an
			// interrupt plus a prompt on THAT session, never a second one.
			const outcome = await steerSession(
				{ url: handle.url, password: passwordOf(handle), sessionId: handle.sessionId },
				params.message,
				params.hard === true,
			);
			const text = lines(handle, [
				outcome.delivered
					? `${outcome.note} · ${handle.sessionId} · message ${outcome.messageId ?? "(no id)"}` +
						(outcome.interrupted ? " · interrupted=true" : "")
					: `steer FAILED · ${handle.sessionId} · ${outcome.error}`,
			]);
			return outcome.delivered
				? {
						content: [{ type: "text", text }],
						details: { handle, steered: true, hard: params.hard === true, interrupted: outcome.interrupted } as unknown,
					}
				: {
						content: [{ type: "text", text }],
						details: { handle, steered: false, hard: params.hard === true, error: outcome.error } as unknown,
						isError: true,
					};
		},
	});

	pi.registerTool({
		name: "abort_delegate",
		label: "Abort delegate",
		description:
			"Interrupt a running delegated run. Reports the server's own answer: `interrupted: true` means an active " +
			"execution was stopped, `false` means nothing was running (already settled). The run's transcript stays " +
			"readable through delegate_result.",
		promptSnippet: "Interrupt a background delegated run",
		parameters: Type.Object({
			handle: Type.String({ description: "Handle returned by delegate({background: true})." }),
		}),
		async execute(_id, params) {
			const handle = getHandle(params.handle);
			if (!handle) throw new Error(`abort_delegate: unknown handle ${JSON.stringify(params.handle)}`);
			const reply = await interruptSession(handle.url, passwordOf(handle), handle.sessionId);
			const text = lines(handle, [
				reply.error
					? `abort FAILED · ${handle.sessionId} · ${reply.error}`
					: `interrupted=${reply.interrupted} · ${handle.sessionId}`,
			]);
			return reply.error
				? { content: [{ type: "text", text }], details: { handle, interrupted: false, error: reply.error } as unknown, isError: true }
				: { content: [{ type: "text", text }], details: { handle, interrupted: reply.interrupted } as unknown };
		},
	});

	pi.registerTool({
		name: "delegate_result",
		label: "Delegate result",
		description:
			"Collect the answer of a background delegated run. Waits (bounded) for the session to go idle, then returns " +
			"the child's own text plus its usage — the same route line `delegate` prints. After a successful collect the " +
			"handle is released and its server stopped.",
		promptSnippet: "Collect the answer of a background delegated run",
		parameters: Type.Object({
			handle: Type.String({ description: "Handle returned by delegate({background: true})." }),
			timeoutMs: Type.Optional(Type.Number({ description: `How long to wait for the run to settle. Defaults to ${DEFAULT_TIMEOUT_MS}.` })),
		}),
		async execute(_id, params) {
			const handle = getHandle(params.handle);
			if (!handle) throw new Error(`delegate_result: unknown handle ${JSON.stringify(params.handle)}`);
			const budget = params.timeoutMs && params.timeoutMs > 0 ? params.timeoutMs : DEFAULT_TIMEOUT_MS;
			// Only the CURRENT exchange: a hard steer leaves an interrupted step behind, and its
			// error/text are not this run's answer.
			const { settled, messages } = await waitForSettleAfterPrompt(
				handle.url,
				passwordOf(handle),
				handle.sessionId,
				budget,
			);
			const aggregate: ServeAggregate = aggregateServeMessages(currentExchange(messages));
			const body = aggregate.text.trim();
			const text = lines(handle, [
				`${settled ? "settled" : "still running"} · ${handle.sessionId} · steps ${aggregate.steps}` +
					` · tokens in ${aggregate.usage.input} out ${aggregate.usage.output} · cost ${aggregate.usage.cost}` +
					(aggregate.model ? ` · ran ${aggregate.model}` : ""),
				...(aggregate.error ? [`error: ${aggregate.error}`] : []),
				"",
				body.length > 0 ? body : "(the delegated run produced no text)",
			]);
			if (settled) {
				stopServer(handle.id);
				forgetHandle(handle.id);
			}
			const failed = aggregate.error !== undefined || !settled;
			return failed
				? { content: [{ type: "text", text }], details: { handle, aggregate, settled } as unknown, isError: true }
				: { content: [{ type: "text", text }], details: { handle, aggregate, settled } as unknown };
		},
	});
}

/** The password for a handle's server. Kept out of the handle so it never reaches a tool result. */
const passwords = new Map<string, string>();
const serversSeen = new Map<string, string>();

/** Called by the background starter once the handle exists. */
export function rememberPassword(handleId: string, password: string): void {
	passwords.set(handleId, password);
}

function passwordOf(handle: RunHandle): string {
	const password = passwords.get(handle.id) ?? serversSeen.get(handle.url);
	if (!password) throw new Error(`delegate: the server for ${handle.sessionId} is gone (restarted pi?)`);
	return password;
}
