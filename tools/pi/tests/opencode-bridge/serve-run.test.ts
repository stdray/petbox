/**
 * The pure half of server-backed runs: the handle store, message aggregation, error mapping,
 * and the model/auth encoding. Everything that needs a live server is covered by the live run,
 * not here.
 */

import assert from "node:assert/strict";
import test from "node:test";

import {
	aggregateServeMessages,
	apiError,
	basicAuth,
	createSession,
	forgetHandle,
	getHandle,
	interruptSession,
	isSettled,
	listHandles,
	modelRef,
	promptSession,
	readMessages,
	registerHandle,
	rememberServer,
	resetHandles,
	stopAllServers,
	stopServer,
	steerSession,
	type RunHandle,
	type ServeMessage,
} from "../../extensions/opencode-bridge/serve-run.ts";

function handle(id: string): RunHandle {
	return {
		id,
		sessionId: `ses_${id}`,
		url: "http://127.0.0.1:1",
		model: "opencode/hy4-flash-free",
		createdAt: "2026-01-01T00:00:00.000Z",
		routeLine: "route opencode · scout · opencode:opencode/hy4-flash-free · low · explicit",
	};
}

test("the handle store round-trips and releases", () => {
	resetHandles();
	const run = registerHandle(handle("run_a"));
	assert.equal(getHandle("run_a")?.sessionId, "ses_run_a");
	assert.deepEqual(listHandles().map((entry) => entry.id), ["run_a"]);
	assert.equal(forgetHandle("run_a"), true);
	assert.equal(getHandle("run_a"), undefined);
	assert.equal(forgetHandle("run_a"), false, "forgetting twice is not an error");
});

test("servers behind handles stop once", () => {
	resetHandles();
	let stopped = 0;
	registerHandle(handle("run_b"));
	rememberServer("run_b", () => {
		stopped += 1;
	});
	assert.equal(stopServer("run_b"), true);
	assert.equal(stopServer("run_b"), false, "a second stop is a no-op");
	assert.equal(stopped, 1);
	assert.equal(stopAllServers(), 0);
});

test("aggregation folds assistant text, usage and the idle marker", () => {
	const messages: ServeMessage[] = [
		{ type: "user", id: "u1", text: "count to 5" },
		{
			type: "assistant",
			id: "a1",
			model: { providerID: "opencode", id: "hy4-flash-free" },
			content: [
				{ type: "reasoning", text: "thinking" },
				{ type: "text", text: "one two " },
				{ type: "tool", tool: "read", state: { status: "completed" } },
			],
			tokens: { input: 10, output: 2, reasoning: 3, cache: { read: 4, write: 0 } },
			cost: 0,
		},
		{ type: "assistant", content: [{ type: "text", text: "three" }], tokens: { input: 5, output: 1 } },
		{ type: "idle", outcome: "succeeded" },
	];
	const aggregate = aggregateServeMessages(messages);
	assert.equal(aggregate.text, "one two three");
	assert.equal(aggregate.steps, 2);
	assert.deepEqual(aggregate.toolCalls, ["read:completed"]);
	assert.equal(aggregate.model, "opencode/hy4-flash-free", "what answered, read from the transcript");
	assert.equal(aggregate.outcome, "succeeded");
	assert.equal(aggregate.error, undefined);
	assert.deepEqual(aggregate.usage, { input: 15, output: 3, reasoning: 3, cacheRead: 4, cacheWrite: 0, cost: 0 });
});

test("a failed idle outcome is an error, not a quiet success", () => {
	const aggregate = aggregateServeMessages([
		{ type: "assistant", content: [{ type: "text", text: "partial" }] },
		{ type: "idle", outcome: "aborted" },
	]);
	assert.equal(aggregate.outcome, "aborted");
	assert.equal(aggregate.error, "idle outcome: aborted");

	const providerError = aggregateServeMessages([
		{ type: "assistant", content: [], error: { message: "Free usage limit reached" } },
		{ type: "idle", outcome: "succeeded" },
	]);
	assert.equal(providerError.error, "Free usage limit reached", "the provider's own words survive");
});

test("settling is keyed on the idle message", () => {
	assert.equal(isSettled([{ type: "user" }, { type: "assistant" }]), false);
	assert.equal(isSettled([{ type: "assistant" }, { type: "idle", outcome: "succeeded" }]), true);
});

test("server errors are mapped to one readable line", () => {
	assert.equal(apiError({ _tag: "UnauthorizedError", message: "Authentication required" }, 401), "UnauthorizedError: Authentication required");
	assert.equal(apiError({ message: "boom" }, 500), "boom");
	assert.equal(apiError({ error: "plain" }, 400), "plain");
	assert.equal(apiError(undefined, 503), "HTTP 503");
});

