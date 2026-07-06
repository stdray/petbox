// Minimal Streamable-HTTP MCP client for the kit hooks — zero deps, plain TS.
//
// petbox exposes tasks/memory ONLY over the MCP endpoint (`POST {baseUrl}/mcp`), not as plain
// REST (the canon endpoint is the sole memory REST route). The prompt-RAG hook needs a
// deterministic exact-match lookup against task nodes, so it speaks just enough of the MCP
// Streamable-HTTP transport to do a single `tools/call`:
//
//   1. POST initialize            → 200 SSE, response carries the `Mcp-Session-Id` header
//   2. POST notifications/initialized (with that session id)   → 202
//   3. POST tools/call            → 200 SSE, one `data:` frame with the JSON-RPC result
//
// The transport is stateful (session id + `text/event-stream` responses). We read the whole
// body and pull the JSON out of the single SSE `data:` frame (or a raw JSON body, if the server
// ever answers application/json). Every path is best-effort and TOTAL: any failure returns null,
// never throws — the caller (a global hook) must degrade to silence.
//
// Plain TS for native node type-stripping: no enum/namespace/parameter-properties, zero deps.

const PROTOCOL_VERSION = "2024-11-05";

export type McpTarget = {
  baseUrl: string;
  apiKey: string;
  timeoutMs: number;
};

export type McpClient = {
  // Invoke a tool. Returns the tool's `structuredContent` on success, or null on ANY failure
  // (transport error, JSON-RPC error, or a tool-level {isError:true} envelope — e.g. an exact
  // `keys` lookup that matched nothing). A miss is indistinguishable from an error on purpose:
  // both mean "no confident result" → the caller injects nothing.
  call(name: string, args: unknown): Promise<unknown | null>;
};

type RpcResult = { status: number; sessionId: string | null; json: any };

// POST one JSON-RPC message and parse the (SSE or JSON) response body. Never throws.
async function postRpc(
  url: string,
  apiKey: string,
  sessionId: string | null,
  body: unknown,
  timeoutMs: number,
): Promise<RpcResult | null> {
  try {
    const headers: Record<string, string> = {
      "X-Api-Key": apiKey,
      "Content-Type": "application/json",
      // The Streamable-HTTP transport requires the client to accept an event-stream reply.
      Accept: "application/json, text/event-stream",
    };
    if (sessionId) headers["Mcp-Session-Id"] = sessionId;
    const resp = await fetch(url, {
      method: "POST",
      headers,
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(timeoutMs),
    });
    const text = await resp.text();
    return { status: resp.status, sessionId: resp.headers.get("mcp-session-id"), json: parseFrame(text) };
  } catch {
    return null;
  }
}

// Pull a JSON object out of a response body that is either a raw JSON document or an SSE stream
// of `event:`/`data:` lines. Returns the LAST parseable `data:` frame (a single tools/call reply
// carries exactly one). Returns null when nothing parses.
function parseFrame(text: string): any {
  const trimmed = (text ?? "").trim();
  if (!trimmed) return null;
  const dataLines = trimmed
    .split(/\r?\n/)
    .filter((l) => l.startsWith("data:"))
    .map((l) => l.slice(5).trim());
  const candidates = dataLines.length > 0 ? dataLines : [trimmed];
  for (let i = candidates.length - 1; i >= 0; i--) {
    try {
      return JSON.parse(candidates[i]);
    } catch {
      // try the previous frame
    }
  }
  return null;
}

// Open an MCP session (initialize + initialized handshake) and return a client that can issue
// tool calls on it. Returns null when the handshake fails for any reason (unreachable, no session
// id, bad JSON) — the caller degrades to silence.
export async function connectMcp(t: McpTarget): Promise<McpClient | null> {
  const url = `${t.baseUrl}/mcp`;
  const init = await postRpc(
    url,
    t.apiKey,
    null,
    {
      jsonrpc: "2.0",
      id: 1,
      method: "initialize",
      params: {
        protocolVersion: PROTOCOL_VERSION,
        capabilities: {},
        clientInfo: { name: "petbox-wire", version: "0" },
      },
    },
    t.timeoutMs,
  );
  const sid = init?.sessionId;
  if (!init || init.status !== 200 || !sid) return null;

  // Best-effort initialized notification (202, no body). A failure here is non-fatal: some
  // servers accept tools/call without it, so we press on and let the tool call be the real test.
  await postRpc(url, t.apiKey, sid, { jsonrpc: "2.0", method: "notifications/initialized" }, t.timeoutMs);

  let nextId = 2;
  return {
    async call(name: string, args: unknown): Promise<unknown | null> {
      const r = await postRpc(
        url,
        t.apiKey,
        sid,
        { jsonrpc: "2.0", id: nextId++, method: "tools/call", params: { name, arguments: args } },
        t.timeoutMs,
      );
      const result = r?.json?.result;
      if (!result || result.isError) return null; // transport/JSON-RPC/tool error or exact-miss
      return result.structuredContent ?? null;
    },
  };
}
