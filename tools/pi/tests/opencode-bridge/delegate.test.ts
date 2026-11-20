/**
 * `buildInvocation` is the whole external contract expressed as args, so it gets
 * asserted rather than eyeballed: the flags that were measured to be mandatory
 * (flag order after `run`, `-m` always present, `OPENCODE_CONFIG_DIR` always set,
 * the prompt kept verbatim past `--`) are the failure modes that cost a debugging
 * session each.
 */

import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import test from "node:test";

import {
	ARGV_FIXED_OVERHEAD_CHARS,
	buildChildConfig,
	buildInvocation,
	classifyResult,
	DEFAULT_MODEL,
	EMPTY_ANSWER_PLACEHOLDER,
	estimateArgvCost,
	MAX_ARGV_CHARS,
	modelArgument,
	normalizeCwd,
	resolveToolCwd,
	needsAutoApprove,
	resolveOpencodeExecutable,
	resultBody,
	runOpencode,
} from "../../extensions/opencode-bridge/delegate.ts";

const baseEnv = { PATH: process.env.PATH ?? "" };

test("flags come after `run` and before the prompt", () => {
	const inv = buildInvocation({ prompt: "hi", baseEnv });
	assert.equal(inv.args[0], "run", "flags before `run` make the CLI print help");
	assert.deepEqual(inv.args.slice(1, 5), ["--standalone", "--archived", "--format", "json"]);
	assert.equal(inv.args[5], "-m");
	assert.equal(inv.args[6], DEFAULT_MODEL, "-m is mandatory: without it the config default 403s");
	assert.equal(inv.args.at(-2), "--");
	assert.equal(inv.args.at(-1), "hi");
});

test("model and variant", () => {
	assert.equal(buildInvocation({ prompt: "p", baseEnv }).args[6], DEFAULT_MODEL);
	assert.equal(buildInvocation({ prompt: "p", model: "openai/gpt-5", baseEnv }).args[6], "openai/gpt-5");
	assert.equal(modelArgument("openai/gpt-5", "fast"), "openai/gpt-5#fast");
	assert.equal(modelArgument("openai/gpt-5#fast", "slow"), "openai/gpt-5#fast", "an explicit variant wins");
	assert.equal(buildInvocation({ prompt: "p", variant: "fast", baseEnv }).args[6], `${DEFAULT_MODEL}#fast`);
});

test("optional flags appear only when asked for", () => {
	const bare = buildInvocation({ prompt: "p", baseEnv }).args;
	assert.equal(bare.includes("--agent"), false);
	assert.equal(bare.includes("-s"), false);
	assert.equal(bare.includes("--auto"), false);

	const full = buildInvocation({ prompt: "p", agent: "build", session: "ses_abc", auto: true, baseEnv }).args;
	assert.equal(full[full.indexOf("--agent") + 1], "build");
	assert.equal(full[full.indexOf("-s") + 1], "ses_abc");
	assert.equal(full.includes("--auto"), true);
});

test("OPENCODE_CONFIG_DIR is set from configDir and the env is layered", () => {
	const inv = buildInvocation({
		prompt: "p",
		configDir: "C:\\tmp\\cfg",
		baseEnv: { PATH: "x", KEEP: "yes" },
		env: { EXTRA: "1" },
	});
	assert.equal(inv.env.OPENCODE_CONFIG_DIR, "C:\\tmp\\cfg");
	assert.equal(inv.env.KEEP, "yes");
	assert.equal(inv.env.EXTRA, "1");

	const noConfig = buildInvocation({ prompt: "p", baseEnv: {} });
	assert.equal(noConfig.env.OPENCODE_CONFIG_DIR, undefined);
});

test("the prompt is passed verbatim, past `--`, with no shell in the middle", () => {
	const prompt = 'say "hi" & echo %PATH% | del *';
	const inv = buildInvocation({ prompt, baseEnv });
	assert.equal(inv.args.at(-1), prompt);
	assert.equal(inv.command.endsWith(".exe"), true, "a .cmd shim would go through cmd.exe and mangle this prompt");
});

test("cwd is pinned into PWD, because opencode prefers PWD over the spawn cwd", () => {
	// Measured against opencode 2.3.2: spawn cwd = A with PWD = B makes every child tool
	// run in B. A pi session launched from a shell carries that shell's PWD.
	const inv = buildInvocation({ prompt: "p", cwd: "C:\\work\\a", baseEnv: { PATH: "x", PWD: "C:\\work\\b" } });
	assert.equal(inv.env.PWD, "C:\\work\\a");
	assert.equal(buildInvocation({ prompt: "p", baseEnv: { PWD: "C:\\work\\b" } }).env.PWD, "C:\\work\\b");
});

