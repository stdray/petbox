/**
 * Live acceptance for the bridge: real `opencode` processes, real tokens.
 *
 * These cost about a minute of wall clock and a network round trip each, so they
 * are skippable (`OPENCODE_BRIDGE_SKIP_LIVE=1`) — but they are the only proof
 * that the measured CLI contract still holds, so they run by default.
 *
 * THE MODEL COMES FROM `OPENCODE_BRIDGE_LIVE_MODEL` (default `opencode/hy4-flash-free`).
 * The free quota is per-model: `deepseek-v4.1-flash-free` answered all through one day and
 * then answered `provider.free-limit` while `hy4-flash-free` kept working. A live test that
 * hardcodes one model turns an exhausted quota into a false red, so the model is a parameter
 * and a `free-limit` error is a loud SKIP, never a failure.
 *
 * What each one pins:
 * - a forced tool call comes back as a `completed` tool call with the file's
 *   contents, the run reports `cost: 0` on the free model, and the session id
 *   looks like `ses_…`;
 * - passing that session id back resumes it (same id, remembers the value);
 * - `timeoutMs` kills the child tree and leaves no orphan `opencode` process;
 * - a delegated ROLE keeps its identity and its capability ceiling (system prompt applied,
 *   write refused).
 */

import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtemp, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import * as path from "node:path";
import test from "node:test";

import {
	buildChildConfig,
	type ChildPolicy,
	DEFAULT_MODEL,
	type RunResult,
	runOpencode,
} from "../../extensions/opencode-bridge/delegate.ts";
import { readModelCatalog } from "../../extensions/opencode-bridge/route.ts";
import { buildRoleConfig, loadRole } from "../../extensions/opencode-bridge/role-config.ts";

const SKIP_LIVE = process.env.OPENCODE_BRIDGE_SKIP_LIVE === "1";
const LIVE_TIMEOUT_MS = 180_000;

/** Free quota is per-model; override it rather than editing the test. */
const LIVE_MODEL = process.env.OPENCODE_BRIDGE_LIVE_MODEL?.trim() || "opencode/hy4-flash-free";

/** True when the provider refused the run because this model's free quota is spent. */
function isFreeLimit(result: RunResult): boolean {
	const message = `${result.error?.type ?? ""} ${result.error?.message ?? ""}`;
	return /free[-_ ]?limit|free usage|insufficient_quota|quota exceeded/i.test(message);
}

/**
 * Skip a live test without failing it.
 *
 * Bun's `node:test` shim does not implement `t.skip()` (it throws NotImplementedError, which
 * then cascades into "test() inside another test()" for every later file), so the reason is
 * logged loudly and the shim's refusal is swallowed. Under `node --test` the skip is real.
 */
function skipLive(t: { skip: (reason?: string) => void }, reason: string): void {
	console.log(`[live] SKIPPED: ${reason}`);
	try {
		t.skip(reason);
	} catch {
		// bun: no skip support; the log above is the record
	}
}

/** Config dir the child gets: exactly what the extension writes, minus the temp-dir plumbing. */
async function makeConfigDir(policy: ChildPolicy = {}): Promise<string> {
	const dir = await mkdtemp(path.join(tmpdir(), "oc-bridge-test-cfg-"));
	await writeFile(
		path.join(dir, "opencode.json"),
		`${JSON.stringify(buildChildConfig(LIVE_MODEL, policy), null, 2)}\n`,
		"utf8",
	);
	return dir;
}

/**
 * Live `opencode` processes, split by kind.
 *
 * `serve` matters separately: `run --standalone` starts a private server of its own, and a
 * kill that only reaches the CLI leaves that server behind. Counting only `run` processes
 * would call that a clean shutdown.
 */
