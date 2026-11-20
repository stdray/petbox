/**
 * `buildInvocation` is the whole external contract expressed as args, so it gets
 * asserted rather than eyeballed: the flags that were measured to be mandatory
 * (flag order after `run`, `-m` always present, `OPENCODE_CONFIG_DIR` always set,
 * the prompt kept verbatim past `--`) are the failure modes that cost a debugging
 * session each.
 */

import assert from "node:assert/strict";
import test from "node:test";

import {
	buildInvocation,
	DEFAULT_MODEL,
	modelArgument,
	resolveOpencodeExecutable,
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