test("the executable resolves to a real file", () => {
	const exe = resolveOpencodeExecutable();
	assert.equal(exe, resolveOpencodeExecutable(), "resolution is cached");
	if (process.platform === "win32") {
		assert.match(exe, /\.exe$/i);
		assert.equal(exe.includes(".cmd"), false);
	}
});

test("the generated child config is read-only by default", () => {
	const config = buildChildConfig(DEFAULT_MODEL) as {
		model: string;
		permission: Record<string, string>;
		$schema: string;
		mcp?: unknown;
	};
	assert.equal(config.model, DEFAULT_MODEL);
	assert.equal(config["mcp"], undefined, "no MCP servers: the child must not reach production PetBox");
	assert.equal(config.permission["*"], "deny", "the catch-all must come first; specific rules override it");
	assert.deepEqual(Object.keys(config.permission)[0], "*", "a later catch-all would shadow the allows");
	for (const tool of ["edit", "write", "shell", "subagent"]) {
		assert.equal(config.permission[tool], "deny", `${tool} must be denied unless asked for`);
	}
	for (const tool of ["read", "glob", "grep", "websearch"]) {
		assert.equal(config.permission[tool], "allow", `${tool} is the read-only surface`);
	}
});

test("allowWrite / allowShell are the only way to lift the deny policy", () => {
	const write = buildChildConfig(DEFAULT_MODEL, { allowWrite: true }) as { permission: Record<string, string> };
	assert.equal(write.permission.edit, "allow");
	assert.equal(write.permission.write, "allow");
	assert.equal(write.permission.shell, "deny", "allowing writes must not also allow shell");
	assert.equal(write.permission.subagent, "deny");

	const shell = buildChildConfig(DEFAULT_MODEL, { allowShell: true }) as { permission: Record<string, string> };
	assert.equal(shell.permission.shell, "allow");
	assert.equal(shell.permission.edit, "deny");

	assert.equal(needsAutoApprove({}), false, "--auto must stay out of the argv when nothing is approved");
	assert.equal(needsAutoApprove({ allowWrite: true }), true);
	assert.equal(needsAutoApprove({ allowShell: true }), true);

	const bare = buildInvocation({ prompt: "p", baseEnv: {} });
	assert.equal(bare.args.includes("--auto"), false);
});

test("an oversized prompt is refused before it reaches CreateProcess", () => {
	const affordable = "x".repeat(MAX_ARGV_CHARS - ARGV_FIXED_OVERHEAD_CHARS);
	assert.equal(buildInvocation({ prompt: affordable, baseEnv: {} }).args.at(-1)?.length, affordable.length);

	const tooBig = "x".repeat(MAX_ARGV_CHARS - ARGV_FIXED_OVERHEAD_CHARS + 1);
	assert.throws(
		() => buildInvocation({ prompt: tooBig, baseEnv: {} }),
		/characters but cost[sd]? \d+ on the command line once escaped, over the 32000-character argv budget/,
	);
});

test("the argv guard counts the ESCAPED size, not the character count", () => {
	// Node doubles every `"` and `\\` when building the Windows command line, so a
	// quote-heavy prompt blows the 32 767 limit at roughly half its character count.
	const quotes = '"'.repeat(MAX_ARGV_CHARS / 2);
	assert.ok(quotes.length < MAX_ARGV_CHARS - 2_000, "the character count alone looks safe");
	assert.equal(estimateArgvCost(quotes), quotes.length * 2 + ARGV_FIXED_OVERHEAD_CHARS);
	assert.throws(
		() => buildInvocation({ prompt: quotes, baseEnv: {} }),
		/once escaped, over the 32000-character argv budget/,
		"a quote-heavy prompt under the character limit must still be refused",
	);
	assert.throws(
		() => buildInvocation({ prompt: "\\".repeat(MAX_ARGV_CHARS / 2), baseEnv: {} }),
		/argv budget/,
		"backslashes are doubled too",
	);
});

test("runOpencode rejects when the invocation cannot be built", async () => {
	await assert.rejects(
		runOpencode({ prompt: "x".repeat(MAX_ARGV_CHARS), timeoutMs: 5_000 }),
		/argv budget/,
		"a build failure must be a rejection, not a silent empty result",
	);
});

const baseRun = {
	exitCode: 0 as number | null,
	text: "answer",
	steps: 1,
	malformedLines: 0,
	unknownEvents: 0,
	error: undefined,
	timedOut: false,
	aborted: false,
};

test("a clean run is a success", () => {
	const verdict = classifyResult(baseRun);
	assert.equal(verdict.failed, false);
	assert.equal(verdict.drift, false);
	assert.deepEqual(verdict.reasons, []);
});

