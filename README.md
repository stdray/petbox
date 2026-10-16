[Русская версия](README.ru.md)

# PetBox

PetBox is one server with two separate directions. They share a codebase, but you can use them apart.

1. **A system for developing with AI agents.** Memory, sessions, task boards with a methodology you configure, and tools for the agents. This is the main part.
2. **A light self-hosted backend for pet projects.** Settings, logs and databases, with small client packages.

I built it for my own projects, and I build PetBox using PetBox. Its plan and tasks live on its own boards.

It is a "modular monolith": one program made of clearly separated modules. Each module has a
switch in the settings. A module that is off costs nothing.

## Stack

- **Languages:** C# on .NET 10, TypeScript, Python (client only).
- **Web:** ASP.NET Core with Razor Pages rendered on the server. In the browser: htmx and Alpine.js. Styles: Tailwind and daisyUI. bun builds the frontend.
- **Databases:** SQLite everywhere, accessed with linq2db. FluentMigrator changes the schema.
- **Search:** SQLite FTS5 for words, with Snowball stemming of Russian and English tokens (`src/PetBox.Core/Search/TokenStemmer.cs`). DuckDB for session search. Embeddings for meaning. A language model can rerank.
- **Logs and queries:** Kusto.Language parses KQL, and PetBox runs it on SQL. OpenTelemetry and Seq formats come in. OpenTelemetry also traces PetBox itself.
- **Agents:** a Model Context Protocol (MCP) server, and a router for language models.
- **Delivery:** Docker, Cake with GitVersion, GitHub Actions, Playwright for browser tests.

# Part A. Developing with AI agents

An AI agent forgets everything when a chat ends. It does not know what it decided last week, and
nothing tells it which work is open. PetBox gives the agent memory, a process and tools.

## Methodology: the process is a graph, and it is data

**Problem:** every team has its own process. Hard-coded statuses fit one team and annoy the rest.

**Solution:** the process is a graph that you describe in a document. The server has no
built-in process. It reads your document and enforces it.

- **Nodes and boards.** A board holds one kind of work item, such as an idea, a spec item, a task or an intake request.
- **Statuses.** Each kind has statuses. Each status is open, finished-ok or finished-cancelled.
- **Transitions.** The document lists the allowed moves between statuses. A move can require approval or a named artifact, such as a written reason.
- **Links.** A rule can say: "a new task must link to a spec item". Another rule can say: "when this task is done, move the linked item".
- **Instances.** A project can run several processes at once. Each one is a named instance.
- **Presets and templates.** You start from a built-in preset (`quartet`, `classic` or `simple`), a saved template, or another instance. Then you change it.
- **Safe edits.** A change is validated before it is saved. Every revision is kept. Two people editing at once get a conflict, not a silent overwrite.

You edit the document through MCP tools (`tasks_methodology_*`), so an agent can read the rules
before it acts. My own process is: idea, then spec, then work, with an intake board for new
requests. Details: [doc/methodology.md](doc/methodology.md) and
[doc/methodology-engine.md](doc/methodology-engine.md).

## Observations

**Problem:** an agent often sees a bug that is not part of its task. The finding is easy to lose.

**Solution:** each project has a special board for such findings. If another agent reports the
same bug, PetBox adds no copy. It counts the repeats. A person can turn an observation into a
task. When the task is done, the observation closes. If the bug returns, it opens again.

## Memory

**Problem:** search by words misses a note written with other words. Search by meaning misses
exact names and error codes.

**Solution:** PetBox runs both searches and merges the lists with Reciprocal Rank Fusion (RRF).
An item that ranks high in both lists wins. A language model can then reorder the top results.
The merge code is in `src/PetBox.Core/Search/HybridMerge.cs`.

- **History.** PetBox never overwrites a note. Each change is a new revision, and one revision is current.
- **Duplicates.** Before saving a fact, PetBox compares it with old facts, first by text, then by meaning. A near copy is not saved.
- **Quality.** An evaluation harness scores retrieval on labeled queries, so a change to ranking can be measured (`src/PetBox.Core/Search/Eval/SearchEval.cs`, tests in `SearchEvalHarnessTests`).

## Sessions

**Problem:** an agent works for hours, and the chat is lost.

**Solution:** the agent saves a transcript of each session. A background job writes a short
summary, called a digest. Later the agent can search old sessions to find why it chose something.

Search has two steps. First it finds the right sessions by their digests. Then it looks inside them.

- For each session found, PetBox builds a temporary in-memory DuckDB index. It has full-text search with Russian stemming and BM25 ranking. Message embeddings are compared by cosine.
- The two result lists are merged with RRF. If one list fails, the other still answers.
- The index is built on demand and dropped after about ten minutes idle. Settings are under `Search:Episodic`.
- Code: `src/PetBox.Sessions/Episodic/DuckDbSessionEpisodicIndex.cs`.

## LLM router

**Problem:** a provider of language models can be down, or can limit your requests.

**Solution:** the router sends each request to the first provider that works, and falls back to
the next one on failure. A circuit breaker stops requests to a failing provider for a short time.
It looks at the type of failure. An unreachable server is skipped for all request types. A "too
many requests" answer is skipped only for that request type. I added this after a chat limit
silently moved embedding requests to a backup server.

