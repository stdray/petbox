/**
 * opencode_delegate — run a prompt through an external `opencode` CLI process and
 * return its answer as a typed tool result.
 *
 * Why delegate to opencode at all: it is a second agent harness with its own
 * model roster, its own free tier and its own tool surface. Handing it one
 * self-contained prompt is cheaper than wiring those models into pi, and the
 * result comes back with usage/cost so the caller can see what it spent.
 *
 * Three things are load-bearing and must not be "cleaned up":
 *
 * 1. **A fresh `OPENCODE_CONFIG_DIR` per call.** It makes opencode load ONLY that
 *    directory's config. Without it the child inherits the project's
 *    `.opencode/opencode.json`, which lists the production PetBox MCP server — a
 *    child agent would then hold write access to the live task boards. The temp
 *    dir is created before the run and removed in a `finally`.
 * 2. **`-m` is always passed** (resolved default below). Without an explicit
 *    model the config default resolves to an `opencode-go/*` model and the run
 *    dies with `403 … subscription required`.
 * 3. **stdin is closed** on the child (`delegate.ts` uses `stdio: ['ignore', …]`).
 *    Otherwise `opencode run` reads stdin to EOF and hangs.
 */

import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import * as path from "node:path";
import { Type } from "typebox";

import { DEFAULT_MODEL, DEFAULT_TIMEOUT_MS, runOpencode } from "./delegate.ts";
import type { OpencodeErrorInfo, OpencodeEvent, Usage } from "./types.ts";

const TOOL_NAME = "opencode_delegate";

/** Head and tail kept when the child's answer is longer than this. */
const RESULT_HEAD_CHARS = 4_000;
const RESULT_TAIL_CHARS = 8_000;

/** Same shape for every result, including progress updates: pi requires it. */
interface DelegateDetails {
	model: string;
	agent?: string;
	sessionId?: string;
	usage: Usage;
	steps: number;
	finishReason?: string;
	toolCalls: Array<{ tool: string; status: string; title?: string }>;
	exitCode: number | null;
	timedOut: boolean;
	aborted: boolean;
	durationMs: number;
	malformedLines: number;
	textTruncated: boolean;
	stderrTail?: string;
	error?: OpencodeErrorInfo;
}

function headTail(text: string, head: number, tail: number): string {
	if (text.length <= head + tail) return text;
	const omitted = text.length - head - tail;
	return `${text.slice(0, head)}\n…[${omitted} chars omitted]…\n${text.slice(text.length - tail)}`;
}

function describeUsage(usage: Usage): string {
	const cache = usage.cacheRead + usage.cacheWrite === 0
		? ""
		: ` cache r/w ${usage.cacheRead}/${usage.cacheWrite}`;
	return `tokens in ${usage.input} out ${usage.output} reasoning ${usage.reasoning}${cache} | cost ${usage.cost}`;
}

function summarize(details: DelegateDetails): string {
	const parts = [
		details.sessionId ? `session ${details.sessionId}` : "no session id",
		`exit ${details.exitCode ?? "null"}`,
		`steps ${details.steps}`,
		`tools ${details.toolCalls.length}`,
		describeUsage(details.usage),
		`${(details.durationMs / 1000).toFixed(1)}s`,
	];
	if (details.timedOut) parts.push("TIMED OUT");
	if (details.aborted) parts.push("ABORTED");
	if (details.finishReason) parts.push(`finish ${details.finishReason}`);
	return `---\nopencode_delegate | ${parts.join(" | ")}`;
}

/** Write the child's private config: one model pin, no MCP servers, nothing else. */
async function makeConfigDir(model: string): Promise<string> {
	const dir = await mkdtemp(path.join(tmpdir(), "pi-opencode-bridge-"));
	const config = {
		$schema: "https://opencode.ai/config.json",
		model,
		// Deliberately no `mcp` key: the child must not reach the production
		// PetBox MCP server that the repo's own .opencode/opencode.json declares.
	};
	await writeFile(path.join(dir, "opencode.json"), `${JSON.stringify(config, null, 2)}\n`, "utf8");
	return dir;
}

