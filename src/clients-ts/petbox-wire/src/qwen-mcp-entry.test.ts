// Unit tests for qwen-mcp-entry.ts — the shared `mcpServers.petbox` entry shape wire.ts writes
// at BOTH user scope ($QWEN_HOME/settings.json, installGlobalHooks) and workspace scope
// (<project>/.qwen/settings.json, writeProjectFiles). Regression coverage for two live-smoke
// defects (task wire-support-codex-qwen):
//   - qwen-dead-default-model's sibling — qwen-mcp-json-shadows-workspace-entry: the entry must
//     carry an UNRESOLVED `${VAR}` placeholder (never a literal key), because a literal key here
//     is exactly what the project's OWN `.mcp.json` gets wrong (that file's loader never resolves
//     env vars at all — see wire.ts's writeProjectFiles comment on the workspace-scope write).
//   - the `reserve` role binds a non-DeepSeek model (`openai:qwen3.8-max`), so `alwaysLoadTools`
//     must be true or qwen defers every mcp__petbox__* tool to tool_search for that role.
//
// Run: node --test src/qwen-mcp-entry.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { buildQwenMcpServerEntry } from "./qwen-mcp-entry.ts";

test("buildQwenMcpServerEntry: httpUrl is baseUrl + /mcp (streamable HTTP transport)", () => {
  const entry = buildQwenMcpServerEntry("https://petbox.3po.su", "PETBOX_SMOKE_API_KEY");
  assert.equal(entry.httpUrl, "https://petbox.3po.su/mcp");
});

test("buildQwenMcpServerEntry: header carries an UNRESOLVED ${VAR} placeholder, never a literal key", () => {
  const entry = buildQwenMcpServerEntry("https://petbox.3po.su", "PETBOX_SMOKE_API_KEY");
  assert.equal(entry.headers["X-Api-Key"], "${PETBOX_SMOKE_API_KEY}");
  assert.doesNotMatch(entry.headers["X-Api-Key"], /^yb_key_/, "must never embed a real key literal");
});

test("buildQwenMcpServerEntry: trust=true and alwaysLoadTools=true, every call — no model-dependent branching", () => {
  const entry = buildQwenMcpServerEntry("https://petbox.3po.su", "PETBOX_SMOKE_API_KEY");
  assert.equal(entry.trust, true);
  assert.equal(entry.alwaysLoadTools, true, "reserve binds a non-DeepSeek model — without this it never sees the tools eagerly");
  assert.equal(entry.timeout, 30000);
});

test("buildQwenMcpServerEntry: the env var name is substituted verbatim, whatever the project key derives", () => {
  const entry = buildQwenMcpServerEntry("https://petbox.3po.su", "PETBOX_MY_OTHER_PROJECT_API_KEY");
  assert.equal(entry.headers["X-Api-Key"], "${PETBOX_MY_OTHER_PROJECT_API_KEY}");
});

test("buildQwenMcpServerEntry: user-scope and workspace-scope callers get byte-identical shapes for the same inputs (single source of truth)", () => {
  const a = buildQwenMcpServerEntry("https://petbox.3po.su", "PETBOX_SMOKE_API_KEY");
  const b = buildQwenMcpServerEntry("https://petbox.3po.su", "PETBOX_SMOKE_API_KEY");
  assert.deepEqual(a, b);
});
