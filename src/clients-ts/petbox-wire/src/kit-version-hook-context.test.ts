// Regression test for card kit-version-unknown-inside-hooks.
//
// THE BUG (confirmed live, 2026-09-06): agent-definition.ts's loadKitVersion() reads
// `../package.json` relative to its OWN import.meta.dirname. That resolves fine from an npx
// cache dir or a checkout (package.json sits right next to `src/` there) — but pull-memory.ts /
// droid-pull-memory.ts / opencode-plugin.ts all run from the STABLE mirror (~/.petbox/wire/),
// where wire.ts's copyKitToStable copies only HERE (this src/ dir), never the sibling
// package.json. `../package.json` from a hook's dirname then resolves to
// ~/.petbox/package.json, which does not exist — KIT_VERSION was "unknown" in EVERY hook.
//
// A second, independent gap (found while measuring the live SessionStart banner for this fix):
// nothing in protocol.ts's buildProtocol() — the ONE shared banner builder all three harnesses'
// SessionStart injectors call — ever referenced KIT_VERSION at all. Fixing loadKitVersion() alone
// would have left the banner exactly as silent as before; a test that only imports KIT_VERSION
// in-process (this test file's own directory always has package.json next to it) would not catch
// EITHER half of that regression. So this file tests both halves as REAL child processes, from a
// REAL mirror shape, the same way copyKitToStable actually produces it:
//
//   1. loadKitVersion()'s fallback chain (package.json -> kit-version.json -> "unknown"),
//      exercised from a directory that is byte-for-byte what a hook actually runs from.
//   2. The SessionStart banner text itself (pull-memory.ts, droid-pull-memory.ts) — spawned
//      from that same real mirror, after a REAL `wire.ts update` wrote it — must carry a kit
//      identifier that is not "unknown".
//
// opencode-plugin.ts is not spawned here (it is a Plugin module, not a standalone CLI process —
// faking the opencode host object is out of scope for this card). It shares the exact same
// buildProtocol()/KIT_VERSION path as the two hooks tested below (see protocol.ts's own header:
// "the ONE implementation every SessionStart injector renders from"), so the two spawns here are
// the behavioral proof for all three harnesses, not just two of them.
//
// Run: node --test src/kit-version-hook-context.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import http from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

const SRC_DIR = import.meta.dirname; // this file's own directory === the real kit src/
const WIRE_TS = join(SRC_DIR, "wire.ts");

function freshDir(prefix: string): string {
  return mkdtempSync(join(tmpdir(), prefix));
}

// ---- part 1: loadKitVersion()'s fallback chain, from a real mirror shape ------------------

// Reproduces copyKitToStable's exact output shape: a `wire/` directory holding a full copy of
// HERE (src/), with NO package.json anywhere above it — the one thing the real defect hinges on.
// `cpSync(SRC_DIR, ...)`, not a hand-picked file list, so this cannot drift from what the real
// copy actually ships as agent-definition.ts's dependencies change over time.
function seedMirrorShape(mirrorRoot: string): string {
  const wireDir = join(mirrorRoot, "wire");
  mkdirSync(wireDir, { recursive: true });
  cpSync(SRC_DIR, wireDir, { recursive: true });
  return wireDir;
}

function probeKitVersion(wireDir: string): string {
  const probePath = join(wireDir, "__kit_version_probe.ts");
  writeFileSync(
    probePath,
    'import { KIT_VERSION } from "./agent-definition.ts";\nprocess.stdout.write(KIT_VERSION);\n',
    "utf8",
  );
  const res = spawnSync(process.execPath, [probePath], { encoding: "utf8" });
  assert.equal(res.status, 0, `probe process failed (status ${res.status}): ${res.stderr}`);
  return res.stdout;
}

test("KIT_VERSION resolves the delivery stamp (not \"unknown\") when run from a real mirror shape lacking ../package.json", () => {
  const mirrorRoot = freshDir("petbox-kv-mirror-stamped-");
  try {
    const wireDir = seedMirrorShape(mirrorRoot);
    assert.ok(!existsSync(join(mirrorRoot, "package.json")), "test setup sanity: no package.json above the mirror");
    writeFileSync(
      join(mirrorRoot, "kit-version.json"),
      JSON.stringify({
        version: "0.0.0", // the real checked-in value — CI only stamps a real semver on publish
        kitHash: "5991098e2824",
        installedAt: new Date().toISOString(),
        source: "test",
      }),
      "utf8",
    );

    const out = probeKitVersion(wireDir);

    assert.notEqual(out, "unknown", "must not regress to unknown just because ../package.json is absent");
    // Not bare "0.0.0" either (point 3 of the card): a checkout-sourced kit's version is
    // permanently 0.0.0, so a bare version can never tell one generation of shipped prose from
    // another. The hash must ride along to make the label actually meaningful.
    assert.equal(out, "0.0.0+5991098e2824", `expected version+hash composition, got ${JSON.stringify(out)}`);
  } finally {
    rmSync(mirrorRoot, { recursive: true, force: true });
  }
});

