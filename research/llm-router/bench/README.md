# Bench stand for 64-bench.md

Fake local upstreams (no deps, `node u1/server.js [port] [logfile]`):
- `u1/server.js` — healthy OpenAI-compatible chat-completions (SSE, tool_calls incl. codex's
  `exec_command`), `/v1/embeddings`, `/v1/rerank` (Cohere-shape), `/v1/models`.
- `u2/server.js` — identical code; run with `U1_FAIL_MODE=429` (honors `U1_RETRY_AFTER`) or `=500`
  to simulate an upstream failure for fallback tests.

Both bind 127.0.0.1 only, log every request as JSONL to `requests.log`, and can dump full request
bodies with `U1_DUMP_BODIES=<dir>`.

`bifrost/config.yaml` and `agentgateway/config.yaml` are NOT loaded from files by Bifrost (it's
configured live via its `/api/*` admin REST — see 64-bench.md for the exact calls); agentgateway's
`config.yaml` is the real `-f` config used to reproduce the bench (`docker run ... -f
/config.yaml`, models point at `host.docker.internal:910{1,2}`). No real provider keys anywhere —
`dummy-u{1,2}-key` placeholders only.
