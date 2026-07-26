// Unit tests for skill-files.ts, extracted from wire.ts specifically so it's importable here —
// wire.ts itself runs main() at module top level and must never be imported by a test (see
// posix-env.ts's comment on the identical problem).
//
// Run: node --test src/skill-files.test.ts   (Node >= 23.6 native TS type-stripping; no build step)

import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import {
  PROJECT_SKILLS,
  SKILL_SURFACES,
  renderSkillTemplate,
  writeSkillFiles,
  type SkillWriteOutcome,
} from "./skill-files.ts";
import { hasPetboxMarker, PETBOX_MARKER_LINE } from "./origin-marker.ts";

const HERE = dirname(fileURLToPath(import.meta.url));
const TEMPLATES_ROOT = join(HERE, "templates");

function freshDir(): string {
  return mkdtempSync(join(tmpdir(), "petbox-wire-skill-test-"));
}

// The legacy (pre-marker) rendering: what the OLD template — before this fix added
// `petbox: managed` to the three templates' frontmatter — would have produced for the same
// project/workspace. Used to set up "already materialized by an old wire" fixtures.
function legacyRender(spec: string, project: string, workspace: string): string {
  const tpl = readFileSync(join(TEMPLATES_ROOT, spec, "SKILL.md"), "utf8");
  const rendered = renderSkillTemplate(tpl, project, workspace);
  return rendered.replace(new RegExp(`^${PETBOX_MARKER_LINE}\\r?\\n`, "m"), "");
}

function pathFor(dir: string, surface: string[], specDir: string): string {
  return join(dir, ...surface, specDir, "SKILL.md");
}