export default function opencodeBridge(pi: ExtensionAPI): void {
	pi.registerTool({
		name: TOOL_NAME,
		label: "opencode delegate",
		description:
			"Delegate a self-contained prompt to an external `opencode` CLI agent and return its final answer, " +
			"tool calls, token usage and cost. Runs a private opencode server with a throwaway config (no MCP servers). " +
			"Use it to get a second harness's answer, or to reach models this session does not have.",
		promptSnippet: "Delegate a prompt to an external opencode agent and return its answer, tool calls and usage",
		promptGuidelines: [
			"Use opencode_delegate when the user asks to run something through opencode, or names an opencode model explicitly.",
			`opencode_delegate defaults to ${DEFAULT_MODEL}; pass model to override it, and pass session to continue a previous opencode_delegate conversation.`,
		],
		parameters: Type.Object({
			prompt: Type.String({ description: "The prompt for the opencode agent. Include all context it needs." }),
			agent: Type.Optional(Type.String({ description: 'opencode agent role, e.g. "build" or "plan".' })),
			model: Type.Optional(
				Type.String({ description: `provider/model, e.g. "${DEFAULT_MODEL}". Defaults to that model.` }),
			),
			variant: Type.Optional(Type.String({ description: 'Model variant, appended as "#variant" to model.' })),
			session: Type.Optional(
				Type.String({ description: "opencode session id (ses_…) from a previous call, to continue that conversation." }),
			),
			cwd: Type.Optional(Type.String({ description: "Working directory for the child. Defaults to the pi session cwd." })),
			timeoutMs: Type.Optional(
				Type.Number({ description: `Wall-clock timeout in milliseconds. Defaults to ${DEFAULT_TIMEOUT_MS}.` }),
			),
			auto: Type.Optional(
				Type.Boolean({ description: "Auto-approve permissions the child's config does not deny (opencode --auto)." }),
			),
		}),

		async execute(_toolCallId, params, signal, onUpdate, ctx) {
			const model = params.model?.trim() || DEFAULT_MODEL;
			const cwd = params.cwd ? path.resolve(ctx.cwd, params.cwd) : ctx.cwd;
			const timeoutMs = params.timeoutMs && params.timeoutMs > 0 ? params.timeoutMs : DEFAULT_TIMEOUT_MS;

			let configDir: string;
			try {
				configDir = await makeConfigDir(model);
			} catch (err) {
				throw new Error(`opencode_delegate: could not create the child config dir: ${(err as Error).message}`);
			}

			const toolCalls: DelegateDetails["toolCalls"] = [];
			const details: DelegateDetails = {
				model,
				agent: params.agent,
				usage: { input: 0, output: 0, reasoning: 0, cacheRead: 0, cacheWrite: 0, cost: 0 },
				steps: 0,
				toolCalls,
				exitCode: null,
				timedOut: false,
				aborted: false,
				durationMs: 0,
				malformedLines: 0,
				textTruncated: false,
			};

			const onEvent = (event: OpencodeEvent) => {
				if (event.type === "tool_use" && typeof event.part?.tool === "string") {
					const state = (event.part.state ?? {}) as { status?: string; title?: string };
					const entry: DelegateDetails["toolCalls"][number] = {
						tool: event.part.tool,
						status: state.status ?? "unknown",
					};
					if (state.title) entry.title = state.title;
					toolCalls.push(entry);
					onUpdate?.({
						content: [{ type: "text", text: `opencode: ${entry.tool} ${entry.status}` }],
						details,
					});
				}
			};

			try {
				const result = await runOpencode({
					prompt: params.prompt,
					agent: params.agent,
					model,
					variant: params.variant,
					session: params.session,
					auto: params.auto,
					cwd,
					timeoutMs,
					signal,
					configDir,
					onEvent,
				});

				details.sessionId = result.sessionId;
				details.usage = result.usage;
				details.steps = result.steps;
				details.finishReason = result.finishReason;
				details.toolCalls = result.toolCalls.map((call) => {
					const entry: DelegateDetails["toolCalls"][number] = { tool: call.tool, status: call.status };
					if (call.title) entry.title = call.title;
					return entry;
				});
				details.exitCode = result.exitCode;
				details.timedOut = result.timedOut;
				details.aborted = result.aborted;
				details.durationMs = result.durationMs;
				details.malformedLines = result.malformedLines;
				details.textTruncated = result.textTruncated;
				if (result.stderrTail.trim()) details.stderrTail = result.stderrTail.trim().slice(-2_000);
				if (result.error) details.error = result.error;

				const answer = result.text.trim();
				const body =
					answer.length > 0
						? headTail(answer, RESULT_HEAD_CHARS, RESULT_TAIL_CHARS)
						: "(the opencode agent produced no text)";
				const notes: string[] = [];
				if (result.error) notes.push(`error: ${result.error.message}`);
				if (result.timedOut) notes.push(`timed out after ${timeoutMs} ms — the child was killed`);
				if (result.aborted) notes.push("aborted");
				if (result.exitCode !== null && result.exitCode !== 0) notes.push(`child exited with ${result.exitCode}`);
				if (details.stderrTail) notes.push(`stderr: ${details.stderrTail}`);
				const text = notes.length > 0 ? `${body}\n\n${notes.join("\n")}\n${summarize(details)}` : `${body}\n\n${summarize(details)}`;

				const failed = Boolean(result.error) || result.timedOut || result.aborted || (result.exitCode ?? 0) !== 0;
				return failed
					? { content: [{ type: "text", text }], details, isError: true }
					: { content: [{ type: "text", text }], details };
			} finally {
				await rm(configDir, { recursive: true, force: true }).catch(() => {});
			}
		},
	});
}
