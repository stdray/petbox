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
  readPetboxSkillTriggers,
  SKILL_SURFACES,
  renderSkillTemplate,
  shouldInjectOnce,
  writeSkillFiles,
  type SkillWriteOutcome,
  type WorkspaceProbeResult,
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

test("readPetboxSkillTriggers: no .claude/skills directory at all — [] (wire apply never ran)", () => {
  const root = freshDir();
  try {
    assert.deepEqual(readPetboxSkillTriggers(root), []);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("readPetboxSkillTriggers: only petbox*-prefixed dirs, sorted; non-petbox, bodyless, and description-less dirs skipped", () => {
  const root = freshDir();
  try {
    const skillsDir = join(root, ".claude", "skills");
    writeSkillMd(
      skillsDir,
      "petbox-write-economy",
      "---\nname: petbox-write-economy\ndescription: >-\n  Pay for the change. Use before any long write.\n---\n\n# Write economy\n",
    );
    writeSkillMd(
      skillsDir,
      "petbox-agent-factory",
      "---\nname: petbox-agent-factory\ndescription: Compile artifacts. Use after role changes.\n---\n\n# Agent factory\n",
    );
    writeSkillMd(skillsDir, "factory-run", "---\nname: factory-run\ndescription: Use for factory runs.\n---\n\n# Factory run — not petbox, must be excluded\n");
    writeSkillMd(skillsDir, "petbox-no-description", "---\nname: petbox-no-description\n---\n\n# No description field\n");
    // A petbox*-named dir with no SKILL.md inside (e.g. mid-write, or a stray folder) — skipped,
    // not an error for the whole read.
    mkdirSync(join(skillsDir, "petbox-empty"), { recursive: true });

    const triggers = readPetboxSkillTriggers(root);
    assert.deepEqual(
      triggers.map((t) => t.name),
      ["petbox-agent-factory", "petbox-write-economy"], // sorted; factory-run, petbox-no-description, petbox-empty absent
    );
    assert.equal(triggers.find((t) => t.name === "petbox-write-economy")!.trigger, "Use before any long write.");
    assert.equal(triggers.find((t) => t.name === "petbox-agent-factory")!.trigger, "Use after role changes.");
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
test("readPetboxSkillTriggers: a description with no 'Use' sentence is still included (first-sentence fallback), never silently dropped; a blank description IS excluded", () => {
  const root = freshDir();
  try {
    const skillsDir = join(root, ".claude", "skills");
    writeSkillMd(
      skillsDir,
      "petbox-no-use-sentence",
      "---\nname: petbox-no-use-sentence\ndescription: Does a thing. Covers detail with no trigger sentence.\n---\n\n# No Use sentence\n",
    );
    writeSkillMd(skillsDir, "petbox-blank-description", "---\nname: petbox-blank-description\ndescription:\n---\n\n# Blank description\n");

    const triggers = readPetboxSkillTriggers(root);
    assert.deepEqual(triggers.map((t) => t.name), ["petbox-no-use-sentence"]); // blank one excluded
    assert.equal(triggers[0]!.trigger, "Does a thing.", "falls back to the first sentence, not dropped");

    const index = buildAutoSkillsIndex(root)!;
    assert.match(index, /Does a thing\. → `petbox-no-use-sentence`/, "the fallback trigger must actually reach the injected index, not just the pure function");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("buildAutoSkillsIndex: null when no petbox skills are materialized", () => {
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
      "---\nname: petbox-node-authoring\ndescription: >-\n  How to structure a node body. Use before writing any node/comment body longer than a couple of lines.\n---\n\n# Body authoring\n\nFull instructions the index must NOT contain.\n",
    );
    writeSkillMd(
      skillsDir,
      "petbox-write-economy",
      "---\nname: petbox-write-economy\ndescription: >-\n  Pay for the change. Use before any tasks_upsert call whose body is more than a few lines.\n---\n\n# Write economy\n\nFull instructions the index must NOT contain.\n",
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

test("shouldInjectOnce: true the first time per sessionID, false thereafter for that id, true again for a different id", () => {
  const seen = new Set<string>();
  assert.equal(shouldInjectOnce(seen, "sess-1"), true);
  assert.equal(shouldInjectOnce(seen, "sess-1"), false);
  assert.equal(shouldInjectOnce(seen, "sess-1"), false);
  assert.equal(shouldInjectOnce(seen, "sess-2"), true);
});

test("shouldInjectOnce: undefined sessionID always injects (never silently drop content for a missing id)", () => {
  const seen = new Set<string>();
  assert.equal(shouldInjectOnce(seen, undefined), true);
  assert.equal(shouldInjectOnce(seen, undefined), true);
});
