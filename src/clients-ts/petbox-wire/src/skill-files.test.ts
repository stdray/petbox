// Unit tests for skill-files.ts, extracted from wire.ts specifically so it's importable here —
// wire.ts itself runs main() at module top level and must never be imported by a test (see
// posix-env.ts's comment on the identical problem).
//
// Run: node --test src/skill-files.test.ts   (Node >= 23.6 native TS type-stripping; no build step)

import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import {
  buildAutoSkillsIndex,
  buildSkillReports,
  checkSkillFile,
  describeWorkspaceProbeFailure,
  extractSkillDescription,
  extractSkillTrigger,
  formatSkillFile,
  PROJECT_SKILLS,
  probeWorkspace,
  readAutoDigestSkillTriggers,
  SKILL_SURFACES,
  renderSkillTemplate,
  writeSkillFiles,
  type SkillTemplateSpec,
  type SkillWriteOutcome,
  type WorkspaceProbeResult,
} from "./skill-files.ts";
import {
  hasPetboxMarker,
  isDeclaredManual,
  PETBOX_DIGEST_KEY,
  PETBOX_MANUAL_LINE,
  PETBOX_MARKER_LINE,
  readArtifactState,
  readDigestMode,
  readPetboxProvenance,
} from "./origin-marker.ts";

const HERE = dirname(fileURLToPath(import.meta.url));
const TEMPLATES_ROOT = join(HERE, "templates");

function freshDir(): string {
  return mkdtempSync(join(tmpdir(), "petbox-wire-skill-test-"));
}

// The legacy (pre-declaration) rendering: what the OLD template — before `petbox: managed` and
// `petbox-digest: <mode>` were added to the templates' frontmatter — would have produced for the
// same project/workspace. Used to set up "already materialized by an old wire" fixtures. BOTH
// lines come back out: a file left by a pre-fix wire carries neither.
function legacyRender(spec: string, project: string, workspace: string): string {
  const tpl = readFileSync(join(TEMPLATES_ROOT, spec, "SKILL.md"), "utf8");
  const rendered = renderSkillTemplate(tpl, project, workspace);
  return rendered
    .replace(new RegExp(`^${PETBOX_MARKER_LINE}\\r?\\n`, "m"), "")
    .replace(new RegExp(`^${PETBOX_DIGEST_KEY}:[ \\t]*\\S+\\r?\\n`, "m"), "");
}

function pathFor(dir: string, surface: string[], specDir: string): string {
  return join(dir, ...surface, specDir, "SKILL.md");
}

