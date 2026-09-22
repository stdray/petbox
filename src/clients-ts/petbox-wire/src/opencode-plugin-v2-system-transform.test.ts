// V2 counterpart of opencode-plugin-system-transform.test.ts and
// hooks-unresolved-env-ref-opencode.test.ts (card opencode-v2-plugin-api-port). Same regression
// scenarios, driven through the V2 entry point (`petboxPluginV2Setup`, registered via
// `ctx.session.hook("context", ...)`) instead of the V1 one (`PetboxPlugin`, returning
// `experimental.chat.system.transform`) — proving the port did not silently drop any of the
// system-prompt content the V1 hook carried. The V1 tests are untouched (see opencode-plugin.ts's
// module comment: `PetboxPlugin` itself was not rewritten, only wrapped).
//
// A fourth test below exercises the OTHER hook this plugin owns — push-session — against v2's
// `SessionMessageInfo` shape (flat `.text` on user messages, `.content[].text` on assistant
// messages), which is structurally different from v1's `{info, parts}` and has no prior test
// coverage in this file's v1 counterpart (opencode-plugin.ts's v1 pushSession was never unit-
// tested in isolation either — this closes that gap for the new code, not a pre-existing one).
//
// Run: node --test src/opencode-plugin-v2-system-transform.test.ts

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import http from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { petboxPluginV2Setup } from "./opencode-plugin.ts";

const SKILL_BODY_SENTINEL = "BODY-SENTINEL-must-never-be-inlined-into-the-system-prompt";
const SKILL_TRIGGER = "Use when demonstrating the salience index.";

function setupProject(): { home: string; projectDir: string; envVar: string } {
  const home = mkdtempSync(join(tmpdir(), "petbox-oc-v2-home-"));
  const projectDir = mkdtempSync(join(tmpdir(), "petbox-oc-v2-proj-"));
  const envVar = "PETBOX_OPENCODE_V2_TRANSFORM_TEST_KEY";

  mkdirSync(join(home, ".petbox"), { recursive: true });
  writeFileSync(
    join(home, ".petbox", "projects.json"),
    JSON.stringify({
      entries: [{ prefix: projectDir, project: "transform-v2-test", envVar, baseUrl: "http://127.0.0.1:1" }],
    }),
    "utf8",
  );

  const skillDir = join(projectDir, ".claude", "skills", "petbox-demo");
  mkdirSync(skillDir, { recursive: true });
  writeFileSync(
    join(skillDir, "SKILL.md"),
    ["---", "name: petbox-demo", `description: A demo skill. ${SKILL_TRIGGER}`, "petbox-digest: auto", "---", "", SKILL_BODY_SENTINEL, ""].join(
      "\n",
    ),
    "utf8",
  );

  return { home, projectDir, envVar };
}

// A minimal V2 Context mock — only the fields petboxPluginV2Setup actually touches:
// `location.directory`, `session.hook`/`session.context`, `event.subscribe`. Structural, like
// every `as any` PluginInput mock the V1 tests already use.
function mockContext(directory: string, opts?: { sessionContextEntries?: unknown[] }): {
  ctx: any;
  contextHooks: Array<(event: { system: Array<{ type: "text"; text: string }> }) => Promise<void> | void>;
} {
  const contextHooks: Array<(event: { system: Array<{ type: "text"; text: string }> }) => Promise<void> | void> = [];
  const ctx = {
    location: { directory },
    session: {
      hook: async (name: string, cb: (event: any) => Promise<void> | void) => {
        if (name === "context") contextHooks.push(cb);
        return { dispose: async () => {} };
      },
      context: async () => opts?.sessionContextEntries ?? [],
    },
    event: {
      // No session.idle traffic by default — the system-prompt tests below never await this.
      subscribe: async function* () {},
    },
  };
  return { ctx, contextHooks };
}

// Drive the "context" hook exactly as the real opencode agent-loop would for ONE model request:
// a fresh `event.system` array, the plugin's registered callback appends `{type:"text", text}`
// entries onto it.
async function systemPromptForOneRequest(
  contextHooks: Array<(event: { system: Array<{ type: "text"; text: string }> }) => Promise<void> | void>,
): Promise<string> {
  const event = { system: [{ type: "text" as const, text: "<<BASE PROMPT>>" }] };
  for (const hook of contextHooks) await hook(event);
  return event.system.map((p) => p.text).join("\n");
}

