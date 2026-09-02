// Regression test for the LIVE delivery of the petbox-* salience index through opencode's
// `experimental.chat.system.transform` hook (bug: opencode-skills-not-autoinjected, second
// round — the first fix shipped green tests and a feature that never reached the model).
//
// WHY THIS TEST EXISTS AND WHY THE OLD ONES DID NOT CATCH THE BUG
// ---------------------------------------------------------------
// The first fix gated the index behind a per-session "inject once" set, on the assumption that
// the block "stays in the model's context from then on". That assumption is FALSE for opencode:
// it rebuilds `output.system` FROM SCRATCH for every request. Measured live (opencode 1.18.25,
// instrumented plugin, one `opencode run`):
//
//   call 1  model=deepseek-v4-flash  sessionID=ses_fadd…  system.length BEFORE the hook = 1
//   call 2  model=deepseek-v4-pro    sessionID=ses_fadd…  system.length BEFORE the hook = 1
//
// Two facts in those two lines: (a) the array is empty of our previous push each time, so a
// once-per-session gate delivers the block to exactly ONE request and no other; (b) the FIRST
// request of a session is the small-model title generation, which shares the session's id — so
// the gate burned there and the real chat request got nothing. The agent then honestly answered
// STRING NOT FOUND for the index while quoting the canon block (pushed unconditionally) fine.
//
// The old tests exercised the gate helper in isolation and asserted that the second call returns
// false — they encoded the broken behaviour AS the contract, and nothing at all exercised the
// hook. So this test drives the REAL hook, twice, the way opencode drives it: same sessionID, a
// FRESH output.system each time. It fails on the pre-fix plugin and passes on the fixed one.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { PetboxPlugin } from "./opencode-plugin.ts";

const SKILL_BODY_SENTINEL = "BODY-SENTINEL-must-never-be-inlined-into-the-system-prompt";
const SKILL_TRIGGER = "Use when demonstrating the salience index.";

// A registered project rooted at a temp dir, with one materialized petbox-* skill. baseUrl points
// at a closed port so the plugin's best-effort canon / agent-definition fetches fail FAST and
// degrade (both are explicitly best-effort), keeping this test offline and quick.
function setupProject(): { home: string; projectDir: string; envVar: string } {
  const home = mkdtempSync(join(tmpdir(), "petbox-oc-home-"));
  const projectDir = mkdtempSync(join(tmpdir(), "petbox-oc-proj-"));
  const envVar = "PETBOX_OPENCODE_TRANSFORM_TEST_KEY";

  mkdirSync(join(home, ".petbox"), { recursive: true });
  writeFileSync(
    join(home, ".petbox", "projects.json"),
    JSON.stringify({
      entries: [{ prefix: projectDir, project: "transform-test", envVar, baseUrl: "http://127.0.0.1:1" }],
    }),
    "utf8",
  );

  const skillDir = join(projectDir, ".claude", "skills", "petbox-demo");
  mkdirSync(skillDir, { recursive: true });
  writeFileSync(
    join(skillDir, "SKILL.md"),
    // `petbox-digest: auto` is what puts a skill in the index — the directory name does not
    // (spec: wire-skill-invocation-mode).
    ["---", "name: petbox-demo", `description: A demo skill. ${SKILL_TRIGGER}`, "petbox-digest: auto", "---", "", SKILL_BODY_SENTINEL, ""].join(
      "\n",
    ),
    "utf8",
  );

  return { home, projectDir, envVar };
}

// Drive the hook exactly as LLMRequestPrep.prepare does: a fresh one-element system array per
// request (the base prompt), the plugin appends to it, the request is sent. Returns what the
// model would actually receive for that ONE request.
async function systemPromptForOneRequest(hooks: any, sessionID: string): Promise<string> {
  const output = { system: ["<<BASE PROMPT>>"] };
  await hooks["experimental.chat.system.transform"]({ sessionID, model: {} }, output);
  return output.system.join("\n");
}

test("opencode system.transform: the skills index reaches EVERY request of a session, not just the first", async () => {
  const { home, projectDir, envVar } = setupProject();
  const prevHome = process.env["HOME"];
  const prevUserProfile = process.env["USERPROFILE"];
  process.env["HOME"] = home;
  process.env["USERPROFILE"] = home;
  process.env[envVar] = "test-key";
  try {
    const hooks: any = await PetboxPlugin({ client: {} as any, directory: projectDir } as any);

    // opencode's own first request of a session is the small-model title generation; it carries
    // the SAME sessionID as the real chat request that follows it.
    const sessionID = "ses_transform_test";
    const titleRequest = await systemPromptForOneRequest(hooks, sessionID);
    const chatRequest = await systemPromptForOneRequest(hooks, sessionID);

    // THE regression: before the fix this assertion failed — the index landed only in the first
    // request (the title generation) and the real chat request never saw it.
    assert.ok(
      chatRequest.includes("## PetBox skills"),
      "the skills index must be present in the SECOND request of a session (opencode rebuilds the system prompt per request)",
    );
    assert.ok(
      chatRequest.includes("`petbox-demo`"),
      "the index must name the skill to call in the second request too",
    );
    assert.ok(chatRequest.includes(SKILL_TRIGGER), "the index must carry the skill's trigger sentence");

    // Sanity: it was there for the first request as well — the fix must not swap one hole for another.
    assert.ok(titleRequest.includes("## PetBox skills"), "the skills index must be present in the first request too");
  } finally {
    if (prevHome === undefined) delete process.env["HOME"];
    else process.env["HOME"] = prevHome;
    if (prevUserProfile === undefined) delete process.env["USERPROFILE"];
    else process.env["USERPROFILE"] = prevUserProfile;
    delete process.env[envVar];
  }
});

test("opencode system.transform: the index stays an index — skill BODIES are never inlined", async () => {
  const { home, projectDir, envVar } = setupProject();
  const prevHome = process.env["HOME"];
  const prevUserProfile = process.env["USERPROFILE"];
  process.env["HOME"] = home;
  process.env["USERPROFILE"] = home;
  process.env[envVar] = "test-key";
  try {
    const hooks: any = await PetboxPlugin({ client: {} as any, directory: projectDir } as any);
    const prompt = await systemPromptForOneRequest(hooks, "ses_body_test");

    assert.ok(prompt.includes("## PetBox skills"), "precondition: the index is injected");
    assert.ok(
      !prompt.includes(SKILL_BODY_SENTINEL),
      "the skill BODY must never be inlined — it stays behind opencode's lazy `skill` tool",
    );
  } finally {
    if (prevHome === undefined) delete process.env["HOME"];
    else process.env["HOME"] = prevHome;
    if (prevUserProfile === undefined) delete process.env["USERPROFILE"];
    else process.env["USERPROFILE"] = prevUserProfile;
    delete process.env[envVar];
  }
});
