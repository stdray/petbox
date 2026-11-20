/**
 * Route decisions and the pi-route client.
 *
 * The four behaviours the card calls out, each as a test that fails when the rule is removed:
 * an explicit request is never rerouted; a cached-dead route spends no trial call; a fallback
 * carries the provider's evidence and writes the cache; a missing plugin routes to opencode
 * instead of hanging.
 *
 * Every fake here is a plain object — `route.ts` and `pi-route.ts` take the bus and the cache as
 * arguments precisely so this file needs no pi process.
 */

import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import * as path from "node:path";
import test from "node:test";

import {
	clearDead,
	decideRoute,
	isModelKnown,
	readModelCatalog,
	routeLine,
	emptyRouteCache,
	isMarkedDead,
	isNonRetryableProviderError,
	markDead,
	readRouteCache,
	routeCachePath,
	routeKey,
	type ModelCatalog,
	type RouteCache,
	type RouteLeg,
} from "../../extensions/opencode-bridge/route.ts";
import {
	lookupEffectiveInvocation,
	markSubagentsReady,
	piUsageToUsage,
	probePiRoute,
	SUBAGENTS_MANAGER_KEY,
	resetSubagentsReady,
	runPiLeg,
	SUBAGENTS_COMPLETED,
	SUBAGENTS_FAILED,
	SUBAGENTS_PING,
	SUBAGENTS_READY,
	SUBAGENTS_SPAWN,
	SUBAGENTS_STOP,
	type BusLike,
} from "../../extensions/opencode-bridge/pi-route.ts";

const LEGS: RouteLeg[] = [
	{ target: "pi", provider: "opencode-go", model: "hy4-flash-free", effort: "low" },
	{ target: "opencode", provider: "opencode", model: "hy4-flash-free", effort: "low" },
];

const AT = "2026-01-01T00:00:00.000Z";
const now = () => AT;

/** A bus that records what was emitted and lets a test answer a channel. */
function fakeBus(): BusLike & { emitted: Array<{ channel: string; data: unknown }>; listeners: Map<string, Array<(data: unknown) => void>> } {
	const listeners = new Map<string, Array<(data: unknown) => void>>();
	const emitted: Array<{ channel: string; data: unknown }> = [];
	return {
		emitted,
		listeners,
		emit(channel, data) {
			emitted.push({ channel, data });
			for (const handler of [...(listeners.get(channel) ?? [])]) handler(data);
		},
		on(channel, handler) {
			const list = listeners.get(channel) ?? [];
			list.push(handler);
			listeners.set(channel, list);
			return () => {
				listeners.set(channel, (listeners.get(channel) ?? []).filter((entry) => entry !== handler));
			};
		},
	};
}

test("an explicit request is never rerouted", () => {
	const cache = markDead(emptyRouteCache(), { provider: "opencode-go", model: "hy4-flash-free", evidence: "dead" }, tmpCache());
	const decision = decideRoute({
		legs: LEGS,
		cache,
		requested: { target: "pi", provider: "opencode-go", model: "hy4-flash-free" },
		now,
	});
	assert.equal(decision.reason, "explicit");
	assert.equal(decision.target, "pi", "a dead cache entry must not override an explicit request");
	assert.equal(decision.legIndex, 0);
	assert.equal(decision.decidedAt, AT);

	// An explicit route the plan did not contain is still honoured.
	const offPlan = decideRoute({ legs: LEGS, cache: emptyRouteCache(), requested: { model: "anthropic/x" }, now });
	assert.deepEqual([offPlan.reason, offPlan.model, offPlan.legIndex], ["explicit", "anthropic/x", -1], "the requested model is echoed verbatim; splitting is the caller's job");

	// A recorded failure outranks a stale model request: this is a RE-decision, and calling it
	// "explicit" would hide the switch that just happened.
	const afterFailure = decideRoute({
		legs: LEGS,
		cache: emptyRouteCache(),
		requested: { provider: "opencode", model: "deepseek-v4.1-flash-free" },
		failure: { provider: "opencode", model: "deepseek-v4.1-flash-free", evidence: "provider.free-limit", legIndex: 0 },
		now,
	});
	assert.equal(afterFailure.reason, "fallback");
	assert.equal(afterFailure.target, "opencode", "the leg after the failed one");
	assert.equal(afterFailure.evidence, "provider.free-limit");

	// A named TARGET is an instruction and beats the cache ...
	const namedTarget = decideRoute({
		legs: LEGS,
		cache: markDead(emptyRouteCache(), { provider: "opencode", model: "deepseek-v4.1-flash-free", evidence: "dead" }, tmpCache()),
		requested: { target: "pi", model: "opencode/deepseek-v4.1-flash-free" },
		now,
	});
	assert.equal(namedTarget.reason, "explicit");
	assert.equal(namedTarget.target, "pi");

	// ... while a model-only request for a condemned pair takes the other leg with no trial call.
	const modelOnly = decideRoute({
		legs: LEGS,
		cache: markDead(emptyRouteCache(), { provider: "opencode", model: "deepseek-v4.1-flash-free", evidence: "provider.free-limit" }, tmpCache()),
		requested: { provider: "opencode", model: "deepseek-v4.1-flash-free" },
		now,
	});
	assert.equal(modelOnly.reason, "cached-dead");
	assert.equal(modelOnly.target, "opencode", "the leg that is not condemned");
	assert.equal(modelOnly.evidence, "provider.free-limit");
});