test("writeSkillFiles writes every PROJECT_SKILLS entry into every SKILL_SURFACES root", () => {
  const dir = freshDir();
  try {
    const outcomes = writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    assert.equal(outcomes.length, PROJECT_SKILLS.length * SKILL_SURFACES.length);
    for (const spec of PROJECT_SKILLS) {
      for (const surface of SKILL_SURFACES) {
        const p = pathFor(dir, surface, spec.dir);
        assert.equal(existsSync(p), true, `expected ${p} to exist`);
        assert.ok(
          outcomes.some((o) => o.path === p && o.kind === "written" && o.reason === "new"),
          `expected writeSkillFiles to report a fresh write for ${p}`,
        );
      }
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("{{PROJECT}} is substituted everywhere; no template placeholder survives rendering", () => {
  const dir = freshDir();
  try {
    writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    for (const spec of PROJECT_SKILLS) {
      const body = readFileSync(join(dir, ".claude", "skills", spec.dir, "SKILL.md"), "utf8");
      assert.ok(!body.includes("{{PROJECT}}"), `${spec.dir}: unresolved {{PROJECT}} placeholder`);
      assert.ok(!body.includes("{{WORKSPACE}}"), `${spec.dir}: unresolved {{WORKSPACE}} placeholder`);
    }
    const petboxBody = readFileSync(join(dir, ".claude", "skills", "petbox", "SKILL.md"), "utf8");
    assert.ok(petboxBody.includes("hellopet"), "petbox skill must carry the project key");
    assert.ok(petboxBody.includes("newpet"), "petbox skill must carry the workspace");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("renderSkillTemplate is a no-op on a template with neither placeholder", () => {
  const tpl = "static content, no placeholders here\n";
  assert.equal(renderSkillTemplate(tpl, "anyproject", "anyworkspace"), tpl);
});

test("petbox-methodology skill: identical across two different projects except for the substituted key", () => {
  // Regression guard for the bug this fixes: the methodology skill must be a THIN, project-agnostic
  // pointer at the live tasks_methodology_guide, never this repo's own hardcoded rules. If the
  // rendered body differed by more than the {{PROJECT}} substitution between two unrelated projects,
  // something project-specific (or this-repo-specific) leaked into the template.
  const tplRaw = readFileSync(join(TEMPLATES_ROOT, "petbox-methodology", "SKILL.md"), "utf8");
  const renderedAlpha = renderSkillTemplate(tplRaw, "alpha-project", "unused");
  const renderedBeta = renderSkillTemplate(tplRaw, "beta-project", "unused");
  const stripped = (s: string) => s.split("alpha-project").join("<P>").split("beta-project").join("<P>");
  assert.equal(stripped(renderedAlpha), stripped(renderedBeta));
});

test("petbox-methodology skill: defers to the live server guide, never hardcodes this repo's own gates", () => {
  const body = readFileSync(join(TEMPLATES_ROOT, "petbox-methodology", "SKILL.md"), "utf8");
  // Must tell the agent to fetch the ACTUAL rules for the wired project at runtime.
  assert.ok(body.includes("tasks_methodology_guide"), "must point at the runtime methodology guide tool");
  // Must NOT assert this repo's own dogfooded gate mechanics as if they were universal — those are
  // $system-specific conventions (see doc/methodology.md) that a different project may not share.
  for (const leaked of ["spec_plan", "ideaRef", "specRef", "quartet is", "$system"]) {
    assert.ok(!body.includes(leaked), `template must not hardcode this repo's own rule: "${leaked}"`);
  }
});

test("petbox-methodology skill frontmatter names the skill correctly", () => {
  const body = readFileSync(join(TEMPLATES_ROOT, "petbox-methodology", "SKILL.md"), "utf8");
  assert.match(body, /^---\nname: petbox-methodology\n/);
});

// ---- origin marker (bug: skill-files-clobber-and-apply-skips) -------------------------------

test("every template's frontmatter carries the PetBox origin marker", () => {
  for (const spec of PROJECT_SKILLS) {
    const body = readFileSync(join(TEMPLATES_ROOT, spec.dir, "SKILL.md"), "utf8");
    assert.ok(hasPetboxMarker(body), `${spec.dir}: template frontmatter must carry \`${PETBOX_MARKER_LINE}\``);
  }
});

test("writeSkillFiles: every written skill carries the origin marker", () => {
  const dir = freshDir();
  try {
    writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    for (const spec of PROJECT_SKILLS) {
      for (const surface of SKILL_SURFACES) {
        const body = readFileSync(pathFor(dir, surface, spec.dir), "utf8");
        assert.ok(hasPetboxMarker(body), `${pathFor(dir, surface, spec.dir)} must carry the origin marker`);
      }
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("writeSkillFiles: a re-run overwrites its own marked files silently (reason 'own')", () => {
  const dir = freshDir();
  try {
    writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    const outcomes = writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    assert.ok(outcomes.every((o) => o.kind === "written" && o.reason === "own"));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("writeSkillFiles: a foreign file (no marker, different content) is blocked and left byte-for-byte untouched", () => {
  const dir = freshDir();
  const target = pathFor(dir, SKILL_SURFACES[0]!, "petbox");
  try {
    const foreign = "# my own petbox notes\n\nthis is MY file, not generated by wire\n";
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, foreign, "utf8");

    const outcomes = writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    const outcome = outcomes.find((o) => o.path === target) as SkillWriteOutcome;
    assert.equal(outcome.kind, "blocked");
    assert.equal(readFileSync(target, "utf8"), foreign, "foreign file must be left byte-for-byte untouched");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("writeSkillFiles: an unmarked file byte-identical to the pre-marker render is migrated in place", () => {
  const dir = freshDir();
  const project = "hellopet";
  const workspace = "newpet";
  try {
    // Simulate a project materialized by a `wire` run from BEFORE this fix: every surface has the
    // OLD (unmarked) rendered body already on disk.
    for (const spec of PROJECT_SKILLS) {
      const legacy = legacyRender(spec.dir, project, workspace);
      for (const surface of SKILL_SURFACES) {
        const p = pathFor(dir, surface, spec.dir);
        mkdirSync(dirname(p), { recursive: true });
        writeFileSync(p, legacy, "utf8");
        assert.equal(hasPetboxMarker(legacy), false, "fixture must reproduce the pre-fix, unmarked state");
      }
    }

    // The very first wire/apply after this fix must NOT block on the owner's own already-
    // materialized skills — it must recognize them as ours and promote them.
    const outcomes = writeSkillFiles(dir, TEMPLATES_ROOT, project, workspace);
    assert.ok(
      outcomes.every((o) => o.kind === "written" && o.reason === "migrated"),
      `expected every outcome to be a migration: ${JSON.stringify(outcomes)}`,
    );
    for (const spec of PROJECT_SKILLS) {
      for (const surface of SKILL_SURFACES) {
        const body = readFileSync(pathFor(dir, surface, spec.dir), "utf8");
        assert.ok(hasPetboxMarker(body), "migrated file must now carry the origin marker");
      }
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("writeSkillFiles: an unmarked file that differs from the pre-marker render (real edits) is blocked, not migrated", () => {
  const dir = freshDir();
  const project = "hellopet";
  const workspace = "newpet";
  const target = pathFor(dir, SKILL_SURFACES[0]!, "petbox");
  try {
    const edited = legacyRender("petbox", project, workspace) + "\n\n## my own added section\n";
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, edited, "utf8");

    const outcomes = writeSkillFiles(dir, TEMPLATES_ROOT, project, workspace);
    const outcome = outcomes.find((o) => o.path === target) as SkillWriteOutcome;
    assert.equal(outcome.kind, "blocked", "an owner edit on top of the legacy render must never be silently migrated");
    assert.equal(readFileSync(target, "utf8"), edited, "edited file must be left byte-for-byte untouched");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
