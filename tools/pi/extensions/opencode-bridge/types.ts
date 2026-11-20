/**
 * Types for the opencode bridge: the NDJSON event union emitted by
 * `opencode run --format json`, and the shapes produced from it.
 *
 * The wire format is not ours — it belongs to the `opencode` CLI. Every field
 * that the aggregation actually reads is therefore optional here where the CLI
 * could plausibly omit it, and unknown event types are represented by
 * `UnknownEvent` rather than rejected, so a CLI upgrade adds a type instead of
 * crashing the parser.
 */

/** Token counters as reported by a `step_finish` event. */
export interface TokenCounts {
	input: number;
	output: number;
	reasoning: number;
	cacheRead: number;
	cacheWrite: number;
}

/** Tokens plus the cost the CLI reports for the run (0 for free models). */
export interface Usage extends TokenCounts {
	cost: number;
}

/** `part.state` of a `tool_use` event. Only the fields we surface are typed. */
export interface ToolCallState {
	status?: string;
	title?: string;
	input?: unknown;
	output?: unknown;
}

/** A tool call flattened out of a `tool_use` event. */
export interface ToolCallRecord {
	tool: string;
	/** `state.status` as reported (`completed`, `error`, `running`, …) or `"unknown"`. */
	status: string;
	title?: string;
	input?: unknown;
	/** Tail of the tool output; full output can be megabytes (a `read` of a big file). */
	outputTail?: string;
}

/** Payload of an `error` event. `type`/`status` are absent for local failures. */
export interface OpencodeErrorInfo {
	type?: string;
	message: string;
	status?: number;
}

export interface BaseEvent {
	type: string;
	timestamp?: number;
	sessionID?: string;
	part?: unknown;
	error?: unknown;
}

export interface StepStartEvent extends BaseEvent {
	type: "step_start";
	part?: { messageID?: string };
}

export interface TextEvent extends BaseEvent {
	type: "text";
	part: { type?: string; text: string };
}

export interface ReasoningEvent extends BaseEvent {
	type: "reasoning";
	part: { type?: string; text?: string };
}

export interface ToolUseEvent extends BaseEvent {
	type: "tool_use";
	part: { tool: string; state?: ToolCallState };
}

export interface StepFinishEvent extends BaseEvent {
	type: "step_finish";
	part: {
		reason?: string;
		cost?: number;
		tokens?: {
			input?: number;
			output?: number;
			reasoning?: number;
			cache?: { read?: number; write?: number };
		};
	};
}

export interface ErrorEvent extends BaseEvent {
	type: "error";
	error: OpencodeErrorInfo;
}

/** `retry`, `background_wait`, `reasoning` under `--thinking`, and anything newer. */
export interface UnknownEvent extends BaseEvent {
	type: string;
}

export type OpencodeEvent =
	| StepStartEvent
	| TextEvent
	| ReasoningEvent
	| ToolUseEvent
	| StepFinishEvent
	| ErrorEvent
	| UnknownEvent;

/** Everything `aggregate()` derives from a stream of events. */
export interface AggregateResult {
	/** Concatenated `text` events across all steps. */
	text: string;
	toolCalls: ToolCallRecord[];
	usage: Usage;
	/** Session id taken from the first event that carries one. */
	sessionId?: string;
	/** Last `error` event, if the run reported one. */
	error?: OpencodeErrorInfo;
	/** Number of model requests: one per `step_start`. */
	steps: number;
	/** `reason` of the final `step_finish` (`"stop"`, `"tool-calls"`, …). */
	finishReason?: string;
}

/** A completed `runOpencode()` call: the aggregate plus process facts. */
export interface RunResult extends AggregateResult {
	exitCode: number | null;
	/** Signal that terminated the child, or `null`. */
	killedBy: string | null;
	/** Tail of stderr, capped by `maxStderrChars`. */
	stderrTail: string;
	timedOut: boolean;
	aborted: boolean;
	durationMs: number;
	/** stdout lines that were not usable events (empty, non-JSON, wrong shape). */
	malformedLines: number;
	/** Well-formed events whose `type` we do not model. */
	unknownEvents: number;
	/** True when `text` was cut down to `maxTextChars`. */
	textTruncated: boolean;
	/** Command actually spawned, for error messages. */
	executable: string;
}
