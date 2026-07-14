// Unit tests for the wire's identity resolution: the canonical per-project API-key env var name
// and the workspace used by the skill template. Both live in wire-identity.ts specifically so
// they're importable here — wire.ts runs main() at module top level and must never be imported
// by a test (see posix-env.test.ts for the same note).
//
// Run: node --test src/wire-identity.test.ts   (Node >= 23.6 native TS type-stripping)

import assert from "node:assert/strict";
import { test } from "node:test";
import { WIRE_EXIT } from "./wire-exit.ts";
import { deriveEnvVar, resolveWorkspace } from "./wire-identity.ts";

test("deriveEnvVar matches the server's EnvSlug (PETBOX_<PROJECT>_API_KEY)", () => {
  // The Connect page (ProjectConnect.cshtml.cs EnvSlug) is the single source of truth for the
  // name an operator is told to set — the CLI default must be byte-identical.
  assert.equal(deriveEnvVar("$system"), "PETBOX_SYSTEM_API_KEY");
  assert.equal(deriveEnvVar("kpvotes"), "PETBOX_KPVOTES_API_KEY");
  assert.equal(deriveEnvVar("petbox"), "PETBOX_PETBOX_API_KEY");
});

test("deriveEnvVar collapses runs of punctuation and trims leading/trailing underscores", () => {
  assert.equal(deriveEnvVar("my--weird..key"), "PETBOX_MY_WEIRD_KEY_API_KEY");
  assert.equal(deriveEnvVar("$$foo-bar$$"), "PETBOX_FOO_BAR_API_KEY");
  assert.equal(deriveEnvVar("a.b-c"), "PETBOX_A_B_C_API_KEY");
  // Regression: the old scheme replaced 1:1 and never trimmed → "_SYSTEM_API_KEY".
  assert.ok(!deriveEnvVar("$system").startsWith("_"));
  assert.ok(!deriveEnvVar("$$foo-bar$$").includes("__"));
});

test("resolveWorkspace: --workspace flag wins over the server-reported workspace", () => {
  const r = resolveWorkspace("acme", "server-ws");
  assert.equal(r.ok, true);
  assert.equal(r.ok && r.workspace, "acme");
  assert.equal(r.ok && r.source, "flag");
});

test("resolveWorkspace: server-reported workspace is used when no flag is passed", () => {
  const r = resolveWorkspace(undefined, "server-ws");
  assert.equal(r.ok, true);
  assert.equal(r.ok && r.workspace, "server-ws");
  assert.equal(r.ok && r.source, "server");
});

test("resolveWorkspace: neither flag nor server value → usage error, exit 2 (no hardcoded default)", () => {
  // An old server predating the `workspace` field on /api/auth/validate reports none.
  const r = resolveWorkspace(undefined, undefined);
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.exitCode, WIRE_EXIT.usage);
  assert.equal(r.exitCode, 2);
  assert.match(r.message, /--workspace is required: this server did not report a workspace/);
  // Never a personal workspace baked into the published CLI.
  assert.doesNotMatch(r.message, /stdray/);
});

test("resolveWorkspace: blank strings count as absent (flag and server alike)", () => {
  // Stored to one variable — two independent calls cannot be correlated by the type
  // checker's control-flow narrowing (each is a fresh, unrelated WorkspaceResolution),
  // and calling the function twice to check two different things was also wasteful.
  const blank = resolveWorkspace("   ", "server-ws");
  assert.equal(blank.ok && blank.workspace, "server-ws");
  assert.equal(resolveWorkspace("", "  ").ok, false);
  const trimmed = resolveWorkspace(" acme ", undefined);
  assert.equal(trimmed.ok && trimmed.workspace, "acme");
});