## MCP: tools for agents

**Problem:** PetBox has more than 100 tools. Showing all of them confuses an agent, and not every
agent should change data.

**Solution:** MCP is a standard way for an agent to call tools. Each API key has a list of
permissions called scopes. For example, `tasks:write` allows changing tasks.

- **Discovery.** The tool list shows only tools the key may use. Without `tasks:write`, the agent does not see `tasks_upsert`.
- **Honest answers.** `whoami` lists all modules, scopes and tool names. The agent can tell "my key may not use this" from "this tool does not exist". `tool_describe` explains any tool.
- **Safety.** Hiding is not protection. A call without the scope is refused. Each tool declares its scope once, and a test fails the build if one is missing.
- **Test keys.** A sandbox key works only in test projects. PetBox rejects it elsewhere.

## Process gates

**Problem:** agents write code fast, and they can skip steps.

**Solution:** some rules are checked by machines. Others are team conventions that I wrote down in [AGENTS.md](AGENTS.md).

Checked by machines:

- One `Test` target is the gate: build, formatting, frontend lint and more than 4,000 tests. CI runs the same target.
- Architecture tests fail the build when a layer rule breaks.
- A test fails the build if an MCP tool has no scope declaration.

Conventions in AGENTS.md. The engine does not enforce them:

- Every change starts from a task card and happens in its own git worktree.
- Work ends as a pushed branch. An agent moves a card to Review, and the owner moves it to Done.
- A card is closed with a written verdict. The server once enforced this, but I removed the check, because the web UI had no way to write a verdict.

# Part B. A light backend for pet projects

A small project also needs settings, logs and a database. Three separate services are too much.
PetBox provides all three in one process.

## Config

**Problem:** the same setting has different values in different places.

**Solution:** you attach a value to a set of tags, for example `project:shop`, then
`service:api`, then `env:prod`. A request brings its own tags. PetBox picks the value whose tags
are all present in the request. The value with the most matching tags wins. Two equal winners
give an error, not a guess, so the answer is always the same.

- A default value has few tags. A production override has more tags, so it wins.
- The .NET client plugs into `IConfiguration` and polls for changes.

## Log

**Problem:** small projects need logs, traces and metrics, but not a big logging platform.

**Solution:** PetBox accepts logs from Seq clients (the CLEF format) and from OpenTelemetry
(logs, traces and metrics). Each project has its own store. You query it with KQL, a query language
from Azure Data Explorer.

- PetBox parses KQL with the official Kusto.Language library, then turns it into SQL.
- Supported: `where`, `take`, `sort`, `top`, `project`, `extend`, `summarize`, `count`, `distinct`, `join`, `lookup`, `mv-expand` and `parse`.
- Not supported: `union`, `search`, `render`, `make-series` and many others. Unsupported operators give a clear error.
- The same language works on three tables: events, spans and metric points.
- There is live tail, retention cleanup and shareable results.

**Two SQL backends.** The translator is built for two databases, not one. It has a SQLite
dialect and a DuckDB dialect. They differ in details such as JSON functions and array expansion,
so each has its own set of SQL rules. A smoke test (`KqlDuckDbSmokeTests`) loads the same data into both databases, runs queries covering each group of functions, and checks that the DuckDB result equals the SQLite result.
In production, logs are stored in SQLite. The DuckDB dialect is ready, but no setting turns it on yet.

## Data

**Problem:** a small project needs a database, but not a database server.

**Solution:** each project gets its own SQLite files. You send parameterized SQL over REST, and
PetBox runs it. It can also change the schema through versioned migrations.

- The client builds the SQL, so the server does not need to know your model classes.
- You can reuse the same query against a local database. See "Client packages".

## Dashboard and Deploy

- **Dashboard.** It calls the health URL of each service you list, on a timer. Services can also push their status. The status page marks a service as stale when its last report is too old.
- **Deploy.** It keeps a list of nodes and the services that should run on them. Agents on the nodes poll for their work and send heartbeats. If a node is silent for 90 seconds, PetBox moves its movable services to an online node.

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

# Engineering practices

- **One gate.** The `Test` target builds the code, checks formatting, lints the frontend and runs more than 4,000 tests. CI runs the same target.
- **Rules as tests.** Tests check the architecture. Modules may not reach into each other. Database connections cannot be injected, because they are not thread-safe. Tenants cannot read each other's data. See `tests/PetBox.Tests/Architecture`.
- **Safe deploys.** The `deploy` tag runs the tests, builds the image, deploys over SSH and checks health. A code-quality scan can block the deploy.

# Build and run

You need the .NET 10 SDK (see `global.json`), bun, and PowerShell or bash.

```sh
./build.ps1 -Target Test        # or: ./build.sh --target=Test
./build.ps1 -Target Dev         # frontend watchers and `dotnet watch run`
./build.ps1 -Target Verify      # Test, plus checks for the client libraries
```

The container listens on port 8080 and stores data in `/app/data`. More targets are in `build.cs`.

# Status

- One author. I build and maintain PetBox alone.
- It runs in production for my own projects.
- It is still in active development.

# License

MIT. See [LICENSE](LICENSE).
