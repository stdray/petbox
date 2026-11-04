// Собирает исходящие заголовки провайдера opencode-go, не обращаясь к сети.
// Использует РЕАЛЬНОЕ определение провайдера из pi-ai и подменённый fetch.
//
//   PI_PKG=<...>/@earendil-works/pi-ai  node header-probe.mjs [sessionId]
//
// PI_PKG — каталог пакета pi-ai (в нём лежит dist/).
import { pathToFileURL } from "node:url";
import { join } from "node:path";

const PI_PKG = process.env.PI_PKG;
if (!PI_PKG) {
  console.error("PI_PKG is not set — point it at the @earendil-works/pi-ai package directory");
  process.exit(2);
}
const load = (rel) => import(pathToFileURL(join(PI_PKG, "dist", rel)).href);

const { opencodeGoProvider } = await load("providers/opencode-go.js");
const provider = opencodeGoProvider();
const models = provider.getModels();
const SESSION_ID = process.argv[2] ?? "00000000-0000-7000-8000-000000000000";

async function capture(label, model, options) {
  let seen = null;
  const fakeFetch = async (url, init) => {
    const h = new Map();
    if (url && typeof url === "object" && typeof url.headers?.forEach === "function") {
      url.headers.forEach((v, k) => h.set(k, v));
    }
    const extra = init?.headers;
    if (extra) {
      if (typeof extra.forEach === "function" && !Array.isArray(extra)) extra.forEach((v, k) => h.set(k, v));
      else for (const [k, v] of Object.entries(extra)) h.set(k, v);
    }
    seen = Object.fromEntries(h);
    throw new Error("captured");
  };
  const stream = provider.stream(
    model,
    { messages: [{ role: "user", content: "hi" }] },
    { apiKey: "dummy", fetch: fakeFetch, ...options },
  );
  try {
    for await (const _ of stream) void _;
  } catch {
    /* expected: fakeFetch throws */
  }
  const headers = seen ?? {};
  const routing = Object.keys(headers).filter((k) => /session|affinity|client-request|opencode/i.test(k));
  console.log(`\n--- ${label}`);
  console.log(`    model: ${model.provider}/${model.id} (${model.api})`);
  if (routing.length === 0) console.log("    NO routing headers sent");
  for (const k of routing) console.log(`    ${k}: ${headers[k]}`);
  return headers;
}

for (const api of [...new Set(models.map((m) => m.api))]) {
  const model = models.find((m) => m.api === api);
  await capture(`${api} — sessionId present`, model, { sessionId: SESSION_ID });
}

const dl = models.find((m) => m.id === "deepseek-v4.1-flash") ?? models[0];
await capture("no sessionId", dl, {});
await capture("caller already set x-opencode-session", dl, {
  sessionId: SESSION_ID,
  headers: { "x-opencode-session": "caller-value" },
});
// то же, что models.json: compat.sendSessionAffinityHeaders = true
// (compat живёт на модели, поэтому правим её, а не опции вызова)
const withAffinity = { ...dl, compat: { ...(dl.compat ?? {}), sendSessionAffinityHeaders: true } };
await capture("sessionId + compat.sendSessionAffinityHeaders=true", withAffinity, {
  sessionId: SESSION_ID,
  cacheRetention: "short",
});