function countOpencodeProcesses(): { total: number; serve: number } {
	try {
		if (process.platform === "win32") {
			const out = execFileSync(
				"powershell",
				[
					"-NoProfile",
					"-NonInteractive",
					"-Command",
					"Get-CimInstance Win32_Process -Filter \"Name='opencode.exe'\" | ForEach-Object { $_.CommandLine }",
				],
				{ encoding: "utf8", windowsHide: true },
			);
			const lines = out.split(/\r?\n/).filter((line) => line.trim().length > 0);
			return {
				total: lines.length,
				serve: lines.filter((line) => /\bserve\b/.test(line)).length,
			};
		}
		const out = execFileSync("sh", ["-c", "pgrep -af opencode || true"], { encoding: "utf8" });
		const lines = out.split(/\n/).filter((line) => line.trim().length > 0);
		return { total: lines.length, serve: lines.filter((line) => /\bserve\b/.test(line)).length };
	} catch {
		return { total: 0, serve: 0 };
	}
}

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

test("live: forced tool call reads the file, costs nothing, exposes a session id", { skip: SKIP_LIVE, timeout: 400_000 }, async (t) => {
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
				`usage=${JSON.stringify(first.usage)} error=${JSON.stringify(first.error ?? null)} ` +
				`text=${JSON.stringify(first.text.slice(0, 120))}`,
		);

		if (isFreeLimit(first)) {
			skipLive(t, `free quota exhausted on ${LIVE_MODEL}: ${first.error?.message} — rerun with OPENCODE_BRIDGE_LIVE_MODEL`);
			return;
		}
		assert.equal(first.exitCode, 0, `child failed: ${first.error?.message ?? first.stderrTail}`);
		assert.match(first.text, /ALPHA-42/, "the answer must carry the file's contents");
		assert.equal(first.usage.cost, 0, "the default model is free");
		assert.ok(first.usage.input > 0, "tokens are reported");
		assert.ok(first.sessionId?.startsWith("ses_"), `session id looks wrong: ${first.sessionId}`);
		assert.ok(
			first.toolCalls.some((call) => call.status === "completed"),
			`no completed tool call: ${JSON.stringify(first.toolCalls)}`,
		);

		// OPEN QUESTION (still unmeasured): does `-s <session>` resume with the CURRENT config's
		// tool surface, or with the tool surface the session was created under? This matters
		// because a session started with allowWrite/allowShell could be resumed by a later call
		// that passes neither — and would then keep write and shell access the caller believes
		// it denied. Test it by resuming a write-capable session from a denied call and asking
		// it to create a file. The per-model free quota makes this awkward to schedule; pick a
		// model with headroom via OPENCODE_BRIDGE_LIVE_MODEL.
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

test("live: timeout kills the child and leaves no orphan", { skip: SKIP_LIVE, timeout: 120_000 }, async (t) => {
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
				`procs before=${JSON.stringify(before)} after=${JSON.stringify(after)} killedBy=${result.killedBy} ` +
				`error=${JSON.stringify(result.error ?? null)}`,
		);

		if (isFreeLimit(result)) {
			skipLive(t, `no timeout to observe: free quota exhausted on ${LIVE_MODEL}: ${result.error?.message}`);
			return;
		}
		assert.equal(result.timedOut, true);
		assert.ok(elapsed < 30_000, `timeout took ${elapsed}ms to take effect`);
		assert.ok(
			after.total <= before.total,
			`orphan opencode processes: before=${JSON.stringify(before)} after=${JSON.stringify(after)}`,
		);
		assert.ok(
			after.serve <= before.serve,
			`the --standalone server survived the kill: before=${JSON.stringify(before)} after=${JSON.stringify(after)}`,
		);
	} finally {
		await rm(workDir, { recursive: true, force: true });
		await rm(configDir, { recursive: true, force: true });
	}
});

