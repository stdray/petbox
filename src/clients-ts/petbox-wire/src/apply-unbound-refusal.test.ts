// Integration tests for reserve-unbound-inherits-session-model (owner decision 2026-07-26,
// narrowed same-card 2026-07-26 once the first cut broke the newcomer-equivalent-experience
// happy path — see apply-artifacts.ts's file header for the full closed/open rationale):
//   1. `apply` on a machine with NO ~/.petbox/roles.json seeds the default claude-code roster
//      (aliases) AND a droid roster (literal `inherit` for every role) first (previously only
//      the full `wire` run's step 11 did this), so a bare `apply` no longer ships a claude-code
//      role with no `model:` line at all.
//   2. `apply` HARD REFUSES a declared role with no local model binding ONLY on a CLOSED-model-
//      space harness (claude-code): the kit can both name and verify a correct binding there.
//   3. On an OPEN-model-space harness (opencode — the kit cannot know a correct binding for this
//      machine's local provider config), an unbound role is still written inheriting the session
//      model, with a loud warning instead of a block — exit code stays 0. This is the newcomer's
//      happy path: a totally fresh machine's first `apply`/`wire` must exit 0.
//
// wire.ts runs main() at import time (see its own top-of-file comment on why testable logic
// lives in side modules), so the only way to exercise `apply`'s actual argv/behavior end-to-end
// is to spawn it as a real subprocess with a redirected HOME — same technique
// doctor-definition.test.ts already uses for `doctor`.
//
// Run: node --test src/apply-unbound-refusal.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { WIRE_EXIT } from "./wire-exit.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

function runApply(
  cwd: string,
  homeDir: string,
): { stdout: string; stderr: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, "apply", "--offline"], {
    cwd,
    encoding: "utf8",
    env: {
      ...process.env,
      // Windows resolves homedir() from USERPROFILE; POSIX from HOME. Set both so the test is
      // portable across the dev machine (win32) and any Linux CI runner (doctor-definition.test.ts
      // pattern).
      USERPROFILE: homeDir,
      HOME: homeDir,
      HOMEDRIVE: undefined,
      HOMEPATH: undefined,
    },
  });
  return { stdout: res.stdout ?? "", stderr: res.stderr ?? "", status: res.status };
}

