// Regression test for card kit-version-unknown-inside-hooks.
//
// THE BUG (confirmed live, 2026-09-06): agent-definition.ts's loadKitVersion() reads
// `../package.json` relative to its OWN import.meta.dirname. That resolves fine from a checkout
// (`node wire.ts ...` run straight from `src/`) — but pull-memory.ts / droid-pull-memory.ts /
// opencode-plugin.ts all run from the STABLE mirror (~/.petbox/wire/), where wire.ts's
// copyKitToStable copies only HERE (this src/ dir), never a sibling package.json.
// `../package.json` from a hook's dirname then resolves to ~/.petbox/package.json, which does
// not exist — KIT_VERSION was "unknown" in EVERY hook.
//
// A second, independent gap (found while measuring the live SessionStart banner for the first
// fix): nothing in protocol.ts's buildProtocol() — the ONE shared banner builder all three
// harnesses' SessionStart injectors call — ever referenced KIT_VERSION at all. Fixing
// loadKitVersion() alone would have left the banner exactly as silent as before.
//
// A THIRD gap — the one that let the first fix (petbox-wire@0.1.0-ci.2242) ship broken and pass
// review anyway (measured live on the owner's machine, 2026-09-06): `../package.json` ALSO
// misses on a REAL `npx petbox-wire` invocation, not just later hook runs. bin/petbox-wire.js
// cannot run wire.ts in place (Node refuses to type-strip `.ts` under node_modules — the npx
// cache is exactly that), so it copies `src/` into a FLAT scratch dir directly under the OS temp
// dir and imports wire.ts from there. That scratch dir's parent is plain %TEMP%/tmp, never this
// package's root — so the very first `KIT_VERSION` resolution of a real run was ALREADY
// "unknown", and copyKitToStable's own delivery stamp then recorded that "unknown" verbatim.
// Tests 1-2 below only ever exercised a HAND-BUILT mirror shape; test 3 only ever ran `wire.ts`
// directly from the checkout (where `../package.json` DOES exist) — neither reproduces bin.js's
// scratch dir, so the suite passed while the real entry point stayed broken. Test 4 below runs
// the REAL bin/petbox-wire.js, from a throwaway package with an unmistakable test version, to
// close that gap.
//
// So this file tests every half as REAL child processes, from a REAL mirror shape, the same way
// copyKitToStable / bin.js actually produce it — never an in-process import (this test file's
// own directory always has package.json next to it, which would hide all three regressions):
//
//   1-2. loadKitVersion()'s fallback chain (package.json -> same-dir stamp -> sibling stamp ->
//        "unknown"), exercised from a directory that is byte-for-byte what a hook actually runs
//        from.
//   3.   The SessionStart banner text itself (pull-memory.ts, droid-pull-memory.ts) — spawned
//        from a real mirror after a checkout-sourced `wire.ts update` wrote it — must carry a
//        kit identifier that is not "unknown".
//   4.   The REAL `npx` entry point (bin/petbox-wire.js, not wire.ts directly): its scratch dir
//        must resolve a real version end to end, the mirror must inherit that stamp for free,
//        and a LATER checkout-sourced `update` on the same machine must sweep the now-stale
//        mirror-internal copy while the sibling stays a correctly-updated fallback.
//
// opencode-plugin.ts is not spawned here (it is a Plugin module, not a standalone CLI process —
// faking the opencode host object is out of scope for this card). It shares the exact same
// buildProtocol()/KIT_VERSION path as the two hooks tested below (see protocol.ts's own header:
// "the ONE implementation every SessionStart injector renders from"), so the spawns here are the
// behavioral proof for all three harnesses, not just two of them.
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

// ---- part 4: the REAL npx invocation path — bin/petbox-wire.js's flat scratch dir ---------
//
// See the file header's "THIRD gap" for why this is required and why nothing above it catches
// this: it is the ACTUAL `npx petbox-wire` entry point, not `wire.ts` run directly.
//
// The checked-in package.json version is "0.0.0" (real semver is CI-stamped only on publish,
// without a commit) — not itself distinguishable from a broken/never-run install — so this test
// copies bin/ + src/ into a throwaway package carrying an unmistakable test version rather than
// relying on the repo's own permanently-0.0.0 value.
function buildFakeNpxPackage(version: string): { binPath: string } {
  const pkgDir = freshDir("petbox-kv-fakepkg-");
  cpSync(SRC_DIR, join(pkgDir, "src"), { recursive: true });
  cpSync(join(SRC_DIR, "..", "bin"), join(pkgDir, "bin"), { recursive: true });
  writeFileSync(join(pkgDir, "package.json"), JSON.stringify({ name: "petbox-wire", version }, null, 2), "utf8");
  return { binPath: join(pkgDir, "bin", "petbox-wire.js") };
}

