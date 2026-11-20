/**
 * Routing for delegated work: which leg runs a prompt, and why.
 *
 * The decision is DATA (`RouteDecision`), not a branch buried in a tool call, because two later
 * cards read it: a fallback CHAIN grows out of it (`model-fallback-chain` — hence the ordered
 * `legs` input and `legIndex` on the decision, today always one of two), and the model that
 * actually ran is displayed from it (`agent-row-model-id` — hence `provider`/`model`/`effort`
 * being first-class fields rather than text to parse back out).
 *
 * `decideRoute` is pure: no clock, no disk, no pi. The cache helpers below do touch disk, and
 * are separate calls with the cache passed in.
 *
 * Why the cache has an explicit reset and not a TTL: the free-tier failure that motivated this
 * layer was cleared by RECONNECTING THE ACCOUNT, not by time passing. A dead route can come back
 * between two calls, so `resetRoute` / `clearDead` is the way out, and a TTL would both expire a
 * still-dead route and leave a revived one blocked.
 */

import { existsSync, readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { homedir } from "node:os";
import * as path from "node:path";

/** Who runs the prompt. */
export type RouteTarget = "pi" | "opencode";

/**
 * Why this leg was chosen.
 *
 * `explicit` — the caller named the model or target.
 * `default` — nothing named, nothing dead: the preferred leg.
 * `fallback` — the previous leg failed with a non-retryable provider error.
 * `cached-dead` — a `(provider, model)` known-bad from an earlier failure, so no trial call.
 */
export type RouteReason =
	| "explicit"
	| "default"
	| "fallback"
	| "cached-dead"
	/** The leg ran a different model than the one asked for (pi-subagents falls back silently). */
	| "silent-substitution"
	/** The requested model is not in pi's catalog, so the pi leg was never asked. */
	| "unknown-model";

/** One candidate route. Ordered inside `DecideRouteInput.legs`, which is what makes a chain fit. */
export interface RouteLeg {
	target: RouteTarget;
	provider: string;
	model: string;
	/** pi thinking level. Clamping to a model's catalogue is card `bridge-effort-from-catalog`. */
	effort?: string;
}

/** The decision object. Everything a caller needs is readable here, never inferred. */
export interface RouteDecision {
	target: RouteTarget;
	provider: string;
	model: string;
	effort?: string;
	reason: RouteReason;
	/** The failure text that caused a switch, verbatim. */
	evidence?: string;
	/** ISO timestamp of the decision. */
	decidedAt: string;
	/** Position of the chosen leg in the plan (0 = preferred). A chain grows around this. */
	legIndex: number;
	/** What the caller asked for, when it differs from what ran. */
	requestedModel?: string;
	/** What actually ran, read from the harness rather than echoed from the request. */
	effectiveModel?: string;
	/** How `effectiveModel` was obtained ("manager record invocation.modelId", "unknown"). */
	detection?: string;
}

/** A route known to fail. Kept with its evidence so a human can see why it is blacklisted. */
export interface DeadRoute {
	provider: string;
	model: string;
	evidence: string;
	markedAt: string;
}

export interface RouteCache {
	version: 1;
	dead: DeadRoute[];
}

export function emptyRouteCache(): RouteCache {
	return { version: 1, dead: [] };
}

export function routeKey(provider: string, model: string): string {
	return `${provider}/${model}`;
}

/** Where the cache lives. `OPENCODE_BRIDGE_ROUTE_CACHE` overrides it (tests, unusual installs). */
export function routeCachePath(): string {
	const override = process.env.OPENCODE_BRIDGE_ROUTE_CACHE?.trim();
	if (override) return override;
	return path.join(homedir(), ".pi", "agent", "opencode-bridge-routes.json");
}

/**
 * The dead routes in the cache. A missing, empty or corrupt file reads as "nothing is dead" —
 * a routing hint must never be able to break delegation.
 */
export function readRouteCache(file: string = routeCachePath()): RouteCache {
	if (!existsSync(file)) return emptyRouteCache();
	try {
		const parsed = JSON.parse(readFileSync(file, "utf8")) as Partial<RouteCache> | null;
		const dead = Array.isArray(parsed?.dead) ? parsed.dead : [];
		return {
			version: 1,
			dead: dead.filter(
				(entry): entry is DeadRoute =>
					Boolean(entry) &&
					typeof entry.provider === "string" &&
					typeof entry.model === "string" &&
					typeof entry.evidence === "string",
			),
		};
	} catch {
		return emptyRouteCache();
	}
}

export function writeRouteCache(cache: RouteCache, file: string = routeCachePath()): void {
	mkdirSync(path.dirname(file), { recursive: true });
	writeFileSync(file, `${JSON.stringify(cache, null, 2)}\n`, "utf8");
}

export function findDead(cache: RouteCache, provider: string, model: string): DeadRoute | undefined {
	const key = routeKey(provider, model);
	return cache.dead.find((entry) => routeKey(entry.provider, entry.model) === key);
}

export function isMarkedDead(cache: RouteCache, provider: string, model: string): boolean {
	return findDead(cache, provider, model) !== undefined;
}

/** Record a failed route and persist it. Re-marking replaces the evidence, not the entry. */
export function markDead(
	cache: RouteCache,
	entry: { provider: string; model: string; evidence: string; markedAt?: string },
	file: string = routeCachePath(),
): RouteCache {
	const dead = cache.dead.filter(
		(existing) => routeKey(existing.provider, existing.model) !== routeKey(entry.provider, entry.model),
	);
	dead.push({
		provider: entry.provider,
		model: entry.model,
		evidence: entry.evidence,
		markedAt: entry.markedAt ?? new Date().toISOString(),
	});
	const next: RouteCache = { version: 1, dead };
	writeRouteCache(next, file);
	return next;
}

/**
 * Clear one dead route, or (with no argument) the whole cache. This is the explicit reset: the
 * free tier was restored by reconnecting the account, so nothing but the caller can know a dead
 * route is alive again.
 */
export function clearDead(
	cache: RouteCache,
	only?: { provider: string; model: string },
	file: string = routeCachePath(),
): RouteCache {
	const next: RouteCache = only
		? {
				version: 1,
				dead: cache.dead.filter(
					(entry) => routeKey(entry.provider, entry.model) !== routeKey(only.provider, only.model),
				),
			}
		: emptyRouteCache();
	writeRouteCache(next, file);
	return next;
}

/**
 * Is this failure worth trying the next leg for?
 *
 * Yes: provider refusals that no amount of repetition fixes — a spent quota, a rejected key, a
 * model that does not exist. No: transport hiccups and timeouts, where the same leg may well work
 * on the next call. Marking a route dead on a transient error is how a working route gets
 * blacklisted forever, so the default for anything unrecognised is "retryable".
 */
export function isNonRetryableProviderError(message: string | undefined): boolean {
	if (!message) return false;
	return /provider\.(free|auth|quota|no-route|model)|free[-_ ]?limit|free usage limit|insufficient_quota|quota exceeded|401|403|429|not found|no such model/i.test(
		message,
	);
}

export interface DecideRouteInput {
	/** Candidate legs in preference order. Today two (pi, opencode); a chain is simply more. */
	legs: RouteLeg[];
	/**
	 * pi's catalog. When given, a chosen PI leg whose model pi does not know is skipped instead
	 * of spending a call on it — pi-subagents silently substitutes the parent model for an
	 * unresolvable id, so the only cheap moment to notice is before the spawn.
	 */
	catalog?: ModelCatalog;
	/** The caller named a target, provider, model or effort — it wins, cache or not. */
	requested?: { target?: RouteTarget; provider?: string; model?: string; effort?: string };
	/** Routes already known to fail. */
	cache: RouteCache;
	/**
	 * A failure on one leg. `legIndex` is the reliable attribution (a leg can fail before it has
	 * a model to report, e.g. the pi plugin being absent); `provider`/`model` are the fallback
	 * match for callers that only know those.
	 */
	failure?: { provider: string; model: string; evidence: string; legIndex?: number };
	/** Injectable clock, so the decision object is testable. */
	now?: () => string;
}

function legMatches(leg: RouteLeg, requested: NonNullable<DecideRouteInput["requested"]>): boolean {
	if (requested.target && leg.target !== requested.target) return false;
	if (requested.provider && leg.provider !== requested.provider) return false;
	if (requested.model && leg.model !== requested.model) return false;
	return true;
}

/**
 * Skip a pi leg whose model the catalog does not contain.
 *
 * Applied to a decision that is about to run on the pi leg with a NAMED model. Returns the
 * decision unchanged when there is nothing to check, and the next leg otherwise.
 */
function guardPiModel(
	decision: RouteDecision,
	legs: RouteLeg[],
	catalog: ModelCatalog | undefined,
	now: () => string,
): RouteDecision {
	if (!catalog || decision.target !== "pi") return decision;
	if (!decision.provider || !decision.model || decision.model === "default") return decision;
	if (isModelKnown(catalog, decision.provider, decision.model)) return decision;

	const evidence = `${routeKey(decision.provider, decision.model)} is not in pi's model catalog (${
		catalog.sources.join(", ") || "no catalog file readable"
	}) — pi-subagents would silently substitute the parent model`;
	const start = Math.max(0, decision.legIndex) + 1;
	let index = -1;
	for (let position = start; position < legs.length; position += 1) {
		index = position;
		break;
	}
	if (index < 0) index = legs.length - 1;
	const leg = legs[index];
	const guarded: RouteDecision = {
		target: leg.target,
		provider: leg.provider,
		model: leg.model,
		reason: "unknown-model",
		evidence,
		decidedAt: now(),
		legIndex: index,
	};
	if (leg.effort !== undefined) guarded.effort = leg.effort;
	return guarded;
}

/**
 * Pick a leg.
 *
 * Order of precedence, and each is a rule rather than a fallthrough:
 * 1. a recorded FAILURE wins first: a re-decision happens only after a leg failed, so reporting
 *    it as anything but `fallback` (with the provider's own words) would hide the switch;
 * 2. otherwise an explicit request wins — including when that route is marked dead;
 * 3. otherwise the first leg that is not dead;
 * 4. if every leg is dead, the last one anyway: refusing to route would turn a stale cache into
 *    a hard outage, and the caller still gets the evidence from `RouteDecision.reason`.
 */
export function decideRoute(input: DecideRouteInput): RouteDecision {
	const now = input.now ?? (() => new Date().toISOString());
	return guardPiModel(pickLeg(input), input.legs, input.catalog, now);
}

function pickLeg(input: DecideRouteInput): RouteDecision {
	const now = input.now ?? (() => new Date().toISOString());
	const legs = input.legs;
	if (legs.length === 0) throw new Error("opencode-bridge: decideRoute needs at least one leg");

	const stamp = now();
	const build = (leg: RouteLeg, legIndex: number, reason: RouteReason, evidence?: string): RouteDecision => {
		const decision: RouteDecision = {
			target: leg.target,
			provider: leg.provider,
			model: leg.model,
			reason,
			decidedAt: stamp,
			legIndex,
		};
		if (leg.effort !== undefined) decision.effort = leg.effort;
		if (evidence !== undefined) decision.evidence = evidence;
		return decision;
	};

	if (input.failure) {
		const failedKey = routeKey(input.failure.provider, input.failure.model);
		const attributed =
			input.failure.legIndex ??
			legs.findIndex((leg) => routeKey(leg.provider, leg.model) === failedKey);
		// Skip past the failed leg and take the first usable one — a chain keeps walking.
		let index = -1;
		for (let position = Math.max(0, attributed) + 1; position < legs.length; position += 1) {
			if (!isMarkedDead(input.cache, legs[position].provider, legs[position].model)) {
				index = position;
				break;
			}
		}
		if (index < 0) index = Math.max(legs.length - 1, attributed + 1 > legs.length - 1 ? legs.length - 1 : attributed);
		if (index >= legs.length) index = legs.length - 1;
		return build(legs[index], index, "fallback", input.failure.evidence);
	}

	if (input.requested) {
		const index = legs.findIndex((leg) => legMatches(leg, input.requested!));
		if (index >= 0) {
			const leg = legs[index];
			const chosen: RouteLeg = {
				...leg,
				effort: input.requested.effort ?? leg.effort,
			};
			return build(chosen, index, "explicit");
		}
		// An explicit route the plan did not contain.
		const explicit: RouteLeg = {
			target: input.requested.target ?? legs[0].target,
			provider: input.requested.provider ?? legs[0].provider,
			model: input.requested.model ?? legs[0].model,
			effort: input.requested.effort ?? legs[0].effort,
		};
		// Naming a MODEL is not the same as naming a TARGET. A model-only request for a pair the
		// cache already condemned goes to the other leg with no trial call; naming the target too
		// is an instruction, and instructions are followed even against the cache.
		if (input.requested.target === undefined) {
			const condemned = findDead(input.cache, explicit.provider, explicit.model);
			if (condemned) {
				// The named pair would have run on the leg for its target; take the first usable
				// leg AFTER that one. Anything else would pick a leg by accident of ordering.
				const wouldRunOn = legs.findIndex((leg) => leg.target === explicit.target);
				let index = -1;
				for (let position = Math.max(0, wouldRunOn) + 1; position < legs.length; position += 1) {
					if (!isMarkedDead(input.cache, legs[position].provider, legs[position].model)) {
						index = position;
						break;
					}
				}
				if (index < 0) index = legs.length - 1;
				return build(legs[index], index, "cached-dead", condemned.evidence);
			}
		}
		return build(explicit, -1, "explicit");
	}

	const preferred = legs.findIndex((leg) => !isMarkedDead(input.cache, leg.provider, leg.model));
	if (preferred === 0) return build(legs[0], 0, "default");
	if (preferred > 0) {
		const dead = findDead(input.cache, legs[0].provider, legs[0].model);
		return build(legs[preferred], preferred, "cached-dead", dead?.evidence);
	}
	const last = legs.length - 1;
	const dead = findDead(input.cache, legs[last].provider, legs[last].model);
	return build(legs[last], last, "cached-dead", dead?.evidence);
}

/**
 * pi's model catalog, flattened to `(provider, model)` membership.
 *
 * Two files feed it: `models-store.json` (the synced catalog, `{provider: {models: [{id, …}]}}`)
 * and `models.json` (hand-written custom providers, `{providers: {id: {models: …}}}`). Only
 * membership is read here — the thinking-level clamp is `bridge-effort-from-catalog`.
 */
/** One catalog entry: what the model is, and which thinking levels it actually has. */
export interface ModelEntry {
	provider: string;
	model: string;
	/** pi's own `reasoning` flag; a non-reasoning model supports only `off`. */
	reasoning?: boolean;
	/** `{"minimal": null, "low": "low", …}` — `null` means the level is NOT supported. */
	thinkingLevelMap?: Record<string, string | null>;
}

export interface ModelCatalog {
	/** `provider/model` keys, lowercased. */
	known: Set<string>;
	/** Full entries, keyed the same way. Effort clamping reads `thinkingLevelMap` from here. */
	entries: Map<string, ModelEntry>;
	/** Files that were readable, for the evidence line. */
	sources: string[];
}

/** The catalog entry for `provider/model`, or undefined when the catalog does not list it. */
export function findModelEntry(
	catalog: ModelCatalog,
	provider: string,
	model: string,
): ModelEntry | undefined {
	if (!provider || !model) return undefined;
	return catalog.entries.get(routeKey(provider, model).toLowerCase());
}

export function catalogPaths(): { store: string; custom: string } {
	const dir = path.join(homedir(), ".pi", "agent");
	return {
		store: process.env.OPENCODE_BRIDGE_MODELS_STORE?.trim() || path.join(dir, "models-store.json"),
		custom: process.env.OPENCODE_BRIDGE_MODELS_CUSTOM?.trim() || path.join(dir, "models.json"),
	};
}

function addModels(catalog: ModelCatalog, providerId: string, models: unknown): void {
	if (!Array.isArray(models)) return;
	for (const model of models) {
		if (typeof model !== "object" || model === null) continue;
		const entry = model as { id?: unknown; reasoning?: unknown; thinkingLevelMap?: unknown };
		if (typeof entry.id !== "string" || entry.id.length === 0) continue;
		const key = routeKey(providerId, entry.id).toLowerCase();
		catalog.known.add(key);
		const recorded: ModelEntry = { provider: providerId, model: entry.id };
		if (typeof entry.reasoning === "boolean") recorded.reasoning = entry.reasoning;
		if (entry.thinkingLevelMap && typeof entry.thinkingLevelMap === "object") {
			const map: Record<string, string | null> = {};
			for (const [level, raw] of Object.entries(entry.thinkingLevelMap as Record<string, unknown>)) {
				if (raw === null || typeof raw === "string") map[level] = raw;
			}
			recorded.thinkingLevelMap = map;
		}
		catalog.entries.set(key, recorded);
	}
}

/** Read both catalog files. A missing or corrupt file contributes nothing, never throws. */
export function readModelCatalog(paths: { store: string; custom: string } = catalogPaths()): ModelCatalog {
	const catalog: ModelCatalog = { known: new Set<string>(), entries: new Map<string, ModelEntry>(), sources: [] };
	const sources = catalog.sources;

	const readJson = (file: string): unknown => {
		if (!existsSync(file)) return undefined;
		try {
			const parsed: unknown = JSON.parse(readFileSync(file, "utf8"));
			sources.push(file);
			return parsed;
		} catch {
			return undefined;
		}
	};

	const store = readJson(paths.store) as Record<string, { models?: unknown }> | undefined;
	if (store && typeof store === "object") {
		for (const [providerId, entry] of Object.entries(store)) {
			if (entry && typeof entry === "object") addModels(catalog, providerId, entry?.models);
		}
	}

	const custom = readJson(paths.custom) as { providers?: Record<string, { models?: unknown }> } | undefined;
	if (custom?.providers && typeof custom.providers === "object") {
		for (const [providerId, entry] of Object.entries(custom.providers)) {
			if (entry && typeof entry === "object") addModels(catalog, providerId, entry?.models);
		}
	}

	return catalog;
}

/** Is `provider/model` in pi's catalog? Unknown providers and bare ids are "no". */
export function isModelKnown(catalog: ModelCatalog, provider: string, model: string): boolean {
	if (!provider || !model) return false;
	return catalog.known.has(routeKey(provider, model).toLowerCase());
}

/**
 * Evidence squeezed onto one line.
 *
 * Providers hand back JSON in an error string (`429: {"type":…,"message":"…"}`) and this text
 * is read by the parent model, so keep the status code and the sentence, drop the payload.
 */
function shortEvidence(evidence: string): string {
	const first = evidence.split("\n")[0].trim();
	const match = /^(\d{3}):\s*(\{.*\})$/.exec(first);
	if (match) {
		try {
			const parsed = JSON.parse(match[2]) as { message?: unknown; type?: unknown };
			const message = typeof parsed.message === "string" ? parsed.message : String(parsed.type ?? "");
			return `${match[1]} ${message}`.slice(0, 140);
		} catch {
			// not JSON after all; the raw text below is fine
		}
	}
	return first.slice(0, 140);
}

/**
 * The one line the parent model reads to know what ran.
 *
 * Visibility lives HERE and nowhere else (owner decision): the subagents fleet view already
 * renders model + level via its `showModel` setting, so a second UI surface aimed at the agent
 * row is not built. This line is the whole requirement, and it is the same shape whether the
 * run succeeded, fell back, or was routed around a dead route:
 *
 *   route pi · scout · opencode-go/hy4-flash-free · low
 *   route opencode · scout · opencode:opencode/hy4-flash-free · low · fallback (provider.free-limit)
 */
export function routeLine(route: RouteDecision, role?: string): string {
	// The provider slot is never a placeholder: a leg with no provider of its own borrows the one
	// its model id names, and a model nobody could resolve says `unknown` rather than "-".
	const provider = route.provider && route.provider !== "pi" ? route.provider : (route.model.split("/")[0] || "unknown");
	const modelId = route.model || "unknown";
	const label = route.target === "opencode" ? `opencode:${provider}/${modelId}` : `${provider}/${modelId}`;
	// A silent substitution must show both models: what was asked for, and what answered.
	const shown =
		route.reason === "silent-substitution" && route.effectiveModel
			? `${label} → ${route.effectiveModel}`
			: label;
	// The reason is ALWAYS printed — it is the one field that says whether the caller got what it
	// asked for, so it may not be implied by the absence of a word.
	const parts = [`route ${route.target}`, role ?? "-", shown, route.effort ?? "-", route.reason];
	if (route.reason === "silent-substitution" && route.detection) parts.push(`detected via ${route.detection}`);
	if (route.evidence) parts.push(`(${shortEvidence(route.evidence)})`);
	return parts.join(" · ");
}