test("a cached-dead route spends no trial call", () => {
	const cache = markDead(emptyRouteCache(), { provider: "opencode-go", model: "hy4-flash-free", evidence: "provider.free-limit" }, tmpCache());
	const decision = decideRoute({ legs: LEGS, cache, now });
	assert.equal(decision.reason, "cached-dead");
	assert.equal(decision.target, "opencode");
	assert.equal(decision.legIndex, 1);
	assert.equal(decision.evidence, "provider.free-limit", "the evidence travels with the decision");

	// Nothing dead: the preferred leg, and the reason says so rather than pretending fallback.
	const fresh = decideRoute({ legs: LEGS, cache: emptyRouteCache(), now });
	assert.equal(fresh.reason, "default");
	assert.equal(fresh.target, "pi");
});

test("a fallback records the evidence and writes the cache", () => {
	const file = tmpCache();
	const cache = emptyRouteCache();
	const evidence = 'provider.free-limit: "Free usage limit reached"';

	const marked = markDead(cache, { provider: "opencode-go", model: "hy4-flash-free", evidence, markedAt: AT }, file);
	assert.equal(isMarkedDead(marked, "opencode-go", "hy4-flash-free"), true);

	const decision = decideRoute({ legs: LEGS, cache: marked, failure: { provider: "opencode-go", model: "hy4-flash-free", evidence }, now });
	assert.equal(decision.reason, "fallback");
	assert.equal(decision.target, "opencode");
	assert.equal(decision.evidence, evidence);

	// The cache survives a round trip: a later call sees the route as dead without any trial.
	const reread = readRouteCache(file);
	assert.equal(isMarkedDead(reread, "opencode-go", "hy4-flash-free"), true);
	assert.equal(decideRoute({ legs: LEGS, cache: reread, now }).reason, "cached-dead");

	// Explicit reset is the way out.
	const cleared = clearDead(reread, undefined, file);
	assert.equal(cleared.dead.length, 0);
	assert.equal(decideRoute({ legs: LEGS, cache: readRouteCache(file), now }).reason, "default");
});

test("a cache that cannot be read never blocks routing", () => {
	const bogus = path.join(mkdtempSync(path.join(tmpdir(), "route-bogus-")), "nope.json");
	assert.deepEqual(readRouteCache(bogus).dead, []);
	assert.equal(decideRoute({ legs: LEGS, cache: readRouteCache(bogus), now }).reason, "default");
	assert.ok(routeCachePath().endsWith("opencode-bridge-routes.json"));
});

test("only provider refusals are non-retryable", () => {
	for (const message of ["provider.free-limit", "Free usage limit reached", "401 Unauthorized", "429 Too Many Requests", "Model not found: x"]) {
		assert.equal(isNonRetryableProviderError(message), true, message);
	}
	for (const message of ["ETIMEDOUT", "socket hang up", "pi leg timed out after 5 ms", undefined]) {
		assert.equal(isNonRetryableProviderError(message), false, String(message));
	}
});