test("HAPPY PATH: apply on a clean HOME exits 0 — claude-code roles get model:, droid gets inherit, opencode warns but still writes", () => {
  const homeDir = freshDir("petbox-apply-seed-home-");
  const projectDir = freshDir("petbox-apply-seed-proj-");
  try {
    const rolesPath = join(homeDir, ".petbox", "roles.json");
    assert.equal(existsSync(rolesPath), false, "precondition: no roles.json yet");

    const { stdout, stderr, status } = runApply(projectDir, homeDir);
    const out = stdout + stderr;

    // apply (not just full `wire`) now seeds a fresh machine's roster: claude-code aliases +
    // a droid `inherit` roster. opencode is deliberately NOT seeded (open/unknowable space).
    assert.equal(
      existsSync(rolesPath),
      true,
      `apply must seed ~/.petbox/roles.json on a fresh machine. Output:\n${out}`,
    );
    const roles = JSON.parse(readFileSync(rolesPath, "utf8"));
    const ccRoles = roles.profiles.default.agents["claude-code"].roles;
    assert.equal(ccRoles.reserve.model, "fable", "reserve must be seeded, aliased to fable");
    assert.equal(ccRoles.orchestrator.model, "opus");
    assert.equal(ccRoles.worker.model, "sonnet");
    assert.equal(ccRoles["worker-highstakes"].model, "opus");
    assert.equal(ccRoles.explore.model, "haiku");
    const droidRoles = roles.profiles.default.agents["droid"].roles;
    for (const role of ["orchestrator", "worker", "worker-highstakes", "explore", "reserve"]) {
      assert.equal(droidRoles[role].model, "inherit", `droid ${role} must be seeded to inherit`);
    }
    assert.equal(
      roles.profiles.default.agents["opencode"],
      undefined,
      "opencode must NOT be seeded — its model space is open/unknowable from the kit",
    );

    // Every default claude-code role file actually carries a model: line — the bug this card
    // fixes (a bare `apply` used to ship every role with NO model: key at all).
    for (const role of ["orchestrator", "worker", "worker-highstakes", "explore", "reserve"]) {
      const p = join(projectDir, ".claude", "agents", `petbox-${role}.md`);
      assert.equal(existsSync(p), true, `expected ${p} to be written. Output:\n${out}`);
      assert.match(
        readFileSync(p, "utf8"),
        /^model: /m,
        `${role}'s file must carry a model: line`,
      );
    }
    assert.match(
      readFileSync(join(projectDir, ".claude", "agents", "petbox-reserve.md"), "utf8"),
      /^model: fable$/m,
    );

    // droid roles are written too, with the seeded literal `inherit` — a real, explicit binding,
    // not the old implicit fallback.
    for (const role of ["orchestrator", "worker", "worker-highstakes", "explore", "reserve"]) {
      const p = join(projectDir, ".factory", "droids", `petbox-${role}.md`);
      assert.equal(existsSync(p), true, `expected ${p} to be written. Output:\n${out}`);
      assert.match(readFileSync(p, "utf8"), /^model: inherit$/m, `droid ${role} must carry model: inherit`);
    }

    // opencode is genuinely unbound (no safe placeholder exists for its open id space) — apply
    // must still WRITE its role files (inheriting the session model, exactly the pre-card
    // behavior) and warn loudly, rather than block. This is the crux of the happy path: a first
    // wire/apply on a brand-new machine must not fail just because opencode has no default.
    for (const role of ["orchestrator", "worker", "worker-highstakes", "explore", "reserve"]) {
      const p = join(projectDir, ".opencode", "agent", `petbox-${role}.md`);
      assert.equal(existsSync(p), true, `expected ${p} to be written (inheriting). Output:\n${out}`);
      const body = readFileSync(p, "utf8");
      assert.ok(!/^model:/m.test(body.split("---")[1] ?? ""), `opencode ${role} must NOT invent a model`);
    }
    // The `utility` role was deleted from the roster: a fresh apply must not render it on
    // ANY harness (leaf-tools-mechanical-and-drop-utility).
    for (const [dir, sub] of [
      [".claude", "agents"],
      [".opencode", "agent"],
      [".factory", "droids"],
    ] as const) {
      for (const name of ["petbox-utility.md", "utility.md"]) {
        assert.equal(
          existsSync(join(projectDir, dir, sub, name)),
          false,
          `${join(projectDir, dir, sub, name)} must not be written — the utility role is gone`,
        );
      }
    }

    assert.match(out, /no local model binding for harness 'opencode'/);
    assert.match(out, /model set .* --agent opencode/);

    // The happy path: exit 0, every harness fully written, nothing blocked.
    assert.equal(
      status,
      WIRE_EXIT.ok,
      `expected exit 0 (opencode warns but still writes; claude-code/droid are fully bound). Output:\n${out}`,
    );
    assert.match(out, /ok=\[claude-code,opencode,droid\]/);
    assert.match(out, /blocked=\[\]/, "nothing blocked");
    assert.match(out, /partial=\[\]/, "nothing partial");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("apply refuses a declared role with no local model binding — hard block, WIRE_EXIT.truthfulness, no silent file", () => {
  const homeDir = freshDir("petbox-apply-refuse-home-");
  const projectDir = freshDir("petbox-apply-refuse-proj-");
  try {
    const petboxDir = join(homeDir, ".petbox");
    mkdirSync(petboxDir, { recursive: true });
    // A pre-existing roles.json (so the seed step is a no-op — it never touches an existing
    // file) that fully binds opencode/droid (isolating this test from the separate opencode/
    // droid seeding gap covered by the other test) but is MISSING one claude-code role's
    // binding ("explore") — an operator's own incomplete roles.json, the shape this card's bug
    // report described.
    writeFileSync(
      join(petboxDir, "roles.json"),
      JSON.stringify(
        {
          activeProfile: "default",
          profiles: {
            default: {
              agents: {
                "claude-code": {
                  roles: {
                    orchestrator: { model: "opus" },
                    worker: { model: "sonnet" },
                    "worker-highstakes": { model: "opus" },
                    reserve: { model: "fable" },
                    // "explore" deliberately absent — the case under test.
                  },
                },
                opencode: {
                  roles: {
                    orchestrator: { model: "opencode-default" },
                    worker: { model: "opencode-default" },
                    "worker-highstakes": { model: "opencode-default" },
                    explore: { model: "opencode-default" },
                    reserve: { model: "opencode-default" },
                  },
                },
                droid: {
                  roles: {
                    orchestrator: { model: "inherit" },
                    worker: { model: "inherit" },
                    "worker-highstakes": { model: "inherit" },
                    explore: { model: "inherit" },
                    reserve: { model: "inherit" },
                  },
                },
              },
            },
          },
        },
        null,
        2,
      ),
      "utf8",
    );

    const { stdout, stderr, status } = runApply(projectDir, homeDir);
    const out = stdout + stderr;

    assert.equal(
      status,
      WIRE_EXIT.truthfulness,
      `expected the truthfulness exit (political block, not a hard crash — see wire-exit.ts). Output:\n${out}`,
    );
    assert.match(out, /explore/);
    assert.match(out, /NO local model binding/);
    assert.match(out, /claude-code/);

    // The unbound role is never written...
    assert.equal(
      existsSync(join(projectDir, ".claude", "agents", "petbox-explore.md")),
      false,
      "an unbound declared role must not be written at all",
    );
    // ...but its clean siblings on the SAME harness still are (partial write, not
    // all-or-nothing — matches the existing capability/model-violation behavior).
    assert.equal(existsSync(join(projectDir, ".claude", "agents", "petbox-worker.md")), true);
    // opencode/droid, fully bound in this fixture, are unaffected by the claude-code refusal.
    assert.equal(existsSync(join(projectDir, ".opencode", "agent", "petbox-worker.md")), true);
    assert.equal(existsSync(join(projectDir, ".factory", "droids", "petbox-worker.md")), true);

    // roles.json is untouched — apply never seeds over an existing file, even a partial one.
    const roles = JSON.parse(readFileSync(join(petboxDir, "roles.json"), "utf8"));
    assert.equal("explore" in roles.profiles.default.agents["claude-code"].roles, false);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
