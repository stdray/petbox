// Фейковый шлюз opencode: логирует заголовки каждого запроса и отдаёт OpenAI-совместимый SSE.
// Первый ответ — tool_calls (ls), чтобы pi сделал ВТОРОЙ запрос в той же сессии:
// так видно, что маршрутный заголовок ставится на каждом ходу, а не один раз.
//
//   node fake-gateway.mjs [logFile] [port]
import http from "node:http";
import { appendFileSync } from "node:fs";

const LOG = process.argv[2] ?? "requests.log";
const PORT = Number(process.argv[3] ?? 8792);
let n = 0;

http
  .createServer((req, res) => {
    let body = "";
    req.on("data", (c) => (body += c));
    req.on("end", () => {
      const headers = Object.fromEntries(
        Object.entries(req.headers).filter(([k]) => /session|affinity|client|opencode|authorization|user-agent/i.test(k)),
      );
      appendFileSync(LOG, JSON.stringify({ n: n + 1, method: req.method, url: req.url, headers }) + "\n");

      if (!req.url.includes("/chat/completions")) {
        res.writeHead(200, { "content-type": "application/json" });
        res.end("{}");
        return;
      }

      n += 1;
      res.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-cache" });
      const base = { id: `chatcmpl-e2e-${n}`, object: "chat.completion.chunk", created: 1, model: "fake" };
      const event = (delta, finish, usage) =>
        res.write(`data: ${JSON.stringify({ ...base, choices: [{ index: 0, delta, finish_reason: finish }], ...(usage ? { usage } : {}) })}\n\n`);

      if (n === 1) {
        event({ role: "assistant", tool_calls: [{ index: 0, id: "call_1", type: "function", function: { name: "ls", arguments: '{"path":"."}' } }] }, "tool_calls", {
          prompt_tokens: 50,
          completion_tokens: 5,
          total_tokens: 55,
        });
      } else {
        event({ role: "assistant", content: "done" }, "stop", { prompt_tokens: 80, completion_tokens: 1, total_tokens: 81 });
      }
      res.write("data: [DONE]\n\n");
      res.end();
    });
  })
  .listen(PORT, "127.0.0.1", () => console.log(`fake gateway on 127.0.0.1:${PORT}, log: ${LOG}`));
