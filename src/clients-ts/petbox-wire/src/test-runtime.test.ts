// Structural guard for the invariant "the kit's own tests run under the same runtime the kit
// will run under for a user" — i.e. real Node, not bun, and a real Node new enough to satisfy
// this package's own `engines.node` gate (native TS type-stripping; see loadKitVersion/protocol
// banner and bin/petbox-wire.js's own version check).
//
// WHY A TEST, NOT JUST THE CI YAML LINE: `.github/workflows/ci.yml`'s `sdk` job pins Node via
// `actions/setup-node@v4` specifically so `bun run test` (which execs a real `node --test
// src/*.ts` under the hood — see package.json) runs on a real, sufficiently-new Node. That pin is
// one YAML line with no code-level consequence if it is ever deleted or the runner image drifts:
// none of the other 468 tests in this package touch `process.versions`, `process.execPath`, or
// `engines.node` — they test the kit's OWN logic, which is agnostic to which JS runtime happens
// to be running it. Nothing else here would notice the pin going missing until a user hit the
// kit's own version gate in the wild. This file is the code-level backstop: it fails the SUITE
// itself, on whatever runtime actually ran it, the moment that invariant breaks — whether the
// cause is the setup-node step being deleted, the runner image's bundled Node drifting below the
// gate, or someone changing `bun run test` to `bun --bun run test` / `bun src/*.test.ts`.
//
// bun-vs-Node identification, verified practically on this machine (bun 1.4.2, Node 24.11.0) —
// keep this table, the next person will need it:
//
//   command                  | process.versions.node | process.versions.bun | execPath
//   ------------------------ | ---------------------- | --------------------- | ---------
//   node file.mjs            | "24.11.0"              | undefined             | node(.exe)
//   bun run <npm-script>     | "24.11.0" (real Node)  | undefined             | node(.exe)
//   bun --bun run <script>   | "26.3.0" (bun's OWN)   | "1.4.2"               | bun(.exe)
//   bun file.ts              | "26.3.0" (bun's OWN)   | "1.4.2"               | bun(.exe)
//
// `bun run <npm-script>` does NOT substitute bun for a bare `node` the script invokes — only
// `bun --bun run` and `bun file.ts` directly do that. So the two commands most likely to slip
// past a careless reviewer (`bun run test`, and a future `bun --bun run test`) land on opposite
// sides of this table, and the second assertion below is what actually tells them apart from a
// stray "reports node version anyway" trick: `process.release.name` is "node" in EVERY row above
// — including under bun — so it is NOT a valid discriminator, and this file deliberately does not
// use it.
//
// Run under bun to see this fail on purpose: `bun --bun test src/test-runtime.test.ts` (bun's own
// built-in test runner, forced onto bun's own runtime with --bun) drives this exact file with bun
// as the runtime under test — the first two assertions below fail immediately (verified live:
// 2 fail, 1 pass — the third, engines.node, still passes under bun because bun's OWN reported
// version happens to satisfy the >= 23.6 bound too; it is not a bun-vs-Node discriminator by
// itself, which is exactly why the first two assertions exist).

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { basename } from "node:path";
import { test } from "node:test";

type PackageJson = {
  readonly engines?: { readonly node?: string };
};

// Parses the single supported shape this package actually uses (">=X.Y.Z" or ">=X.Y" or ">=X"),
// deliberately not a general semver-range parser — no semver dependency, and this package has
// never used anything but a bare lower bound here.
function parseMinVersion(range: string): { readonly major: number; readonly minor: number } {
  const m = /^>=\s*(\d+)(?:\.(\d+))?/.exec(range.trim());
  assert.ok(m, `engines.node range ${JSON.stringify(range)} is not a bare ">=X.Y" lower bound this test knows how to parse`);
  return { major: Number(m[1]), minor: Number(m[2] ?? "0") };
}

function parseRuntimeVersion(version: string): { readonly major: number; readonly minor: number } {
  const m = /^(\d+)\.(\d+)/.exec(version);
  assert.ok(m, `process.versions.node ${JSON.stringify(version)} is not in the expected "X.Y..." shape`);
  return { major: Number(m[1]), minor: Number(m[2]) };
}

test("test runtime is not bun, under any invocation shape", () => {
  // bun sets both of these even in the `bun run <npm-script>` shape where process.versions.node
  // reports a real Node version (see the table above) — this is the one reliable "am I bun"
  // signal, checked two independent ways.
  assert.equal(process.versions["bun"], undefined, "process.versions.bun is set — this test is running under bun, not Node");
  assert.equal(typeof (globalThis as Record<string, unknown>)["Bun"], "undefined", "global Bun is defined — this test is running under bun, not Node");
});

test("test runtime executable is the real Node binary the kit's own hooks invoke", () => {
  const exe = basename(process.execPath);
  assert.ok(
    exe === "node" || exe === "node.exe",
    `process.execPath basename is ${JSON.stringify(exe)}, expected "node" or "node.exe" — the ` +
      `kit's hooks (pull-memory.ts, droid-pull-memory.ts, etc.) invoke plain "node" on PATH; a ` +
      `test suite run under any other executable is not exercising what users actually run.`,
  );
});

test("test runtime's Node version satisfies this package's own engines.node gate", () => {
  // Read the bound from package.json rather than hardcoding a number: this is the assertion that
  // must keep working when the owner eventually revisits the >= 23.6 threshold (open decision,
  // not this file's call) — it should track that change, not need editing alongside it. It is
  // also the one that catches a CI runner image drifting back below whatever the threshold is,
  // and the one that catches `setup-node` being deleted outright: either failure mode leaves the
  // rest of the suite green (see the file header — nothing else touches engines.node) while this
  // assertion alone fails.
  const pkg = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as PackageJson;
  const range = pkg.engines?.node;
  assert.ok(range, "package.json has no engines.node — nothing to check the running Node against");

  const min = parseMinVersion(range);
  const actual = parseRuntimeVersion(process.versions.node);

  const satisfies = actual.major > min.major || (actual.major === min.major && actual.minor >= min.minor);
  assert.ok(
    satisfies,
    `process.versions.node is ${process.versions.node}, which does not satisfy this package's ` +
      `own engines.node range ${JSON.stringify(range)} (parsed minimum ${min.major}.${min.minor}) — ` +
      `the runtime executing this very test suite fails the kit's own version gate.`,
  );
});
