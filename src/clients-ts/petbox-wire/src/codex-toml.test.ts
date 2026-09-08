// Tests for codex-toml.ts — the minimal, section-preserving TOML reader/writer wire.ts uses to
// merge into codex's config.toml. See that file's own header for what it deliberately does NOT
// do (no full AST, no array-of-tables) and why (never regenerate a user's whole config.toml —
// codex-spec.md's "merge, never clobber, any config.toml/hooks.json that already exists").
//
// Run: node --test src/codex-toml.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import {
  buildDeepseekProviderBlock,
  buildMcpServerBlock,
  buildOpencodeGoProviderBlock,
  extractOpencodeSessionUuid,
  findBlock,
  getRootScalar,
  parseTomlBlocks,
  serializeTomlBlocks,
  setRootScalar,
  tomlString,
  upsertBlock,
} from "./codex-toml.ts";

// ---- round-trip / parse -------------------------------------------------------------------------

test("parseTomlBlocks/serializeTomlBlocks: empty text round-trips to empty", () => {
  assert.equal(serializeTomlBlocks(parseTomlBlocks("")), "");
});

test("parseTomlBlocks: root-only content (no headers) stays entirely in the root block", () => {
  const text = 'instructions = "hi"\nmodel = "x"\n';
  const blocks = parseTomlBlocks(text);
  assert.equal(blocks.length, 1);
  assert.equal(blocks[0]!.header, null);
  assert.equal(getRootScalar(blocks, "instructions"), '"hi"');
});

test("parseTomlBlocks: a user's own sections and content survive byte-for-byte through a no-op round-trip", () => {
  const text = [
    "# a user comment",
    'instructions = "be nice"',
    "",
    "[sandbox]",
    'mode = "workspace-write"',
    "",
    "[profiles.default]",
    'model = "gpt-5"',
    "",
  ].join("\n");
  const blocks = parseTomlBlocks(text);
  assert.equal(serializeTomlBlocks(blocks), text.replace(/\n+$/, "\n"));
  assert.ok(findBlock(blocks, "sandbox"));
  assert.ok(findBlock(blocks, "profiles.default"));
});

test("parseTomlBlocks: a `[` inside a triple-quoted multi-line string is NOT mistaken for a header", () => {
  const text = ['instructions = """', "look: [not.a.header]", 'still text"""', "", "[real_section]", "x = 1", ""].join(
    "\n",
  );
  const blocks = parseTomlBlocks(text);
  // Only ONE real header block was found; the bracketed line inside the triple-quoted string
  // stayed inside the root block's lines.
  assert.equal(blocks.filter((b) => b.header !== null).length, 1);
  assert.equal(blocks[0]!.lines.some((l) => l.includes("look: [not.a.header]")), true);
  assert.ok(findBlock(blocks, "real_section"));
});

// ---- setRootScalar / getRootScalar ---------------------------------------------------------------

test("setRootScalar: appends a new key when absent, preserving existing root lines", () => {
  const blocks = parseTomlBlocks('a = "1"\n');
  const next = setRootScalar(blocks, "b", tomlString("2"));
  assert.equal(getRootScalar(next, "a"), '"1"');
  assert.equal(getRootScalar(next, "b"), '"2"');
});

test("setRootScalar: replaces an existing key in place rather than duplicating it", () => {
  const blocks = parseTomlBlocks('model_provider = "old"\nother = "x"\n');
  const next = setRootScalar(blocks, "model_provider", tomlString("opencode-go"));
  assert.equal(getRootScalar(next, "model_provider"), '"opencode-go"');
  const occurrences = serializeTomlBlocks(next).split("model_provider").length - 1;
  assert.equal(occurrences, 1, "must not duplicate the key");
});