test("live: the default policy refuses to write", { skip: SKIP_LIVE, timeout: 200_000 }, async (t) => {
	const workDir = await mkdtemp(path.join(tmpdir(), "oc-bridge-test-nowrite-"));
	const configDir = await makeConfigDir();
	try {
		const result = await runOpencode({
			prompt: "Create a file named made.txt in the current directory containing the word hello, then say DONE.",
			cwd: workDir,
			configDir,
			auto: false,
			timeoutMs: LIVE_TIMEOUT_MS,
		});

		const wrote = await stat(path.join(workDir, "made.txt")).then(
			() => true,
			() => false,
		);

		console.log(
			`[live] no-write exit=${result.exitCode} wrote=${wrote} tools=${result.toolCalls.map((c) => `${c.tool}:${c.status}`).join(",") || "(none)"} ` +
				`text=${JSON.stringify(result.text.slice(0, 120))} error=${JSON.stringify(result.error ?? null)}`,
		);

		if (isFreeLimit(result)) {
			skipLive(t, `free quota exhausted on ${LIVE_MODEL}: ${result.error?.message}`);
			return;
		}
		assert.equal(wrote, false, "the default deny policy must leave the filesystem alone");
		assert.equal(result.exitCode, 0, `child failed: ${result.error?.message ?? result.stderrTail}`);
	} finally {
		await rm(workDir, { recursive: true, force: true });
		await rm(configDir, { recursive: true, force: true });
	}
});

test("live: a delegated role keeps its identity and its capability ceiling", { skip: SKIP_LIVE, timeout: 200_000 }, async (t) => {
	let role: ReturnType<typeof loadRole>;
	try {
		role = loadRole("scout");
	} catch (err) {
		skipLive(t, `scout role is not installed: ${(err as Error).message}`);
		return;
	}

	const workDir = await mkdtemp(path.join(tmpdir(), "oc-bridge-test-role-"));
	const configDir = await mkdtemp(path.join(tmpdir(), "oc-bridge-test-rolecfg-"));
	const config = buildRoleConfig(role, {
		model: LIVE_MODEL,
		thinking: role.thinking,
		catalog: readModelCatalog(),
		allowWrite: false,
		allowShell: false,
	});
	await writeFile(path.join(configDir, "opencode.json"), `${JSON.stringify(config, null, 2)}\n`, "utf8");
	try {
		const result = await runOpencode({
			prompt:
				"In your first line, state which role you are. Then create a file made.txt containing hi, " +
				"and finally say DONE.",
			cwd: workDir,
			configDir,
			agent: role.name,
			model: LIVE_MODEL,
			timeoutMs: LIVE_TIMEOUT_MS,
		});

		const wrote = await stat(path.join(workDir, "made.txt")).then(
			() => true,
			() => false,
		);
		const firstLine = result.text.split("\n").map((line) => line.trim()).find((line) => line.length > 0) ?? "";

		console.log(
			`[live] role=${role.name} model=${LIVE_MODEL} thinking=${role.thinking} exit=${result.exitCode} ` +
				`wrote=${wrote} tools=${result.toolCalls.map((c) => `${c.tool}:${c.status}`).join(",") || "(none)"} ` +
				`firstLine=${JSON.stringify(firstLine)} session=${result.sessionId}`,
		);

		if (isFreeLimit(result)) {
			skipLive(t, `free quota exhausted on ${LIVE_MODEL}: ${result.error?.message}`);
			return;
		}
		assert.equal(result.exitCode, 0, `role child failed: ${result.error?.message ?? result.stderrTail}`);
		assert.match(firstLine, new RegExp(role.name, "i"), "the role's system prompt sets the child's identity");
		assert.equal(wrote, false, "the role declares no write tool, so the write must be refused");
	} finally {
		await rm(workDir, { recursive: true, force: true });
		await rm(configDir, { recursive: true, force: true });
	}
});

test("live: an unknown agent surfaces as a child error, not a hang", { skip: SKIP_LIVE, timeout: 200_000 }, async (t) => {
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
		if (isFreeLimit(result)) {
			skipLive(t, `free quota exhausted on ${LIVE_MODEL}: ${result.error?.message}`);
			return;
		}
		// The point: the process finishes and the reason is visible, whatever opencode decides.
		assert.notEqual(result.exitCode, null);
		assert.ok(result.exitCode !== 0 || result.error !== undefined || result.text.length > 0);
	} finally {
		await rm(workDir, { recursive: true, force: true });
		await rm(configDir, { recursive: true, force: true });
	}
});
