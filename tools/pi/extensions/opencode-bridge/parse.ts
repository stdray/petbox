/**
 * Parsing and aggregation for `opencode run --format json` output.
 *
 * Pure: no I/O, no clock, no process. `parseEventLine` never throws — a partial
 * line (the child was killed mid-write), a log line that leaked onto stdout, or
 * an event type from a newer CLI all come back as a counted non-event instead.
 */

import type {
	AggregateResult,
	ErrorEvent,
	OpencodeErrorInfo,
	OpencodeEvent,
	ReasoningEvent,
	StepFinishEvent,
	StepStartEvent,
	TextEvent,
	ToolCallRecord,
	ToolUseEvent,
	Usage,
} from "./types.ts";

/** Event types this module understands. Anything else is an `UnknownEvent`. */
export const KNOWN_EVENT_TYPES = [
	"step_start",
	"text",
	"reasoning",
	"tool_use",
	"step_finish",
	"error",
] as const;

const KNOWN: ReadonlySet<string> = new Set<string>(KNOWN_EVENT_TYPES);

/** Why a line did not yield an event. `empty` lines are common and harmless. */
export type ParseFailure = "empty" | "not-json" | "not-object" | "no-type" | "bad-shape";

export type ParseResult =
	| { ok: true; event: OpencodeEvent; known: boolean }
	| { ok: false; error: ParseFailure };

/** Tool output is kept as a tail: one `read` can be megabytes of file content. */
export const TOOL_OUTPUT_TAIL_CHARS = 2000;

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function str(value: unknown): string | undefined {
	return typeof value === "string" ? value : undefined;
}

