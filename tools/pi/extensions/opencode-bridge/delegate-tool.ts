/**
 * `delegate` — the routing tool.
 *
 * Try pi's own subagent first; fall to the opencode CLI only when pi's route is known-bad
 * (`route.ts` cache) or just failed with a non-retryable provider error. The decision comes back
 * as data (`RouteDecision`) in the tool result's first line and in `details.route`, because the
 * caller (and two later cards) must be able to read what actually ran instead of inferring it.
 *
 * `opencode_delegate` stays exactly as it is: the explicit opencode path, no routing.
 */

import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import * as path from "node:path";
import { Type } from "typebox";

import { buildChildConfig, DEFAULT_TIMEOUT_MS, runOpencode } from "./delegate.ts";
import { PI_LEG_TIMEOUT_MS, runPiLeg } from "./pi-route.ts";
import {
	clearDead,
	decideRoute,
	routeLine,
	markDead,
	readModelCatalog,
	readRouteCache,
	routeKey,
	type RouteDecision,
	type RouteLeg,
} from "./route.ts";
import { clampLegEfforts, settledEffort, UNAUTHENTICATED_PROVIDERS, loadRole, planRoleRun, type RoleDefinition } from "./role-config.ts";
import { startBackgroundRun } from "./steer-tools.ts";
import type { Usage } from "./types.ts";

/** Opencode-leg model when nothing names one the standalone CLI can authenticate. */
const OPENCODE_FALLBACK_MODEL = "opencode/hy4-flash-free";

const RESULT_HEAD_CHARS = 4_000;
const RESULT_TAIL_CHARS = 8_000;

/** `provider/model` → parts. A bare id keeps an empty provider so the defect stays visible. */
function splitModel(value: string): { provider: string; model: string } {
	const slash = value.indexOf("/");
	if (slash <= 0) return { provider: "", model: value };
	return { provider: value.slice(0, slash), model: value.slice(slash + 1) };
}

/** `provider/model#effort` → parts, effort kept. */
function splitModelEffort(value: string): { provider: string; model: string; effort?: string } {
	const hash = value.indexOf("#");
	const base = hash >= 0 ? value.slice(0, hash) : value;
	const parts = splitModel(base);
	return hash >= 0 ? { ...parts, effort: value.slice(hash + 1) } : parts;
}

/**
 * The model THIS pi session runs.
 *
 * A subagent with no model of its own inherits it, so it is the truthful pi-leg default — and
 * using it keeps the route line's provider slot from being a placeholder.
 */
function parentModelOf(ctx: unknown): string | undefined {
	const model = (ctx as { model?: { provider?: unknown; id?: unknown } } | null)?.model;
	if (!model || typeof model !== "object") return undefined;
	const provider = typeof model.provider === "string" ? model.provider : "";
	const id = typeof model.id === "string" ? model.id : "";
	return provider && id ? `${provider}/${id}` : undefined;
}

function headTail(text: string, head: number, tail: number): string {
	if (text.length <= head + tail) return text;
	return `${text.slice(0, head)}\n…[${text.length - head - tail} chars omitted]…\n${text.slice(text.length - tail)}`;
}

function describeUsage(usage: Usage): string {
	return `tokens in ${usage.input} out ${usage.output} reasoning ${usage.reasoning} | cost ${usage.cost}`;
}

async function makeConfigDir(config: Record<string, unknown>): Promise<string> {
	const dir = await mkdtemp(path.join(tmpdir(), "pi-opencode-bridge-"));
	await writeFile(path.join(dir, "opencode.json"), `${JSON.stringify(config, null, 2)}\n`, "utf8");
	return dir;
}

/** Same shape for every result of this tool, including progress updates. */
interface DelegateDetails {
	route: RouteDecision;
	/** Set for a background run: the handle the steer/abort/result tools take. */
	handle?: string;
	agentId?: string;
	piFailure?: string;
	opencode?: {
		text: string;
		sessionId?: string;
		usage?: Usage;
		exitCode?: number | null;
		timedOut?: boolean;
		error?: string;
	};
	sessionId?: string;
	usage?: Usage;
	exitCode?: number | null;
	timedOut?: boolean;
	durationMs: number;
}

