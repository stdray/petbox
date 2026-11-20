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
 * 2. **The child is read-only by default.** That config also carries an explicit deny
 *    policy (`buildChildConfig` in `delegate.ts`): edit/write/shell/subagent denied, only
 *    read/glob/grep/websearch allowed. Without it `--auto` would turn an unattended
 *    delegation into an agent that writes and runs shell in the session cwd, with nobody
 *    watching. `allowWrite` / `allowShell` are the only way to lift that, and they are the
 *    only reason `--auto` ever appears in the argv.
 * 3. **`-m` is always passed** (resolved default below). Without an explicit
 *    model the config default resolves to an `opencode-go/*` model and the run
 *    dies with `403 … subscription required`.
 * 4. **stdin is closed** on the child (`delegate.ts` uses `stdio: ['ignore', …]`).
 *    Otherwise `opencode run` reads stdin to EOF and hangs.
 */

import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import * as path from "node:path";
import { Type } from "typebox";

import {
	buildChildConfig,
	classifyResult,
	type ChildPolicy,
	DEFAULT_MODEL,
	DEFAULT_TIMEOUT_MS,
	needsAutoApprove,
	resultBody,
	runOpencode,
} from "./delegate.ts";
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
	unknownEvents: number;
	allowWrite: boolean;
	allowShell: boolean;
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
	parts.push(`writes ${details.allowWrite ? "allowed" : "denied"}, shell ${details.allowShell ? "allowed" : "denied"}`);
	return `---\nopencode_delegate | ${parts.join(" | ")}`;
}

/** Write the child's private config: model pin, deny policy, and no MCP servers. */
async function makeConfigDir(model: string, policy: ChildPolicy): Promise<string> {
	const dir = await mkdtemp(path.join(tmpdir(), "pi-opencode-bridge-"));
	const config = buildChildConfig(model, policy);
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
			"opencode_delegate runs the delegated agent READ-ONLY by default: no file writes, no shell. Pass allowWrite or allowShell only when the user has asked for changes on disk; they are the dangerous flags.",
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
			allowWrite: Type.Optional(
				Type.Boolean({
					description:
						"DANGEROUS: let the delegated agent edit and write files in the session working directory. " +
						"Denied by default (the generated child config denies it). Also passes --auto.",
				}),
			),
			allowShell: Type.Optional(
				Type.Boolean({
					description:
						"DANGEROUS: let the delegated agent run shell commands in the session working directory. " +
						"Denied by default (the generated child config denies it). Also passes --auto.",
				}),
			),
		}),

		async execute(_toolCallId, params, signal, onUpdate, ctx) {
			const model = params.model?.trim() || DEFAULT_MODEL;
			const cwd = params.cwd ? path.resolve(ctx.cwd, params.cwd) : ctx.cwd;
			const timeoutMs = params.timeoutMs && params.timeoutMs > 0 ? params.timeoutMs : DEFAULT_TIMEOUT_MS;
			const policy: ChildPolicy = {
				allowWrite: params.allowWrite === true,
				allowShell: params.allowShell === true,
			};
			// `--auto` only when something was explicitly approved; the deny rules in the
			// generated config stand either way.
			const auto = needsAutoApprove(policy);

			let configDir: string;
			try {
				configDir = await makeConfigDir(model, policy);
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
				unknownEvents: 0,
				allowWrite: policy.allowWrite === true,
				allowShell: policy.allowShell === true,
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
					auto,
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
				details.unknownEvents = result.unknownEvents;
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
				const verdict = classifyResult(result);
				// On failure this is "": the placeholder must not stand in for an answer.
				const body = resultBody(answer, verdict);
				const notes: string[] = [...verdict.reasons];
				if (details.stderrTail) notes.push(`stderr: ${details.stderrTail}`);
				const sections = [body.length > 0 ? headTail(body, RESULT_HEAD_CHARS, RESULT_TAIL_CHARS) : "", ...notes, summarize(details)];
				const text = sections.filter((section) => section.length > 0).join("\n\n");

				return verdict.failed
					? { content: [{ type: "text", text }], details, isError: true }
					: { content: [{ type: "text", text }], details };
			} finally {
				await rm(configDir, { recursive: true, force: true }).catch(() => {});
			}
		},
	});
}
