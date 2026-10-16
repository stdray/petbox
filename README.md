[Русская версия](README.ru.md)

# PetBox

PetBox is one server with two jobs.

1. It helps AI coding agents remember things and organize their work.
2. It is a small backend for your own projects: settings, logs and databases.

An AI agent forgets everything when a chat ends. It does not know what it decided last week.
PetBox gives it a notebook, a task board and a set of tools. A pet project also needs a place for
its settings, its logs and its data. PetBox provides these too, so you do not run three services.

I built it for my own projects, and I build PetBox using PetBox. Its plan and tasks live on its
own boards.

It is a "modular monolith": one program made of clearly separated modules. Each module has a
switch in the settings. A module that is off costs nothing.

## Stack

- **Languages:** C# on .NET 10, TypeScript, Python (client only).
- **Web:** ASP.NET Core with Razor Pages rendered on the server. In the browser: htmx and Alpine.js. Styles: Tailwind and daisyUI. bun builds the frontend.
- **Databases:** SQLite everywhere, accessed with linq2db. FluentMigrator changes the schema.
- **Search:** SQLite FTS5 for words, DuckDB for session search, with Snowball stemming. Embeddings for meaning. A language model can rerank.
- **Logs and queries:** Kusto.Language parses KQL, and PetBox runs it on SQLite. OpenTelemetry and Seq formats come in. OpenTelemetry also traces PetBox itself.
- **Agents:** the Model Context Protocol (MCP) server, and a router for language models.
- **Delivery:** Docker, Cake with GitVersion, GitHub Actions, Playwright for browser tests.

DuckDB runs in production in one place: the session search index (see Sessions). The log module also has a DuckDB dialect for KQL, but no setting turns it on.

## Modules

### Config

**Problem:** the same setting has different values in different places.
**Solution:** you attach a value to a set of tags, for example `project:shop`, then
`service:api`, then `env:prod`. A request brings its own tags. PetBox picks the value whose tags
are all present in the request. The value with the most matching tags wins. Two equal winners
give an error, not a guess, so the answer is always the same.

- A default value has few tags. A production override has more tags, so it wins.
- The .NET client plugs into `IConfiguration` and polls for changes.

### Log

**Problem:** small projects need logs, traces and metrics, but not a big logging platform.
**Solution:** PetBox accepts logs from Seq clients (the CLEF format) and from OpenTelemetry
(logs, traces and metrics). Each project has its own store. You query it with KQL, a query language
from Azure Data Explorer.

- PetBox parses KQL with the official Kusto.Language library, then turns it into SQL for SQLite.
- Supported: `where`, `take`, `sort`, `top`, `project`, `extend`, `summarize`, `count`, `distinct`, `join`, `lookup`, `mv-expand` and `parse`.
- Not supported: `union`, `search`, `render`, `make-series` and many others. Unsupported operators give a clear error.
- The same language works on three tables: events, spans and metric points.
- There is live tail, retention cleanup and shareable results.
- Logs are stored in SQLite. The KQL translator also has a DuckDB dialect, tested next to SQLite. The server does not use it, and no setting selects it.

### Data

**Problem:** a small project needs a database, but not a database server.
**Solution:** each project gets its own SQLite files. You send parameterized SQL over REST, and
PetBox runs it. It can also change the schema through versioned migrations.

- The client builds the SQL, so the server does not need to know your model classes.
- You can reuse the same query against a local database. See "Client packages".

### Tasks

**Problem:** tasks get lost without rules, and teams have different rules.
**Solution:** each board follows a methodology that you define: statuses and allowed moves. Mine
is idea, then spec, then work, with an intake board for new requests. The engine blocks moves that
break the rules. See [doc/methodology.md](doc/methodology.md).

**Observations.** An agent often sees a bug that is not part of its task. Each project has a
special board for such findings. If another agent reports the same bug, PetBox adds no copy. It
counts the repeats. A person can turn an observation into a task. When the task is done, the
observation closes. If the bug returns, it opens again.

### Memory

**Problem:** search by words misses a note written with other words. Search by meaning misses
exact names and error codes.
**Solution:** PetBox runs both searches and merges the lists with Reciprocal Rank Fusion (RRF).
An item that ranks high in both lists wins. A language model can then reorder the top results.
The merge code is in `src/PetBox.Core/Search/HybridMerge.cs`.

