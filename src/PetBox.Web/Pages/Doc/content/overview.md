# What PetBox is

PetBox is a self-hostable **module monolith** — a small backplane for personal projects and bots. Each subsystem is feature-toggled; together they cover the boring infra (config, logs, data, health) plus a durable place for **agents** to plan and remember work. One .NET service, SQLite storage, behind Caddy.

Three audiences, three surfaces:

- **`/ui/…`** — humans (admin + read views).
- **`/api/…`, `/v1/…`** — programs (REST).
- **`/mcp`** — coding agents (MCP tools). See [Connect an agent](/doc/agent).

Auth is an **API key** (`X-Api-Key`) scoped to a project, with enumerable scopes (`config:read`, `logs:query`, `data:write`, `tasks:write`, …).

## Modules

- **Config** — a tag-based config store. A binding has a path, a value (or encrypted secret) and tags; resolution picks the most specific match for a caller's tag vector. Consume the resolved bundle over REST (`/v1/conf`, ETag-aware) or via the client libraries; manage it in the UI or over MCP.
- **Logging** — Seq-compatible structured logs. Ingest CLEF (the Seq `/api/events/raw` shape), query with **KQL** via `log_query` (MCP) or REST. Per-project named logs. A stock Seq client writes into a *named* log via the compat base — `serverUrl = …/api/ingest/{project}/{log}/compat/seq` with a regular project API key as the Seq key (the client appends `api/events/raw` itself); mimicked protocols collect under `compat/{protocol}`.
- **Data** — per-project SQLite databases with a REST raw-SQL pass-through (`/api/data/{project}/{db}/exec` and `/query`) and schema apply; also exposed over MCP (`data.*`).
- **Dashboard** — health/CI polling and a heartbeat endpoint; the at-a-glance status view.
- **Tasks / Memory / Sessions** — the agent-collaboration layer over MCP: task boards (the spec/work/idea rails), durable memory notes, and per-project session plans. This is where an agent plans and records work. See the [methodology cheatsheet](/doc/methodology) and its [model](/doc/methodology/philosophy).

## Client libraries (published)

Today the published SDKs cover the **config** surface (ETag-aware polling into your app's native config); data/log SDK surfaces are planned. Everything else is reachable directly over REST/MCP.

### TypeScript — `@stdray-npm/petbox-client` (npm)

```
npm i @stdray-npm/petbox-client
```

```ts
import { PetBoxConfigClient } from "@stdray-npm/petbox-client";

const cfg = new PetBoxConfigClient({
  baseUrl: "{{origin}}",
  apiKey: process.env.PETBOX_API_KEY!,
  tags: { project: "kpvotes", service: "bot" },
});
const resolved = await cfg.start();        // first fetch + background ETag polling
cfg.on("change", next => { /* hot-reload */ });
// one-shot (no polling): fetchConfig({ ... })
```

### .NET — `PetBox.Client.Config` (NuGet, GitHub Packages)

```csharp
builder.Configuration.AddPetBoxConfig(o =>
{
    o.BaseUrl = "{{origin}}";
    o.ApiKey  = builder.Configuration["PETBOX_API_KEY"]!;
    o.Tags    = new() { ["project"] = "kpvotes", ["service"] = "bot" };
    o.CacheDirectory = "/var/cache/petbox"; // last-known-good on disk (mount a volume)
});
// resolved config flows into IConfiguration; ETag polling hot-reloads it.
// CacheDirectory: boots on the last good config after a restart even if petbox is down.
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

## Building on PetBox (for agents)

A typical loop: connect over MCP ([guide](/doc/agent)), read the [methodology](/doc/methodology), then **build your app against PetBox** (config/logs/data) while **tracking the work in PetBox itself** — capture requirements on a `spec` board, the technical tasks on a `work` board (linked to spec), ideas on an `ideas` board. The plan lives next to the platform it runs on.