test("KIT_VERSION still degrades softly to \"unknown\" (never throws) when the mirror has neither package.json nor a stamp", () => {
  const mirrorRoot = freshDir("petbox-kv-mirror-nostamp-");
  try {
    const wireDir = seedMirrorShape(mirrorRoot);
    const out = probeKitVersion(wireDir);
    assert.equal(out, "unknown", "a pre-fix mirror (no stamp yet written) must degrade exactly as before, never crash");
  } finally {
    rmSync(mirrorRoot, { recursive: true, force: true });
  }
});

// ---- part 2: the real SessionStart banner, end to end, from a REAL `wire.ts update` -------

function startFastFakeCanonServer(): Promise<{ port: number; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      if (req.url?.includes("/memory/") && req.url?.includes("/canon")) {
        res.writeHead(200, { "Content-Type": "application/json" }).end(JSON.stringify({ project: null, workspace: null }));
        return;
      }
      res.writeHead(404).end();
    });
    server.listen(0, "127.0.0.1", () => {
      const port = (server.address() as { port: number }).port;
      resolve({ port, close: () => new Promise((r) => server.close(() => r())) });
    });
  });
}

function registerFakeProject(homeDir: string, baseUrl: string): string {
  const projectDir = join(homeDir, "fake-project");
  mkdirSync(projectDir, { recursive: true });
  mkdirSync(join(homeDir, ".petbox"), { recursive: true });
  writeFileSync(
    join(homeDir, ".petbox", "projects.json"),
    JSON.stringify({
      entries: [{ prefix: projectDir, project: "fake-project", envVar: "FAKE_KIT_VERSION_TEST_KEY", baseUrl }],
    }),
  );
  return projectDir;
}

function runHookSync(scriptPath: string, input: string, env: NodeJS.ProcessEnv, cwd: string) {
  const res = spawnSync(process.execPath, [scriptPath], { input, env, cwd, encoding: "utf8" });
  return { status: res.status, stdout: res.stdout ?? "", stderr: res.stderr ?? "" };
}

test("real `wire.ts update` writes the kit-version stamp, and pull-memory.ts / droid-pull-memory.ts spawned from that real mirror carry a non-\"unknown\" kit identifier in the SessionStart banner", async () => {
  const homeDir = freshDir("petbox-kv-update-home-");
  const { close, port } = await startFastFakeCanonServer();
  try {
    const homeEnv: NodeJS.ProcessEnv = {
      ...process.env,
      HOME: homeDir,
      USERPROFILE: homeDir, // os.homedir() reads USERPROFILE on win32, HOME on POSIX
      HOMEDRIVE: undefined,
      HOMEPATH: undefined,
    };

    const update = spawnSync(process.execPath, [WIRE_TS, "update"], { env: homeEnv, encoding: "utf8" });
    assert.equal(update.status, 0, `wire.ts update failed: ${update.stderr}\n${update.stdout}`);

    const stampPath = join(homeDir, ".petbox", "kit-version.json");
    assert.ok(existsSync(stampPath), `update must write the delivery stamp at ${stampPath}`);
    const stamp = JSON.parse(readFileSync(stampPath, "utf8"));
    assert.equal(typeof stamp.version, "string");
    assert.equal(typeof stamp.kitHash, "string");
    assert.ok(stamp.kitHash.length > 0);
    assert.ok(!existsSync(join(homeDir, ".petbox", "wire", "kit-version.json")), "the stamp must live OUTSIDE the mirror, not inside it (pruneStaleMirrorEntries would sweep it)");

    const projectDir = registerFakeProject(homeDir, `http://127.0.0.1:${port}`);
    const env: NodeJS.ProcessEnv = { ...homeEnv, FAKE_KIT_VERSION_TEST_KEY: "fake-key-for-test" };
    const input = JSON.stringify({ session_id: "test", cwd: projectDir, hook_event_name: "SessionStart", source: "startup" });

    // Claude Code's hook, run from the REAL mirror `update` just produced (not the checkout).
    const ccHook = runHookSync(join(homeDir, ".petbox", "wire", "pull-memory.ts"), input, env, projectDir);
    assert.equal(ccHook.status, 0, `pull-memory.ts (mirror) failed: ${ccHook.stderr}`);
    assert.match(
      ccHook.stdout,
      /kit v(?!unknown\b)\S/,
      `claude-code SessionStart banner must name a real kit identifier, not "unknown" — got: ${JSON.stringify(ccHook.stdout.slice(0, 400))}`,
    );

    // Droid's hook, same real mirror. Output is JSON-wrapped (hookSpecificOutput.additionalContext).
    const droidHook = runHookSync(join(homeDir, ".petbox", "wire", "droid-pull-memory.ts"), input, env, projectDir);
    assert.equal(droidHook.status, 0, `droid-pull-memory.ts (mirror) failed: ${droidHook.stderr}`);
    const droidOut = JSON.parse(droidHook.stdout);
    const droidContext: string = droidOut.hookSpecificOutput.additionalContext;
    assert.match(
      droidContext,
      /kit v(?!unknown\b)\S/,
      `droid SessionStart banner must name a real kit identifier, not "unknown" — got: ${JSON.stringify(droidContext.slice(0, 400))}`,
    );
  } finally {
    await close();
    rmSync(homeDir, { recursive: true, force: true });
  }
});