/**
 * Build the ordered legs.
 *
 * The pi leg may legitimately use a model the standalone CLI cannot authenticate (pi holds those
 * provider keys); the opencode leg may not, so a role pinned to `opencode-go/*` still falls back
 * to a CLI-usable model instead of dying in a handshake.
 */
function buildLegs(
	role: RoleDefinition | undefined,
	effort: string | undefined,
	requestedModel: string | undefined,
	parentModel: string | undefined,
): RouteLeg[] {
	// Precedence: the role's pin, then the caller's model, then what this pi session runs anyway.
	// The last one is the truth (an unset subagent model inherits the parent), which is also why
	// the pi leg is never a "pi/default" placeholder on the line.
	const piModel = role?.model ?? requestedModel ?? parentModel;
	const piLeg: RouteLeg = piModel
		? { target: "pi", ...splitModelEffort(piModel), effort }
		: { target: "pi", provider: "unknown", model: "unknown", effort };
	// Never the caller's explicit model: naming a model asks for it on the pi leg, and the
	// opencode leg is what runs when that model is dead — giving it the same dead model would
	// turn a fallback into a second failure.
	const opencodeModel =
		role?.model && !UNAUTHENTICATED_PROVIDERS.has(splitModel(role.model).provider) ? role.model : OPENCODE_FALLBACK_MODEL;
	return [piLeg, { target: "opencode", ...splitModelEffort(opencodeModel), effort }];
}