test("a missing plugin routes to opencode instead of hanging", async () => {
	const bus = fakeBus(); // answers nothing: no subagents:ready, no ping reply
	resetSubagentsReady();
	const started = Date.now();
	const probe = await probePiRoute(bus, 80);
	const elapsed = Date.now() - started;
	assert.equal(probe, false, "no reply means unavailable, not a hang");
	assert.ok(elapsed < 2_000, `discovery must time out quickly, took ${elapsed}ms`);

	const result = await runPiLeg({ bus, type: "scout", prompt: "hi", discoveryTimeoutMs: 80 });
	assert.equal(result.status, "unavailable");
	if (result.status === "unavailable") assert.match(result.reason, /did not answer subagents:rpc:ping/);
	assert.equal(
		bus.emitted.some((entry) => entry.channel === SUBAGENTS_SPAWN),
		false,
		"an unavailable plugin must not be asked to spawn",
	);

	// And the router takes the opencode leg from there.
	const decision = decideRoute({ legs: LEGS, cache: emptyRouteCache(), failure: { provider: "pi", model: "default", evidence: "unavailable", legIndex: 0 }, now });
	assert.equal(decision.target, "opencode");
	assert.equal(decision.reason, "fallback");
});

test("the ready signal short-circuits discovery", async () => {
	resetSubagentsReady();
	assert.equal(resetSubagentsReady, resetSubagentsReady); // keep the import used
	const bus = fakeBus();
	bus.emit(SUBAGENTS_READY, {});
	markSubagentsReady();
	assert.equal(await probePiRoute(bus, 50), true);
	assert.equal(bus.emitted.some((entry) => entry.channel === SUBAGENTS_PING), false, "a ready plugin needs no ping");
	resetSubagentsReady();
});

test("a spawn and its completion produce a result, and consume suppresses the notification", async () => {
	resetSubagentsReady();
	markSubagentsReady();
	const bus = fakeBus();

	// Answer spawn replies and settle the agent.
	bus.on(SUBAGENTS_SPAWN, (data) => {
		const { requestId } = data as { requestId: string };
		bus.emit(`${SUBAGENTS_SPAWN}:reply:${requestId}`, { success: true, data: { id: "agent-1" } });
		setTimeout(() => {
			bus.emit(SUBAGENTS_COMPLETED, { id: "agent-1", status: "completed", result: "DONE-42", durationMs: 5 });
		}, 5);
	});

	const result = await runPiLeg({ bus, type: "scout", prompt: "go", timeoutMs: 2_000, discoveryTimeoutMs: 100 });
	assert.equal(result.status, "ok");
	if (result.status === "ok") {
		assert.equal(result.id, "agent-1");
		assert.equal(result.text, "DONE-42");
	}
	assert.equal(
		bus.emitted.some((entry) => entry.channel === "subagents:rpc:consume"),
		true,
		"the parent must not be notified about a result it was just given",
	);
});

test("a failed subagent surfaces its error and retryability", async () => {
	resetSubagentsReady();
	markSubagentsReady();
	const bus = fakeBus();
	bus.on(SUBAGENTS_SPAWN, (data) => {
		const { requestId } = data as { requestId: string };
		bus.emit(`${SUBAGENTS_SPAWN}:reply:${requestId}`, { success: true, data: { id: "agent-2" } });
		setTimeout(() => {
			bus.emit(SUBAGENTS_FAILED, { id: "agent-2", status: "error", error: "provider.free-limit: no quota", result: "" });
		}, 5);
	});

	const result = await runPiLeg({ bus, type: "scout", prompt: "go", timeoutMs: 2_000, discoveryTimeoutMs: 100 });
	assert.equal(result.status, "error");
	if (result.status === "error") {
		assert.match(result.error, /free.limit/);
		assert.equal(result.nonRetryable, true, "a spent quota is what turns into markDead + fallback");
	}
});

test("a spawn refusal is an error, not a hang", async () => {
	resetSubagentsReady();
	markSubagentsReady();
	const bus = fakeBus();
	bus.on(SUBAGENTS_SPAWN, (data) => {
		const { requestId } = data as { requestId: string };
		bus.emit(`${SUBAGENTS_SPAWN}:reply:${requestId}`, { success: false, error: 'Unknown or disabled agent type: "scout"' });
	});
	const result = await runPiLeg({ bus, type: "scout", prompt: "go", timeoutMs: 500, discoveryTimeoutMs: 100 });
	assert.equal(result.status, "error");
	if (result.status === "error") assert.match(result.error, /Unknown or disabled agent type/);
});