function num(value: unknown): number | undefined {
	return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function sessionIdOf(obj: Record<string, unknown>): string | undefined {
	const id = str(obj.sessionID);
	return id && id.length > 0 ? id : undefined;
}

function baseOf(obj: Record<string, unknown>) {
	return {
		timestamp: num(obj.timestamp),
		sessionID: sessionIdOf(obj),
		part: obj.part,
		error: obj.error,
	};
}

/**
 * Turn one stdout line into an event.
 *
 * A line with a `type` we do not model is *success* with `known: false` — it is
 * tolerated, not an error. Shape failures on a type we do model are reported as
 * `bad-shape` so the caller can count them separately from unknown types.
 */
export function parseEventLine(line: string): ParseResult {
	if (typeof line !== "string") return { ok: false, error: "empty" };
	const trimmed = line.trim();
	if (trimmed.length === 0) return { ok: false, error: "empty" };

	let parsed: unknown;
	try {
		parsed = JSON.parse(trimmed);
	} catch {
		return { ok: false, error: "not-json" };
	}
	if (!isRecord(parsed)) return { ok: false, error: "not-object" };

	const type = str(parsed.type);
	if (!type) return { ok: false, error: "no-type" };

	const base = baseOf(parsed);

	if (!KNOWN.has(type)) {
		return { ok: true, event: { type, ...base } as OpencodeEvent, known: false };
	}

	switch (type) {
		case "step_start": {
			const event: StepStartEvent = { type: "step_start", ...base };
			return { ok: true, event, known: true };
		}
		case "text": {
			if (!isRecord(base.part) || typeof base.part.text !== "string") {
				return { ok: false, error: "bad-shape" };
			}
			const event: TextEvent = { type: "text", ...base, part: base.part as TextEvent["part"] };
			return { ok: true, event, known: true };
		}
		case "reasoning": {
			if (!isRecord(base.part)) return { ok: false, error: "bad-shape" };
			const event: ReasoningEvent = {
				type: "reasoning",
				...base,
				part: base.part as ReasoningEvent["part"],
			};
			return { ok: true, event, known: true };
		}
		case "tool_use": {
			if (!isRecord(base.part) || typeof base.part.tool !== "string") {
				return { ok: false, error: "bad-shape" };
			}
			const event: ToolUseEvent = {
				type: "tool_use",
				...base,
				part: base.part as unknown as ToolUseEvent["part"],
			};
			return { ok: true, event, known: true };
		}
		case "step_finish": {
			if (!isRecord(base.part)) return { ok: false, error: "bad-shape" };
			const event: StepFinishEvent = {
				type: "step_finish",
				...base,
				part: base.part as StepFinishEvent["part"],
			};
			return { ok: true, event, known: true };
		}
		case "error": {
			const err = parsed.error;
			if (!isRecord(err) || typeof err.message !== "string") {
				return { ok: false, error: "bad-shape" };
			}
			const event: ErrorEvent = {
				type: "error",
				...base,
				error: {
					type: str(err.type),
					message: err.message,
					status: num(err.status),
				},
			};
			return { ok: true, event, known: true };
		}
		default:
			return { ok: true, event: { type, ...base } as OpencodeEvent, known: false };
	}
}

export function emptyUsage(): Usage {
	return { input: 0, output: 0, reasoning: 0, cacheRead: 0, cacheWrite: 0, cost: 0 };
}

function tailOf(value: unknown, max: number): string | undefined {
	if (value === undefined || value === null) return undefined;
	const text = typeof value === "string" ? value : JSON.stringify(value);
	if (text === undefined) return undefined;
	return text.length > max ? text.slice(text.length - max) : text;
}

function toolCallOf(event: ToolUseEvent): ToolCallRecord {
	const state = isRecord(event.part.state) ? event.part.state : {};
	const status = str(state.status) ?? "unknown";
	const record: ToolCallRecord = { tool: event.part.tool, status };
	const title = str(state.title);
	if (title !== undefined) record.title = title;
	if (state.input !== undefined) record.input = state.input;
	const outputTail = tailOf(state.output, TOOL_OUTPUT_TAIL_CHARS);
	if (outputTail !== undefined) record.outputTail = outputTail;
	return record;
}

/**
 * Fold events into one result. Sums usage across every `step_finish` (one per
 * model request), keeps the last `error`, and reports the last `finishReason`
 * whether or not it is `"stop"` — a run that ended on `"tool-calls"` is torn
 * off mid-work and the caller decides what that means.
 */
export function aggregate(events: Iterable<OpencodeEvent>): AggregateResult {
	let text = "";
	const toolCalls: ToolCallRecord[] = [];
	const usage = emptyUsage();
	let sessionId: string | undefined;
	let error: OpencodeErrorInfo | undefined;
	let steps = 0;
	let finishReason: string | undefined;

	for (const event of events) {
		if (!event || typeof event !== "object") continue;
		if (sessionId === undefined && typeof event.sessionID === "string" && event.sessionID) {
			sessionId = event.sessionID;
		}

		switch (event.type) {
			case "step_start":
				steps += 1;
				break;
			case "text":
				if (typeof event.part?.text === "string") text += event.part.text;
				break;
			case "reasoning":
				break;
			case "tool_use":
				if (typeof event.part?.tool === "string") toolCalls.push(toolCallOf(event));
				break;
			case "step_finish": {
				const part = event.part;
				if (!part) break;
				usage.cost += num(part.cost) ?? 0;
				const tokens = isRecord(part.tokens) ? part.tokens : {};
				usage.input += num(tokens.input) ?? 0;
				usage.output += num(tokens.output) ?? 0;
				usage.reasoning += num(tokens.reasoning) ?? 0;
				const cache = isRecord(tokens.cache) ? tokens.cache : {};
				usage.cacheRead += num(cache.read) ?? 0;
				usage.cacheWrite += num(cache.write) ?? 0;
				const reason = str(part.reason);
				if (reason !== undefined) finishReason = reason;
				break;
			}
			case "error":
				error = event.error;
				break;
			default:
				break;
		}
	}

	const result: AggregateResult = { text, toolCalls, usage, steps };
	if (sessionId !== undefined) result.sessionId = sessionId;
	if (error !== undefined) result.error = error;
	if (finishReason !== undefined) result.finishReason = finishReason;
	return result;
}