test("writeSkillFiles writes every PROJECT_SKILLS entry into every SKILL_SURFACES root", () => {
  const dir = freshDir();
  try {
    const { writes: outcomes } = writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
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

// ---- registry <-> templates/ parity (task write-economy-skill-via-wire) ---------------------
//
// The property that lets a NEW skill be added with a single PROJECT_SKILLS entry: every template
// directory on disk is registered, and every registered spec has a template directory on disk —
// no orphan in either direction. Without this, a template could sit unregistered forever (never
// wired into any project) or a registered spec could point at a deleted directory (every wire
// crashes at readFileSync) and nothing here would say so.

test("every directory under templates/ is registered in PROJECT_SKILLS (no unregistered orphan template)", () => {
  const templateDirs = readdirSync(TEMPLATES_ROOT, { withFileTypes: true })
    .filter((e) => e.isDirectory())
    .map((e) => e.name);
  const registered = new Set(PROJECT_SKILLS.map((s) => s.dir));
  for (const dir of templateDirs) {
    assert.ok(registered.has(dir), `templates/${dir} exists but is not registered in PROJECT_SKILLS`);
  }
});

test("every PROJECT_SKILLS entry has a matching templates/ directory (no dangling registry entry)", () => {
  const templateDirs = new Set(
    readdirSync(TEMPLATES_ROOT, { withFileTypes: true })
      .filter((e) => e.isDirectory())
      .map((e) => e.name),
  );
  for (const spec of PROJECT_SKILLS) {
    assert.ok(templateDirs.has(spec.dir), `PROJECT_SKILLS names "${spec.dir}" but templates/${spec.dir}/ does not exist`);
  }
});

// ---- registry <-> README.md parity (task wire-docs-skill-list-stale) ------------------------
//
// The templates<->PROJECT_SKILLS parity tests above catch a skill the CODE forgot; they say
// nothing about the prose in README.md's "What it installs" section, which lists every skill by
// name for a human reader and has now drifted from PROJECT_SKILLS three times in a row (missing
// petbox-methodology, then petbox-write-economy, then petbox-node-authoring, each added to the
// registry without a matching README update). README.md ships inside this same npm package
// (package.json "files") right next to src/, so this check never crosses a repo/package
// boundary — unlike doc/agent-wiring.md or the .NET web doc page, which live outside this
// package and are intentionally NOT covered here (see task report: cross-boundary path coupling
// from inside this package's own test file was judged not worth it).
test("every PROJECT_SKILLS entry is named in README.md's What it installs section", () => {
  const readme = readFileSync(join(HERE, "..", "README.md"), "utf8");
  const marker = "## What it installs";
  const start = readme.indexOf(marker);
  assert.ok(start >= 0, `README.md is missing the "${marker}" section`);
  const nextHeading = readme.indexOf("\n## ", start + marker.length);
  const section = nextHeading >= 0 ? readme.slice(start, nextHeading) : readme.slice(start);
  for (const spec of PROJECT_SKILLS) {
    assert.ok(
      section.includes(spec.dir),
      `README.md's "${marker}" section does not mention "${spec.dir}" — PROJECT_SKILLS and the README have drifted apart`,
    );
  }
});

// ---- origin marker (bug: skill-files-clobber-and-apply-skips) -------------------------------

test("every template's frontmatter carries the PetBox origin marker", () => {
  for (const spec of PROJECT_SKILLS) {
    const body = readFileSync(join(TEMPLATES_ROOT, spec.dir, "SKILL.md"), "utf8");
    assert.ok(hasPetboxMarker(body), `${spec.dir}: template frontmatter must carry \`${PETBOX_MARKER_LINE}\``);
  }
});

// ---- provenance + invocation-mode declarations (work: wire-skill-declared-provenance-and-mode;
// spec: wire-skill-provenance-states, wire-skill-invocation-mode,
// wire-skill-manual-declared-not-error) -------------------------------------------------------

// THE safety property of the whole provenance change, and the reason `hasPetboxMarker` had to
// stop accepting any `petbox: <token>`: that gate decides both what apply OVERWRITES and what
// cleanupLegacyArtifact DELETES. If `petbox: manual` satisfied it, a path the project had
// explicitly claimed as its own would be silently rewritten — and, once the skill pipeline calls
// the cleanup (work: wire-skill-cleanup-on-replace), silently deleted.
test("provenance: `petbox: manual` is NOT the managed marker — never overwritable, never deletable", () => {
  const managed = `---\nname: x\n${PETBOX_MARKER_LINE}\n---\n\nbody`;
  const manual = `---\nname: x\n${PETBOX_MANUAL_LINE}\n---\n\nbody`;
  const undeclared = "---\nname: x\n---\n\nbody";

  assert.equal(readPetboxProvenance(managed), "managed");
  assert.equal(readPetboxProvenance(manual), "manual");
  assert.equal(readPetboxProvenance(undeclared), null);
  // An unrecognized value is undeclared, not "close enough to managed".
  assert.equal(readPetboxProvenance("---\nname: x\npetbox: something-else\n---\n\nbody"), null);

  assert.equal(hasPetboxMarker(managed), true);
  assert.equal(hasPetboxMarker(manual), false, "a manual file must never pass the write/delete gate");
  assert.equal(isDeclaredManual(manual), true);
  assert.equal(isDeclaredManual(managed), false);
});

test("provenance: the `petbox-digest` key is never mistaken for the `petbox` provenance key", () => {
  const digestOnly = `---\nname: x\n${PETBOX_DIGEST_KEY}: auto\n---\n\nbody`;
  assert.equal(readPetboxProvenance(digestOnly), null, "`petbox-digest:` must not satisfy `petbox:`");
  assert.equal(hasPetboxMarker(digestOnly), false);
  assert.equal(readDigestMode(digestOnly), "auto");
  assert.equal(readDigestMode(`---\nname: x\n${PETBOX_DIGEST_KEY}: manual\n---\n\nbody`), "manual");
  assert.equal(readDigestMode("---\nname: x\n---\n\nbody"), null, "no declaration is not 'auto'");
  // Body prose must never be read as a declaration — same frontmatter scoping the marker has.
  assert.equal(readDigestMode(`---\nname: x\n---\n\nprose mentioning ${PETBOX_DIGEST_KEY}: auto`), null);
});

test("readArtifactState: a declared-manual file is its own state, not 'foreign'", () => {
  const dir = freshDir();
  try {
    const p = join(dir, "manual.md");
    writeFileSync(p, `---\nname: x\n${PETBOX_MANUAL_LINE}\n---\n\nmine\n`, "utf8");
    assert.equal(readArtifactState(p), "manual");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// The registry field and the template frontmatter are two copies of the same fact; this is the
// one thing that keeps them from drifting (same discipline as the PROJECT_SKILLS<->templates/
// and PROJECT_SKILLS<->README parity tests above).
test("every PROJECT_SKILLS entry's template declares the invocation mode its spec claims", () => {
  for (const spec of PROJECT_SKILLS) {
    const body = readFileSync(join(TEMPLATES_ROOT, spec.dir, "SKILL.md"), "utf8");
    assert.equal(
      readDigestMode(body),
      spec.digestMode,
      `${spec.dir}: PROJECT_SKILLS says digestMode "${spec.digestMode}" but templates/${spec.dir}/SKILL.md declares "${readDigestMode(body)}"`,
    );
  }
});

// Trap named in the work card: this is code that DELETES and OVERWRITES files in the owner's
// personal `.claude/skills`. A path the project declared manual must survive apply untouched,
// and — the second half, spec wire-skill-manual-declared-not-error — must not be reported as a
// conflict, because a conflict is what drives apply's exit 1.
test("writeSkillFiles: a file declared `petbox: manual` survives apply byte-for-byte and is NOT a conflict", () => {
  const dir = freshDir();
  const target = pathFor(dir, SKILL_SURFACES[0]!, "petbox");
  try {
    const mine = `---\nname: petbox\ndescription: my own replacement. Use always.\n${PETBOX_MANUAL_LINE}\n---\n\n# MY version of this skill\n`;
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, mine, "utf8");

    const { writes: outcomes } = writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    const outcome = outcomes.find((o) => o.path === target) as SkillWriteOutcome;
    assert.equal(outcome.kind, "declared-manual", `expected a declared-manual skip, got ${JSON.stringify(outcome)}`);
    assert.notEqual(outcome.kind, "blocked", "a declared manual path is a legal state, never a conflict");
    assert.equal(readFileSync(target, "utf8"), mine, "a declared-manual file must be left byte-for-byte untouched");
    // Every OTHER surface/skill still got written — the skip is per path, not a whole-run abort.
    assert.ok(
      outcomes.some((o) => o.kind === "written"),
      "the rest of the delivery must still land",
    );
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("writeSkillFiles: a manual declaration survives a SECOND apply too (never migrated, never promoted)", () => {
  const dir = freshDir();
  const target = pathFor(dir, SKILL_SURFACES[0]!, "petbox-methodology");
  try {
    writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    // The owner takes this path over after the first apply: content the kit itself wrote, with
    // the provenance flipped to manual.
    const taken = readFileSync(target, "utf8").replace(PETBOX_MARKER_LINE, PETBOX_MANUAL_LINE);
    writeFileSync(target, taken, "utf8");

    const { writes: outcomes } = writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    const outcome = outcomes.find((o) => o.path === target) as SkillWriteOutcome;
    assert.equal(outcome.kind, "declared-manual");
    assert.equal(readFileSync(target, "utf8"), taken);
  } finally {
    rmSync(dir, { recursive: true, force: true });
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
    const { writes: outcomes } = writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
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

    const { writes: outcomes } = writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
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
    const { writes: outcomes } = writeSkillFiles(dir, TEMPLATES_ROOT, project, workspace);
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

    const { writes: outcomes } = writeSkillFiles(dir, TEMPLATES_ROOT, project, workspace);
    const outcome = outcomes.find((o) => o.path === target) as SkillWriteOutcome;
    assert.equal(outcome.kind, "blocked", "an owner edit on top of the legacy render must never be silently migrated");
    assert.equal(readFileSync(target, "utf8"), edited, "edited file must be left byte-for-byte untouched");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- rename cleanup (bug: wire-skill-cleanup-on-replace; spec: wire-skill-replace-no-orphans)
//
// `cleanupLegacyArtifact` existed for exactly this and had ONE caller — the agent-role rename in
// wire.ts — while the skill write pipeline had none at all, so a renamed skill's old SKILL.md
// stayed on disk forever. These tests run against a FIXTURE registry (writeSkillFiles' `specs`
// parameter): the delivered set has no renames of its own yet, and a mechanism that deletes
// files in the owner's `.claude/skills` must not sit untested until its first real caller lands.

/** A one-skill templates root + registry whose skill used to live under `legacyDirs`. */
function fixtureRegistry(legacyDirs: string[]): { templatesRoot: string; specs: SkillTemplateSpec[] } {
  const templatesRoot = freshDir();
  mkdirSync(join(templatesRoot, "petbox-renamed"), { recursive: true });
  writeFileSync(
    join(templatesRoot, "petbox-renamed", "SKILL.md"),
    `---\nname: petbox-renamed\ndescription: A renamed skill. Use always.\n${PETBOX_MARKER_LINE}\n${PETBOX_DIGEST_KEY}: auto\n---\n\n# Renamed\n`,
    "utf8",
  );
  return { templatesRoot, specs: [{ dir: "petbox-renamed", needsWorkspace: false, digestMode: "auto", legacyDirs }] };
}

/** Put a file at the pre-rename path, as an earlier delivery would have left it. */
function seedLegacy(dir: string, surface: string[], legacyDir: string, body: string): string {
  const p = join(dir, ...surface, legacyDir, "SKILL.md");
  mkdirSync(dirname(p), { recursive: true });
  writeFileSync(p, body, "utf8");
  return p;
}

test("writeSkillFiles: a renamed skill's OWNED pre-rename copy is removed, and its emptied directory with it", () => {
  const dir = freshDir();
  const { templatesRoot, specs } = fixtureRegistry(["petbox-old-name"]);
  try {
    const legacyPaths = SKILL_SURFACES.map((s) =>
      seedLegacy(dir, s, "petbox-old-name", `---\nname: petbox-old-name\n${PETBOX_MARKER_LINE}\n---\n\n# Old\n`),
    );

    const { writes, cleanups } = writeSkillFiles(dir, templatesRoot, "hellopet", "newpet", specs);

    assert.ok(writes.every((o) => o.kind === "written"), "the replacement must land on every surface");
    assert.equal(cleanups.length, SKILL_SURFACES.length, "one sweep per surface");
    for (const [i, surface] of SKILL_SURFACES.entries()) {
      const legacyPath = legacyPaths[i]!;
      assert.equal(existsSync(legacyPath), false, `orphaned ${legacyPath} must be gone`);
      assert.equal(
        existsSync(join(dir, ...surface, "petbox-old-name")),
        false,
        "the emptied legacy skill directory must not be left behind either",
      );
      assert.equal(existsSync(pathFor(dir, surface, "petbox-renamed")), true, "the new path must exist");
    }
    assert.ok(cleanups.every((c) => c.outcome === "removed" && c.removedDir));
  } finally {
    rmSync(dir, { recursive: true, force: true });
    rmSync(templatesRoot, { recursive: true, force: true });
  }
});

// The trap the work card names explicitly: this code DELETES files in a personal
// `.claude/skills`. Deletion is permitted for exactly one state — `petbox: managed`, the paths
// the kit was the only source of truth for. These two tests are the guard rails, and they are
// the reason the marker gate had to become value-exact in the provenance commit: under the old
// `^petbox:\s*\S+` pattern the declared-manual file below satisfied it and was UNLINKED.
test("writeSkillFiles: a FOREIGN file at the pre-rename path survives the sweep, untouched", () => {
  const dir = freshDir();
  const { templatesRoot, specs } = fixtureRegistry(["petbox-old-name"]);
  const surface = SKILL_SURFACES[0]!;
  try {
    const mine = "# my own notes\n\nnever generated by wire, no frontmatter at all\n";
    const legacyPath = seedLegacy(dir, surface, "petbox-old-name", mine);

    const { cleanups } = writeSkillFiles(dir, templatesRoot, "hellopet", "newpet", specs);

    assert.equal(existsSync(legacyPath), true, "a foreign file must NEVER be deleted by the sweep");
    assert.equal(readFileSync(legacyPath, "utf8"), mine, "and must be byte-for-byte untouched");
    assert.equal(cleanups.find((c) => c.path === legacyPath)!.outcome, "kept-foreign");
  } finally {
    rmSync(dir, { recursive: true, force: true });
    rmSync(templatesRoot, { recursive: true, force: true });
  }
});

test("writeSkillFiles: a file declared `petbox: manual` at the pre-rename path survives the sweep, untouched", () => {
  const dir = freshDir();
  const { templatesRoot, specs } = fixtureRegistry(["petbox-old-name"]);
  const surface = SKILL_SURFACES[0]!;
  try {
    const claimed = `---\nname: petbox-old-name\n${PETBOX_MANUAL_LINE}\n---\n\n# I took this path over\n`;
    const legacyPath = seedLegacy(dir, surface, "petbox-old-name", claimed);

    const { cleanups } = writeSkillFiles(dir, templatesRoot, "hellopet", "newpet", specs);

    assert.equal(existsSync(legacyPath), true, "a declared-manual file must NEVER be deleted by the sweep");
    assert.equal(readFileSync(legacyPath, "utf8"), claimed, "and must be byte-for-byte untouched");
    assert.equal(cleanups.find((c) => c.path === legacyPath)!.outcome, "kept-foreign");
  } finally {
    rmSync(dir, { recursive: true, force: true });
    rmSync(templatesRoot, { recursive: true, force: true });
  }
});

test("writeSkillFiles: a legacy directory holding anything the kit did not write survives as a directory", () => {
  const dir = freshDir();
  const { templatesRoot, specs } = fixtureRegistry(["petbox-old-name"]);
  const surface = SKILL_SURFACES[0]!;
  try {
    const legacyPath = seedLegacy(dir, surface, "petbox-old-name", `---\nname: x\n${PETBOX_MARKER_LINE}\n---\n\n# Old\n`);
    const companion = join(dir, ...surface, "petbox-old-name", "references", "notes.md");
    mkdirSync(dirname(companion), { recursive: true });
    writeFileSync(companion, "the owner's own reference material\n", "utf8");

    const { cleanups } = writeSkillFiles(dir, templatesRoot, "hellopet", "newpet", specs);

    assert.equal(existsSync(legacyPath), false, "our own SKILL.md at the old name still goes");
    assert.equal(existsSync(companion), true, "but nothing else in that directory is ours to remove");
    const cleanup = cleanups.find((c) => c.path === legacyPath)!;
    assert.equal(cleanup.outcome, "removed");
    assert.equal(cleanup.removedDir, false, "a non-empty legacy directory must be kept");
  } finally {
    rmSync(dir, { recursive: true, force: true });
    rmSync(templatesRoot, { recursive: true, force: true });
  }
});

// Same rule the agent-role rename cleanup in wire.ts follows: never orphan a skill by deleting
// the old copy when the replacement could not be written. If the sweep ran unconditionally, a
// project that had claimed the NEW path would end up with neither copy of the skill on disk.
test("writeSkillFiles: no sweep at all when the replacement did not land (blocked / declared-manual)", () => {
  for (const [label, newBody] of [
    ["blocked (foreign at the new path)", "# a real file of mine, no marker\n"],
    ["declared-manual (project owns the new path)", `---\nname: x\n${PETBOX_MANUAL_LINE}\n---\n\n# mine\n`],
  ] as const) {
    const dir = freshDir();
    const { templatesRoot, specs } = fixtureRegistry(["petbox-old-name"]);
    const surface = SKILL_SURFACES[0]!;
    try {
      const legacyPath = seedLegacy(dir, surface, "petbox-old-name", `---\nname: x\n${PETBOX_MARKER_LINE}\n---\n\n# Old\n`);
      const newPath = pathFor(dir, surface, "petbox-renamed");
      mkdirSync(dirname(newPath), { recursive: true });
      writeFileSync(newPath, newBody, "utf8");

      const { cleanups } = writeSkillFiles(dir, templatesRoot, "hellopet", "newpet", specs);

      assert.equal(
        existsSync(legacyPath),
        true,
        `${label}: the old copy must survive — deleting it would leave the project with no copy at all`,
      );
      assert.equal(cleanups.some((c) => c.path === legacyPath), false, `${label}: no sweep must have been attempted`);
    } finally {
      rmSync(dir, { recursive: true, force: true });
      rmSync(templatesRoot, { recursive: true, force: true });
    }
  }
});

test("writeSkillFiles: the real specs' legacyDirs sweep the pre-rename copies (petbox-skill-naming)", () => {
  const dir = freshDir();
  try {
    const legacyPaths: string[] = [];
    for (const surface of SKILL_SURFACES) {
      legacyPaths.push(
        seedLegacy(dir, surface, "analysis-workspace", `---\nname: analysis-workspace\n${PETBOX_MARKER_LINE}\n---\n\n# Old\n`),
        seedLegacy(dir, surface, "factory-run", `---\nname: factory-run\n${PETBOX_MARKER_LINE}\n---\n\n# Old\n`),
      );
    }

    const { cleanups } = writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");

    for (const legacyPath of legacyPaths) {
      assert.equal(existsSync(legacyPath), false, `orphaned ${legacyPath} must be gone`);
    }
    const removed = cleanups.filter((c) => c.outcome === "removed");
    assert.equal(removed.length, legacyPaths.length, "one removal per (legacy skill x surface)");
    assert.ok(
      removed.some((c) => c.path.includes("analysis-workspace")),
      "cleanup must name the analysis-workspace legacy dir it deleted",
    );
    assert.ok(
      removed.some((c) => c.path.includes("factory-run")),
      "cleanup must name the factory-run legacy dir it deleted",
    );
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- refill safety (work: wire-skill-refill-project-skills) ----------------------------------
//
// The card that grew PROJECT_SKILLS states the intent as "the kit becomes the only source of
// truth for managed paths — wipe the hand-made copies and keep only the kit's". That sentence is
// one careless step away from "the kit owns the whole skills directory", which would take the
// owner's `petbox-methodology-system` (repo-native, deliberately NEVER shipped: the kit carries
// the generic `petbox-methodology` pointer instead) and their personal integrations with it.
// The tests below pin the boundary against a tree shaped like the REAL $system checkout, not a
// synthetic one-file fixture: an apply may touch a PROJECT_SKILLS path and nothing else, ever.

/** Files under the skills roots that an apply must never write, delete, or reorder. */
function bystanderTree(dir: string): Record<string, string> {
  const seen: Record<string, string> = {};
  for (const surface of SKILL_SURFACES) {
    const root = join(dir, ...surface);
    if (!existsSync(root)) continue;
    const walk = (rel: string): void => {
      for (const entry of readdirSync(join(root, rel), { withFileTypes: true })) {
        const next = rel ? join(rel, entry.name) : entry.name;
        if (entry.isDirectory()) walk(next);
        else seen[join(...surface, next)] = readFileSync(join(root, next), "utf8");
      }
    };
    walk("");
  }
  return seen;
}

test("refill: an apply over a real-shaped tree touches ONLY PROJECT_SKILLS paths — a declared-manual skill and a foreign one survive byte-for-byte, and only the renamed skills' legacy copies are swept", () => {
  const dir = freshDir();
  try {
    // A repo-native skill the kit must never carry, declared manual — the live $system case.
    const methodologySystem = `---\nname: petbox-methodology-system\ndescription: >-\n  Operate PetBox's OWN project methodology. Use when creating or refining ideas on $system itself.\n${PETBOX_MANUAL_LINE}\n---\n\n# $system-specific operator detail the kit must never overwrite\n`;
    // The owner's personal integrations: no frontmatter marker at all — foreign, hands off.
    const droidHandoff = "# droid-handoff\n\nMY integration. Not part of any delivery.\n";
    const playwright = `---\nname: playwright-cli\ndescription: Automate browser interactions. Use for browser work.\n---\n\n# not ours\n`;
    // A multi-file skill the kit does NOT ship (see the SKILL.md-only limitation): its auxiliary
    // files are the ones with no legal place to carry a marker, so they must simply be left alone.
    const script = "#!/usr/bin/env bash\nset -euo pipefail\necho helper\n";
    const fixture = "// Intentionally incomplete fixture.\nexport const add = (a, b) => a + b;\n";

    const bystanders: Array<[string, string]> = [];
    for (const surface of SKILL_SURFACES) {
      bystanders.push(
        [join(dir, ...surface, "petbox-methodology-system", "SKILL.md"), methodologySystem],
        [join(dir, ...surface, "droid-handoff", "SKILL.md"), droidHandoff],
        [join(dir, ...surface, "playwright-cli", "SKILL.md"), playwright],
        [join(dir, ...surface, "multi-file-bystander", "scripts", "helper.sh"), script],
        [join(dir, ...surface, "multi-file-bystander", "self-test", "work", "calc.js"), fixture],
      );
    }
    for (const [path, body] of bystanders) {
      mkdirSync(dirname(path), { recursive: true });
      writeFileSync(path, body, "utf8");
    }
    const before = bystanderTree(dir);

    // Real pre-rename copies of the two renamed skills — owned (petbox: managed), so unlike the
    // bystanders above, THESE must be swept. Seeded after `before` so the survival check below
    // stays scoped to the true bystanders.
    const legacy: Array<[string, string]> = [];
    for (const surface of SKILL_SURFACES) {
      legacy.push(
        [
          join(dir, ...surface, "analysis-workspace", "SKILL.md"),
          `---\nname: analysis-workspace\n${PETBOX_MARKER_LINE}\n---\n\n# Old\n`,
        ],
        [
          join(dir, ...surface, "factory-run", "SKILL.md"),
          `---\nname: factory-run\n${PETBOX_MARKER_LINE}\n---\n\n# Old\n`,
        ],
      );
    }
    for (const [path, body] of legacy) {
      mkdirSync(dirname(path), { recursive: true });
      writeFileSync(path, body, "utf8");
    }

    // Two applies: the first is the one that sweeps the seeded legacy copies now that the
    // renamed skills' own delivery has landed; the second is the one that would expose a "now
    // that I own this, clean up" sweep that only triggers once the delivery is already in place
    // — asserting on it pins that a second apply finds nothing left to re-delete.
    const first = writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    const { writes, cleanups } = writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");

    const removed = first.cleanups.filter((c) => c.outcome === "removed");
    assert.equal(removed.length, legacy.length, "the first apply must sweep every seeded legacy copy");
    assert.ok(
      removed.some((c) => c.path.includes("analysis-workspace")),
      "cleanup must name the analysis-workspace legacy dir it deleted",
    );
    assert.ok(
      removed.some((c) => c.path.includes("factory-run")),
      "cleanup must name the factory-run legacy dir it deleted",
    );
    for (const [path] of legacy) {
      assert.equal(existsSync(path), false, `swept legacy copy must be gone: ${path}`);
    }
    assert.ok(
      cleanups.every((c) => c.outcome === "absent"),
      "the second apply finds nothing left to sweep — the first apply already deleted it",
    );
    const managedPaths = new Set(
      PROJECT_SKILLS.flatMap((spec) => SKILL_SURFACES.map((surface) => pathFor(dir, surface, spec.dir))),
    );
    for (const outcome of writes) {
      assert.ok(managedPaths.has(outcome.path), `apply wrote outside PROJECT_SKILLS: ${outcome.path}`);
    }
    for (const [path, body] of bystanders) {
      assert.ok(existsSync(path), `an apply deleted a file it does not own: ${path}`);
      assert.equal(readFileSync(path, "utf8"), body, `an apply rewrote a file it does not own: ${path}`);
    }
    // Nothing vanished anywhere under the roots, and every surviving bystander is unchanged.
    const after = bystanderTree(dir);
    for (const [rel, body] of Object.entries(before)) {
      assert.equal(after[rel], body, `bystander changed or disappeared after apply: ${rel}`);
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("refill: a declared-manual file AT a PROJECT_SKILLS path is skipped, not refilled — including the newly added entries", () => {
  const dir = freshDir();
  try {
    const mine: Record<string, string> = {};
    for (const spec of PROJECT_SKILLS) {
      const target = pathFor(dir, SKILL_SURFACES[0]!, spec.dir);
      const body = `---\nname: ${spec.dir}\ndescription: MY replacement for ${spec.dir}. Use never.\n${PETBOX_MANUAL_LINE}\n---\n\n# mine, hands off\n`;
      mkdirSync(dirname(target), { recursive: true });
      writeFileSync(target, body, "utf8");
      mine[target] = body;
    }

    const { writes } = writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");

    for (const [target, body] of Object.entries(mine)) {
      const outcome = writes.find((o) => o.path === target) as SkillWriteOutcome;
      assert.equal(outcome.kind, "declared-manual", `${target}: expected declared-manual, got ${outcome.kind}`);
      assert.equal(readFileSync(target, "utf8"), body, `${target}: a manual declaration must survive the refill`);
    }
    // The OTHER surface still received the whole delivery — a manual declaration is per path.
    for (const spec of PROJECT_SKILLS) {
      const other = pathFor(dir, SKILL_SURFACES[1]!, spec.dir);
      assert.ok(hasPetboxMarker(readFileSync(other, "utf8")), `${other}: the rest of the delivery must still land`);
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("refill: the owner's pre-existing hand-placed copy of a NEWLY shipped skill migrates in place, it does not come back BLOCKED", () => {
  const dir = freshDir();
  // What makes item 3 of the card land quietly: the owner already has a hand-placed copy of both
  // of these at the delivery path, carrying NEITHER declaration line. That is exactly the
  // migration carve-out's input, so the refill must PROMOTE it, not refuse it as foreign.
  //
  // Scope, stated so this is not over-read: the fixture is derived from the template via
  // legacyRender, so this pins the CARVE-OUT (both declaration lines stripped, checked before
  // writeArtifact's foreign guard) for the newly added entries — proven by mutation: drop the
  // digest line from stripMarkerLine and every path here comes back "blocked". It does NOT pin
  // "the template equals the owner's current file"; that comparison cannot be a test, because
  // the first successful apply rewrites the owner's file WITH the markers and would invert it.
  // Verbatim-ness is a property of how the templates were produced, enforced at authoring time.
  const added = ["petbox-analysis-workspace", "petbox-factory-run"];
  try {
    for (const specDir of added) {
      for (const surface of SKILL_SURFACES) {
        const target = pathFor(dir, surface, specDir);
        mkdirSync(dirname(target), { recursive: true });
        writeFileSync(target, legacyRender(specDir, "hellopet", "newpet"), "utf8");
      }
    }

    const { writes } = writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");

    for (const specDir of added) {
      for (const surface of SKILL_SURFACES) {
        const target = pathFor(dir, surface, specDir);
        const outcome = writes.find((o) => o.path === target) as SkillWriteOutcome;
        assert.equal(outcome.kind, "written", `${target}: expected a write, got ${JSON.stringify(outcome)}`);
        assert.equal(
          outcome.reason,
          "migrated",
          `${target}: an unmarked hand-placed copy must be PROMOTED, not blocked — if this says "new"/"own" the template body has drifted from the skill source`,
        );
        const body = readFileSync(target, "utf8");
        assert.ok(hasPetboxMarker(body), `${target}: the refilled copy must carry the origin marker`);
        assert.equal(readDigestMode(body), "manual", `${target}: these two ship out of the automatic digest`);
      }
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("digest: the automatic index carries exactly the four auto skills — agent-factory is no longer in it", () => {
  const dir = freshDir();
  try {
    writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    const index = buildAutoSkillsIndex(dir)!;
    const auto = PROJECT_SKILLS.filter((s) => s.digestMode === "auto").map((s) => s.dir);
    assert.deepEqual(auto.sort(), ["petbox", "petbox-methodology", "petbox-node-authoring", "petbox-write-economy"]);
    for (const name of auto) assert.match(index, new RegExp(`\`${name}\``), `${name} must be in the digest`);
    for (const name of ["petbox-agent-factory", "petbox-analysis-workspace", "petbox-factory-run", "petbox-card-check"]) {
      assert.doesNotMatch(
        index,
        new RegExp(`\`${name}\``),
        `${name} is petbox-digest: manual — it must cost no system-prompt room`,
      );
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- template-drift comparison (bugs: skill-files-clobber-and-apply-skips [item 3],
// builtin-definition-drifts-no-catchup [item 3]) -----------------------------------------------
// Moved here from status.test.ts along with checkSkillFile/formatSkillFile/buildSkillReports
// themselves: `status` and `doctor` share this ONE comparison, so its tests live next to it.

test("checkSkillFile: absent -> false; foreign -> false; ours+rendered unknown -> 'unknown'; ours+match/mismatch", () => {
  const dir = freshDir();
  try {
    const absent = join(dir, "a.md");
    assert.deepEqual(checkSkillFile(absent, "anything"), {
      path: absent,
      state: "absent",
      matchesTemplate: false,
    });

    const foreign = join(dir, "f.md");
    writeFileSync(foreign, "not a petbox file\n", "utf8");
    const foreignReport = checkSkillFile(foreign, "anything");
    assert.equal(foreignReport.state, "foreign");
    assert.equal(foreignReport.matchesTemplate, false);

    const ours = join(dir, "o.md");
    const rendered = "---\nname: petbox\npetbox: managed\n---\nbody\n";
    writeFileSync(ours, rendered, "utf8");
    assert.deepEqual(checkSkillFile(ours, undefined), {
      path: ours,
      state: "ours",
      matchesTemplate: "unknown",
    });
    assert.deepEqual(checkSkillFile(ours, rendered), {
      path: ours,
      state: "ours",
      matchesTemplate: true,
    });
    assert.deepEqual(checkSkillFile(ours, rendered + "drift"), {
      path: ours,
      state: "ours",
      matchesTemplate: false,
    });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("formatSkillFile: a foreign (BLOCKED) file reads distinctly from an owned file that DRIFTED from the template", () => {
  const dir = freshDir();
  try {
    const foreignPath = join(dir, "foreign.md");
    writeFileSync(foreignPath, "not a petbox file\n", "utf8");
    const foreignLine = formatSkillFile(checkSkillFile(foreignPath, "whatever the template renders"));
    assert.match(foreignLine, /BLOCKED/, "a real user file must read as BLOCKED, never as drift");
    assert.doesNotMatch(foreignLine, /DRIFTED/);

    const driftedPath = join(dir, "drifted.md");
    const rendered = "---\nname: petbox\npetbox: managed\n---\nbody\n";
    writeFileSync(driftedPath, rendered, "utf8");
    const driftedLine = formatSkillFile(checkSkillFile(driftedPath, rendered + "\nnew template line\n"));
    assert.match(driftedLine, /DRIFTED/, "an owned file whose content no longer matches the template must read as drifted");
    assert.match(driftedLine, /re-run apply\/wire/i, "the drifted remedy must be 're-run apply/wire', not 'sort it out yourself'");
    assert.doesNotMatch(driftedLine, /BLOCKED/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("buildSkillReports: one report per (PROJECT_SKILLS x SKILL_SURFACES); matches a freshly written tree", () => {
  const dir = freshDir();
  try {
    writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    const reports = buildSkillReports(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    assert.equal(reports.length, PROJECT_SKILLS.length * SKILL_SURFACES.length);
    for (const report of reports) {
      assert.equal(report.state, "ours");
      assert.equal(report.matchesTemplate, true, `expected ${report.path} to match its freshly written template`);
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("buildSkillReports: workspace undefined -> 'unknown' match only for the spec that needs {{WORKSPACE}}", () => {
  const dir = freshDir();
  try {
    writeSkillFiles(dir, TEMPLATES_ROOT, "hellopet", "newpet");
    const reports = buildSkillReports(dir, TEMPLATES_ROOT, "hellopet", undefined);
    for (const spec of PROJECT_SKILLS) {
      for (const surface of SKILL_SURFACES) {
        const report = reports.find((r) => r.path === pathFor(dir, surface, spec.dir))!;
        if (spec.needsWorkspace) {
          assert.equal(report.matchesTemplate, "unknown", `${spec.dir} needs {{WORKSPACE}}; an unresolved workspace must never claim a match/mismatch`);
        } else {
          assert.equal(report.matchesTemplate, true, `${spec.dir} does not need {{WORKSPACE}} and should still compare cleanly`);
        }
      }
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- probeWorkspace taxonomy (bug: probe-collapses-http-errors-into-network) -----------------
//
// Regression coverage for the defect itself: a server that ANSWERED with an error status (500,
// 404, and especially 503 — PetBox's own self-recovering "deploying" response) must never be
// classified the same as a genuine transport failure (fetch throwing / a connection refused),
// and the shared message helper must never call an HTTP response "network/timeout".

function startJsonServer(
  handler: (req: import("node:http").IncomingMessage, res: import("node:http").ServerResponse) => void,
): Promise<{ baseUrl: string; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const server = createServer(handler);
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address() as AddressInfo;
      resolve({
        baseUrl: `http://127.0.0.1:${port}`,
        close: () => new Promise((r) => server.close(() => r())),
      });
    });
  });
}

function expectHttpError(result: WorkspaceProbeResult): asserts result is Extract<WorkspaceProbeResult, { reason: "http-error" }> {
  assert.equal(result.ok, false);
  assert.equal((result as { reason: string }).reason, "http-error");
}

test("probeWorkspace: a genuine transport failure (connection refused) classifies as network, and ONLY that", async () => {
  // Start a server, grab its port, then close it — the port is now refused, reproducing a real
  // fetch-throws transport failure without relying on timing/timeout flakiness.
  const fake = await startJsonServer((_req, res) => res.end());
  const { baseUrl } = fake;
  await fake.close();

  const result = await probeWorkspace(baseUrl, "fake-key", 2000);
  assert.equal(result.ok, false);
  assert.equal((result as { reason: string }).reason, "network");
  const text = describeWorkspaceProbeFailure(result as Extract<WorkspaceProbeResult, { ok: false }>);
  assert.match(text, /could not reach/i, "a real transport failure must still say 'could not reach'");
});

test("probeWorkspace: HTTP 503 (deploy_in_progress) is NEVER classified as network — carries status + retryAfterSeconds, message is self-recovering", async () => {
  const fake = await startJsonServer((_req, res) => {
    res.writeHead(503, { "Content-Type": "application/json", "Retry-After": "60" });
    res.end(JSON.stringify({ error: "service_unavailable", reason: "deploy_in_progress", retryAfterSeconds: 60 }));
  });
  try {
    const result = await probeWorkspace(fake.baseUrl, "fake-key", 2000);
    assert.equal(result.ok, false);
    expectHttpError(result);
    assert.notEqual(result.reason, "network", "503 must never fall into the network bucket");
    assert.equal(result.status, 503);
    assert.equal(result.retryAfterSeconds, 60);

    const text = describeWorkspaceProbeFailure(result);
    assert.doesNotMatch(text, /could not reach/i, "a 503 must never be described as unreachable — the server DID answer");
    assert.doesNotMatch(text, /network\/timeout/i);
    assert.match(text, /deploying|self-recovering/i, "503 must be named as the self-recovering deploy state");
    assert.match(text, /60/, "the retry-after seconds must surface in the message");
  } finally {
    await fake.close();
  }
});

test("probeWorkspace: other HTTP error statuses (500, 404) are 'http-error' with the real code, never 'network'", async () => {
  for (const status of [500, 404]) {
    const fake = await startJsonServer((_req, res) => {
      res.writeHead(status, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: "boom" }));
    });
    try {
      const result = await probeWorkspace(fake.baseUrl, "fake-key", 2000);
      assert.equal(result.ok, false);
      expectHttpError(result);
      assert.notEqual(result.reason, "network", `HTTP ${status} must never be classified as network`);
      assert.equal(result.status, status);
      assert.equal(result.retryAfterSeconds, undefined, "no retryAfterSeconds in the body -> field absent");

      const text = describeWorkspaceProbeFailure(result);
      assert.doesNotMatch(text, /could not reach/i, `HTTP ${status} must not be described as unreachable`);
      assert.match(text, new RegExp(String(status)), "the message must name the actual status code");
    } finally {
      await fake.close();
    }
  }
});

test("probeWorkspace: 401/403 still classify as 'forbidden', distinct from 'http-error'", async () => {
  for (const status of [401, 403]) {
    const fake = await startJsonServer((_req, res) => {
      res.writeHead(status, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: "unauthorized" }));
    });
    try {
      const result = await probeWorkspace(fake.baseUrl, "fake-key", 2000);
      assert.equal(result.ok, false);
      assert.equal((result as { reason: string }).reason, "forbidden");
      const text = describeWorkspaceProbeFailure(result as Extract<WorkspaceProbeResult, { ok: false }>);
      assert.match(text, /401\/403/);
      assert.doesNotMatch(text, /could not reach/i);
    } finally {
      await fake.close();
    }
  }
});

test("probeWorkspace: 200 with unparseable JSON is 'parse-error', distinct from an HTTP error or a missing workspace field", async () => {
  const fake = await startJsonServer((_req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end("not actually json{{{");
  });
  try {
    const result = await probeWorkspace(fake.baseUrl, "fake-key", 2000);
    assert.equal(result.ok, false);
    assert.equal((result as { reason: string }).reason, "parse-error");
    const text = describeWorkspaceProbeFailure(result as Extract<WorkspaceProbeResult, { ok: false }>);
    assert.match(text, /did not parse as JSON/);
  } finally {
    await fake.close();
  }
});

test("probeWorkspace: 200 valid JSON with no workspace field stays 'no-workspace-field' (unaffected by this change)", async () => {
  const fake = await startJsonServer((_req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ ok: true }));
  });
  try {
    const result = await probeWorkspace(fake.baseUrl, "fake-key", 2000);
    assert.equal(result.ok, false);
    assert.equal((result as { reason: string }).reason, "no-workspace-field");
  } finally {
    await fake.close();
  }
});

// ---- opencode salience index (bug: opencode-skills-not-autoinjected) --------------------------

function writeSkillMd(skillsDir: string, name: string, body: string): void {
  const dir = join(skillsDir, name);
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, "SKILL.md"), body, "utf8");
}

test("extractSkillDescription: folded block scalar (description: >-) joins lines with single spaces", () => {
  const raw = "---\nname: petbox-foo\ndescription: >-\n  Pay for the change.\n  Use before writing a long body.\n---\n\n# Foo\n";
  assert.equal(extractSkillDescription(raw), "Pay for the change. Use before writing a long body.");
});

test("extractSkillDescription: single-line scalar (no >-)", () => {
  const raw = "---\nname: petbox-foo\ndescription: Pay for the change. Use before writing a long body.\n---\n\n# Foo\n";
  assert.equal(extractSkillDescription(raw), "Pay for the change. Use before writing a long body.");
});

test("extractSkillDescription: no frontmatter, or frontmatter with no description — null", () => {
  assert.equal(extractSkillDescription("# Foo\n\nNo frontmatter.\n"), null);
  assert.equal(extractSkillDescription("---\nname: petbox-foo\n---\n\n# Foo\n"), null);
});

test("extractSkillTrigger: picks the sentence starting 'Use', not the first sentence", () => {
  assert.equal(
    extractSkillTrigger("Pay for the change, not the whole text. Use before any long write. Covers more detail."),
    "Use before any long write.",
  );
});

test("extractSkillTrigger: no 'Use' sentence — falls back to the first sentence, never empty", () => {
  assert.equal(extractSkillTrigger("Does a thing. Does another thing."), "Does a thing.");
});

test("readAutoDigestSkillTriggers: no .claude/skills directory at all — [] (wire apply never ran)", () => {
  const root = freshDir();
  try {
    assert.deepEqual(readAutoDigestSkillTriggers(root), []);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// THE selection rule this card changes (spec: wire-skill-invocation-mode). Every fixture here is
// chosen so the DIRECTORY NAME points the opposite way from the declaration: if selection still
// went by the `petbox-` prefix, this test fails on three of the four dirs at once.
test("readAutoDigestSkillTriggers: selects by the `petbox-digest: auto` DECLARATION, never by the directory name", () => {
  const root = freshDir();
  try {
    const skillsDir = join(root, ".claude", "skills");
    // Declared auto, NOT petbox-named — must be IN (the prefix rule would have dropped it).
    writeSkillMd(
      skillsDir,
      "write-economy",
      `---\nname: write-economy\ndescription: >-\n  Pay for the change. Use before any long write.\n${PETBOX_DIGEST_KEY}: auto\n---\n\n# Write economy\n`,
    );
    // Declared manual, petbox-named — must be OUT (the prefix rule would have kept it).
    writeSkillMd(
      skillsDir,
      "petbox-factory-run",
      `---\nname: petbox-factory-run\ndescription: Use for factory runs.\n${PETBOX_DIGEST_KEY}: manual\n---\n\n# Factory run\n`,
    );
    // Undeclared, petbox-named — must be OUT. This is the live case named in the card:
    // `petbox-methodology-system` is prefixed but repo-native, and the prefix rule injected it
    // into every session.
    writeSkillMd(
      skillsDir,
      "petbox-methodology-system",
      "---\nname: petbox-methodology-system\ndescription: Use when operating $system's methodology.\n---\n\n# Repo-native\n",
    );
    // Declared auto, petbox-named — must be IN.
    writeSkillMd(
      skillsDir,
      "petbox-agent-factory",
      `---\nname: petbox-agent-factory\ndescription: Compile artifacts. Use after role changes.\n${PETBOX_DIGEST_KEY}: auto\n---\n\n# Agent factory\n`,
    );
    // Declared auto but no description — nothing sensible to show, skipped.
    writeSkillMd(skillsDir, "petbox-no-description", `---\nname: petbox-no-description\n${PETBOX_DIGEST_KEY}: auto\n---\n\n# No description\n`);
    // A dir with no SKILL.md inside (mid-write, or a stray folder) — skipped, not an error for
    // the whole read.
    mkdirSync(join(skillsDir, "petbox-empty"), { recursive: true });

    const triggers = readAutoDigestSkillTriggers(root);
    assert.deepEqual(
      triggers.map((t) => t.name),
      ["petbox-agent-factory", "write-economy"], // sorted; every other fixture excluded
    );
    assert.equal(triggers.find((t) => t.name === "write-economy")!.trigger, "Use before any long write.");
    assert.equal(triggers.find((t) => t.name === "petbox-agent-factory")!.trigger, "Use after role changes.");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// A project may declare a delivered skill's path its own AND still want it in its digest — the
// two axes are independent (provenance = may the kit write here; mode = should the agent be told
// about it). This repo's own `petbox-methodology-system` is exactly that combination.
test("readAutoDigestSkillTriggers: provenance and invocation mode are independent axes", () => {
  const root = freshDir();
  try {
    const skillsDir = join(root, ".claude", "skills");
    writeSkillMd(
      skillsDir,
      "mine-but-auto",
      `---\nname: mine-but-auto\ndescription: Use when the project says so.\n${PETBOX_MANUAL_LINE}\n${PETBOX_DIGEST_KEY}: auto\n---\n\n# Mine\n`,
    );
    writeSkillMd(
      skillsDir,
      "kit-but-manual",
      `---\nname: kit-but-manual\ndescription: Use only when explicitly called.\n${PETBOX_MARKER_LINE}\n${PETBOX_DIGEST_KEY}: manual\n---\n\n# Kit's\n`,
    );
    assert.deepEqual(
      readAutoDigestSkillTriggers(root).map((t) => t.name),
      ["mine-but-auto"],
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// A skill that HAS a description but doesn't follow the "Use ..." house convention is not
// enforced at write time (no wire/lint gate requires it) — this pins what the reader-facing
// consequence is: NOT a silent drop from the index (that would be the exact salience defect this
// card fixes, just relocated), but a weaker trigger (the description's first sentence, which
// reads as "what this is" rather than "when to call it"). A description that is present but
// blank IS excluded — there is nothing sensible to show, so omitting beats injecting an empty
// line.
test("readAutoDigestSkillTriggers: a description with no 'Use' sentence is still included (first-sentence fallback), never silently dropped; a blank description IS excluded", () => {
  const root = freshDir();
  try {
    const skillsDir = join(root, ".claude", "skills");
    writeSkillMd(
      skillsDir,
      "petbox-no-use-sentence",
      `---\nname: petbox-no-use-sentence\ndescription: Does a thing. Covers detail with no trigger sentence.\n${PETBOX_DIGEST_KEY}: auto\n---\n\n# No Use sentence\n`,
    );
    writeSkillMd(
      skillsDir,
      "petbox-blank-description",
      `---\nname: petbox-blank-description\ndescription:\n${PETBOX_DIGEST_KEY}: auto\n---\n\n# Blank description\n`,
    );

    const triggers = readAutoDigestSkillTriggers(root);
    assert.deepEqual(triggers.map((t) => t.name), ["petbox-no-use-sentence"]); // blank one excluded
    assert.equal(triggers[0]!.trigger, "Does a thing.", "falls back to the first sentence, not dropped");

    const index = buildAutoSkillsIndex(root)!;
    assert.match(index, /Does a thing\. → `petbox-no-use-sentence`/, "the fallback trigger must actually reach the injected index, not just the pure function");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("buildAutoSkillsIndex: null when no skill declares auto invocation", () => {
  const root = freshDir();
  try {
    assert.equal(buildAutoSkillsIndex(root), null);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// THE regression this card (opencode-skills-not-autoinjected) is about: before this fix,
// opencode-plugin.ts had no equivalent of this block at all — the agent only ever saw skill
// NAMES (available_skills) and had to decide, unprompted, to call `skill` for the right one.
// This pins what closes that gap: one line per discovered petbox-* skill naming ITS OWN trigger
// condition and the exact name to call — never the skill's body (that stays behind the native,
// unmodified `skill` tool — see skill-files.ts's module comment on why full-body injection was
// rejected: ~47.5KB across six skills, every session, duplicating a mechanism that already
// exists on all three harnesses).
test("buildAutoSkillsIndex: one line per discovered skill, trigger + exact name, no body content", () => {
  const root = freshDir();
  try {
    const skillsDir = join(root, ".claude", "skills");
    writeSkillMd(
      skillsDir,
      "petbox-node-authoring",
      `---\nname: petbox-node-authoring\ndescription: >-\n  How to structure a node body. Use before writing any node/comment body longer than a couple of lines.\n${PETBOX_DIGEST_KEY}: auto\n---\n\n# Body authoring\n\nFull instructions the index must NOT contain.\n`,
    );
    writeSkillMd(
      skillsDir,
      "petbox-write-economy",
      `---\nname: petbox-write-economy\ndescription: >-\n  Pay for the change. Use before any tasks_upsert call whose body is more than a few lines.\n${PETBOX_DIGEST_KEY}: auto\n---\n\n# Write economy\n\nFull instructions the index must NOT contain.\n`,
    );

    const index = buildAutoSkillsIndex(root)!;
    assert.match(index, /^## PetBox skills — call `skill\(name\)` on match, don't browse first$/m);
    assert.match(
      index,
      /Use before writing any node\/comment body longer than a couple of lines\. → `petbox-node-authoring`/,
    );
    assert.match(
      index,
      /Use before any tasks_upsert call whose body is more than a few lines\. → `petbox-write-economy`/,
    );
    assert.doesNotMatch(index, /Full instructions the index must NOT contain/, "the skill BODY must never be inlined");
    // Compact by construction: the whole index must stay far smaller than either full body would
    // be alone — the property the earlier (rejected) full-body approach violated.
    assert.ok(
      Buffer.byteLength(index, "utf8") < 700,
      `index unexpectedly large: ${Buffer.byteLength(index, "utf8")} bytes`,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// Regression guard tied to the REAL, currently-shipped descriptions (templates/ + this repo's
// own petbox-methodology-system) rather than synthetic fixtures only — every one of them must
// still parse to a non-empty "Use ..." trigger, proving the house convention this module relies
// on actually holds for the full current skill set, not just the two hand-picked examples above.
test("extractSkillTrigger against every REAL current petbox-* skill description yields a non-empty 'Use ...' trigger", () => {
  const specs = [...PROJECT_SKILLS.map((s) => s.dir), "petbox-methodology-system"];
  for (const dir of specs) {
    const path =
      dir === "petbox-methodology-system"
        ? join(HERE, "..", "..", "..", "..", ".claude", "skills", dir, "SKILL.md")
        : join(TEMPLATES_ROOT, dir, "SKILL.md");
    let raw: string;
    try {
      raw = readFileSync(path, "utf8");
    } catch {
      continue; // petbox-methodology-system may not be present in every checkout layout — skip, don't fail
    }
    const description = extractSkillDescription(raw);
    assert.ok(description, `${dir}: description must be parseable from frontmatter`);
    const trigger = extractSkillTrigger(description!);
    assert.match(trigger, /^Use\b/, `${dir}: expected a "Use ..." trigger sentence, got: "${trigger}"`);
  }
});

