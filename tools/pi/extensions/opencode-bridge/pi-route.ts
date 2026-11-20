/**
 * Client for `@tintinweb/pi-subagents`' cross-extension RPC — the "pi leg" of a route.
 *
 * The contract is theirs, read from
 * `~/.pi/agent/npm/node_modules/@tintinweb/pi-subagents/docs/rpc.md` and the README's
 * cross-extension section:
 *
 * - `subagents:ready` is the discovery signal, emitted on the plugin's first bound
 *   `session_start`. A session that excludes the plugin emits nothing and answers nothing —
 *   indistinguishable from "not installed", so discovery has a TIMEOUT and expiry means "not
 *   available here", never a hang.
 * - `subagents:rpc:spawn` takes `{requestId, type, prompt, options?}` and replies on
 *   `subagents:rpc:spawn:reply:<requestId>` with `{success, data?: {id}, error?}`.
 * - the run then arrives as `subagents:completed` / `subagents:failed` `{id, status, result,
 *   error, usage, tokens, durationMs, ...}` — keyed by the id the spawn handed out, and emitted
 *   for TOP-LEVEL agents only (an RPC spawn is top-level).
 * - `subagents:rpc:consume` (fire and forget) suppresses the plugin's own completion
 *   notification. Called SYNCHRONOUSLY inside the completed handler it stops that notification
 *   from ever being scheduled; skipping it makes the parent pay a model call to dismiss a result
 *   it has already been given.
 *
 * No pi import: the bus is passed in as `BusLike`, so a fake bus drives every branch in tests.
 */

import { isNonRetryableProviderError } from "./route.ts";

/** The slice of `pi.events` this module uses (pi's `EventBus`). */
export interface BusLike {
	emit(channel: string, data: unknown): void;
	on(channel: string, handler: (data: unknown) => void): () => void;
}

export const SUBAGENTS_READY = "subagents:ready";
export const SUBAGENTS_SPAWN = "subagents:rpc:spawn";
export const SUBAGENTS_STOP = "subagents:rpc:stop";
export const SUBAGENTS_CONSUME = "subagents:rpc:consume";
export const SUBAGENTS_COMPLETED = "subagents:completed";
export const SUBAGENTS_FAILED = "subagents:failed";
export const SUBAGENTS_PING = "subagents:rpc:ping";

/** How long to wait for `subagents:ready` / a ping reply before declaring the plugin absent. */
export const DISCOVERY_TIMEOUT_MS = 1_500;

/** Default ceiling for one subagent run. */
export const PI_LEG_TIMEOUT_MS = 600_000;

let ready = false;

/** Called from the extension's `subagents:ready` listener. */
export function markSubagentsReady(value = true): void {
	ready = value;
}

/** True once this process has seen `subagents:ready`. */
export function isSubagentsReady(): boolean {
	return ready;
}

/** Test seam: forget what we knew about the plugin. */
export function resetSubagentsReady(): void {
	ready = false;
}

/** `lifetimeUsage` off the settled record, when the event carried no usage. */
function recordLifetimeUsage(id: string): unknown {
	const manager = (globalThis as Record<symbol, unknown>)[Symbol.for("pi-subagents:manager")] as
		| { getRecord?: (agentId: string) => unknown }
		| undefined;
	const record = manager?.getRecord?.(id) as { lifetimeUsage?: unknown } | undefined;
	return record?.lifetimeUsage;
}

/** `pi-subagents` keeps its manager on a well-known global (docs/rpc.md, "The manager registry"). */
export const SUBAGENTS_MANAGER_KEY = Symbol.for("pi-subagents:manager");

export interface EffectiveInvocation {
	/** Canonical `provider/id` as the child session resolved it. */
	modelId?: string;
	modelName?: string;
	/** The level actually in effect. */
	thinking?: string;
	/** The level that was asked for, when the session clamped or replaced it. */
	requestedThinking?: string;
}

/**
 * What the child session ACTUALLY ran.
 *
 * The record's `invocation` is written back from the session (`agent-manager.ts`, "makes the
 * record authoritative"), which is the only way to see a silent model substitution: pi-subagents
 * resolves a config-specified model id with a fallback to the PARENT model instead of failing
 * (`src/index.ts`, "config-specified: silent fallback to parent"), so the request is not evidence
 * of what ran. Returns undefined when the registry is absent or has already dropped the record.
 */
export function lookupEffectiveInvocation(id: string): EffectiveInvocation | undefined {
	const manager = (globalThis as Record<symbol, unknown>)[SUBAGENTS_MANAGER_KEY] as
		| { getRecord?: (agentId: string) => unknown }
		| undefined;
	const record = manager?.getRecord?.(id) as { invocation?: EffectiveInvocation } | undefined;
	const invocation = record?.invocation;
	if (!invocation || typeof invocation !== "object") return undefined;
	const effective: EffectiveInvocation = {};
	if (typeof invocation.modelId === "string") effective.modelId = invocation.modelId;
	if (typeof invocation.modelName === "string") effective.modelName = invocation.modelName;
	if (typeof invocation.thinking === "string") effective.thinking = invocation.thinking;
	if (typeof invocation.requestedThinking === "string") effective.requestedThinking = invocation.requestedThinking;
	return Object.keys(effective).length > 0 ? effective : undefined;
}

