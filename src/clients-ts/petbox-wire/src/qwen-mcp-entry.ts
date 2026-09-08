// Qwen Code's `mcpServers.petbox` entry shape — the ONE place both of wire.ts's writers build it
// (installGlobalHooks's user-scope `$QWEN_HOME/settings.json` entry, and writeProjectFiles's
// workspace-scope `<project>/.qwen/settings.json` entry — defect
// qwen-mcp-json-shadows-workspace-entry, live smoke wire-support-codex-qwen), so the two can
// never drift apart the way a duplicated object literal risks. wire.ts runs main() at module top
// level and must never be imported by a side module (see its own file header), so this lives
// here — a plain module a test file can import directly, same reason codex-toml.ts exists.
//
// Field meanings (root-caused against the source clone at
// D:\my\prj\_analysis\repos\qwen-code\packages, per this task's brief):
//   httpUrl/headers  — streamable HTTP transport (qwen-spec.md §2, verified against a live
//                      `qwen mcp list`); `${envVar}` is a placeholder, resolved by qwen's own
//                      settings loader (settings.ts's resolveEnvVarsInObject) for BOTH user- and
//                      workspace-scope settings.json — never a literal key on disk.
//   trust            — skips qwen's per-tool-call confirmation prompt for this server.
//   alwaysLoadTools  — qwen defers MCP tools to `tool_search` by default, declaring them to the
//                      model eagerly only when the ACTIVE model's id matches
//                      `/deepseek-(v3|v4|chat)/i` (core/src/config/config.ts). Every role binds a
//                      DeepSeek model today (QWEN_ROLE_MODEL_SEED, roles.ts — owner decision
//                      2026-09-08: both harnesses collapse onto the direct DeepSeek subscription
//                      until a routing proxy exists), so this regex would in fact match — but the
//                      flag is set unconditionally anyway: it is cheap, harness-portable, and
//                      keeps working unchanged the moment a role is rebound off DeepSeek (e.g.
//                      back onto the opencode-go gateway once the proxy lands), which the regex
//                      match alone would not.
//
// Plain TS for native node type-stripping: zero deps.

export type QwenMcpServerEntry = {
  readonly httpUrl: string;
  readonly headers: { readonly "X-Api-Key": string };
  readonly timeout: number;
  readonly trust: boolean;
  readonly alwaysLoadTools: boolean;
};

export function buildQwenMcpServerEntry(baseUrl: string, envVar: string): QwenMcpServerEntry {
  return {
    httpUrl: `${baseUrl}/mcp`,
    headers: { "X-Api-Key": `\${${envVar}}` },
    timeout: 30000,
    trust: true,
    alwaysLoadTools: true,
  };
}