- **History.** PetBox never overwrites a note. Each change is a new revision, and one revision is current.
- **Duplicates.** Before saving a fact, PetBox compares it with old facts, first by text, then by meaning. A near copy is not saved.

### Sessions

**Problem:** an agent works for hours, and the chat is lost.
**Solution:** the agent saves a transcript of each session. A background job writes a short
summary, called a digest. Later the agent can search old sessions to find why it chose something.

Search has two steps. First it finds the right sessions by their digests. Then it looks inside them.

- For each session found, PetBox builds a temporary in-memory DuckDB index. It has full-text search with Russian stemming and BM25 ranking. Message embeddings are compared by cosine.
- The two result lists are merged with RRF. If one list fails, the other still answers.
- The index is built on demand and dropped after about ten minutes idle. Settings are under `Search:Episodic`.
- Code: `src/PetBox.Sessions/Episodic/DuckDbSessionEpisodicIndex.cs`.

### LLM router

**Problem:** a provider of language models can be down, or can limit your requests.
**Solution:** the router sends each request to the first provider that works, and falls back to
the next one on failure. A circuit breaker stops requests to a failing provider for a short time.
It looks at the type of failure. An unreachable server is skipped for all request types. A "too
many requests" answer is skipped only for that request type. We added this after a chat limit
silently moved embedding requests to a backup server.

### MCP

**Problem:** PetBox has more than 100 tools. Showing all of them confuses an agent, and not every
agent should change data.
**Solution:** MCP is a standard way for an agent to call tools. Each API key has a list of
permissions called scopes. For example, `tasks:write` allows changing tasks.

- **Discovery.** The tool list shows only tools the key may use. Without `tasks:write`, the agent does not see `tasks_upsert`.
- **Honest answers.** `whoami` lists all modules, scopes and tool names. The agent can tell "my key may not use this" from "this tool does not exist". `tool_describe` explains any tool.
- **Safety.** Hiding is not protection. A call without the scope is refused. Each tool declares its scope once, and a test fails the build if one is missing.
- **Test keys.** A sandbox key works only in test projects. PetBox rejects it elsewhere.

### Dashboard and Deploy

- **Dashboard.** It checks the health of your services and polls CI, so you see status in one place.
- **Deploy.** It tracks deployments, so you can see what was deployed and when.

## Client packages

| Package | Registry | What it does |
|---|---|---|
| `PetBox.Client` | NuGet | Authenticated HTTP client. Sends raw SQL to the Data module. |
| `PetBox.Client.Config` | NuGet | Loads resolved settings into `IConfiguration`, with ETag-aware polling. |
| `PetBox.Client.Data.Linq2Db` | NuGet | Write queries in LINQ with linq2db. |
| `@stdray-npm/petbox-client` | npm | TypeScript client for Config and Data. |
| `petbox-client` | PyPI | Python client for Config and Data. |
| `petbox-wire` | npm | One `npx` command that wires a project to PetBox: hooks, MCP settings and skills for Claude Code, opencode and Factory Droid. |

How the LINQ package works:

- You build a normal linq2db `IQueryable`. A SQLite connection is enough, and no connection opens.
- The package extracts the parameterized SQL and sends it through `PetBox.Client`.
- Rows come back as your own class. The server never sees your query objects.
- The same `IQueryable` also runs on a local SQLite file with ordinary linq2db. So one query works on the remote database and on a local copy.

## Build and run

You need the .NET 10 SDK (see `global.json`), bun, and PowerShell or bash.

```sh
./build.ps1 -Target Test        # or: ./build.sh --target=Test
./build.ps1 -Target Dev         # frontend watchers and `dotnet watch run`
./build.ps1 -Target Verify      # Test, plus checks for the client libraries
```

The container listens on port 8080 and stores data in `/app/data`. More targets are in `build.cs`.

## How I keep quality high

- **One gate.** The `Test` target builds the code, checks formatting, lints the frontend and runs more than 4,000 tests. CI runs the same target.
- **Rules as tests.** Tests check the architecture. Modules may not reach into each other. Database connections cannot be injected, because they are not thread-safe. Tenants cannot read each other's data. See `tests/PetBox.Tests/Architecture`.
- **Safe deploys.** The `deploy` tag runs the tests, builds the image, deploys over SSH and checks health. A code-quality scan can block the deploy.
- **Process.** Every change starts from a task card and happens in its own git worktree. See [AGENTS.md](AGENTS.md).
