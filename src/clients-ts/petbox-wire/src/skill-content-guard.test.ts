// Narrow content regressions for two point-of-action fixes made under
// skills-audit-against-both-axes-criterion (L3 of umbrella-agent-text-names-both-axes).
//
// This is deliberately NOT a generic composition ratchet for SKILL.md prose (that surface's own
// ratchet is tracked separately, see protocol.test.ts's note above assertRuleNamesBothAxes). The
// audit for this card found the "trailing exception overrides an earlier unconditional rule"
// class does not currently reproduce anywhere outside the already-fixed petbox-write-economy
// case (31f345b) — so there is no live target that would justify a general markdown-section
// parser here, and one built without a target risks false positives on legitimate "When not to
// use this" sections (petbox-analysis-workspace, petbox-factory-run) that describe the whole
// skill's own applicability, not an exception to an inner rule.
//
// What IS worth pinning: two specific facts this audit added, each at the exact point of the act
// R1 asks for, that a future edit could silently drop without any other test noticing.
//
// Run: node --test src/skill-content-guard.test.ts

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

const HERE = dirname(fileURLToPath(import.meta.url));
const TEMPLATES_ROOT = join(HERE, "templates");

function readTemplate(spec: string): string {
  return readFileSync(join(TEMPLATES_ROOT, spec, "SKILL.md"), "utf8");
}

// petbox-write-economy recommends a shell heredoc as the mechanism to stage non-ASCII (Cyrillic/
// CJK) text before a bodyRef upload — exactly the content most likely to carry `$`, backticks or
// backslashes that an UNQUOTED heredoc delimiter puts through shell substitution (observation
// heredoc-quoting-rediscovered-every-session). The skill must name the quoted form AT the point
// it recommends heredoc use, not merely assert the technique "removes the failure mode
// completely" without the one condition that makes that true.
test("petbox-write-economy names a quoted heredoc delimiter where it recommends heredoc for non-ASCII staging", () => {
  const text = readTemplate("petbox-write-economy");
  const heredocLine = text.split("\n").find((l) => l.includes("shell heredoc"));
  assert.ok(heredocLine, `petbox-write-economy must still recommend a shell heredoc:\n${text}`);
  assert.match(
    text,
    /<<'EOF'/,
    "the quoted-delimiter form must be named explicitly, not left implicit — an unquoted " +
      "delimiter reintroduces exactly the corruption this technique exists to remove",
  );
  assert.match(
    text,
    /never bare `<<EOF`/,
    "the non-trigger (bare/unquoted delimiter) must be named too, not just the correct form",
  );
});

// petbox-methodology's intake-triage rule used to gate skipping intake on "the destination is
// already obvious" — a FEELING, not something checkable at the point of the act — with no stated
// branch for "not obvious" at all (candidate 4, skills-audit-against-both-axes-criterion). The
// fixed rule must both replace "obvious" with a checkable test (maps onto exactly one of the
// three enumerated destinations without weighing alternatives) and name the non-trigger branch
// (weighing alternatives -> file into intake instead).
test("petbox-methodology's intake-skip rule is checkable and names the non-trigger branch explicitly", () => {
  const text = readTemplate("petbox-methodology");
  assert.ok(
    !/destination is already obvious/.test(text),
    "the unfalsifiable 'already obvious' phrasing must not return",
  );
  assert.match(
    text,
    /without\s+weighing alternatives/,
    "the trigger must be a checkable fact (maps onto exactly one destination), not a feeling",
  );
  assert.match(
    text,
    /file it into intake instead/,
    "the non-trigger branch (weighing alternatives -> use intake) must be named explicitly",
  );
});

// petbox-node-authoring is opened 10/10 in the agent-behaviour probe while its neighbor
// petbox-write-economy — written specifically for delivering a long/non-ASCII body via bodyRef
// instead of inlining — is opened 0/10 on the same task. node-authoring's body never named
// write-economy or bodyRef/non-ASCII at all, so an agent reading only node-authoring got no
// handoff to the skill that actually covers the failure mode. The fix must land the handoff AT
// the point node-authoring discusses how to write the body (section (a)) — not as a trailing
// section, which is the exact ordering mistake that already cost an incident in write-economy's
// own section (e) — and must name both axes: format lives here, delivery of a long/non-ASCII
// composed body lives in the neighbor, open before the write call.
test("petbox-node-authoring hands off to petbox-write-economy at the point it discusses writing the body, naming both axes", () => {
  const text = readTemplate("petbox-node-authoring");

  const sectionA = text.split(/^## \(b\)/m)[0] ?? "";
  assert.ok(
    /petbox-write-economy/.test(sectionA) && /bodyRef/.test(sectionA),
    "the handoff to petbox-write-economy (naming bodyRef) must live in section (a), where the " +
      "skill discusses HOW to write the body — not merely somewhere else in the document:\n" +
      sectionA,
  );
  assert.match(
    sectionA,
    /non-ASCII/,
    "the trigger for the neighbor (long or non-ASCII composed text) must be named explicitly",
  );
  assert.match(
    sectionA,
    /FORMAT only/,
    "the non-trigger axis (this skill covers FORMAT, not delivery) must be named explicitly too, " +
      "so the handoff reads as a scope split rather than an unconditional redirect",
  );
});