test("opencode v2 context hook: the skills index reaches the request, same content as the v1 hook", async () => {
  const { home, projectDir, envVar } = setupProject();
  const prevHome = process.env["HOME"];
  const prevUserProfile = process.env["USERPROFILE"];
  process.env["HOME"] = home;
  process.env["USERPROFILE"] = home;
  process.env[envVar] = "test-key";
  try {
    const { ctx, contextHooks } = mockContext(projectDir);
    await petboxPluginV2Setup(ctx);
    assert.equal(contextHooks.length, 1, "petboxPluginV2Setup must register exactly one \"context\" hook");

    const prompt = await systemPromptForOneRequest(contextHooks);
    assert.ok(prompt.includes("## PetBox skills"), "the skills index must be present in the v2 context-hook request");
    assert.ok(prompt.includes("`petbox-demo`"), "the index must name the skill to call");
    assert.ok(prompt.includes(SKILL_TRIGGER), "the index must carry the skill's trigger sentence");
  } finally {
    if (prevHome === undefined) delete process.env["HOME"];
    else process.env["HOME"] = prevHome;
    if (prevUserProfile === undefined) delete process.env["USERPROFILE"];
    else process.env["USERPROFILE"] = prevUserProfile;
    delete process.env[envVar];
  }
});

test("opencode v2 context hook: the owner-only-skills block reaches the request, with opencode's OWN fact", async () => {
  const { home, projectDir, envVar } = setupProject();
  const skillDir = join(projectDir, ".claude", "skills", "petbox-factory-run");
  mkdirSync(skillDir, { recursive: true });
  writeFileSync(
    join(skillDir, "SKILL.md"),
    [
      "---",
      "name: petbox-factory-run",
      "description: Fan tasks out to workers. Use for an unattended multi-task pass.",
      "disable-model-invocation: true",
      "---",
      "",
      "# Factory run",
      "",
    ].join("\n"),
    "utf8",
  );
  const prevHome = process.env["HOME"];
  const prevUserProfile = process.env["USERPROFILE"];
  process.env["HOME"] = home;
  process.env["USERPROFILE"] = home;
  process.env[envVar] = "test-key";
  try {
    const { ctx, contextHooks } = mockContext(projectDir);
    await petboxPluginV2Setup(ctx);
    const prompt = await systemPromptForOneRequest(contextHooks);

    assert.ok(prompt.includes("`petbox-factory-run`"), "the owner-only block must name the skill");
    assert.ok(
      prompt.includes("does not recognize the key"),
      "opencode must get its OWN fact (the flag does nothing here), not the Claude-Code one",
    );
    assert.ok(
      !prompt.includes("removes these from your own listing entirely"),
      "opencode must NEVER receive the Claude-Code/Droid 'hidden entirely' claim",
    );
  } finally {
    if (prevHome === undefined) delete process.env["HOME"];
    else process.env["HOME"] = prevHome;
    if (prevUserProfile === undefined) delete process.env["USERPROFILE"];
    else process.env["USERPROFILE"] = prevUserProfile;
    delete process.env[envVar];
  }
});