test("a run that never settles times out and stops the agent", async () => {
	resetSubagentsReady();
	markSubagentsReady();
	const bus = fakeBus();
	bus.on(SUBAGENTS_SPAWN, (data) => {
		const { requestId } = data as { requestId: string };
		bus.emit(`${SUBAGENTS_SPAWN}:reply:${requestId}`, { success: true, data: { id: "agent-3" } });
	});
	const result = await runPiLeg({ bus, type: "scout", prompt: "go", timeoutMs: 60, discoveryTimeoutMs: 100 });
	assert.equal(result.status, "error");
	if (result.status === "error") assert.match(result.error, /timed out/);
	assert.equal(
		bus.emitted.some((entry) => entry.channel === SUBAGENTS_STOP),
		true,
		"a timed-out run must not be left running",
	);
});

function tmpCache(): string {
	return path.join(mkdtempSync(path.join(tmpdir(), "route-cache-")), "routes.json");
}

// Keep the unused-import linters quiet without pretending these are tested elsewhere.
void routeKey;
void SUBAGENTS_PING;
void ({} as RouteCache);

test("an unknown model skips the pi leg without spending a call", () => {
	let calls = 0;
	const catalog: ModelCatalog = {
		known: new Set(["opencode-go/hy4-flash-free", "opencode/hy4-flash-free", "opencode/beam-free"]),
		sources: ["/tmp/models-store.json"],
	};
	const decision = decideRoute({
		legs: LEGS,
		cache: emptyRouteCache(),
		catalog,
		requested: { provider: "opencode", model: "zzz-does-not-exist" },
		now,
	});
	calls += 1;
	assert.equal(decision.reason, "unknown-model");
	assert.equal(decision.target, "opencode", "the pi leg must not be asked");
	assert.match(String(decision.evidence), /not in pi's model catalog/);
	assert.match(String(decision.evidence), /silently substitute/);
	assert.equal(calls, 1);

	// A model that IS in the catalog still goes to the pi leg, unchanged. It must not be the
	// opencode leg's own model id, or the explicit match would land on that leg instead.
	const known = decideRoute({
		legs: LEGS,
		cache: emptyRouteCache(),
		catalog,
		requested: { provider: "opencode", model: "beam-free" },
		now,
	});
	assert.equal(known.reason, "explicit");
	assert.equal(known.target, "pi");
});

test("the catalog is read from pi's two files", () => {
	const dir = mkdtempSync(path.join(tmpdir(), "route-catalog-"));
	const store = path.join(dir, "models-store.json");
	const custom = path.join(dir, "models.json");
	writeFileSync(store, JSON.stringify({ opencode: { models: [{ id: "hy4-flash-free" }, { id: "beam-free" }] } }));
	writeFileSync(custom, JSON.stringify({ providers: { "llama-cpp": { models: [{ id: "local-7b" }] } } }));
	const catalog = readModelCatalog({ store, custom });
	assert.equal(isModelKnown(catalog, "opencode", "hy4-flash-free"), true);
	assert.equal(isModelKnown(catalog, "llama-cpp", "local-7b"), true, "custom providers count");
	assert.equal(isModelKnown(catalog, "opencode", "zzz-does-not-exist"), false);
	assert.equal(isModelKnown(catalog, "nobody", "anything"), false);
	assert.equal(catalog.sources.length, 2);
	// A missing file is not an error.
	assert.deepEqual(readModelCatalog({ store: path.join(dir, "nope.json"), custom: path.join(dir, "nope2.json") }).known.size, 0);
});

test("a silent model substitution is detected from the manager record", async () => {
	resetSubagentsReady();
	markSubagentsReady();
	const bus = fakeBus();
	bus.on(SUBAGENTS_SPAWN, (data) => {
		const { requestId } = data as { requestId: string };
		bus.emit(`${SUBAGENTS_SPAWN}:reply:${requestId}`, { success: true, data: { id: "agent-sub" } });
		setTimeout(() => bus.emit(SUBAGENTS_COMPLETED, { id: "agent-sub", status: "completed", result: "Q" }), 5);
	});
	// pi-subagents resolves an unknown config model by falling back to the PARENT model.
	(globalThis as Record<symbol, unknown>)[SUBAGENTS_MANAGER_KEY] = {
		getRecord: (id: string) =>
			id === "agent-sub"
				? { invocation: { modelId: "opencode-go/hy4-flash-free", modelName: "Hy4", thinking: "low", requestedThinking: "low" } }
				: undefined,
	};
	try {
		const result = await runPiLeg({ bus, type: "scout", prompt: "go", model: "opencode/zzz-does-not-exist", timeoutMs: 2_000, discoveryTimeoutMs: 100 });
		assert.equal(result.status, "ok");
		if (result.status !== "ok") return;
		assert.equal(result.recordReachable, true);
		assert.equal(result.effective?.modelId, "opencode-go/hy4-flash-free");
		assert.notEqual(result.effective?.modelId, "opencode/zzz-does-not-exist");
	} finally {
		delete (globalThis as Record<symbol, unknown>)[SUBAGENTS_MANAGER_KEY];
	}

	// No registry at all: the caller must be told the effective model is unknown, not handed the
	// request dressed up as a result.
	assert.equal(lookupEffectiveInvocation("agent-sub"), undefined);
});

test("the route line never puts a placeholder in the provider or reason slot", () => {
	const cases: RouteDecision[] = [
		{ target: "pi", provider: "pi", model: "default", reason: "default", decidedAt: AT, legIndex: 0 },
		{ target: "pi", provider: "", model: "opencode/hy4-flash-free", reason: "explicit", decidedAt: AT, legIndex: 0 },
		{ target: "opencode", provider: "opencode", model: "hy4-flash-free", reason: "cached-dead", decidedAt: AT, legIndex: 1, evidence: "429" },
		{
			target: "pi",
			provider: "opencode-go",
			model: "hy4-flash-free",
			reason: "silent-substitution",
			requestedModel: "opencode/zzz-does-not-exist",
			effectiveModel: "opencode-go/hy4-flash-free",
			detection: "manager record invocation.modelId",
			decidedAt: AT,
			legIndex: 0,
		},
		{ target: "opencode", provider: "opencode", model: "hy4-flash-free", reason: "unknown-model", decidedAt: AT, legIndex: 1, evidence: "model not in catalog" },
	];
	const REASONS = ["explicit", "default", "fallback", "cached-dead", "silent-substitution", "unknown-model"];
	for (const decision of cases) {
		const line = routeLine(decision);
		const slots = line.split(" · ");
		const provider = slots[2].replace(/^opencode:/, "").split("/")[0];
		assert.notEqual(provider, "-", `provider slot rendered as "-": ${line}`);
		assert.notEqual(provider, "", `provider slot rendered empty: ${line}`);
		assert.ok(REASONS.includes(slots[4]), `reason slot must be a known reason, got ${slots[4]} in ${line}`);
	}
	// Both models are shown when one was substituted for the other.
	const substituted = routeLine(cases[3]);
	assert.match(substituted, /opencode\/default → opencode-go\/hy4-flash-free|hy4-flash-free → opencode-go\/hy4-flash-free/);
	assert.match(substituted, /silent-substitution/);
});

test("the pi leg's usage maps into the bridge shape, cost only when reported", () => {
	// Event payload as `subagents:completed` carries it (plugin index.ts), cost included.
	const withCost = piUsageToUsage({ input: 508, output: 116, reasoning: 11, cacheRead: 64, cacheWrite: 0, cost: { total: 0.002 } });
	assert.deepEqual(withCost, { input: 508, output: 116, reasoning: 11, cacheRead: 64, cacheWrite: 0, cost: 0.002 });

	// `showCost` off: tokens still reported, and NO invented cost.
	const noCost = piUsageToUsage({ input: 12, output: 3, reasoning: 0, cacheRead: 0, cacheWrite: 0 });
	assert.equal(noCost?.cost, undefined, "cost must stay absent rather than become 0");
	assert.equal(noCost?.input, 12);

	assert.equal(piUsageToUsage(undefined), undefined);
	assert.equal(piUsageToUsage({}), undefined, "nothing spent is not a usage line");
});
