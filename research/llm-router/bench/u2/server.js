// Fake upstream u1: OpenAI-compatible chat-completions (SSE streaming), embeddings, rerank (Cohere-shape).
// Logs every request (method, path, model, a few headers, body summary) to requests.log (JSON lines).
// Behavior:
//   - /v1/chat/completions: if the LAST message is not a tool result, returns a streamed tool_call
//     (name="get_weather", args={"city":"Paris"}). If the last message IS a tool result (role=="tool"),
//     returns a streamed plain text answer that echoes the tool result content.
//   - /v1/embeddings: returns one fixed-size vector per input string.
//   - /v1/rerank: Cohere-shape request {model, query, documents, top_n}; returns scored+reordered results.
// No real provider, no real keys. Bind 127.0.0.1 only.
const http = require('http');
const fs = require('fs');
const path = require('path');

const PORT = process.argv[2] ? parseInt(process.argv[2], 10) : 9101;
const LOG = process.argv[3] || path.join(__dirname, 'requests.log');
const FAIL_MODE = process.env.U1_FAIL_MODE || ''; // '', '429', '500'
const RETRY_AFTER = process.env.U1_RETRY_AFTER || '2';

function log(obj) {
  const line = JSON.stringify({ ts: new Date().toISOString(), ...obj });
  fs.appendFileSync(LOG, line + '\n');
  process.stdout.write(line + '\n');
}

function readBody(req) {
  return new Promise((resolve) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
  });
}

function sseChatToolCall(res, model) {
  res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache', Connection: 'keep-alive' });
  const id = 'chatcmpl-tool-' + Date.now();
  const base = { id, object: 'chat.completion.chunk', created: Math.floor(Date.now() / 1000), model };
  const send = (delta, finish) => res.write(`data: ${JSON.stringify({ ...base, choices: [{ index: 0, delta, finish_reason: finish }] })}\n\n`);
  send({ role: 'assistant', content: null, tool_calls: [{ index: 0, id: 'call_1', type: 'function', function: { name: 'get_weather', arguments: '' } }] }, null);
  send({ tool_calls: [{ index: 0, function: { arguments: '{"city":' } }] }, null);
  send({ tool_calls: [{ index: 0, function: { arguments: '"Paris"}' } }] }, null);
  send({}, 'tool_calls');
  res.write('data: [DONE]\n\n');
  res.end();
}

function sseChatText(res, model, text) {
  res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache', Connection: 'keep-alive' });
  const id = 'chatcmpl-text-' + Date.now();
  const base = { id, object: 'chat.completion.chunk', created: Math.floor(Date.now() / 1000), model };
  const send = (delta, finish) => res.write(`data: ${JSON.stringify({ ...base, choices: [{ index: 0, delta, finish_reason: finish }] })}\n\n`);
  send({ role: 'assistant', content: '' }, null);
  for (const word of text.split(' ')) send({ content: word + ' ' }, null);
  send({}, 'stop');
  res.write('data: [DONE]\n\n');
  res.end();
}

function jsonChatToolCall(res, model) {
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({
    id: 'chatcmpl-tool-' + Date.now(), object: 'chat.completion', created: Math.floor(Date.now() / 1000), model,
    choices: [{ index: 0, message: { role: 'assistant', content: null, tool_calls: [{ id: 'call_1', type: 'function', function: { name: 'get_weather', arguments: '{"city":"Paris"}' } }] }, finish_reason: 'tool_calls' }],
    usage: { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 },
  }));
}

function jsonChatText(res, model, text) {
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({
    id: 'chatcmpl-text-' + Date.now(), object: 'chat.completion', created: Math.floor(Date.now() / 1000), model,
    choices: [{ index: 0, message: { role: 'assistant', content: text }, finish_reason: 'stop' }],
    usage: { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 },
  }));
}

function fakeVector(seed, dim) {
  const v = [];
  let x = seed;
  for (let i = 0; i < dim; i++) { x = (x * 1103515245 + 12345) & 0x7fffffff; v.push((x % 1000) / 1000); }
  return v;
}

