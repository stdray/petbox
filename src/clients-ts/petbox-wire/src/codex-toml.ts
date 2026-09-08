// Minimal, section-preserving TOML reader/writer for petbox-wire's codex config.toml merges.
//
// WHY NOT A DEPENDENCY (brief: wire-support-codex-qwen). The package has no TOML dependency
// today, and codex's config.toml is a file petbox-wire must MERGE, never clobber (spec:
// codex-spec.md — "Merge, never clobber, any config.toml/hooks.json that already exists"): a
// user's own `[profiles.*]`, sandbox policy, `instructions`, etc. must survive every re-run of
// `wire`/`apply` untouched. Pulling a full TOML parser only to re-serialize the WHOLE document
// (losing comments/formatting the user wrote) would violate that as surely as clobbering it
// outright. So this module only ever touches ITS OWN sections/keys and passes everything else
// through byte-for-byte as opaque lines — the same "merge, never regenerate whole" discipline
// wire.ts already applies to droid's `.factory/mcp.json` (mergeMcpServer) and Claude Code's
// settings.json hooks, just for TOML's syntax instead of JSON's.
//
// MODEL: a document is a sequence of BLOCKS — the "root" block (header: null, everything before
// the first `[table]` header) followed by one block per `[table.path]` header encountered, each
// holding that header's own lines verbatim until the next header. This is deliberately NOT a
// full TOML AST: it cannot parse or validate arbitrary TOML, it only knows how to (a) find a
// block by its exact header path string, (b) replace/insert one wholesale, and (c) set/read a
// single `key = value` scalar line in the root block. That is everything petbox-wire's own
// codex integration needs to write (model_providers.*, model_provider, model,
// model_catalog_json, hooks.state.*, mcp_servers.petbox) — every one of those is either a
// petbox-wire-OWNED whole section (regenerated wholesale each run, exactly like droid's
// `mcpServers.petbox` entry) or a top-level scalar this kit alone sets.
//
// KNOWN SIMPLIFICATION: header detection is line-based (`^\[...\]$`, no `[[array-of-tables]]`
// support — codex's own config never needs one for what this kit writes) and tracks
// triple-quoted multi-line strings only by counting `'''`/`"""` occurrences per line, to avoid
// mistaking a `[` inside a user's own multi-line string value for a header. This does not
// implement inline-table or arbitrary nesting parsing beyond that — it is a deliberately narrow
// tool for a narrow, well-tested job, not a general TOML library.
//
// Plain TS for native node type-stripping: zero deps.

export type TomlBlock = { readonly header: string | null; readonly lines: readonly string[] };

