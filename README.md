# PetBox

A self-hosted **module monolith** (.NET 10) that gives AI coding agents durable infrastructure:
working-session history, searchable long-term memory, task boards with a configurable
methodology, a resilient LLM router, and an MCP tool surface over all of it. It also carries the
classic pet-project plumbing (tag-based config, log ingestion with a KQL engine, per-project
SQLite databases, health/CI dashboard, deploy tracking) behind feature toggles.

Live instance: <https://petbox.3po.su> (the project is developed on itself: its own plan, spec
and work items live on PetBox boards, reached over MCP).

## The agent side

- **Sessions and digests** - agents append working-session transcripts; a background job
  (`SessionDigestJob`) distills them into digests, and an episodic search finds past decisions
  across sessions.
- **Memory search** - memory entries are indexed twice: SQLite **FTS5** (lexical) and embeddings
  (semantic). The two rankings are merged with **Reciprocal Rank Fusion**
  (`PetBox.Core/Search/HybridMerge.cs`), then optionally **reranked** by an LLM reranker. Because
  the RRF score is rank-based it is comparable across stores, so several memory containers are
  merged globally rather than store by store.
- **Temporal memory** - an entry is never overwritten: each key keeps a revision history, and a
  partial unique index guarantees at most one *active* revision per key. Deletes are safe because
  history stays.
- **Fact deduplication** - auto-captured facts are checked against existing ones by normalized
  text, then by embedding cosine similarity (default threshold 0.92) before being stored.
- **Observations** - a built-in board per project for defect-like findings. A create-time write
  is deduplicated (exact normalized match, then cosine similarity over title+body, threshold
  0.75) and a hit bumps a recurrence count instead of adding a duplicate. A `seen` observation can
  be *promoted* to a real work or idea node; when that work reaches a terminal-OK status the
  observation flips to `fixed`, and a later recurrence reopens it.
- **Resilient LLM router** (`PetBox.LlmRouter`) - chat / embed / rerank behind one facade with an
  ordered fallback chain per capability. A circuit breaker scopes each failure by its *class*:
  an unreachable host opens the whole endpoint, while a 429 or 5xx opens only that endpoint's leg
  for that capability. This came from a measured production incident (a Chat rate limit was
  silently diverting Embed and Rerank traffic). Opening is logged once per transition, not per
  skipped request.
- **MCP surface** - 100+ tools (`[McpServerTool]` methods under `src/PetBox.Web/Mcp`) covering
  tasks, memory, sessions, comments, relations, config, logs, data, LLM and deploy. Each tool
  declares its required scope once; `tools/list`, the call gate and `whoami` all read that
  declaration, and a unit test fails the build if a tool lacks one. API keys carry an enumerated
  scope set plus a project claim, and sandbox-only keys are rejected against non-sandbox
  projects.
- **Methodology engine** - boards follow a user-defined methodology (here
  idea -> spec -> work -> intake) with status transitions and guards enforced by the engine
  (`PetBox.Tasks.Engine`). See [doc/methodology.md](doc/methodology.md) and
  [doc/methodology-engine.md](doc/methodology-engine.md).

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

Client SDKs (.NET, TypeScript, Python) live under `src/clients-net`, `src/clients-ts`,
`src/clients-py`. Every subsystem is gated by a `Features:<Name>` toggle; a disabled one costs
nothing at runtime.

## Stack

.NET 10, Razor Pages (SSR) + htmx 2 + Alpine.js 3, Tailwind 3 + daisyUI 4, TypeScript bundled
with bun, SQLite via linq2db, FluentMigrator, Kusto.Language + KustoLoco (KQL), OpenTelemetry,
Docker (chiseled noble runtime), Cake + GitVersion, GitHub Actions, Playwright for E2E.

## Build and run

Requirements: .NET SDK 10 (see `global.json`), bun, and PowerShell or bash.

```sh
./build.ps1 -Target Test        # or: ./build.sh --target=Test
./build.ps1 -Target Dev         # bun watchers + `dotnet watch run` from src/PetBox.Web
./build.ps1 -Target Verify      # Test + client SDK lint/typecheck/test
```

Other targets (`build.cs`): `Docker`, `DockerSmoke`, `DockerPush`, and per-SDK pack/publish
targets. The container exposes port 8080 and keeps its data under `/app/data`.

## Engineering practices

- **One gate.** The Cake `Test` target is the single definition of "green": build, formatting
  verification (`dotnet format`), frontend lint and unit tests, then the .NET test suite
  (4,000+ test cases). CI runs the same target, so a green local run cannot be a red pipeline.
- **Architecture as tests.** Layering is enforced mechanically: module boundary tests, a guard
  that no `DataConnection` is ever injectable (it is not thread-safe), a rule that the database
  is reachable only from the service layer, cross-tenant authorization probes, and a ratchet
  requiring a scope declaration on every MCP tool. See `tests/PetBox.Tests/Architecture`.
- **CI/CD.** GitHub Actions: tests on every push to `main`; a `deploy` tag runs test, image
  publish to ghcr and SSH deploy with a post-deploy health check. A `jb inspectcode` gate blocks
  the deploy at the tagged commit. SDKs publish from their own tags.
- **Process.** Every change starts from a card on the PetBox boards, is made in a git worktree,
  and ends as a pushed branch; the rules are in [AGENTS.md](AGENTS.md).

## Docs

[AGENTS.md](AGENTS.md) (process and architecture contract), [doc/](doc) (methodology, UI
conventions, settings taxonomy, agent wiring).