function handleEmbeddings(req, res, body) {
  let parsed = {};
  try { parsed = JSON.parse(body); } catch (e) {}
  const input = Array.isArray(parsed.input) ? parsed.input : [parsed.input || ''];
  const dim = 8;
  const data = input.map((s, i) => ({ object: 'embedding', index: i, embedding: fakeVector((typeof s === 'string' ? s.length : 1) + i, dim) }));
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ object: 'list', data, model: parsed.model || 'u1-embed', usage: { prompt_tokens: input.length, total_tokens: input.length } }));
}

function handleRerank(req, res, body) {
  let parsed = {};
  try { parsed = JSON.parse(body); } catch (e) {}
  const docs = parsed.documents || [];
  const results = docs.map((d, i) => ({ index: i, relevance_score: 1 / (i + 1), document: parsed.return_documents ? { text: typeof d === 'string' ? d : d.text } : undefined }))
    .sort((a, b) => b.relevance_score - a.relevance_score);
  const top_n = parsed.top_n || docs.length;
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ id: 'rerank-' + Date.now(), model: parsed.model || 'u1-rerank', results: results.slice(0, top_n), meta: { api_version: { version: '1' } } }));
}

const server = http.createServer(async (req, res) => {
  const body = await readBody(req);
  let parsed = {};
  try { parsed = JSON.parse(body); } catch (e) {}
  const headersSummary = {
    authorization: req.headers['authorization'] ? '(present)' : '(absent)',
    'x-gateway-injected': req.headers['x-gateway-injected'] || undefined,
    'x-client-should-be-stripped': req.headers['x-client-should-be-stripped'] || undefined,
    'content-type': req.headers['content-type'],
  };
  log({ path: req.url, method: req.method, model: parsed.model, stream: parsed.stream, headers: headersSummary, bodyLen: body.length });

  if (FAIL_MODE === '429') {
    res.writeHead(429, { 'Content-Type': 'application/json', 'Retry-After': RETRY_AFTER });
    res.end(JSON.stringify({ error: { message: 'rate limited (fake u2)', type: 'rate_limit_error' } }));
    return;
  }
  if (FAIL_MODE === '500') {
    res.writeHead(500, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ error: { message: 'internal error (fake u2)', type: 'server_error' } }));
    return;
  }

  if (req.url.startsWith('/v1/chat/completions')) {
    const messages = parsed.messages || [];
    const last = messages[messages.length - 1];
    const isToolResult = last && (last.role === 'tool' || last.role === 'function');
    const model = parsed.model || 'u1-chat';
    if (parsed.stream) {
      if (isToolResult) sseChatText(res, model, `tool result received: ${String(last.content).slice(0, 40)}`);
      else sseChatToolCall(res, model);
    } else {
      if (isToolResult) jsonChatText(res, model, `tool result received: ${String(last.content).slice(0, 40)}`);
      else jsonChatToolCall(res, model);
    }
    return;
  }
  if (req.url.startsWith('/v1/embeddings')) return handleEmbeddings(req, res, body);
  if (req.url.startsWith('/v1/rerank')) return handleRerank(req, res, body);
  if (req.url.startsWith('/v1/responses')) {
    // Minimal Responses-API passthrough for direct probing (not the primary path; gateway does the translation).
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ id: 'resp_u1', status: 'completed', output: [{ type: 'message', role: 'assistant', content: [{ type: 'output_text', text: 'u1 direct responses ok' }] }] }));
    return;
  }
  if (req.url === '/health' || req.url === '/') {
    res.writeHead(200, { 'Content-Type': 'text/plain' }); res.end('ok'); return;
  }
  if (req.url.startsWith('/v1/models')) {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ object: 'list', data: [{ id: 'm1', object: 'model', owned_by: 'fake' }, { id: 'm2', object: 'model', owned_by: 'fake' }] }));
    return;
  }
  res.writeHead(404, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ error: { message: `not found: ${req.url}` } }));
});

server.listen(PORT, '127.0.0.1', () => log({ event: 'listening', port: PORT, failMode: FAIL_MODE || 'none' }));
