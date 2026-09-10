// opencode-plugin.ts's half of commit a5618dbe's UnresolvedEnvRefError contract — split out from
// hooks-unresolved-env-ref.test.ts because this hook is loaded IN-PROCESS (opencode calls
// `PetboxPlugin({client, directory})` itself; there is no separate CLI file to spawn), the same
// reason opencode-plugin-system-transform.test.ts drives it by importing PetboxPlugin directly
// instead of via child_process.
//
// Contract for this hook specifically (see opencode-plugin.ts's own comment at the top of
// PetboxPlugin): resolveProject() is called ONCE at plugin load. On UnresolvedEnvRefError it
// logs to stderr immediately and remembers a "⚠ <message>" note; that note is then pushed into
// `output.system` on EVERY `experimental.chat.system.transform` call — the one channel this
// plugin has back into the model, since plugin load itself has no session to talk to yet. Both
// hooks (`system.transform` and the `session.idle` push) still no-op exactly as before
// (`resolved` stays null) for every OTHER error, unchanged.
//
// Run: node --test src/hooks-unresolved-env-ref-opencode.test.ts

import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { PetboxPlugin } from "./opencode-plugin.ts";

function setUpUnresolvedRefRegistry(): { home: string; projectDir: string; envVar: string; refVar: string; project: string } {
  const home = mkdtempSync(join(tmpdir(), "petbox-oc-envref-home-"));
  const projectDir = mkdtempSync(join(tmpdir(), "petbox-oc-envref-proj-"));
  const envVar = "FAKE_OC_UNRESOLVED_ENVREF_TEST_KEY";
  const refVar = "FAKE_OC_UNRESOLVED_ENVREF_TEST_TARGET"; // deliberately never set
  const project = "fake-oc-unresolved-project";
  mkdirSync(join(home, ".petbox"), { recursive: true });
  writeFileSync(
    join(home, ".petbox", "projects.json"),
    JSON.stringify({ entries: [{ prefix: projectDir, project, envVar, baseUrl: "http://127.0.0.1:1" }] }),
    "utf8",
  );
  writeFileSync(join(home, ".petbox", "keys.json"), JSON.stringify({ [envVar]: `$${refVar}` }), "utf8");
  return { home, projectDir, envVar, refVar, project };
}

async function systemPromptForOneRequest(hooks: any, sessionID: string): Promise<string> {
  const output = { system: ["<<BASE PROMPT>>"] };
  await hooks["experimental.chat.system.transform"]({ sessionID, model: {} }, output);
  return output.system.join("\n");
}

test("opencode-plugin: an unresolved keys.json $VAR reference logs to stderr at load AND reaches the system prompt of every request (naming the var + project)", async () => {
  const { home, projectDir, envVar, refVar, project } = setUpUnresolvedRefRegistry();
  const prevHome = process.env["HOME"];
  const prevUserProfile = process.env["USERPROFILE"];
  process.env["HOME"] = home;
  process.env["USERPROFILE"] = home;
  delete process.env[envVar];
  delete process.env[refVar];

  const originalConsoleError = console.error;
  const errorCalls: string[] = [];
  console.error = (...args: unknown[]) => {
    errorCalls.push(args.map(String).join(" "));
  };
  try {
    const hooks: any = await PetboxPlugin({ client: {} as any, directory: projectDir } as any);

    assert.ok(errorCalls.length > 0, "PetboxPlugin must console.error the note at load time");
    assert.ok(errorCalls.some((c) => c.includes(envVar)), `stderr must name the env var "${envVar}". Got: ${JSON.stringify(errorCalls)}`);
    assert.ok(errorCalls.some((c) => c.includes(project)), `stderr must name the project "${project}". Got: ${JSON.stringify(errorCalls)}`);

    // opencode's own first request of a session is the small-model title generation, sharing the
    // session's id with the real chat request — the note must reach BOTH, since system.transform
    // rebuilds output.system from scratch per request (same fact opencode-plugin-system-
    // transform.test.ts's header documents for the skills index).
    const titleRequest = await systemPromptForOneRequest(hooks, "ses_envref_test");
    const chatRequest = await systemPromptForOneRequest(hooks, "ses_envref_test");

    for (const [label, prompt] of [
      ["title", titleRequest],
      ["chat", chatRequest],
    ] as const) {
      assert.ok(prompt.includes("⚠"), `${label} request: system prompt must carry the ⚠ note. Got: ${prompt}`);
      assert.ok(prompt.includes(envVar), `${label} request: system prompt must name the env var "${envVar}". Got: ${prompt}`);
      assert.ok(prompt.includes(refVar), `${label} request: system prompt must name the referenced var "${refVar}". Got: ${prompt}`);
      assert.ok(prompt.includes(project), `${label} request: system prompt must name the project "${project}". Got: ${prompt}`);
    }
  } finally {
    console.error = originalConsoleError;
    if (prevHome === undefined) delete process.env["HOME"];
    else process.env["HOME"] = prevHome;
    if (prevUserProfile === undefined) delete process.env["USERPROFILE"];
    else process.env["USERPROFILE"] = prevUserProfile;
    rmSync(home, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

// Regression guard, same rationale as hooks-unresolved-env-ref.test.ts's "ORDINARY unregistered
// project" tests: an unregistered directory is the pre-existing, UNCHANGED silent case — both
// hooks no-op, nothing pushed to the system prompt, nothing on stderr.
test("opencode-plugin: an ORDINARY unregistered project stays a silent no-op (unaffected by the UnresolvedEnvRefError special-case)", async () => {
  const home = mkdtempSync(join(tmpdir(), "petbox-oc-envref-noreg-home-"));
  const projectDir = mkdtempSync(join(tmpdir(), "petbox-oc-envref-noreg-proj-"));
  const prevHome = process.env["HOME"];
  const prevUserProfile = process.env["USERPROFILE"];
  process.env["HOME"] = home;
  process.env["USERPROFILE"] = home;

  const originalConsoleError = console.error;
  const errorCalls: string[] = [];
  console.error = (...args: unknown[]) => {
    errorCalls.push(args.map(String).join(" "));
  };
  try {
    // No ~/.petbox/projects.json at all — never wired.
    const hooks: any = await PetboxPlugin({ client: {} as any, directory: projectDir } as any);
    assert.equal(errorCalls.length, 0, `must not console.error for an ordinary unregistered project. Got: ${JSON.stringify(errorCalls)}`);

    const prompt = await systemPromptForOneRequest(hooks, "ses_noreg_test");
    assert.ok(!prompt.includes("⚠"), `must not push any ⚠ note for an ordinary unregistered project. Got: ${prompt}`);
  } finally {
    console.error = originalConsoleError;
    if (prevHome === undefined) delete process.env["HOME"];
    else process.env["HOME"] = prevHome;
    if (prevUserProfile === undefined) delete process.env["USERPROFILE"];
    else process.env["USERPROFILE"] = prevUserProfile;
    rmSync(home, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
