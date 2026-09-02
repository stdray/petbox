// Behavioral proof for kit-version-lands-everywhere-and-sweeps item 1: `update` must leave
// ~/.petbox/wire/templates/ holding EXACTLY the template set this kit ships, never a union with
// whatever an older install left behind.
//
// THE BUG (measured live, 2026-09-02, see the card body): copyKitToStable's orphan cleanup only
// ever compared the TOP LEVEL of STABLE (~/.petbox/wire/) against the top level of HERE (this
// package's src/). `templates/` itself survives on both sides — it is a directory, not a file
// the kit dropped — so the diff never looked ONE LEVEL DEEPER, where the actual rename lived
// (`analysis-workspace` -> `petbox-analysis-workspace`, `factory-run` -> `petbox-factory-run`).
// cpSync then copied the new template dirs in ALONGSIDE the old ones, which just sat there
// forever: two complete generations installed at once, silently.
//
// This test reproduces that exact shape without touching the real ~/.petbox: seed a throwaway
// STABLE (via a throwaway HOME) with a template directory this kit does NOT ship, run `update`
// against it, and assert the stale directory is gone while every currently-shipped template
// survives untouched.
//
// Seam: same throwaway-HOME spawn pattern as wire-full-exit-step11.test.ts (no fake server
// needed — `update` never touches the network).
//
// Run: node --test src/update-template-sweep.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readdirSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

const WIRE_TS = join(import.meta.dirname, "wire.ts");
const HERE_TEMPLATES = join(import.meta.dirname, "templates");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

test("update sweeps a stale template directory nested under templates/, not just top-level entries", () => {
  const homeDir = freshDir("petbox-update-sweep-home-");
  try {
    // Seed a STABLE install that pre-dates this kit's template set: two directories this kit
    // has never shipped (one mimicking the recorded live defect's pre-rename name, one with an
    // arbitrary name to prove this isn't a name-specific special case), sitting next to the
    // stable dir's other top-level entries so the OLD top-level-only diff would have missed both.
    const templatesDir = join(homeDir, ".petbox", "wire", "templates");
    mkdirSync(join(templatesDir, "analysis-workspace"), { recursive: true });
    writeFileSync(join(templatesDir, "analysis-workspace", "SKILL.md"), "stale pre-rename template\n", "utf8");
    mkdirSync(join(templatesDir, "some-other-retired-skill"), { recursive: true });
    writeFileSync(join(templatesDir, "some-other-retired-skill", "SKILL.md"), "unrelated stale template\n", "utf8");

    const result = spawnSync(process.execPath, [WIRE_TS, "update"], {
      env: {
        ...process.env,
        USERPROFILE: homeDir,
        HOME: homeDir,
        HOMEDRIVE: undefined,
        HOMEPATH: undefined,
      },
      encoding: "utf8",
    });

    assert.equal(result.status, 0, `update should exit 0; stderr:\n${result.stderr}\nstdout:\n${result.stdout}`);

    const after = readdirSync(templatesDir).sort();
    const shipped = readdirSync(HERE_TEMPLATES).sort();

    // The two stale directories seeded above must be gone — swept, not left standing next to
    // the current templates.
    assert.equal(existsSync(join(templatesDir, "analysis-workspace")), false, "stale analysis-workspace must be swept");
    assert.equal(
      existsSync(join(templatesDir, "some-other-retired-skill")),
      false,
      "stale some-other-retired-skill must be swept",
    );
    // The installed set must be an EXACT mirror of what this kit ships — no more, no less.
    assert.deepEqual(after, shipped, "installed templates/ must exactly match this kit's shipped templates/");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
  }
});

test("update leaves a stale top-level kit file swept too (pre-existing behavior stays intact)", () => {
  const homeDir = freshDir("petbox-update-sweep-home2-");
  try {
    const stableDir = join(homeDir, ".petbox", "wire");
    mkdirSync(stableDir, { recursive: true });
    writeFileSync(join(stableDir, "prompt-rag.ts"), "// retired module, should be swept\n", "utf8");

    const result = spawnSync(process.execPath, [WIRE_TS, "update"], {
      env: {
        ...process.env,
        USERPROFILE: homeDir,
        HOME: homeDir,
        HOMEDRIVE: undefined,
        HOMEPATH: undefined,
      },
      encoding: "utf8",
    });

    assert.equal(result.status, 0, `update should exit 0; stderr:\n${result.stderr}`);
    assert.equal(existsSync(join(stableDir, "prompt-rag.ts")), false, "stale top-level file must still be swept");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
  }
});
