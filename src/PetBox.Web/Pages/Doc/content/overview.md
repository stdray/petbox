# What PetBox is

PetBox is a self-hostable **module monolith** — a small backplane for personal projects and bots. Each subsystem is feature-toggled; together they cover the boring infra (config, logs, data, health) plus a durable place for **agents** to plan and remember work. One .NET service, SQLite storage, behind Caddy.

Three audiences, three surfaces:

- **`/ui/…`** — humans (admin + read views).
- **`/api/…`, `/v1/…`** — programs (REST).
- **`/mcp`** — coding agents (MCP tools). See [Connect an agent](/doc/agent).

Auth is an **API key** (`X-Api-Key`) scoped to a project, with enumerable scopes (`config:read`, `logs:query`, `data:write`, `tasks:write`, …).

## Modules

This is the whole platform, not just the agent layer — most of it has nothing to do with agents at all, it's infra your app talks to at runtime:

- **Config** — a tag-based config store. A binding has a path, a value (or encrypted secret) and tags; resolution picks the most specific match for a caller's tag vector. Consume the resolved bundle over REST (`/v1/conf`, ETag-aware) or via the client libraries; manage it in the UI or over MCP.
- **Logging** — Seq-compatible structured logs. Ingest CLEF (the Seq `/api/events/raw` shape) or OTLP (`/v1/logs`, `/v1/traces`, `/v1/metrics`), query with **KQL** via `log_query` (MCP) or REST. Per-project named logs. A stock Seq client writes into a *named* log via the compat base — `serverUrl = …/api/ingest/{project}/{log}/compat/seq` with a regular project API key as the Seq key (the client appends `api/events/raw` itself); mimicked protocols collect under `compat/{protocol}`. No client SDK yet (see below) — ingest over REST/OTLP/the Seq-compat shape directly.
- **Data** — per-project SQLite databases with a REST raw-SQL pass-through (`/api/data/{project}/{db}/exec` and `/query`) and schema apply; also exposed over MCP (`data.*`) and the client libraries.
- **LLM router** — an OpenAI-compatible chat endpoint (`POST /v1/chat/completions`) proxied through your configured providers, so your app can call one stable endpoint instead of wiring each provider's SDK. Configured and callable over MCP too (`llm_config_get/upsert`, `llm_chat`, `llm_embed`, `llm_rerank`). No client SDK — it's already an OpenAI-compatible shape, point your existing OpenAI client at it.
- **Deploy** — a small fleet control plane for agent-run services: node enrollment and heartbeat (`GET /agent/poll`, `POST /agent/heartbeat`, `POST /api/deploy/nodes`) plus `deploy_*` MCP tools (`upsert`/`start`/`stop`/`move`/`list`/`node_*`) to declare and move deployments. REST + MCP only, no client SDK.
- **Dashboard** — health/CI polling and a heartbeat endpoint; the at-a-glance status view.
- **Tasks / Memory / Sessions** — the agent-collaboration layer over MCP: task boards (the spec/work/idea rails), durable memory notes, and per-project session plans. This is where an agent plans and records work. MCP-only by design — there's no separate public REST surface for it. See the [methodology cheatsheet](/doc/methodology) and its [model](/doc/methodology/philosophy).
- **Projects & API keys** — project creation and key minting/scoping (`project_*`, `apikey_*` over MCP; the UI's **Connect** page is the only place a key is actually minted). No client SDK; this is setup, not runtime traffic.

## Client libraries (published)

Today the published SDKs cover the **config** surface (ETag-aware polling into your app's native config) and the **Data** module (raw parameterized SQL — `query`/`exec` — plus DataDb provisioning), in all three languages (.NET / TypeScript / Python). The **Log**, **LLM router** and **Deploy** modules have no SDK yet — use REST/OTLP/OpenAI-compatible calls directly (see "No SDK for your language" below; the same guidance applies here even in .NET/TS/Python).

Package inventory, exactly as published (verify against `build.cs`'s `NuGetPush`/`NpmPublish`/`PyPiPublish` targets if this drifts):

| Language | Package | Registry | Covers |
| --- | --- | --- | --- |
| TypeScript | `@stdray-npm/petbox-client` | npm (public, `@stdray-npm` scope) | Config + Data |
| .NET | `PetBox.Client` | nuget.org | Data (`.Data` on `PetBoxClient`) — the base client |
| .NET | `PetBox.Client.Config` | nuget.org | Config (`AddPetBoxConfig`) |
| .NET | `PetBox.Client.Data.Linq2Db` | nuget.org | LINQ-to-SQL over the Data module, built on `PetBox.Client` |
| Python | `petbox-client` | PyPI | Config + Data |

`petbox-wire` (npm, unscoped, also published from this repo) is a **different product** — the agent-wiring CLI, not a config/Data client. See the [wire guide](/doc/wire); don't confuse the two `petbox-*` npm packages.

### TypeScript — `@stdray-npm/petbox-client` (npm)

```
npm i @stdray-npm/petbox-client
```

```ts
import { PetBoxConfigClient } from "@stdray-npm/petbox-client";

const cfg = new PetBoxConfigClient({
  endpoint: "{{origin}}",
  apiKey: process.env.PETBOX_API_KEY!,
  tags: { project: "kpvotes", service: "bot" },
});
const resolved = await cfg.start();        // first fetch + background ETag polling
cfg.on("change", next => { /* hot-reload */ });
// one-shot (no polling): fetchConfig({ ... })
```

Data module (raw parameterized SQL + DataDb provisioning):

```ts
import { PetBoxDataClient } from "@stdray-npm/petbox-client";

const data = new PetBoxDataClient({ endpoint: "{{origin}}", apiKey: process.env.PETBOX_API_KEY! });
await data.createDb("kpvotes", "cache");
await data.exec("kpvotes", "cache", "INSERT INTO votes (id, film) VALUES (@id, @film)",
  [{ name: "@id", value: 1 }, { name: "@film", value: "Matrix" }]);
const rows = await data.query("kpvotes", "cache", "SELECT * FROM votes");
```

### .NET — `PetBox.Client.Config` (NuGet, nuget.org)

```csharp
builder.Configuration.AddPetBoxConfig(o =>
{
    o.BaseUrl = "{{origin}}";
    o.ApiKey  = builder.Configuration["PETBOX_API_KEY"]!;
    o.WithTag("project", "kpvotes")
     .WithTag("service", "bot");
    o.CacheDirectory = "/var/cache/petbox"; // last-known-good on disk (mount a volume)
});
// resolved config flows into IConfiguration; ETag polling hot-reloads it.
// CacheDirectory: boots on the last good config after a restart even if petbox is down.
// (TS: `cacheDir`; Python: `cache_path` — same last-known-good behaviour, all three languages.)
```

Data module (from the `PetBox.Client` package — `.Data` on `PetBoxClient`):

```csharp
using var client = new PetBoxClient(new PetBoxClientOptions
{
    Endpoint = "{{origin}}",
    ApiKey   = builder.Configuration["PETBOX_API_KEY"]!,
});
await client.Data.CreateDbAsync("kpvotes", "cache");
await client.Data.ExecAsync("kpvotes", "cache", "INSERT INTO votes (id) VALUES (@id)",
    new[] { new PetBoxSqlParam("@id", 1) });
var rows = await client.Data.QueryAsync("kpvotes", "cache", "SELECT * FROM votes");
```

### Python — `petbox-client` (PyPI)

```
pip install petbox-client
```

```python
import os
from petbox_client import PetBoxConfigClient, PetBoxConfigClientOptions

client = PetBoxConfigClient(PetBoxConfigClientOptions(
    endpoint="{{origin}}",
    api_key=os.environ["PETBOX_API_KEY"],
    tags={"project": "kpvotes", "service": "bot"},
))
config = client.start()                  # first fetch + background ETag polling
client.on("change", lambda cfg: ...)     # hot-reload
# one-shot (no polling): fetch_config(...)
```

Data module (raw parameterized SQL + DataDb provisioning):

```python
from petbox_client import PetBoxDataClient, PetBoxSqlParam

data = PetBoxDataClient(endpoint="{{origin}}", api_key=os.environ["PETBOX_API_KEY"])
data.create_db("kpvotes", "cache")
data.exec("kpvotes", "cache", "INSERT INTO votes (id, film) VALUES (@id, @film)",
          [PetBoxSqlParam("@id", 1), PetBoxSqlParam("@film", "Matrix")])
rows = data.query("kpvotes", "cache", "SELECT * FROM votes")
```

### What the Config SDK buys you over raw REST

All three Config clients give you the same three things, on top of a plain HTTP GET:

- **ETag conditional polling** — background refetch sends `If-None-Match`; a `304` is a no-op, so polling is cheap and hot-reload is near-instant on real changes.
- **Last-known-good (LKG) disk cache** — every successful fetch is written to disk (`CacheDirectory` / `cacheDir` / `cache_path`); on boot with PetBox unreachable, the client loads that file instead of failing. **This is the load-bearing guarantee**: without it, a PetBox restart or outage takes your app down with it, not just loses hot-reload.
- Change notification (`on("change", …)`) instead of you diffing polls yourself.

What none of the three SDKs do, in any language: **automatic retries or backoff** on a failed request (a failed fetch just logs and gives up until the next poll), and **no batching** anywhere (this includes the Log module once it gets a client — ingest calls are 1:1, not queued/flushed). If you need retry semantics, wrap the client's one-shot fetch yourself.

### No SDK for your language?

Go straight to REST — `/v1/conf` (config, ETag-aware), `/api/data/{project}/{db}/query|exec` (data), `/api/ingest/.../clef` or the OTLP endpoints (logs), `/v1/chat/completions` (LLM router, already OpenAI-compatible). All of it needs only the `X-Api-Key` header.

**The price of skipping the SDK**: you inherit none of the guarantees above for free. Concretely, for config in particular — implement your own conditional GET (`ETag`/`If-None-Match`) and, more importantly, **your own on-disk last-known-good fallback**. Skip that second one and a PetBox restart or network blip becomes an outage in your app too, not a graceful hold-last-value. This is the one guarantee worth reimplementing even in a five-line script; the others (retries, batching) the SDKs don't have either, so you're not worse off there.

### OpenAPI

The REST surface has a build-time-generated OpenAPI 3 document, checked into the repo at `doc/api/PetBox.Web.json`. It's served live at `/openapi/v1.json` — but **only when the instance runs in `Development`**; a deployed production instance (like this one) exposes no live spec endpoint today. That's a real gap, not a design choice — if you're working against a running production PetBox and have no repo access, there is currently no URL to fetch the schema from; read the routes above, the module descriptions on this page, or the `*Api.cs` endpoint files in the repo (e.g. `src/PetBox.Config/ConfigApi.cs`, `src/PetBox.Data/QueryExecApi.cs`, `src/PetBox.Log.Core/LogApi.cs`).

## Building on PetBox (for agents)

A typical loop: connect over MCP ([guide](/doc/agent)), read the [methodology](/doc/methodology), then **build your app against PetBox** (config/logs/data/LLM router/deploy — see Modules above) while **tracking the work in PetBox itself** — capture requirements on a `spec` board, the technical tasks on a `work` board (linked to spec), ideas on an `ideas` board. The plan lives next to the platform it runs on.