test("setRootScalar: does not touch a same-named key that is commented out", () => {
  const blocks = parseTomlBlocks('# model = "commented"\n');
  const next = setRootScalar(blocks, "model", tomlString("real"));
  assert.equal(getRootScalar(next, "model"), '"real"');
  assert.match(serializeTomlBlocks(next), /# model = "commented"/);
});

// ---- upsertBlock / findBlock ----------------------------------------------------------------------

test("upsertBlock: appends a brand-new section with a blank separator line before it", () => {
  const blocks = parseTomlBlocks('a = "1"\n');
  const next = upsertBlock(blocks, "mcp_servers.petbox", ['url = "https://x/mcp"']);
  const text = serializeTomlBlocks(next);
  assert.match(text, /a = "1"\n\n\[mcp_servers\.petbox\]\nurl = "https:\/\/x\/mcp"\n/);
});

test("upsertBlock: replaces an existing section WHOLESALE, not merged field-by-field", () => {
  const blocks = parseTomlBlocks('[mcp_servers.petbox]\nurl = "old"\nstale_field = "gone"\n');
  const next = upsertBlock(blocks, "mcp_servers.petbox", ['url = "new"']);
  const text = serializeTomlBlocks(next);
  assert.match(text, /url = "new"/);
  assert.doesNotMatch(text, /stale_field/);
});

test("upsertBlock: leaves every OTHER section untouched", () => {
  const blocks = parseTomlBlocks('[sandbox]\nmode = "workspace-write"\n\n[mcp_servers.petbox]\nurl = "old"\n');
  const next = upsertBlock(blocks, "mcp_servers.petbox", ['url = "new"']);
  const text = serializeTomlBlocks(next);
  assert.match(text, /\[sandbox\]\nmode = "workspace-write"/);
});

// ---- opencode-go session UUID preservation --------------------------------------------------------

test("extractOpencodeSessionUuid: reads the UUID back out of a rendered provider block", () => {
  const lines = buildOpencodeGoProviderBlock("11111111-2222-3333-4444-555555555555");
  assert.equal(extractOpencodeSessionUuid(lines), "11111111-2222-3333-4444-555555555555");
});

test("extractOpencodeSessionUuid: undefined when the block has no such line", () => {
  assert.equal(extractOpencodeSessionUuid(buildDeepseekProviderBlock()), undefined);
});

test("re-running upsertBlock with a freshly-read UUID preserves it (the install-time idempotency wire.ts relies on)", () => {
  const first = upsertBlock(parseTomlBlocks(""), "model_providers.opencode-go", buildOpencodeGoProviderBlock("uuid-1"));
  // Simulate a second `wire`/`apply` run: read the existing block back, extract its UUID, and
  // regenerate with that SAME UUID (wire.ts's installGlobalHooks does exactly this).
  const existing = findBlock(first, "model_providers.opencode-go")!;
  const preserved = extractOpencodeSessionUuid(existing.lines)!;
  assert.equal(preserved, "uuid-1");
  const second = upsertBlock(first, "model_providers.opencode-go", buildOpencodeGoProviderBlock(preserved));
  assert.equal(extractOpencodeSessionUuid(findBlock(second, "model_providers.opencode-go")!.lines), "uuid-1");
});

// ---- tomlString escaping ----------------------------------------------------------------------------

test("tomlString: escapes backslashes and double-quotes (Windows paths, command strings)", () => {
  assert.equal(tomlString('C:\\Users\\me\\hooks.json'), '"C:\\\\Users\\\\me\\\\hooks.json"');
  assert.equal(tomlString('say "hi"'), '"say \\"hi\\""');
});

// ---- buildMcpServerBlock shape (codex-spec.md §2's VERIFIED round-trip) -----------------------------

test("buildMcpServerBlock: matches the verified shape (url, env_http_headers as header->envvar, timeouts, enabled)", () => {
  const lines = buildMcpServerBlock("https://petbox.3po.su", "PETBOX_PROJ_API_KEY");
  const text = lines.join("\n");
  assert.match(text, /url = "https:\/\/petbox\.3po\.su\/mcp"/);
  assert.match(text, /env_http_headers = \{ "X-Api-Key" = "PETBOX_PROJ_API_KEY" \}/);
  assert.match(text, /startup_timeout_sec = 30/);
  assert.match(text, /tool_timeout_sec = 120/);
  assert.match(text, /enabled = true/);
  // codex-spec.md §2: DO NOT write `type = "..."` — untagged transport, url alone means http.
  assert.doesNotMatch(text, /^type =/m);
});
