// Unit tests for the observed role-binding session-meta header helper (append.ts).
import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { buildSessionMetaHeader } from "./append.ts";

function freshHome(): string {
  const home = mkdtempSync(join(tmpdir(), "petbox-append-meta-"));
  mkdirSync(join(home, ".petbox"), { recursive: true });
  return home;
}

test("buildSessionMetaHeader returns null when no roles for agent", () => {
  const home = freshHome();
  try {
    assert.equal(buildSessionMetaHeader("claude-code", home), null);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("buildSessionMetaHeader wraps resolveObservedBinding as roleBinding", () => {
  const home = freshHome();
  try {
    writeFileSync(
      join(home, ".petbox", "roles.json"),
      JSON.stringify({
        activeProfile: "default",
        profiles: {
          default: {
            agents: {
              "claude-code": {
                roles: {
                  orchestrator: { model: "claude-opus-4" },
                  worker: { model: "claude-sonnet-4" },
                },
              },
            },
          },
        },
      }),
    );
    const raw = buildSessionMetaHeader("claude-code", home);
    assert.ok(raw);
    assert.deepEqual(JSON.parse(raw!), {
      roleBinding: {
        profile: "default",
        agent: "claude-code",
        roles: {
          orchestrator: "claude-opus-4",
          worker: "claude-sonnet-4",
        },
      },
    });
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});
