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
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { petboxKeysJsonPath } from "./petbox-dir.ts";
import {
  inspectKeyStoreEntry,
  parseEnvRef,
  readKeyStore,
  readRegistry,
  registryPath,
  resolveProject,
  toEnvRef,
  UnresolvedEnvRefError,
} from "./registry.ts";
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

// --- card keys-json-supports-env-var-references ------------------------------------------------

test("parseEnvRef: $VAR and ${VAR} both parse; a literal (including one that merely contains '$') does not", () => {
  assert.equal(parseEnvRef("$FOO"), "FOO");
  assert.equal(parseEnvRef("${FOO}"), "FOO");
  assert.equal(parseEnvRef("plain-literal-key"), null);
  assert.equal(parseEnvRef(""), null);
  assert.equal(parseEnvRef("prefix$FOOsuffix"), null, "must be the WHOLE value, not a substring");
  assert.equal(parseEnvRef("$"), null);
});

test("toEnvRef round-trips through parseEnvRef", () => {
  assert.equal(parseEnvRef(toEnvRef("PETBOX_X_API_KEY")), "PETBOX_X_API_KEY");
});

test("acceptance #1 + #4: a keys.json REFERENCE resolves from the environment; an old-format LITERAL file keeps working byte-for-byte", () => {
  const home = freshHome();
  const envVar = "PETBOX_REFTEST_API_KEY";
  const prevEnv = process.env[envVar];
  try {
    mkdirSync(join(home, ".petbox"), { recursive: true });
    const projectDir = join(home, "proj");
    mkdirSync(projectDir, { recursive: true });
    writeFileSync(
      registryPath(home),
      JSON.stringify({ entries: [{ prefix: projectDir, project: "reftest", envVar }] }),
      "utf8",
    );

    // Reference form.
    writeFileSync(join(home, ".petbox", "keys.json"), JSON.stringify({ [envVar]: "${" + envVar + "}" }), "utf8");
    process.env[envVar] = "live-env-value-1";
    assert.equal(readKeyStore(envVar, home, "reftest"), "live-env-value-1");
    const resolved = resolveProject(projectDir, home);
    assert.equal(resolved?.apiKey, "live-env-value-1");

    // Old literal format, unchanged behavior: the file value is used verbatim, no parsing.
    delete process.env[envVar];
    writeFileSync(join(home, ".petbox", "keys.json"), JSON.stringify({ [envVar]: "literal-key-value" }), "utf8");
    assert.equal(readKeyStore(envVar, home, "reftest"), "literal-key-value");
    const resolvedLiteral = resolveProject(projectDir, home);
    assert.equal(resolvedLiteral?.apiKey, "literal-key-value");
  } finally {
    if (prevEnv === undefined) delete process.env[envVar];
    else process.env[envVar] = prevEnv;
    rmSync(home, { recursive: true, force: true });
  }
});

test("acceptance #2: rotating the env var reaches resolveProject with ZERO edits to keys.json", () => {
  const home = freshHome();
  const envVar = "PETBOX_ROTATETEST_API_KEY";
  const prevEnv = process.env[envVar];
  try {
    mkdirSync(join(home, ".petbox"), { recursive: true });
    const projectDir = join(home, "proj");
    mkdirSync(projectDir, { recursive: true });
    writeFileSync(
      registryPath(home),
      JSON.stringify({ entries: [{ prefix: projectDir, project: "rotatetest", envVar }] }),
      "utf8",
    );
    writeFileSync(join(home, ".petbox", "keys.json"), JSON.stringify({ [envVar]: "${" + envVar + "}" }), "utf8");
    const keysJsonBefore = readFileSync(petboxKeysJsonPath(home), "utf8");

    process.env[envVar] = "key-before-rotation";
    assert.equal(resolveProject(projectDir, home)?.apiKey, "key-before-rotation");

    process.env[envVar] = "key-after-rotation";
    assert.equal(resolveProject(projectDir, home)?.apiKey, "key-after-rotation", "rotation must reach resolution with no file edit");

    const keysJsonAfter = readFileSync(petboxKeysJsonPath(home), "utf8");
    assert.equal(keysJsonAfter, keysJsonBefore, "keys.json itself must be untouched by a rotation");
  } finally {
    if (prevEnv === undefined) delete process.env[envVar];
    else process.env[envVar] = prevEnv;
    rmSync(home, { recursive: true, force: true });
  }
});

test("acceptance #3: an UNRESOLVED reference fails LOUDLY (named envVar+project in the message), never silently as null, and never as the literal '${VAR}' text", () => {
  const home = freshHome();
  const envVar = "PETBOX_UNRESOLVED_API_KEY";
  const prevEnv = process.env[envVar];
  try {
    delete process.env[envVar]; // the exact scenario: not inherited by this process
    mkdirSync(join(home, ".petbox"), { recursive: true });
    const projectDir = join(home, "proj");
    mkdirSync(projectDir, { recursive: true });
    writeFileSync(
      registryPath(home),
      JSON.stringify({ entries: [{ prefix: projectDir, project: "unresolved-proj", envVar }] }),
      "utf8",
    );
    writeFileSync(join(home, ".petbox", "keys.json"), JSON.stringify({ [envVar]: "${" + envVar + "}" }), "utf8");

    // Non-throwing classification must still see it (used by doctor) — never silently "absent".
    const entry = inspectKeyStoreEntry(envVar, home);
    assert.deepEqual(entry, { kind: "reference", refVar: envVar, resolved: null });

    // readKeyStore throws, naming the var and the project — not "", not the literal text.
    assert.throws(
      () => readKeyStore(envVar, home, "unresolved-proj"),
      (e: unknown) => {
        assert.ok(e instanceof UnresolvedEnvRefError);
        assert.equal(e.envVar, envVar);
        assert.equal(e.refVar, envVar);
        assert.equal(e.project, "unresolved-proj");
        assert.match(e.message, new RegExp(envVar));
        assert.match(e.message, /unresolved-proj/);
        assert.doesNotMatch(e.message, /\$\{/, "must never echo the raw reference syntax back as if it were a value");
        return true;
      },
    );

    // resolveProject is the ONE deliberate exception to its own "never throws" contract: this
    // must propagate, not collapse into null (which a caller treats as "not wired" — silence).
    assert.throws(() => resolveProject(projectDir, home), UnresolvedEnvRefError);
  } finally {
    if (prevEnv === undefined) delete process.env[envVar];
    else process.env[envVar] = prevEnv;
    rmSync(home, { recursive: true, force: true });
  }
});