test("opencode v2 context hook: an unresolved keys.json $VAR reference reaches the request (naming the var + project)", async () => {
  const home = mkdtempSync(join(tmpdir(), "petbox-oc-v2-envref-home-"));
  const projectDir = mkdtempSync(join(tmpdir(), "petbox-oc-v2-envref-proj-"));
  const envVar = "FAKE_OC_V2_UNRESOLVED_ENVREF_TEST_KEY";
  const refVar = "FAKE_OC_V2_UNRESOLVED_ENVREF_TEST_TARGET"; // deliberately never set
  const project = "fake-oc-v2-unresolved-project";
  mkdirSync(join(home, ".petbox"), { recursive: true });
  writeFileSync(
    join(home, ".petbox", "projects.json"),
    JSON.stringify({ entries: [{ prefix: projectDir, project, envVar, baseUrl: "http://127.0.0.1:1" }] }),
    "utf8",
  );
  writeFileSync(join(home, ".petbox", "keys.json"), JSON.stringify({ [envVar]: `$${refVar}` }), "utf8");

  const prevHome = process.env["HOME"];
  const prevUserProfile = process.env["USERPROFILE"];
  process.env["HOME"] = home;
  process.env["USERPROFILE"] = home;
  delete process.env[envVar];
  delete process.env[refVar];
  try {
    const { ctx, contextHooks } = mockContext(projectDir);
    await petboxPluginV2Setup(ctx);
    const prompt = await systemPromptForOneRequest(contextHooks);

    assert.ok(prompt.includes("⚠"), `system prompt must carry the ⚠ note. Got: ${prompt}`);
    assert.ok(prompt.includes(envVar), `system prompt must name the env var "${envVar}". Got: ${prompt}`);
    assert.ok(prompt.includes(refVar), `system prompt must name the referenced var "${refVar}". Got: ${prompt}`);
    assert.ok(prompt.includes(project), `system prompt must name the project "${project}". Got: ${prompt}`);
  } finally {
    if (prevHome === undefined) delete process.env["HOME"];
    else process.env["HOME"] = prevHome;
    if (prevUserProfile === undefined) delete process.env["USERPROFILE"];
    else process.env["USERPROFILE"] = prevUserProfile;
  }
});

function startFakeServer(
  handler: (req: http.IncomingMessage, res: http.ServerResponse) => void,
): Promise<{ baseUrl: string; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const server = http.createServer(handler);
    server.listen(0, "127.0.0.1", () => {
      const port = (server.address() as { port: number }).port;
      resolve({
        baseUrl: `http://127.0.0.1:${port}`,
        close: () => new Promise((r) => server.close(() => r())),
      });
    });
  });
}

test("opencode v2 push-session: session.execution.succeeded (NOT session.idle) triggers the push; extracts user .text and assistant .content[].text", async () => {
  const home = mkdtempSync(join(tmpdir(), "petbox-oc-v2-push-home-"));
  const projectDir = mkdtempSync(join(tmpdir(), "petbox-oc-v2-push-proj-"));
  const envVar = "FAKE_OC_V2_PUSH_TEST_KEY";
  const project = "fake-oc-v2-push-project";

  let receivedBody = "";
  const { baseUrl, close } = await startFakeServer((req, res) => {
    if (req.url?.includes("/append")) {
      const chunks: Buffer[] = [];
      req.on("data", (c) => chunks.push(c));
      req.on("end", () => {
        receivedBody = Buffer.concat(chunks).toString("utf8");
        res.writeHead(200, { "Content-Type": "application/json" }).end(JSON.stringify({ lastOrdinal: 2, appended: 2 }));
      });
      return;
    }
    res.writeHead(500).end();
  });

  mkdirSync(join(home, ".petbox"), { recursive: true });
  writeFileSync(
    join(home, ".petbox", "projects.json"),
    JSON.stringify({ entries: [{ prefix: projectDir, project, envVar, baseUrl }] }),
    "utf8",
  );

  const prevHome = process.env["HOME"];
  const prevUserProfile = process.env["USERPROFILE"];
  process.env["HOME"] = home;
  process.env["USERPROFILE"] = home;
  process.env[envVar] = "test-key";
  try {
    // v2 SessionMessageInfo entries: a "user" message (flat .text) and an "assistant" message
    // (.content[] with a text part plus a reasoning part that must NOT reach the transcript).
    const entries = [
      { type: "user", id: "m1", time: { created: 1 }, text: "hi there" },
      {
        type: "assistant",
        id: "m2",
        time: { created: 2 },
        agent: "build",
        model: { providerID: "acme", modelID: "x" },
        content: [
          { type: "reasoning", text: "thinking..." },
          { type: "text", text: "hello back" },
        ],
      },
      // A lifecycle row that carries no conversation text — must be skipped, not throw.
      { type: "session.idle" as unknown as "system", id: "m3", time: { created: 3 } },
    ];
    const { ctx } = mockContext(projectDir, { sessionContextEntries: entries });
    // Regression for the live finding (card opencode-v2-plugin-api-port, Do §1): a plain
    // "session.idle" event — the v1 hook name, still a valid v2 event TYPE — must be IGNORED,
    // not treated as a turn-completion signal. Measured live against opencode 2.0.12 (`opencode
    // serve` + a real deepseek-v4-flash turn): the per-turn event sequence never emits
    // "session.idle" at all; the actual completion signal is "session.execution.succeeded" (also
    // "failed"/"interrupted", same {data:{sessionID}} shape). This test yields BOTH — session.idle
    // first (must be a no-op) — so a regression back to filtering on "session.idle" alone would
    // fail this test on the FIRST assertion (no /append request received) rather than silently
    // never firing on the real opencode wire, the way the mistake shipped invisibly before.
    ctx.event.subscribe = async function* () {
      yield { type: "session.idle", data: { sessionID: "ses_v2_push_test" } };
      yield { type: "session.execution.succeeded", data: { sessionID: "ses_v2_push_test" } };
    };

    await petboxPluginV2Setup(ctx);
    // The subscription loop is a fire-and-forget `void (async () => {...})()` inside setup — give
    // it a tick to run past both yielded events and complete pushSession.
    await new Promise((r) => setTimeout(r, 100));

    assert.ok(receivedBody.length > 0, "the fake server must have received an /append request");
    const lines = receivedBody.trim().split("\n").map((l) => JSON.parse(l));
    assert.deepEqual(
      lines.map((m: { role: string; content: string }) => ({ role: m.role, content: m.content })),
      [
        { role: "user", content: "hi there" },
        { role: "assistant", content: "hello back" },
      ],
      "user .text and assistant .content[type=text].text must reach the server, reasoning content excluded",
    );
  } finally {
    await close();
    if (prevHome === undefined) delete process.env["HOME"];
    else process.env["HOME"] = prevHome;
    if (prevUserProfile === undefined) delete process.env["USERPROFILE"];
    else process.env["USERPROFILE"] = prevUserProfile;
    delete process.env[envVar];
  }
});

