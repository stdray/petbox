// Unit tests for the POSIX env-sourcing logic (persistKeyForAgentsPosix), extracted into its
// own module specifically so it's importable here — wire.ts itself runs main() at module top
// level and must never be imported by a test (see bin/petbox-wire.js's comment on that).
//
// Run: node --test src/posix-env.test.ts   (Node >= 23.6 native TS type-stripping; no build step)

import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { persistKeyForAgentsPosix } from "./posix-env.ts";
import { UnresolvedEnvRefError } from "./registry.ts";

const MARKER = "# petbox-wire";

function freshHome(): string {
  return mkdtempSync(join(tmpdir(), "petbox-wire-test-"));
}

test("no pre-existing profile files: .zshenv is created and gets the marker (macOS/zsh case)", () => {
  const home = freshHome();
  try {
    assert.equal(existsSync(join(home, ".profile")), false);
    assert.equal(existsSync(join(home, ".zshenv")), false);

    persistKeyForAgentsPosix(home);

    const zshenvPath = join(home, ".zshenv");
    assert.equal(existsSync(zshenvPath), true, ".zshenv must be created even with no pre-existing profile files");
    assert.match(readFileSync(zshenvPath, "utf8"), new RegExp(MARKER));
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("pre-existing .bashrc (no .zshenv): marker still appended to .bashrc, AND .zshenv is created too", () => {
  const home = freshHome();
  try {
    writeFileSync(join(home, ".bashrc"), "# my existing bashrc\n", "utf8");

    persistKeyForAgentsPosix(home);

    const bashrc = readFileSync(join(home, ".bashrc"), "utf8");
    assert.match(bashrc, new RegExp(MARKER), "existing .bashrc must still get the source line (no regression)");
    assert.ok(bashrc.includes("# my existing bashrc"), "existing .bashrc content must be preserved");

    const zshenvPath = join(home, ".zshenv");
    assert.equal(existsSync(zshenvPath), true, ".zshenv must be created even when another profile pre-existed");
    assert.match(readFileSync(zshenvPath, "utf8"), new RegExp(MARKER));
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("pre-existing .zshenv without the marker: marker gets appended, content preserved", () => {
  const home = freshHome();
  try {
    writeFileSync(join(home, ".zshenv"), "# my existing zshenv\n", "utf8");

    persistKeyForAgentsPosix(home);

    const zshenv = readFileSync(join(home, ".zshenv"), "utf8");
    assert.match(zshenv, new RegExp(MARKER));
    assert.ok(zshenv.includes("# my existing zshenv"), "existing .zshenv content must be preserved");
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("idempotent: running twice does not duplicate the marker in .zshenv or .bashrc", () => {
  const home = freshHome();
  try {
    writeFileSync(join(home, ".bashrc"), "# existing\n", "utf8");

    persistKeyForAgentsPosix(home);
    persistKeyForAgentsPosix(home);

    const countMarker = (s: string) => s.split(MARKER).length - 1;
    assert.equal(countMarker(readFileSync(join(home, ".bashrc"), "utf8")), 1);
    assert.equal(countMarker(readFileSync(join(home, ".zshenv"), "utf8")), 1);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("writes ~/.petbox/env.sh from the key store", () => {
  const home = freshHome();
  try {
    mkdirSync(join(home, ".petbox"), { recursive: true });
    writeFileSync(join(home, ".petbox", "keys.json"), JSON.stringify({ MY_PROJECT_API_KEY: "secret" }), "utf8");

    persistKeyForAgentsPosix(home);

    const envSh = readFileSync(join(home, ".petbox", "env.sh"), "utf8");
    assert.match(envSh, /export MY_PROJECT_API_KEY="secret"/);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("keys-json-supports-env-var-references: a $VAR reference in keys.json resolves to the REAL value in env.sh — never the raw '${VAR}' text", () => {
  const home = freshHome();
  const envVar = "MY_REF_PROJECT_API_KEY";
  const prevEnv = process.env[envVar];
  try {
    mkdirSync(join(home, ".petbox"), { recursive: true });
    writeFileSync(join(home, ".petbox", "keys.json"), JSON.stringify({ [envVar]: "${" + envVar + "}" }), "utf8");
    process.env[envVar] = "the-real-secret-value";

    persistKeyForAgentsPosix(home);

    const envSh = readFileSync(join(home, ".petbox", "env.sh"), "utf8");
    assert.match(envSh, new RegExp(`export ${envVar}="the-real-secret-value"`));
    assert.doesNotMatch(envSh, /\$\{/, "must never write the raw reference syntax into env.sh");
  } finally {
    if (prevEnv === undefined) delete process.env[envVar];
    else process.env[envVar] = prevEnv;
    rmSync(home, { recursive: true, force: true });
  }
});

test("keys-json-supports-env-var-references: an UNRESOLVABLE reference throws instead of writing an empty/placeholder export", () => {
  const home = freshHome();
  const envVar = "MY_UNRESOLVED_PROJECT_API_KEY";
  const prevEnv = process.env[envVar];
  try {
    delete process.env[envVar];
    mkdirSync(join(home, ".petbox"), { recursive: true });
    writeFileSync(join(home, ".petbox", "keys.json"), JSON.stringify({ [envVar]: "${" + envVar + "}" }), "utf8");

    assert.throws(() => persistKeyForAgentsPosix(home, "my-project"), (e: unknown) => {
      assert.ok(e instanceof UnresolvedEnvRefError);
      assert.match(e.message, new RegExp(envVar));
      assert.match(e.message, /my-project/);
      return true;
    });
    assert.equal(existsSync(join(home, ".petbox", "env.sh")), false, "must not write a half-formed env.sh");
  } finally {
    if (prevEnv === undefined) delete process.env[envVar];
    else process.env[envVar] = prevEnv;
    rmSync(home, { recursive: true, force: true });
  }
});
