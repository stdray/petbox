// MITM-прокси для одного хоста: терминирует TLS локально подписанным сертификатом,
// логирует строку запроса, заголовки, поле model и начало тела, затем отдаёт фейковый SSE.
// Нужен, чтобы увидеть, что НАСТОЯЩИЙ клиент opencode шлёт на opencode.ai, и чтобы снять
// тело запроса для побайтового реплея.
//
// Подготовка (openssl):
//   openssl req -x509 -newkey rsa:2048 -nodes -keyout ca.key -out ca.pem -days 2 -subj "//CN=petbox-mitm-ca"
//   openssl req -newkey rsa:2048 -nodes -keyout oc.key -out oc.csr -subj "//CN=opencode.ai"
//   printf "subjectAltName=DNS:opencode.ai,DNS:*.opencode.ai
basicConstraints=CA:FALSE
" > san.ext
//   openssl x509 -req -in oc.csr -CA ca.pem -CAkey ca.key -CAcreateserial -out oc.pem -days 2 -extfile san.ext
//
// Запуск:
//   node mitm-capture.mjs mitm.log 8797 oc.pem oc.key
//   HTTPS_PROXY=http://127.0.0.1:8797 NODE_EXTRA_CA_CERTS=<abs path to ca.pem> opencode run --standalone ...
//
// MITM proxy for one host: terminates TLS with a locally signed cert for that host,
// logs the full request line + headers + body head, then answers with a fake SSE stream.
// Purpose: see what the official OpenCode client actually sends to opencode.ai.
//
//   node mitm.mjs <logFile> <port> <certPem> <keyPem>
import http from "node:http";
import tls from "node:tls";
import { readFileSync, appendFileSync, writeFileSync } from "node:fs";

const LOG = process.argv[2] ?? "mitm.log";
const PORT = Number(process.argv[3] ?? 8797);
const CERT = readFileSync(process.argv[4], "utf8");
const KEY = readFileSync(process.argv[5], "utf8");

const server = http.createServer((req, res) => res.writeHead(405).end());

server.on("connect", (_req, clientSocket) => {
  clientSocket.write("HTTP/1.1 200 Connection Established\r\n\r\n");
  const secure = new tls.TLSSocket(clientSocket, { isServer: true, cert: CERT, key: KEY });
  secure.on("error", () => clientSocket.destroy());
  let buffer = "";
  secure.on("data", (chunk) => {
    buffer += chunk.toString("latin1");
    const headerEnd = buffer.indexOf("\r\n\r\n");
    if (headerEnd === -1) return;
    const head = buffer.slice(0, headerEnd);
    const lines = head.split("\r\n");
    const [method, url] = lines[0].split(" ");
    const headers = {};
    for (const line of lines.slice(1)) {
      const i = line.indexOf(":");
      if (i > 0) headers[line.slice(0, i).toLowerCase()] = line.slice(i + 1).trim();
    }
    const length = Number(headers["content-length"] ?? 0);
    const bodyStart = headerEnd + 4;
    if (buffer.length - bodyStart < length) return; // wait for the full body
    const body = buffer.slice(bodyStart, bodyStart + length);
    if (url.includes("chat/completions")) {
      writeFileSync("replay-body.json", body);
      writeFileSync("replay-headers.json", JSON.stringify({ headers, url }, null, 1));
    }
    appendFileSync(
      LOG,
      JSON.stringify({ method, url, model: (safeJson(body).model ?? null), bodyHead: body.slice(0,120), headers }, null, 0) + "\n",
    );
    const lines2 = [
      "HTTP/1.1 200 OK",
      "content-type: text/event-stream",
      "cache-control: no-cache",
      "connection: close",
      "",
    ];
    const base = JSON.stringify({ id: "chatcmpl-mitm", object: "chat.completion.chunk", created: 1, model: "fake" });
    secure.write(
      `${lines2.join("\r\n")}\r\ndata: ${base.replace("}", ',"choices":[{"index":0,"delta":{"role":"assistant","content":"MITM-OK"}}]}')}\r\n\r\ndata: [DONE]\r\n\r\n`,
    );
    secure.end();
  });
});

function safeJson(body) {
  try {
    const parsed = JSON.parse(body);
    return parsed && typeof parsed === "object" ? parsed : {};
  } catch {
    return {};
  }
}

server.listen(PORT, "127.0.0.1", () => console.log(`mitm on ${PORT} -> ${LOG}`));
