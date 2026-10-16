[Русская версия](README.ru.md)

# PetBox

A self-hosted **module monolith** (.NET 10) that gives AI coding agents durable infrastructure.

- **What it is:** session history, searchable long-term memory, task boards with a configurable
  methodology, a resilient LLM router, and an MCP tool surface over all of it.
- **Also included:** tag-based config, log ingestion with a KQL engine, per-project SQLite
  databases, a health/CI dashboard and deploy tracking, each behind a feature toggle.
- **Developed on itself:** its own plan, spec and work items live on PetBox boards, reached over MCP.

Live instance: <https://petbox.3po.su>

## The agent side

### Sessions and digests

- **What:** agents append working-session transcripts; episodic search finds past decisions.
- **How:** a background job (`SessionDigestJob`) distills transcripts into digests.

### Memory search

- **What:** hybrid retrieval over memory entries.
- **How:**
  - lexical ranking from SQLite **FTS5**, semantic ranking from embeddings;
  - the two are merged with **Reciprocal Rank Fusion** (`PetBox.Core/Search/HybridMerge.cs`);
  - an optional LLM **reranker** reorders the result.
- **Why this shape:** the RRF score is rank-based, so it is comparable across stores. Several
  memory containers are merged globally instead of store by store.

### Temporal memory and fact dedup

- **Temporal:** an entry is never overwritten. Each key keeps a revision history, and a partial
  unique index guarantees at most one *active* revision per key. Deletes are safe: history stays.
- **Dedup:** auto-captured facts are compared with existing ones by normalized text, then by
  embedding cosine similarity (default threshold 0.92), before being stored.

### Observations

- **What:** a built-in board per project for defect-like findings.
- **How:**
  - a create-time write is checked by normalized text, then by cosine similarity over
    title+body (threshold 0.75);
  - a hit bumps a recurrence count instead of adding a duplicate;
  - a `seen` observation can be *promoted* to a work or idea node. When that work reaches a
    terminal-OK status the observation flips to `fixed`; a later recurrence reopens it.

### Resilient LLM router

- **What:** chat / embed / rerank behind one facade (`PetBox.LlmRouter`), with an ordered
  fallback chain per capability.
- **How the breaker scopes failures, by class:**
  - unreachable host: the whole endpoint opens, for every capability;
  - 429 or 5xx: only that endpoint's leg opens, for that one capability.
- **Why:** measured on production, a Chat rate limit was silently diverting Embed and Rerank
  traffic. An opening is logged once per transition, not per skipped request.

### MCP surface

100+ tools (`[McpServerTool]` methods under `src/PetBox.Web/Mcp`) for tasks, memory, sessions,
comments, relations, config, logs, data, LLM and deploy. The agent is not handed all of them.

**Discovery**
- `tools/list` shows a key only the tools whose scope it holds, decided per tool: without
  `tasks:write`, `tasks_upsert` is not listed.
- `whoami` returns every module, its scopes with a `granted` flag, and all of its tool names, so an
  agent can tell "not available to this key" from "does not exist".
- `tool_describe` describes any tool by name.

**Scopes**
- Each tool declares its required scope once. The call gate, `tools/list` and `whoami` all read
  that single declaration.
- API keys carry an enumerated scope set plus a project claim (possibly the wildcard `*`).

**Safety**
- Hiding is not the boundary: a call without the scope is refused regardless of visibility.
- A test fails the build if a tool has no declaration.
- Sandbox-only keys are rejected against projects that are not flagged as sandbox.

### Methodology engine

- **What:** boards follow a user-defined methodology (here idea -> spec -> work -> intake).
- **How:** status transitions and guards are enforced by the engine (`PetBox.Tasks.Engine`).
- **More:** [doc/methodology.md](doc/methodology.md), [doc/methodology-engine.md](doc/methodology-engine.md).

## Modules

| Project | Responsibility |
|---|---|
| `PetBox.Core` | auth, entity models, SQLite context and migrations, search (FTS, hybrid merge, rerank), feature toggles |
| `PetBox.Web` | single entry point: Program.cs, Razor Pages, frontend assets, MCP endpoint |
| `PetBox.Tasks` / `PetBox.Tasks.Engine` | task boards, methodology engine |
| `PetBox.Memory` | temporal memory stores, hybrid search |
| `PetBox.Sessions` | transcripts, digests, episodic search |
| `PetBox.LlmRouter` (+ `.Contract`) | routed chat / embed / rerank |
| `PetBox.Config` | tag-based configuration, deterministic resolve pipeline |
| `PetBox.Log.Core` | Seq-compatible and OTLP ingestion, KQL query engine |
| `PetBox.Data` | per-project SQLite databases, raw-SQL REST, schema migration |
| `PetBox.Dashboard`, `PetBox.Deploy` | health and CI polling, deployment tracking |

- Client SDKs (.NET, TypeScript, Python): `src/clients-net`, `src/clients-ts`, `src/clients-py`.
- Every subsystem is gated by a `Features:<Name>` toggle; a disabled one costs nothing at runtime.

## Stack

- **Backend:** .NET 10, SQLite via linq2db, FluentMigrator, Kusto.Language + KustoLoco (KQL), OpenTelemetry.
- **Frontend:** Razor Pages (SSR), htmx 2, Alpine.js 3, Tailwind 3 + daisyUI 4, TypeScript bundled with bun.
- **Delivery:** Docker (chiseled noble runtime), Cake + GitVersion, GitHub Actions, Playwright for E2E.

## Build and run

Requirements: .NET SDK 10 (see `global.json`), bun, PowerShell or bash.

```sh
./build.ps1 -Target Test        # or: ./build.sh --target=Test
./build.ps1 -Target Dev         # bun watchers + `dotnet watch run` from src/PetBox.Web
./build.ps1 -Target Verify      # Test + client SDK lint/typecheck/test
```

- Other targets in `build.cs`: `Docker`, `DockerSmoke`, `DockerPush`, per-SDK pack/publish.
- The container exposes port 8080 and keeps its data under `/app/data`.

## Engineering practices

### One gate

- The Cake `Test` target is the single definition of "green": build, formatting verification,
  frontend lint and unit tests, then the .NET suite (4,000+ test cases).
- CI runs the same target, so a green local run cannot be a red pipeline.

### Architecture as tests

- Module boundary tests for every subsystem.
- No `DataConnection` is ever injectable (it is not thread-safe); the database is reachable only
  from the service layer.
- Cross-tenant authorization probes; a ratchet requiring a scope declaration on every MCP tool.
- Where: `tests/PetBox.Tests/Architecture`.

### CI/CD

- Tests run on every push to `main`.
- A `deploy` tag runs test, image publish to ghcr, SSH deploy and a post-deploy health check.
- A `jb inspectcode` gate blocks the deploy at the tagged commit; SDKs publish from their own tags.

### Process

- Every change starts from a card on the PetBox boards, is made in a git worktree, and ends as a
  pushed branch. Rules: [AGENTS.md](AGENTS.md).

## Docs

[AGENTS.md](AGENTS.md) (process and architecture contract), [doc/](doc) (methodology, UI
conventions, settings taxonomy, agent wiring).
