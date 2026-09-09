// Qwen Code's `mcpServers.petbox` entry shape — built in exactly ONE place, and now consumed by
// exactly one writer: writeProjectFiles's WORKSPACE-scope `<project>/.qwen/settings.json` entry
// (defect qwen-mcp-json-shadows-workspace-entry, live smoke wire-support-codex-qwen). The second
// consumer — installGlobalHooks's USER-scope `$QWEN_HOME/settings.json` entry — is GONE (owner
// decision 09.09.2026, «Нет — убрать глобальную запись»: that entry was machine-global while the
// env var it named was per-project, so it silently pointed a bare `qwen` at whatever project was
// wired last; see installGlobalHooks's own header and wire-qwen-user-scope-no-mcp.test.ts). The
// module stays because workspace scope still needs the shape stated once, in one place, and
// because the field notes below are the root-caused record of WHY each field is what it is.
// wire.ts runs main() at module top
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