function isHeaderLine(trimmed: string): string | null {
  const m = /^\[([^[\]]+)\]\s*(#.*)?$/.exec(trimmed);
  return m ? m[1]!.trim() : null;
}

function countOccurrences(line: string, marker: string): number {
  let count = 0;
  let idx = 0;
  for (;;) {
    idx = line.indexOf(marker, idx);
    if (idx < 0) break;
    count++;
    idx += marker.length;
  }
  return count;
}

/** Parse `text` into blocks. Always returns at least the root block (header: null). */
export function parseTomlBlocks(text: string): TomlBlock[] {
  if (text.length === 0) return [{ header: null, lines: [] }];
  const rawLines = text.replace(/\r\n/g, "\n").split("\n");
  const blocks: { header: string | null; lines: string[] }[] = [{ header: null, lines: [] }];
  let inTriple: '"""' | "'''" | null = null;
  for (const line of rawLines) {
    if (!inTriple) {
      const header = isHeaderLine(line.trim());
      if (header !== null) {
        blocks.push({ header, lines: [] });
        continue;
      }
    }
    blocks[blocks.length - 1]!.lines.push(line);
    for (const marker of ['"""', "'''"] as const) {
      if (inTriple === marker) {
        if (countOccurrences(line, marker) % 2 === 1) inTriple = null;
      } else if (!inTriple && countOccurrences(line, marker) % 2 === 1) {
        inTriple = marker;
      }
    }
  }
  return blocks;
}

/** Serialize blocks back to TOML text, single trailing newline, no trailing blank lines. */
export function serializeTomlBlocks(blocks: readonly TomlBlock[]): string {
  const parts: string[] = [];
  for (const b of blocks) {
    if (b.header !== null) parts.push(`[${b.header}]`);
    parts.push(...b.lines);
  }
  while (parts.length > 0 && parts[parts.length - 1] === "") parts.pop();
  return parts.length > 0 ? parts.join("\n") + "\n" : "";
}

function escapeForRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/** Read a top-level `key = value` scalar from the root block (first non-comment match wins). */
export function getRootScalar(blocks: readonly TomlBlock[], key: string): string | undefined {
  const root = blocks[0];
  if (!root) return undefined;
  const re = new RegExp(`^\\s*${escapeForRegExp(key)}\\s*=\\s*(.+?)\\s*$`);
  for (const line of root.lines) {
    if (line.trim().startsWith("#")) continue;
    const m = re.exec(line);
    if (m) return m[1];
  }
  return undefined;
}

/**
 * Set (replace, or append if absent) a top-level `key = valueLiteral` line in the root block.
 * `valueLiteral` is the RAW TOML value text (already quoted if it needs to be) — see tomlString.
 */
export function setRootScalar(blocks: readonly TomlBlock[], key: string, valueLiteral: string): TomlBlock[] {
  const next = blocks.map((b) => ({ header: b.header, lines: [...b.lines] }));
  const root = next[0]!;
  const re = new RegExp(`^\\s*${escapeForRegExp(key)}\\s*=`);
  const idx = root.lines.findIndex((l) => !l.trim().startsWith("#") && re.test(l));
  const line = `${key} = ${valueLiteral}`;
  if (idx >= 0) root.lines[idx] = line;
  else root.lines.push(line);
  return next;
}

/** The block whose header is exactly `header` (dotted path, e.g. `model_providers.deepseek`). */
export function findBlock(blocks: readonly TomlBlock[], header: string): TomlBlock | undefined {
  return blocks.find((b) => b.header === header);
}

/** Read a top-level `key = value` scalar from the NAMED block (first non-comment match wins). */
export function getBlockScalar(blocks: readonly TomlBlock[], header: string, key: string): string | undefined {
  const block = findBlock(blocks, header);
  if (!block) return undefined;
  const re = new RegExp(`^\\s*${escapeForRegExp(key)}\\s*=\\s*(.+?)\\s*$`);
  for (const line of block.lines) {
    if (line.trim().startsWith("#")) continue;
    const m = re.exec(line);
    if (m) return m[1];
  }
  return undefined;
}

/**
 * Set (replace, or append if absent) a `key = valueLiteral` scalar line inside the NAMED block —
 * creating the block (with a blank separator line, same convention as upsertBlock) if it does
 * not exist yet. Unlike upsertBlock, this NEVER touches any other line already in the block: it
 * is for a block that may carry fields this kit does not own (e.g. `[projects.'<path>']`, whose
 * `ProjectConfig` struct petbox-wire only ever sets ONE field of — `trust_level` — never the
 * whole table, so a future codex config field on the same table survives untouched).
 */
export function setBlockScalar(
  blocks: readonly TomlBlock[],
  header: string,
  key: string,
  valueLiteral: string,
): TomlBlock[] {
  const next = blocks.map((b) => ({ header: b.header, lines: [...b.lines] }));
  let idx = next.findIndex((b) => b.header === header);
  if (idx < 0) {
    const last = next[next.length - 1];
    if (last && last.lines.length > 0 && last.lines[last.lines.length - 1] !== "") {
      last.lines.push("");
    }
    next.push({ header, lines: [] });
    idx = next.length - 1;
  }
  const block = next[idx]!;
  const re = new RegExp(`^\\s*${escapeForRegExp(key)}\\s*=`);
  const lineIdx = block.lines.findIndex((l) => !l.trim().startsWith("#") && re.test(l));
  const line = `${key} = ${valueLiteral}`;
  if (lineIdx >= 0) block.lines[lineIdx] = line;
  else block.lines.push(line);
  return next;
}

/**
 * Replace the block at `header` wholesale (or append a new one if absent), with a blank
 * separator line before a newly-appended section. Used for sections THIS KIT OWNS entirely
 * (model_providers.deepseek/opencode-go, hooks.state.*, mcp_servers.petbox) — regenerated each
 * run, exactly like droid's `mergeMcpServer` always overwrites its one `mcpServers.petbox` key.
 */
export function upsertBlock(blocks: readonly TomlBlock[], header: string, bodyLines: readonly string[]): TomlBlock[] {
  const next = blocks.map((b) => ({ header: b.header, lines: [...b.lines] }));
  const idx = next.findIndex((b) => b.header === header);
  if (idx >= 0) {
    next[idx] = { header, lines: [...bodyLines] };
    return next;
  }
  const last = next[next.length - 1];
  if (last && last.lines.length > 0 && last.lines[last.lines.length - 1] !== "") {
    last.lines.push("");
  }
  next.push({ header, lines: [...bodyLines] });
  return next;
}

/** TOML basic-string quoting: backslash, double-quote, and control chars escaped. */
export function tomlString(s: string): string {
  let out = '"';
  for (const ch of s) {
    const code = ch.codePointAt(0)!;
    if (ch === '"') out += '\\"';
    else if (ch === "\\") out += "\\\\";
    else if (ch === "\n") out += "\\n";
    else if (ch === "\r") out += "\\r";
    else if (ch === "\t") out += "\\t";
    else if (code < 0x20) out += "\\u" + code.toString(16).padStart(4, "0");
    else out += ch;
  }
  return out + '"';
}

/**
 * TOML literal-string quoting (single quotes): every byte between the quotes is taken LITERALLY
 * — no escape processing at all, backslash included. This is the ONLY safe way to write a
 * Windows path as a TOML string value (or, here, a dotted-key table-header segment): a basic
 * (double-quoted) string treats `\U`/`\u`/`\x`/etc as escape-sequence introducers, and codex's
 * own parser aborts loading the WHOLE config.toml on the first invalid one ("too few unicode
 * value digits, expected unicode hexadecimal value") — not just the one bad entry (defect
 * codex-mcp-inert-untrusted-project, live smoke wire-support-codex-qwen). TOML literal strings
 * cannot contain a single quote or a control character (incl. newline) — a value with either
 * has no literal-string representation; callers must not pass one (a project directory path
 * never does).
 */
export function tomlLiteralString(s: string): string {
  if (s.includes("'")) {
    throw new Error(
      `tomlLiteralString: value contains a single quote, which a TOML literal string cannot ` +
        `escape or represent: ${s}`,
    );
  }
  if (/[\x00-\x08\x0a-\x1f\x7f]/.test(s)) {
    throw new Error(
      `tomlLiteralString: value contains a control character, which a TOML literal string ` +
        `(single-line, unescaped) cannot represent: ${s}`,
    );
  }
  return `'${s}'`;
}

export type CodexProjectTrustOutcome =
  | { readonly outcome: "already-trusted"; readonly blocks: readonly TomlBlock[] }
  | { readonly outcome: "left-existing"; readonly blocks: readonly TomlBlock[]; readonly existingValue: string }
  | { readonly outcome: "set"; readonly blocks: readonly TomlBlock[] };

/**
 * Mark `projectDir` trusted in a codex USER-scope config.toml's `[projects.'<path>']` table
 * (`ConfigToml.projects: Option<HashMap<String, ProjectConfig>>`, config/src/config_toml.rs:443,
 * `ProjectConfig { trust_level: Option<TrustLevel> }` at :553-558) — without which codex disables
 * the ENTIRE project config layer, including any `[mcp_servers.petbox]` entry the project's own
 * `.codex/config.toml` carries (config/src/loader/mod.rs:1736's `disabled_reason`; defect
 * codex-mcp-inert-untrusted-project, live smoke wire-support-codex-qwen — `codex mcp list` from
 * the wired directory returned `[]` and no `mcp__petbox__*` tool ever reached the model).
 *
 * Purely additive, never destructive:
 *   - "already-trusted": `trust_level` was already exactly `"trusted"` — `blocks` unchanged.
 *   - "left-existing": some OTHER `trust_level` was already set (an operator's own choice, e.g.
 *     `"untrusted"`) — NEVER flipped; `blocks` unchanged, `existingValue` is the raw (still
 *     TOML-quoted) scalar text for the caller to report.
 *   - "set": `trust_level` was absent for this exact path — written as `"trusted"` now.
 * Only THIS path's block is ever touched (via setBlockScalar, not upsertBlock: a sibling field
 * codex itself might add to `ProjectConfig` later would survive) — every other `[projects.*]`
 * entry in the file is untouched, by construction of setBlockScalar/getBlockScalar's header
 * match.
 *
 * The path segment of the header is ALWAYS a TOML literal string (tomlLiteralString) — see that
 * function's own comment for why a basic double-quoted string containing Windows backslashes
 * would corrupt the WHOLE config.toml on load, not just this entry.
 */
export function applyCodexProjectTrust(
  blocks: readonly TomlBlock[],
  projectDir: string,
): CodexProjectTrustOutcome {
  const header = `projects.${tomlLiteralString(projectDir)}`;
  const trustedValue = tomlString("trusted");
  const existing = getBlockScalar(blocks, header, "trust_level");
  if (existing === trustedValue) return { outcome: "already-trusted", blocks };
  if (existing !== undefined) return { outcome: "left-existing", blocks, existingValue: existing };
  return { outcome: "set", blocks: setBlockScalar(blocks, header, "trust_level", trustedValue) };
}

// ---- codex-specific block builders (codex-spec.md §6, §2) -----------------------------------

export function buildDeepseekProviderBlock(): string[] {
  return [
    `name = ${tomlString("DeepSeek")}`,
    `base_url = ${tomlString("https://api.deepseek.com/v1")}`,
    `env_key = ${tomlString("DEEPSEEK_API_KEY")}`,
    `wire_api = ${tomlString("responses")}`,
  ];
}

export function buildOpencodeGoProviderBlock(sessionUuid: string): string[] {
  return [
    `name = ${tomlString("opencode Zen Go")}`,
    `base_url = ${tomlString("https://opencode.ai/zen/go/v1")}`,
    `env_key = ${tomlString("OPENCODE_GO_API_KEY")}`,
    `wire_api = ${tomlString("responses")}`,
    `http_headers = { "x-opencode-session" = ${tomlString(sessionUuid)} }`,
  ];
}

/** Extract the `x-opencode-session` UUID already written into an existing provider block, if
 * any — so re-installing preserves it instead of minting a new one (the gateway pins sessions
 * to that header; a rotated value is not itself harmful, but there is no reason to churn it). */
export function extractOpencodeSessionUuid(blockLines: readonly string[]): string | undefined {
  const re = /x-opencode-session"\s*=\s*"([^"]+)"/;
  for (const line of blockLines) {
    const m = re.exec(line);
    if (m) return m[1];
  }
  return undefined;
}

export function buildMcpServerBlock(baseUrl: string, envVar: string): string[] {
  return [
    `url = ${tomlString(`${baseUrl}/mcp`)}`,
    `env_http_headers = { "X-Api-Key" = ${tomlString(envVar)} }`,
    `startup_timeout_sec = 30`,
    `tool_timeout_sec = 120`,
    `enabled = true`,
  ];
}

export function buildHookStateBlock(hash: string): string[] {
  return [`enabled = true`, `trusted_hash = ${tomlString(hash)}`];
}