test("model refs and auth encoding", () => {
	assert.deepEqual(modelRef("opencode/hy4-flash-free"), { providerID: "opencode", id: "hy4-flash-free" });
	assert.deepEqual(modelRef("opencode-go/hy4-flash-free#high"), {
		providerID: "opencode-go",
		id: "hy4-flash-free",
		variant: "high",
	});
	assert.throws(() => modelRef("bare-id"), /is not "provider\/model"/);
	assert.equal(basicAuth("pw"), `Basic ${Buffer.from("opencode:pw").toString("base64")}`);
});

test("an unreachable server is data, not a throw", async () => {
	const url = "http://127.0.0.1:9";
	const created = await createSession(url, "pw", { model: "opencode/hy4-flash-free", title: "t" });
	assert.match(String(created.error), /transport:/);
	assert.equal(created.sessionId, undefined);

	const prompted = await promptSession(url, "pw", "ses_x", "hi", "steer");
	assert.match(String(prompted.error), /transport:/);

	const interrupted = await interruptSession(url, "pw", "ses_x");
	assert.equal(interrupted.interrupted, false);
	assert.match(String(interrupted.error), /transport:/);

	assert.deepEqual(await readMessages(url, "pw", "ses_x"), []);
});

function steerFakes(options: { interrupted: boolean; interruptError?: string; promptError?: string }) {
	const calls: string[] = [];
	return {
		calls,
		deps: {
			interrupt: async () => {
				calls.push("interrupt");
				return { interrupted: options.interrupted, ...(options.interruptError ? { error: options.interruptError } : {}) };
			},
			prompt: async (_url: string, _pw: string, _sid: string, text: string, delivery: "steer" | "queue", promptOptions?: { resume?: boolean }) => {
				calls.push(`prompt:${delivery}:${text}${promptOptions?.resume ? ":resume" : ""}`);
				return options.promptError ? { error: options.promptError } : { messageId: "msg_new" };
			},
			waitForIdle: async () => {
				calls.push("waitForIdle");
				return true;
			},
		},
	};
}

const TARGET = { url: "http://127.0.0.1:1", password: "pw", sessionId: "ses_x" };

test("hard steer on a RUNNING session interrupts, waits, then submits as a new prompt", async () => {
	const fakes = steerFakes({ interrupted: true });
	const outcome = await steerSession(TARGET, "Forget the numbers", true, fakes.deps);
	assert.equal(outcome.delivered, true);
	assert.equal(outcome.interrupted, true);
	assert.equal(outcome.messageId, "msg_new");
	assert.deepEqual(fakes.calls, ["interrupt", "waitForIdle", "prompt:queue:Forget the numbers:resume"], "order matters: stop, settle, then submit with resume");
	assert.match(outcome.note, /running step interrupted/);
});

test("hard steer on an IDLE session just submits — no interrupt noise", async () => {
	const fakes = steerFakes({ interrupted: false });
	const outcome = await steerSession(TARGET, "Do this next", true, fakes.deps);
	assert.equal(outcome.delivered, true);
	assert.equal(outcome.interrupted, false, "nothing was interrupted, so nothing is reported as interrupted");
	assert.deepEqual(fakes.calls, ["interrupt", "prompt:queue:Do this next:resume"], "no settle wait when nothing was running");
	assert.match(outcome.note, /already idle/);
});

test("soft steer uses delivery=steer and never calls interrupt", async () => {
	const fakes = steerFakes({ interrupted: false });
	const outcome = await steerSession(TARGET, "nudge", false, fakes.deps);
	assert.equal(outcome.delivered, true);
	assert.equal(outcome.interrupted, false);
	assert.deepEqual(fakes.calls, ["prompt:steer:nudge"]);
	assert.match(outcome.note, /turn boundary/);
});

test("a refused interrupt or prompt is reported, not swallowed", async () => {
	const badInterrupt = await steerSession(TARGET, "x", true, steerFakes({ interrupted: false, interruptError: "HTTP 500" }).deps);
	assert.equal(badInterrupt.delivered, false);
	assert.equal(badInterrupt.error, "HTTP 500");

	const badPrompt = await steerSession(TARGET, "x", true, steerFakes({ interrupted: true, promptError: "HTTP 400" }).deps);
	assert.equal(badPrompt.delivered, false);
	assert.equal(badPrompt.interrupted, true, "the interrupt did happen; the prompt is what failed");
});