export function registerDelegateTool(pi: ExtensionAPI): void {
	pi.registerTool({
		name: "delegate",
		label: "Delegate",
		description:
			"Route one prompt to a working agent harness and return its answer: pi's own subagent first, opencode only " +
			"when pi's route is known-bad or just failed. The first line of the result is " +
			'"route <leg> · <role> · <provider>/<model> · <effort> · <reason>", and the same decision is ' +
			"returned as structured data (target/provider/model/effort/reason/evidence) — including the model " +
			"that actually ran when it differs from the one requested. " +
			"Use opencode_delegate when opencode specifically is wanted.",
		promptSnippet: "Route a prompt to pi's subagent or opencode, and report which ran and why",
		promptGuidelines: [
			"Use delegate when another agent should do the work but it does not matter which harness runs it.",
			"delegate's first line is `[route target=… provider=… model=… reason=…]` — quote it instead of guessing which leg ran.",
			"Pass resetRoute: true to delegate to forget a route marked dead; a spent quota comes back when the account is reconnected, not on a timer.",
		],
		parameters: Type.Object({
			prompt: Type.String({ description: "The prompt for the delegated agent. Include all context it needs." }),
			role: Type.Optional(
				Type.String({
					description:
						"pi role name from ~/.pi/agent/agents/<role>.md — the subagent type on the pi leg, the system prompt " +
						"and permission ceiling on the opencode leg.",
				}),
			),
			model: Type.Optional(
				Type.String({ description: 'Explicit "provider/model": wins outright (reason "explicit") and pins the target.' }),
			),
			effort: Type.Optional(
				Type.String({ description: "pi thinking level (off/minimal/low/medium/high/xhigh/max). Defaults to the role's thinking." }),
			),
			cwd: Type.Optional(Type.String({ description: "Working directory. Defaults to the pi session cwd." })),
			allowWrite: Type.Optional(Type.Boolean({ description: "DANGEROUS: let the opencode leg edit and write files. Denied by default." })),
			allowShell: Type.Optional(Type.Boolean({ description: "DANGEROUS: let the opencode leg run shell commands. Denied by default." })),
			resetRoute: Type.Optional(
				Type.Boolean({
					description:
						"Clear the dead-route cache before deciding. Reset is explicit on purpose: the free tier came back when " +
						"the account was reconnected, not because time passed.",
				}),
			),
			background: Type.Optional(
				Type.Boolean({
					description:
						"Return a steerable handle instead of waiting: the run stays reachable, so steer_delegate, " +
						"abort_delegate and delegate_result can act on it. Background runs use the opencode server leg " +
						"(the pi leg's session lives inside this process, so it cannot be addressed from outside).",
				}),
			),
			session: Type.Optional(Type.String({ description: "opencode session id to resume (opencode leg only)." })),
			timeoutMs: Type.Optional(
				Type.Number({ description: `Timeout for the chosen leg. Defaults to ${PI_LEG_TIMEOUT_MS} ms on pi, ${DEFAULT_TIMEOUT_MS} ms on opencode.` }),
			),
		}),

		async execute(_toolCallId, params, signal, onUpdate, ctx) {
			const startedAt = Date.now();
			const role = params.role ? loadRole(params.role, { cwd: ctx.cwd }) : undefined;
			const effort = params.effort?.trim() || role?.thinking;
			const cwd = params.cwd ? path.resolve(ctx.cwd, params.cwd) : ctx.cwd;
			const catalog = readModelCatalog();
			// EVERY leg, against its OWN model: the leg that runs is not always legs[0], and a level
			// one model supports can be absent on the other (`low: null` on hy4-flash-free).
			const legs = clampLegEfforts(buildLegs(role, effort, params.model?.trim(), parentModelOf(ctx)), catalog);

			let cache = readRouteCache();
			if (params.resetRoute) cache = clearDead(cache);

			if (params.background === true) {
				// A background run is steerable only if a server we own holds the session, so this
				// is the opencode leg whatever the routing preference would have been.
				const opencodeLeg = legs[1];
				const started = await startBackgroundRun({
					prompt: params.prompt,
					role,
					model: routeKey(opencodeLeg.provider, opencodeLeg.model),
					effort: opencodeLeg.effort,
					cwd,
					allowWrite: params.allowWrite === true,
					allowShell: params.allowShell === true,
				});
				const details: DelegateDetails = { route: started.route, durationMs: Date.now() - startedAt, handle: started.handle.id };
				return {
					content: [
						{
							type: "text",
							text: [
								started.handle.routeLine,
								`background · handle ${started.handle.id} · session ${started.handle.sessionId}`,
								"steer_delegate / abort_delegate / delegate_result take this handle",
							].join("\n"),
						},
					],
					details,
				};
			}

			// The explicit model's effort is clamped against THAT model too — `decideRoute` honours a
			// requested model verbatim (legIndex -1), so leaving the raw role level here put an
			// unsupported `low` back into the decision after the legs had been fixed.
			const requestedModelParts = params.model ? splitModelEffort(params.model) : undefined;
			const requestedEffort = requestedModelParts
				? settledEffort(`${requestedModelParts.provider}/${requestedModelParts.model}`, effort, catalog)
				: effort;
			const requested = requestedModelParts ? { ...requestedModelParts, effort: requestedEffort } : undefined;
			// The catalog gate exists because pi-subagents substitutes the PARENT model for an id it
			// cannot resolve instead of failing, so an unknown model would silently "work" there.
			let decision = decideRoute({ legs, requested, cache, catalog });
			onUpdate?.({
				content: [{ type: "text", text: `delegate: ${routeLine(decision, role?.name)}` }],
				details: { route: decision, durationMs: 0 } as DelegateDetails,
			});

			let agentId: string | undefined;
			let piFailure: string | undefined;
			let answer: string | undefined;
			let opencodeRun:
				| { text: string; sessionId?: string; usage?: Usage; exitCode?: number | null; timedOut?: boolean; error?: string }
				| undefined;

			if (decision.target === "pi") {
				const piResult = await runPiLeg({
					bus: pi.events,
					type: role?.name ?? "general-purpose",
					prompt: params.prompt,
					model: legs[0].provider === "pi" ? undefined : routeKey(decision.provider, decision.model),
					effort: decision.effort,
					cwd,
					timeoutMs: params.timeoutMs,
					onSpawned: (id) => {
						agentId = id;
					},
				});

				if (piResult.status === "ok") {
					answer = piResult.text;
					// What ran, not what was asked for: the manager record is authoritative, and without
					// one the line says `unknown` instead of parroting the request back.
					const requestedModel = routeKey(decision.provider, decision.model);
					const effectiveModel = piResult.effective?.modelId;
					if (effectiveModel) {
						decision = { ...decision, effectiveModel, requestedModel, detection: "manager record invocation.modelId" };
						if (effectiveModel.toLowerCase() !== requestedModel.toLowerCase()) {
							const effective = splitModelEffort(effectiveModel);
							decision = {
								...decision,
								reason: "silent-substitution",
								provider: effective.provider,
								model: effective.model,
							};
						}
					} else {
						decision = {
							...decision,
							effectiveModel: "unknown",
							requestedModel,
							detection: "unknown (manager record unreachable)",
						};
					}
				} else if (piResult.status === "unavailable") {
					// Not a route failure: the plugin is absent, so nothing is marked dead.
					decision = { ...decision, reason: "fallback", evidence: piResult.reason };
				} else {
					piFailure = piResult.error;
					if (piResult.nonRetryable) {
						cache = markDead(cache, {
							provider: decision.provider,
							model: decision.model,
							evidence: piResult.error,
						});
						decision = decideRoute({
							legs,
							requested,
							cache,
							failure: { provider: decision.provider, model: decision.model, evidence: piResult.error, legIndex: decision.legIndex },
						});
					} else {
						decision = { ...decision, reason: "fallback", evidence: piResult.error };
					}
				}
			}

			if (answer === undefined) {
				const opencodeLeg = legs[1];
				if (decision.target !== "opencode") {
					decision = {
						target: "opencode",
						provider: opencodeLeg.provider,
						model: opencodeLeg.model,
						...(decision.effort ? { effort: decision.effort } : {}),
						reason: decision.reason === "explicit" ? "explicit" : "fallback",
						...(decision.evidence ? { evidence: decision.evidence } : {}),
						decidedAt: decision.decidedAt,
						legIndex: 1,
					};
				}
				const target = routeKey(decision.provider, decision.model);
				const config = role
					? planRoleRun(role, {
							model: target,
							thinking: decision.effort,
							allowWrite: params.allowWrite === true,
							allowShell: params.allowShell === true,
							defaultModel: OPENCODE_FALLBACK_MODEL,
						}).config
					: buildChildConfig(target, { allowWrite: params.allowWrite === true, allowShell: params.allowShell === true });

				const configDir = await makeConfigDir(config);
				try {
					const result = await runOpencode({
						prompt: params.prompt,
						agent: role?.name,
						model: target,
						variant: decision.effort,
						session: params.session,
						auto: params.allowWrite === true || params.allowShell === true,
						cwd,
						timeoutMs: params.timeoutMs && params.timeoutMs > 0 ? params.timeoutMs : DEFAULT_TIMEOUT_MS,
						signal,
						configDir,
					});
					answer = result.text;
					opencodeRun = {
						text: result.text,
						sessionId: result.sessionId,
						usage: result.usage,
						exitCode: result.exitCode,
						timedOut: result.timedOut,
						error: result.error?.message,
					};
				} finally {
					await rm(configDir, { recursive: true, force: true }).catch(() => {});
				}
			}

			const details: DelegateDetails = { route: decision, durationMs: Date.now() - startedAt };
			if (agentId) details.agentId = agentId;
			if (piFailure) details.piFailure = piFailure;
			if (opencodeRun) {
				details.opencode = opencodeRun;
				details.exitCode = opencodeRun.exitCode ?? null;
				if (opencodeRun.sessionId) details.sessionId = opencodeRun.sessionId;
				if (opencodeRun.usage) details.usage = opencodeRun.usage;
				if (opencodeRun.timedOut !== undefined) details.timedOut = opencodeRun.timedOut;
			}

			const lines = [routeLine(decision, role?.name)];
			if (details.agentId) lines.push(`agent ${details.agentId}`);
			if (decision.evidence) lines.push(`evidence: ${decision.evidence}`);
			if (opencodeRun?.error) lines.push(`error: ${opencodeRun.error}`);
			if (opencodeRun?.usage) lines.push(describeUsage(opencodeRun.usage));
			const body = (answer ?? "").trim();
			lines.push("", body.length > 0 ? headTail(body, RESULT_HEAD_CHARS, RESULT_TAIL_CHARS) : "(the delegated agent produced no text)");

			const failed =
				answer === undefined ||
				Boolean(opencodeRun?.error) ||
				opencodeRun?.timedOut === true ||
				(typeof opencodeRun?.exitCode === "number" && opencodeRun.exitCode !== 0);
			return failed
				? { content: [{ type: "text", text: lines.join("\n") }], details, isError: true }
				: { content: [{ type: "text", text: lines.join("\n") }], details };
		},
	});
}