test("opencode v2 push-session: a lone session.idle event, with no execution.* event, pushes NOTHING", async () => {
  const home = mkdtempSync(join(tmpdir(), "petbox-oc-v2-push2-home-"));
  const projectDir = mkdtempSync(join(tmpdir(), "petbox-oc-v2-push2-proj-"));
  const envVar = "FAKE_OC_V2_PUSH2_TEST_KEY";
  const project = "fake-oc-v2-push2-project";

  let appendRequests = 0;
  const { baseUrl, close } = await startFakeServer((req, res) => {
    if (req.url?.includes("/append")) appendRequests++;
    res.writeHead(200, { "Content-Type": "application/json" }).end(JSON.stringify({ lastOrdinal: 1, appended: 1 }));
  });

  mkdirSync(join(home, ".petbox"), { recursive: true });
  writeFileSync(
    join(home, ".petbox", "projects.json"),
    JSON.stringify({ entries: [{ prefix: projectDir, project, envVar, baseUrl }] }),
    "utf8",
  );

  const prevHome = process.env["HOME"];
  const prevUserProfile = process.env["USERPROFILE"];
  process.env["HOME"] = home;
  process.env["USERPROFILE"] = home;
  process.env[envVar] = "test-key";
  try {
    const { ctx } = mockContext(projectDir, {
      sessionContextEntries: [{ type: "user", id: "m1", time: { created: 1 }, text: "hi" }],
    });
    ctx.event.subscribe = async function* () {
      yield { type: "session.idle", data: { sessionID: "ses_v2_idle_only_test" } };
    };

    await petboxPluginV2Setup(ctx);
    await new Promise((r) => setTimeout(r, 100));

    assert.equal(appendRequests, 0, "session.idle alone must never reach /append — v2's turn-completion signal is session.execution.*");
  } finally {
    await close();
    if (prevHome === undefined) delete process.env["HOME"];
    else process.env["HOME"] = prevHome;
    if (prevUserProfile === undefined) delete process.env["USERPROFILE"];
    else process.env["USERPROFILE"] = prevUserProfile;
    delete process.env[envVar];
  }
});