test("format drift is a failure, not an empty success", () => {
	const noSteps = classifyResult({ ...baseRun, text: "", steps: 0 });
	assert.equal(noSteps.failed, true);
	assert.equal(noSteps.drift, true);
	assert.match(noSteps.reasons[0], /exited 0 but produced no answer text/);
	assert.match(noSteps.reasons[0], /steps 0/);

	const garbage = classifyResult({ ...baseRun, text: "", steps: 0, malformedLines: 7 });
	assert.equal(garbage.failed, true);
	assert.match(garbage.reasons[0], /7 unparsable line\(s\)/);

	const unknownOnly = classifyResult({ ...baseRun, text: "", steps: 1, unknownEvents: 3 });
	assert.equal(unknownOnly.failed, true);
	assert.equal(unknownOnly.drift, true);
});

test("drift counters are reported even when the run succeeded", () => {
	const noisy = classifyResult({ ...baseRun, malformedLines: 2, unknownEvents: 1 });
	assert.equal(noisy.failed, false, "text arrived, so this is a success with a caveat");
	assert.match(noisy.reasons.join("\n"), /2 stdout line\(s\) were not valid events/);
	assert.match(noisy.reasons.join("\n"), /1 event\(s\) had a type this bridge does not model/);
});

test("process failures are failures", () => {
	assert.equal(classifyResult({ ...baseRun, exitCode: 1 }).failed, true);
	assert.equal(classifyResult({ ...baseRun, timedOut: true }).failed, true);
	assert.equal(classifyResult({ ...baseRun, aborted: true }).failed, true);
	assert.equal(
		classifyResult({ ...baseRun, error: { type: "provider.auth", message: "403" } }).failed,
		true,
	);
});

test("a failed run carries the error, never the empty-answer placeholder", () => {
	// Real shape: free tier exhausted -> exit 1, provider error event, no text. The parent
	// model used to read the placeholder as the answer.
	const exhausted = classifyResult({
		...baseRun,
		exitCode: 1,
		text: "",
		steps: 0,
		error: { type: "provider.rate_limit", message: "Free usage limit reached" },
	});
	assert.equal(exhausted.failed, true);
	assert.equal(resultBody("", exhausted), "", "no placeholder on a failed run");
	assert.match(exhausted.reasons.join("\n"), /Free usage limit reached/);

	// A clean run that simply said nothing still gets the placeholder.
	const quiet = classifyResult({ ...baseRun, text: "", steps: 1 });
	assert.equal(quiet.failed, false);
	assert.equal(resultBody("", quiet), EMPTY_ANSWER_PLACEHOLDER);

	// Real text always wins, failure or not.
	assert.equal(resultBody("  partial answer ", exhausted), "partial answer");
});

test("a Git Bash (MSYS) cwd is converted, never handed to a spawn", () => {
	// Measured defect: cwd='/c/Users/…' reached spawn verbatim → "opencode.exe ENOENT" and a
	// misdiagnosis of the binary. On win32 the form must be converted (or refused) first.
	assert.equal(normalizeCwd("/c/Users/stdray/AppData/Local/Temp", "win32"), "C:/Users/stdray/AppData/Local/Temp");
	assert.equal(normalizeCwd("/d/my/prj/petbox", "win32"), "D:/my/prj/petbox");
	assert.equal(normalizeCwd("/c", "win32"), "C:");
	assert.equal(normalizeCwd("C:/Users/stdray", "win32"), "C:/Users/stdray", "Windows paths pass through");
	assert.equal(normalizeCwd("/home/me", "linux"), "/home/me", "posix is untouched");

	// An unknown drive is refused with both forms named — and never reaches a spawn. The letter
	// has to be one this machine does not have, so it is found rather than hardcoded.
	const missing = "DEFGHIJKLMNOPQRSTUVWXYZ".split("").find((letter) => !existsSync(`${letter}:/`));
	if (missing) {
		const lower = missing.toLowerCase();
		assert.throws(
			() => normalizeCwd(`/${lower}/nowhere`, "win32"),
			(err: Error) => {
				assert.match(err.message, new RegExp(`drive ${missing}:`));
				assert.ok(err.message.includes(`"/${lower}/nowhere"`), "names the form given");
				assert.ok(err.message.includes(`${missing}:/nowhere`), "and the form it became");
				assert.match(err.message, /use a Windows path/);
				return true;
			},
		);
	}
});

test("a cwd that does not exist is refused with the resolved path", () => {
	if (process.platform !== "win32") return;
	const resolved = resolveToolCwd(undefined, "/c/Users/stdray/AppData/Local/Temp");
	assert.match(resolved, /^C:\/Users\/stdray\/AppData\/Local\/Temp$/i, "the session cwd is normalised too");
	assert.throws(
		() => resolveToolCwd("C:/definitely-not-here-xyz", "C:/Users/stdray"),
		/does not exist.*Pass an existing directory/s,
	);
});
