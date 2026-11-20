/**
 * Parser/aggregation tests. The fixtures are real `opencode run --format json`
 * lines (opencode 2.3.2) — including the shapes that only show up in anger:
 * a non-final `step_finish` with `reason: "tool-calls"`, and a provider auth
 * error that ends the run with a 403.
 */

import assert from "node:assert/strict";
import test from "node:test";

import { aggregate, parseEventLine, TOOL_OUTPUT_TAIL_CHARS } from "../../extensions/opencode-bridge/parse.ts";

const STEP_START =
	'{"type":"step_start","timestamp":1,"sessionID":"ses_X","part":{"id":"prt_1","sessionID":"ses_X","messageID":"msg_1","type":"step-start"}}';
const TEXT =
	'{"type":"text","sessionID":"ses_X","part":{"type":"text","text":"PONG"}}';
const TOOL_USE =
	'{"type":"tool_use","sessionID":"ses_X","part":{"tool":"read","state":{"status":"completed","input":{"path":"C:\\\\a\\\\b.txt"},"output":"Read file ... lines 1-1\\n1: ALPHA-42","title":"read"}}}';
const STEP_FINISH =
	'{"type":"step_finish","sessionID":"ses_X","part":{"reason":"stop","cost":0,"tokens":{"input":10146,"output":4,"reasoning":0,"cache":{"read":0,"write":0}}}}';
const STEP_FINISH_TOOL_CALLS =
	'{"type":"step_finish","sessionID":"ses_X","part":{"reason":"tool-calls","cost":0,"tokens":{"input":1136,"output":44,"reasoning":0,"cache":{"read":7040,"write":0}}}}';
const ERROR =
	'{"type":"error","sessionID":"ses_X","error":{"type":"provider.auth","message":"An active OpenCode Go subscription is required to use OpenCode Go models.","status":403}}';

function mustParse(line: string) {
	const parsed = parseEventLine(line);
	assert.equal(parsed.ok, true, `expected a parsed event for ${line}`);
	if (!parsed.ok) throw new Error("unreachable");
	return parsed;
}

test("parses each real event shape", () => {
	const start = mustParse(STEP_START);
	assert.equal(start.known, true);
	assert.equal(start.event.type, "step_start");
	assert.equal(start.event.sessionID, "ses_X");

	const text = mustParse(TEXT);
	assert.equal(text.event.type, "text");
	assert.equal(text.event.type === "text" && text.event.part.text, "PONG");

	const tool = mustParse(TOOL_USE);
	assert.equal(tool.event.type, "tool_use");
	assert.equal(tool.event.type === "tool_use" && tool.event.part.tool, "read");

	const finish = mustParse(STEP_FINISH);
	assert.equal(finish.event.type, "step_finish");
	assert.equal(finish.event.type === "step_finish" && finish.event.part.reason, "stop");

	const err = mustParse(ERROR);
	assert.equal(err.event.type, "error");
	if (err.event.type === "error") {
		assert.equal(err.event.error.type, "provider.auth");
		assert.equal(err.event.error.status, 403);
		assert.match(err.event.error.message, /subscription is required/);
	}
});

test("unknown event types are tolerated and flagged, not rejected", () => {
	for (const type of ["retry", "background_wait", "something-brand-new"]) {
		const line = JSON.stringify({ type, sessionID: "ses_X", part: { text: "x" } });
		const parsed = mustParse(line);
		assert.equal(parsed.event.type, type);
		assert.equal(parsed.known, false, `${type} should be reported as unknown`);
	}

	// `reasoning` only appears with --thinking, but it is modelled, so it is known.
	const reasoning = mustParse(JSON.stringify({ type: "reasoning", part: { text: "thinking…" } }));
	assert.equal(reasoning.known, true);
});

test("malformed and partial lines are reported, never thrown", () => {
	const cases: Array<[string, string]> = [
		["", "empty"],
		["   ", "empty"],
		['{"type":"text","part":', "not-json"], // child killed mid-write
		["this is a log line that leaked onto stdout", "not-json"],
		["null", "not-object"],
		["[1,2,3]", "not-object"],
		["42", "not-object"],
		['{"part":{"text":"no type here"}}', "no-type"],
		['{"type":123}', "no-type"],
		['{"type":"text"}', "bad-shape"],
		['{"type":"tool_use","part":{"state":{}}}', "bad-shape"],
		['{"type":"error","error":{"nope":true}}', "bad-shape"],
	];
	for (const [line, expected] of cases) {
		const parsed = parseEventLine(line);
		assert.equal(parsed.ok, false, `expected a failure for ${JSON.stringify(line)}`);
		if (!parsed.ok) assert.equal(parsed.error, expected, `wrong failure for ${JSON.stringify(line)}`);
	}
});

test("aggregate folds a full run", () => {
	const lines = [
		STEP_START,
		TEXT,
		TOOL_USE,
		STEP_FINISH_TOOL_CALLS,
		STEP_START,
		TEXT,
		STEP_FINISH,
	];
	const events = lines.map((line) => mustParse(line).event);
	const result = aggregate(events);

	assert.equal(result.text, "PONGPONG");
	assert.equal(result.sessionId, "ses_X");
	assert.equal(result.steps, 2);
	assert.equal(result.finishReason, "stop");
	assert.equal(result.toolCalls.length, 1);
	assert.equal(result.toolCalls[0].tool, "read");
	assert.equal(result.toolCalls[0].status, "completed");
	assert.match(result.toolCalls[0].outputTail ?? "", /ALPHA-42/);
	assert.deepEqual(result.usage, {
		input: 1136 + 10146,
		output: 44 + 4,
		reasoning: 0,
		cacheRead: 7040,
		cacheWrite: 0,
		cost: 0,
	});
	assert.equal(result.error, undefined);
});

test("aggregate keeps the last finish reason and the last error", () => {
	const events = [
		mustParse(STEP_START).event,
		mustParse(STEP_FINISH_TOOL_CALLS).event,
		mustParse(ERROR).event,
	];
	const result = aggregate(events);
	assert.equal(result.finishReason, "tool-calls");
	assert.equal(result.error?.status, 403);
});

test("aggregate is defensive about missing or wrong-typed fields", () => {
	const events = [
		{ type: "text", part: { text: 123 } },
		{ type: "step_finish", part: { tokens: { input: "nope" } } },
		{ type: "tool_use", part: {} },
		{ type: "error" },
		null,
	] as unknown as Parameters<typeof aggregate>[0];
	const result = aggregate(events as never);
	assert.equal(result.text, "");
	assert.equal(result.toolCalls.length, 0);
	assert.equal(result.usage.input, 0);
	assert.equal(result.error, undefined);
});

test("tool output is capped to a tail", () => {
	const huge = "x".repeat(TOOL_OUTPUT_TAIL_CHARS + 500);
	const line = JSON.stringify({
		type: "tool_use",
		part: { tool: "bash", state: { status: "completed", output: huge } },
	});
	const result = aggregate([mustParse(line).event]);
	assert.equal(result.toolCalls[0].outputTail?.length, TOOL_OUTPUT_TAIL_CHARS);
});
