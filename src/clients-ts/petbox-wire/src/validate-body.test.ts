// Behaviour tests for petbox-node-authoring's validate-body.mjs — specifically the WARNING level
// (work: node-authoring-why-and-term-definitions). The validator's structural checks (SVG
// allowlist, pseudo-headings, ...) are pinned against MarkdownRenderer by the .NET drift guard
// (NodeAuthoringSkillSvgDriftTests.cs); this file covers the softer, non-mechanical norm added on
// top of that: a long body should lead with a `## Why` / `## Зачем` section, and a violation of
// that norm is a WARNING (stdout, exit 0) — never a hard failure like the checks above it.
//
// Run: node --test src/validate-body.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

const HERE = dirname(fileURLToPath(import.meta.url));
const VALIDATOR = join(HERE, "templates", "petbox-node-authoring", "validate-body.mjs");

function runValidator(draft: string): { stdout: string; stderr: string; status: number | null } {
  const dir = mkdtempSync(join(tmpdir(), "petbox-validate-body-"));
  const draftPath = join(dir, "draft.md");
  try {
    writeFileSync(draftPath, draft, "utf8");
    const res = spawnSync(process.execPath, [VALIDATOR, draftPath], { encoding: "utf8" });
    return { stdout: res.stdout ?? "", stderr: res.stderr ?? "", status: res.status };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

// 12 non-empty lines, first heading is not Why/Зачем — must cross the >10 threshold.
const LONG_BODY_NO_WHY = [
  "## Mechanism",
  "",
  ...Array.from({ length: 11 }, (_, i) => `Detail line ${i + 1} about the implementation.`),
].join("\n");

const LONG_BODY_WITH_WHY = [
  "## Why",
  "",
  "This breaks onboarding for a reader with no session context.",
  "",
  "## Mechanism",
  "",
  ...Array.from({ length: 11 }, (_, i) => `Detail line ${i + 1} about the implementation.`),
].join("\n");

const LONG_BODY_WITH_ZACHEM = [
  "## Зачем",
  "",
  "Без этого правило не проверяется механически.",
  "",
  "## Механизм",
  "",
  ...Array.from({ length: 11 }, (_, i) => `Строка деталей ${i + 1}.`),
].join("\n");

const SHORT_BODY_NO_WHY = "## Mechanism\n\nOne short paragraph, well under the line threshold.";

test("validate-body.mjs: long body without a Why/Зачем lead warns on stdout, exit 0", () => {
  const { stdout, status } = runValidator(LONG_BODY_NO_WHY);
  assert.equal(status, 0, "a missing Why lead is a warning, not a violation — exit must stay 0");
  assert.match(stdout, /warning:.*Why.*Зачем/s, "stdout must carry the warning naming both headings");
});

test("validate-body.mjs: long body leading with '## Why' is silent on the norm", () => {
  const { stdout, status } = runValidator(LONG_BODY_WITH_WHY);
  assert.equal(status, 0);
  assert.doesNotMatch(stdout, /warning:/, "a body that already leads with Why must not warn");
});

test("validate-body.mjs: long body leading with '## Зачем' (Russian-language project) is silent too", () => {
  const { stdout, status } = runValidator(LONG_BODY_WITH_ZACHEM);
  assert.equal(status, 0);
  assert.doesNotMatch(stdout, /warning:/);
});

test("validate-body.mjs: a short body under the line threshold never warns, Why or not", () => {
  const { stdout, status } = runValidator(SHORT_BODY_NO_WHY);
  assert.equal(status, 0);
  assert.doesNotMatch(stdout, /warning:/, "the norm only applies past the line threshold");
});

test("validate-body.mjs: a real violation still exits 1 even when the Why warning also fires", () => {
  // Same shape as LONG_BODY_NO_WHY, plus a pseudo-heading violation the validator already checks.
  const draft = LONG_BODY_NO_WHY + "\n\n==fake heading==\n";
  const { stdout, stderr, status } = runValidator(draft);
  assert.equal(status, 1, "a real violation must still fail the gate");
  assert.match(stderr, /pseudo-heading/, "the violation itself is reported on stderr as before");
  assert.match(stdout, /warning:/, "the warning is independent and still prints alongside the violation");
});
