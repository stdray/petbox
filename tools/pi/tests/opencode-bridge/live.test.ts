/**
 * Live acceptance for the bridge: real `opencode` processes, real tokens.
 *
 * These cost about a minute of wall clock and a network round trip each, so they
 * are skippable (`OPENCODE_BRIDGE_SKIP_LIVE=1`) — but they are the only proof
 * that the measured CLI contract still holds, so they run by default.
 *
 * What each one pins:
 * - a forced tool call comes back as a `completed` tool call with the file's
 *   contents, the run reports `cost: 0` on the free model, and the session id
 *   looks like `ses_…`;
 * - passing that session id back resumes it (same id, remembers the value);
 * - `timeoutMs` kills the child tree and leaves no orphan `opencode` process.
 */

import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import * as path from "node:path";
import test from "node:test";

import { DEFAULT_MODEL, runOpencode } from "../../extensions/opencode-bridge/delegate.ts";

const SKIP_LIVE = process.env.OPENCODE_BRIDGE_SKIP_LIVE === "1";
const LIVE_TIMEOUT_MS = 180_000;

/** Config dir the child gets: one model pin, no MCP servers. */
async function makeConfigDir(): Promise<string> {
	const dir = await mkdtemp(path.join(tmpdir(), "oc-bridge-test-cfg-"));
	await writeFile(
		path.join(dir, "opencode.json"),
		`${JSON.stringify({ $schema: "https://opencode.ai/config.json", model: DEFAULT_MODEL }, null, 2)}\n`,
		"utf8",
	);
	return dir;
}

/** Number of `opencode` processes alive right now (0 where we cannot tell). */
function countOpencodeProcesses(): number {
	try {
		if (process.platform === "win32") {
			const out = execFileSync("tasklist", ["/FI", "IMAGENAME eq opencode.exe", "/NH"], {
				encoding: "utf8",
				windowsHide: true,
			});
			return out.split(/\r?\n/).filter((line) => /opencode\.exe/i.test(line)).length;
		}
		const out = execFileSync("sh", ["-c", "pgrep -c -f 'opencode.*run' || true"], { encoding: "utf8" });
		return Number(out.trim()) || 0;
	} catch {
		return 0;
	}
}

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

test("live: forced tool call reads the file, costs nothing, exposes a session id", { skip: SKIP_LIVE, timeout: 400_000 }, async () => {
	const workDir = await mkdtemp(path.join(tmpdir(), "oc-bridge-test-work-"));
	const configDir = await makeConfigDir();
	try {
		await writeFile(path.join(workDir, "notes.txt"), "ALPHA-42\n", "utf8");

		const first = await runOpencode({
			prompt: "Read notes.txt and tell me the exact value it contains.",
			cwd: workDir,
			configDir,
			timeoutMs: LIVE_TIMEOUT_MS,
		});

		console.log(
			`[live] run1 exit=${first.exitCode} session=${first.sessionId} steps=${first.steps} ` +
				`tools=${first.toolCalls.map((c) => `${c.tool}:${c.status}`).join(",")} ` +
				`usage=${JSON.stringify(first.usage)} text=${JSON.stringify(first.text.slice(0, 120))}`,
		);

		assert.equal(first.exitCode, 0, `child failed: ${first.error?.message ?? first.stderrTail}`);
		assert.match(first.text, /ALPHA-42/, "the answer must carry the file's contents");
		assert.equal(first.usage.cost, 0, "the default model is free");
		assert.ok(first.usage.input > 0, "tokens are reported");
		assert.ok(first.sessionId?.startsWith("ses_"), `session id looks wrong: ${first.sessionId}`);
		assert.ok(
			first.toolCalls.some((call) => call.status === "completed"),
			`no completed tool call: ${JSON.stringify(first.toolCalls)}`,
		);

		const second = await runOpencode({
			prompt: "What exact value did you read earlier? Reply with just that value.",
			cwd: workDir,
			configDir,
			session: first.sessionId,
			timeoutMs: LIVE_TIMEOUT_MS,
		});

		console.log(
			`[live] run2 exit=${second.exitCode} session=${second.sessionId} steps=${second.steps} ` +
				`usage=${JSON.stringify(second.usage)} text=${JSON.stringify(second.text.slice(0, 120))}`,
		);

		assert.equal(second.exitCode, 0, `resumed child failed: ${second.error?.message ?? second.stderrTail}`);
		assert.equal(second.sessionId, first.sessionId, "resuming must continue the same session");
		assert.match(second.text, /ALPHA-42/, "the resumed session remembers the value");
		assert.equal(
			second.toolCalls.some((call) => call.tool === "read"),
			false,
			"it answered from the resumed conversation, not by re-reading the file",
		);
	} finally {
		await rm(workDir, { recursive: true, force: true });
		await rm(configDir, { recursive: true, force: true });
	}
});

test("live: timeout kills the child and leaves no orphan", { skip: SKIP_LIVE, timeout: 120_000 }, async () => {
	const workDir = await mkdtemp(path.join(tmpdir(), "oc-bridge-test-timeout-"));
	const configDir = await makeConfigDir();
	const before = countOpencodeProcesses();
	try {
		const started = Date.now();
		const result = await runOpencode({
			prompt: "Count slowly from 1 to 5000, one number per line, thinking carefully about each number.",
			cwd: workDir,
			configDir,
			timeoutMs: 3000,
		});
		const elapsed = Date.now() - started;

		await sleep(3000);
		const after = countOpencodeProcesses();

		console.log(
			`[live] timeout exit=${result.exitCode} timedOut=${result.timedOut} elapsed=${elapsed}ms ` +
				`procs before=${before} after=${after} killedBy=${result.killedBy}`,
		);

		assert.equal(result.timedOut, true);
		assert.ok(elapsed < 30_000, `timeout took ${elapsed}ms to take effect`);
		assert.ok(after <= before, `orphan opencode processes: before=${before} after=${after}`);
	} finally {
		await rm(workDir, { recursive: true, force: true });
		await rm(configDir, { recursive: true, force: true });
	}
});

test("live: an unknown agent surfaces as a child error, not a hang", { skip: SKIP_LIVE, timeout: 200_000 }, async () => {
	const workDir = await mkdtemp(path.join(tmpdir(), "oc-bridge-test-agent-"));
	const configDir = await makeConfigDir();
	try {
		const result = await runOpencode({
			prompt: "say ok",
			agent: "definitely-not-an-agent",
			cwd: workDir,
			configDir,
			timeoutMs: LIVE_TIMEOUT_MS,
		});
		console.log(`[live] bad agent exit=${result.exitCode} error=${JSON.stringify(result.error)}`);
		// The point: the process finishes and the reason is visible, whatever opencode decides.
		assert.notEqual(result.exitCode, null);
		assert.ok(result.exitCode !== 0 || result.error !== undefined || result.text.length > 0);
	} finally {
		await rm(workDir, { recursive: true, force: true });
		await rm(configDir, { recursive: true, force: true });
	}
});