/** The usage shape every leg reports (mirrors `types.ts` Usage). */
export interface BridgeUsage {
	input: number;
	output: number;
	reasoning: number;
	cacheRead: number;
	cacheWrite: number;
	cost?: number;
}

/**
 * Map a `subagents:completed` payload's `usage` into the bridge's shape.
 *
 * The plugin reports a pi `Usage` (`{input, output, reasoning, cacheRead, cacheWrite,
 * cost:{…}}`), and only fills `cost` when its `showCost` setting is on — so `cost` stays
 * OPTIONAL here: the line prints tokens and omits the cost rather than inventing a 0.
 */
export function piUsageToUsage(raw: unknown): BridgeUsage | undefined {
	if (!raw || typeof raw !== "object") return undefined;
	const usage = raw as {
		input?: unknown;
		output?: unknown;
		reasoning?: unknown;
		cacheRead?: unknown;
		cacheWrite?: unknown;
		cost?: { total?: unknown } | unknown;
	};
	const num = (value: unknown) => (typeof value === "number" && Number.isFinite(value) ? value : 0);
	const mapped: BridgeUsage = {
		input: num(usage.input),
		output: num(usage.output),
		reasoning: num(usage.reasoning),
		cacheRead: num(usage.cacheRead),
		cacheWrite: num(usage.cacheWrite),
	};
	const total =
		usage.cost && typeof usage.cost === "object" ? (usage.cost as { total?: unknown }).total : undefined;
	if (typeof total === "number" && Number.isFinite(total)) mapped.cost = total;
	if (mapped.input === 0 && mapped.output === 0 && mapped.cost === undefined) return undefined;
	return mapped;
}

export type PiLegResult =
	| {
			status: "ok";
			id: string;
			text: string;
			usage?: unknown;
			/** The same usage, already mapped to the bridge's shape. */
			bridgeUsage?: BridgeUsage;
			tokens?: unknown;
			durationMs?: number;
			/** Read from the manager registry after settle; absent when the record was unreachable. */
			effective?: EffectiveInvocation;
			recordReachable: boolean;
	  }
	| { status: "unavailable"; reason: string }
	| { status: "error"; id?: string; error: string; nonRetryable: boolean };

export interface PiLegOptions {
	bus: BusLike;
	/** Agent type — the role name. */
	type: string;
	prompt: string;
	/** `"provider/model"`, resolved by the plugin against its model registry. */
	model?: string;
	/** pi thinking level; the plugin clamps it to the model's catalogue. */
	effort?: string;
	cwd?: string;
	description?: string;
	timeoutMs?: number;
	discoveryTimeoutMs?: number;
	/** Called with the agent id as soon as the spawn reply lands. */
	onSpawned?: (id: string) => void;
}

