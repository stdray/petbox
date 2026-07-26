// Unit tests for registry.ts's Class A / Class Б split (bug: wire-silent-failures-invisible,
// card item 4: "порча реестра = «проект не зарегистрирован», хуки молча ничего не делают").
//
// Before this fix, a MISSING projects.json/keys.json and a PRESENT-but-corrupt one were
// indistinguishable — both silently produced an empty registry / no key. That is correct for
// "missing" (Class A: every hook runs in every project on the machine, most of which were never
// wired) but wrong for "corrupt" (Class Б: something actually broke on disk). These tests pin
// the split via the injectable `homeDir` param.
//
// Run: node --test src/registry.test.ts

import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { readRegistry, registryPath, resolveProject } from "./registry.ts";
import { readWireLogTail } from "./wire-log.ts";

function freshHome(): string {
  return mkdtempSync(join(tmpdir(), "petbox-wire-registry-"));
}

test("readRegistry: no projects.json at all → empty, silent (Class A — never wired here)", () => {
  const home = freshHome();
  try {
    assert.deepEqual(readRegistry(home), []);
    assert.deepEqual(readWireLogTail(20, home), [], "a missing registry file must never trace");
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("readRegistry: PRESENT but corrupt JSON → empty, but leaves a Class-Б wire.log trace", () => {
  const home = freshHome();
  try {
    mkdirSync(join(home, ".petbox"), { recursive: true });
    writeFileSync(registryPath(home), "{ not valid json", "utf8");
    assert.deepEqual(readRegistry(home), []);
    const tail = readWireLogTail(20, home);
    assert.ok(tail.length > 0, "a corrupt projects.json must leave a trace — this is the card's 'порча реестра' case");
    assert.match(tail.join("\n"), /projects\.json.*not valid JSON/i);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("resolveProject: unregistered directory → null, silent (Class A, the common hook path)", () => {
  const home = freshHome();
  try {
    mkdirSync(join(home, ".petbox"), { recursive: true });
    writeFileSync(
      registryPath(home),
      JSON.stringify({ entries: [{ prefix: "/some/other/project", project: "other", envVar: "PETBOX_OTHER_API_KEY" }] }),
      "utf8",
    );
    assert.equal(resolveProject("/not/registered/anywhere", home), null);
    assert.deepEqual(readWireLogTail(20, home), []);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("resolveProject: corrupt registry behaves exactly like 'not registered' (returns null) but IS distinguishable via wire.log", () => {
  const home = freshHome();
  try {
    mkdirSync(join(home, ".petbox"), { recursive: true });
    writeFileSync(registryPath(home), "not-json{{{", "utf8");
    // Behavior is unchanged (hooks still no-op cleanly) — this is the point: a hook that runs in
    // every project on the machine must never crash or print noise for this. But an operator
    // running `doctor` (which tails wire.log) can now tell "porcha" apart from "never wired".
    assert.equal(resolveProject("/anywhere", home), null);
    const tail = readWireLogTail(20, home);
    assert.ok(tail.length > 0, "corrupt registry must be distinguishable from a clean 'not registered' via wire.log");
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("resolveProject: registered directory with a valid key resolves normally (no behavior regression)", () => {
  const home = freshHome();
  try {
    mkdirSync(join(home, ".petbox"), { recursive: true });
    const projectDir = join(home, "proj");
    mkdirSync(projectDir, { recursive: true });
    writeFileSync(
      registryPath(home),
      JSON.stringify({
        entries: [{ prefix: projectDir, project: "myproj", envVar: "PETBOX_MYPROJ_API_KEY", baseUrl: "https://example.test/" }],
      }),
      "utf8",
    );
    writeFileSync(join(home, ".petbox", "keys.json"), JSON.stringify({ PETBOX_MYPROJ_API_KEY: "k-123" }), "utf8");
    const resolved = resolveProject(projectDir, home);
    assert.ok(resolved);
    assert.equal(resolved?.project, "myproj");
    assert.equal(resolved?.apiKey, "k-123");
    assert.equal(resolved?.baseUrl, "https://example.test");
    assert.deepEqual(readWireLogTail(20, home), [], "a clean successful resolve must never trace");
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});