test(
  "real bin/petbox-wire.js (the actual `npx petbox-wire` entry point): its scratch dir resolves " +
    'the real version end to end (never "unknown"), the SessionStart banner carries it, and a ' +
    "later checkout-sourced update correctly sweeps the mirror-internal copy while the sibling " +
    "stamp stays a live fallback",
  async () => {
    const { binPath } = buildFakeNpxPackage("9.9.9-test");
    const homeDir = freshDir("petbox-kv-npxpath-home-");
    const { close, port } = await startFastFakeCanonServer();
    try {
      const homeEnv: NodeJS.ProcessEnv = {
        ...process.env,
        HOME: homeDir,
        USERPROFILE: homeDir, // os.homedir() reads USERPROFILE on win32, HOME on POSIX
        HOMEDRIVE: undefined,
        HOMEPATH: undefined,
      };

      // Step 1: the real `npx petbox-wire update` shape — via bin/petbox-wire.js, not wire.ts
      // directly (this is exactly what test 3 above does NOT reproduce).
      const update = spawnSync(process.execPath, [binPath, "update"], { env: homeEnv, encoding: "utf8" });
      assert.equal(update.status, 0, `bin/petbox-wire.js update failed: ${update.stderr}\n${update.stdout}`);

      const siblingStampPath = join(homeDir, ".petbox", "kit-version.json");
      const mirrorStampPath = join(homeDir, ".petbox", "wire", "kit-version.json");

      // The sibling stamp (loadKitVersion's step 3) must carry the REAL version — this is
      // exactly what shipped as "unknown" in petbox-wire@0.1.0-ci.2242.
      const siblingStamp = JSON.parse(readFileSync(siblingStampPath, "utf8"));
      assert.equal(
        siblingStamp.version,
        "9.9.9-test",
        `sibling stamp must carry the real version, not "unknown" — got ${JSON.stringify(siblingStamp)}`,
      );
      assert.ok(typeof siblingStamp.kitHash === "string" && siblingStamp.kitHash.length > 0);

      // The mirror-internal stamp (loadKitVersion's step 2) — written by bin.js into the
      // scratch dir, then copied verbatim into ~/.petbox/wire/ by copyKitToStable's cpSync. A
      // "nice side effect" the brief asked to actually verify, not just assume.
      assert.ok(existsSync(mirrorStampPath), "bin.js's same-directory stamp must ride along into the mirror");
      const mirrorStamp = JSON.parse(readFileSync(mirrorStampPath, "utf8"));
      assert.equal(mirrorStamp.version, "9.9.9-test");

      // Step 2: the SessionStart banner, spawned from the real resulting mirror.
      const projectDir = registerFakeProject(homeDir, `http://127.0.0.1:${port}`);
      const env: NodeJS.ProcessEnv = { ...homeEnv, FAKE_KIT_VERSION_TEST_KEY: "fake-key-for-test" };
      const input = JSON.stringify({
        session_id: "test",
        cwd: projectDir,
        hook_event_name: "SessionStart",
        source: "startup",
      });
      const ccHook = runHookSync(join(homeDir, ".petbox", "wire", "pull-memory.ts"), input, env, projectDir);
      assert.equal(ccHook.status, 0, `pull-memory.ts (mirror) failed: ${ccHook.stderr}`);
      assert.match(
        ccHook.stdout,
        /kit v9\.9\.9-test\b/,
        `claude-code SessionStart banner must carry the real test version, not "unknown" — got: ${JSON.stringify(ccHook.stdout.slice(0, 400))}`,
      );

      // Step 3: a LATER checkout-sourced `update` (real wire.ts, real checkout HERE — no
      // same-directory stamp there at all) must sweep the now-stale mirror-internal copy...
      const checkoutUpdate = spawnSync(process.execPath, [WIRE_TS, "update"], { env: homeEnv, encoding: "utf8" });
      assert.equal(
        checkoutUpdate.status,
        0,
        `checkout-sourced wire.ts update failed: ${checkoutUpdate.stderr}\n${checkoutUpdate.stdout}`,
      );
      assert.ok(
        !existsSync(mirrorStampPath),
        "a checkout-sourced update must sweep the mirror-internal stamp it does not itself ship (pruneStaleMirrorEntries)",
      );

      // ...while the sibling remains the fallback, now correctly reflecting the CHECKOUT's own
      // version — never stuck on the previous run's "9.9.9-test", and never "unknown" either.
      const siblingAfter = JSON.parse(readFileSync(siblingStampPath, "utf8"));
      assert.notEqual(siblingAfter.version, "unknown");
      assert.notEqual(
        `${siblingAfter.version}+${siblingAfter.kitHash}`,
        `${siblingStamp.version}+${siblingStamp.kitHash}`,
        "the sibling stamp must actually be rewritten by the second update, not left stale from the first",
      );
    } finally {
      await close();
      rmSync(homeDir, { recursive: true, force: true });
    }
  },
);