function uuid(): string {
	const maybe = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;
	if (maybe?.randomUUID) return maybe.randomUUID();
	return `req-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

/** Await one value on a channel, tolerating a non-answer. */
function awaitValue(bus: BusLike, channel: string, timeoutMs: number, accept?: (data: unknown) => boolean): Promise<unknown | undefined> {
	return new Promise((resolve) => {
		let done = false;
		const finish = (value: unknown | undefined) => {
			if (done) return;
			done = true;
			clearTimeout(timer);
			try {
				unsubscribe();
			} catch {
				// bus already gone
			}
			resolve(value);
		};
		const unsubscribe = bus.on(channel, (data) => {
			if (accept && !accept(data)) return;
			finish(data);
		});
		// NOT unref'd: this timer governs a promise an awaiter is blocked on, and an unref'd timer
		// in an otherwise idle loop never fires (measured: the unit test hung until the runner).
		const timer = setTimeout(() => finish(undefined), Math.max(1, timeoutMs));
	});
}

/**
 * Is the pi leg usable here?
 *
 * The `subagents:ready` flag is what the listener saw at session start; when it was not seen
 * (plugin filtered out, older build, a session that began before this extension loaded) a ping
 * is the only remaining probe, and it must time out rather than wait.
 */
export async function probePiRoute(bus: BusLike, discoveryTimeoutMs: number = DISCOVERY_TIMEOUT_MS): Promise<boolean> {
	if (ready) return true;
	const requestId = uuid();
	const reply = awaitValue(bus, `${SUBAGENTS_PING}:reply:${requestId}`, discoveryTimeoutMs);
	bus.emit(SUBAGENTS_PING, { requestId });
	const answered = await reply;
	const ok = Boolean(answered && typeof answered === "object" && (answered as { success?: boolean }).success === true);
	if (ok) ready = true;
	return ok;
}

/**
 * Run one prompt on the pi leg.
 *
 * Never rejects and never hangs: absent plugin → `unavailable`; spawn refusal, run failure or a
 * timeout → `error` with `nonRetryable` deciding whether the router may switch legs.
 */
export async function runPiLeg(options: PiLegOptions): Promise<PiLegResult> {
	const { bus } = options;
	const timeoutMs = options.timeoutMs ?? PI_LEG_TIMEOUT_MS;
	const discoveryTimeoutMs = options.discoveryTimeoutMs ?? DISCOVERY_TIMEOUT_MS;

	if (!(await probePiRoute(bus, discoveryTimeoutMs))) {
		return {
			status: "unavailable",
			reason: `pi-subagents did not answer ${SUBAGENTS_PING} within ${discoveryTimeoutMs} ms (plugin absent, or excluded from this session)`,
		};
	}

	const requestId = uuid();
	const completions = new Map<string, (data: unknown) => void>();
	const unsubscribers: Array<() => void> = [];

	const settleFromEvent = (agentId: string, data: unknown) => {
		const handler = completions.get(agentId);
		if (handler) handler(data);
	};

	unsubscribers.push(bus.on(SUBAGENTS_COMPLETED, (data) => {
		const id = (data as { id?: string } | null)?.id;
		if (!id) return;
		// Synchronous consume: this is what stops the plugin notifying the parent about a result
		// it is being given right now.
		bus.emit(SUBAGENTS_CONSUME, { requestId: uuid(), agentId: id });
		settleFromEvent(id, data);
	}));
	unsubscribers.push(bus.on(SUBAGENTS_FAILED, (data) => {
		const id = (data as { id?: string } | null)?.id;
		if (id) settleFromEvent(id, data);
	}));

	const cleanup = () => {
		for (const unsubscribe of unsubscribers.splice(0)) {
			try {
				unsubscribe();
			} catch {
				// bus already gone
			}
		}
		completions.clear();
	};

	try {
		const spawnReply = awaitValue(bus, `${SUBAGENTS_SPAWN}:reply:${requestId}`, discoveryTimeoutMs * 2);
		bus.emit(SUBAGENTS_SPAWN, {
			requestId,
			type: options.type,
			prompt: options.prompt,
			options: {
				description: options.description ?? `delegate: ${options.type}`,
				...(options.model ? { model: options.model } : {}),
				...(options.effort ? { thinkingLevel: options.effort } : {}),
				...(options.cwd ? { cwd: options.cwd } : {}),
			},
		});
		const reply = (await spawnReply) as { success?: boolean; data?: { id?: string }; error?: string } | undefined;
		if (!reply) {
			return { status: "error", error: `pi-subagents did not reply to ${SUBAGENTS_SPAWN}`, nonRetryable: false };
		}
		if (reply.success !== true || !reply.data?.id) {
			const error = reply.error ?? "spawn refused";
			return { status: "error", error, nonRetryable: isNonRetryableProviderError(error) };
		}

		const id = reply.data.id;
		options.onSpawned?.(id);

		const settled = await new Promise<unknown | undefined>((resolve) => {
			let done = false;
			const finish = (value: unknown | undefined) => {
				if (done) return;
				done = true;
				clearTimeout(timer);
				resolve(value);
			};
			completions.set(id, finish);
			const timer = setTimeout(() => finish(undefined), Math.max(1, timeoutMs));
		});

		if (settled === undefined) {
			// Do not leave a runaway agent behind.
			bus.emit(SUBAGENTS_STOP, { requestId: uuid(), agentId: id });
			return {
				status: "error",
				id,
				error: `pi leg timed out after ${timeoutMs} ms (agent ${id} stopped)`,
				nonRetryable: false,
			};
		}

		const payload = settled as { status?: string; result?: string; error?: string; usage?: unknown; tokens?: unknown; durationMs?: number };
		const failed = typeof payload.status === "string" && payload.status !== "completed" && payload.status !== "success";
		if (failed || (typeof payload.error === "string" && payload.error.length > 0) || typeof payload.result !== "string") {
			const error = payload.error?.trim() || `pi subagent ${id} ended with status ${payload.status ?? "unknown"}`;
			return { status: "error", id, error, nonRetryable: isNonRetryableProviderError(error) };
		}

		const effective = lookupEffectiveInvocation(id);
		const ok: PiLegResult = { status: "ok", id, text: payload.result, recordReachable: effective !== undefined };
		if (effective) ok.effective = effective;
		// Usage comes from whichever surface holds it: the event payload first, then the record.
		const bridgeUsage = piUsageToUsage(payload.usage) ?? piUsageToUsage(recordLifetimeUsage(id));
		if (bridgeUsage) ok.bridgeUsage = bridgeUsage;
		if (payload.usage !== undefined) ok.usage = payload.usage;
		if (payload.tokens !== undefined) ok.tokens = payload.tokens;
		if (payload.durationMs !== undefined) ok.durationMs = payload.durationMs;
		return ok;
	} finally {
		cleanup();
	}
}
