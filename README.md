[Русская версия](README.ru.md)

# PetBox

PetBox is a server that helps AI coding agents remember things and organize their work.

An AI agent forgets everything when a chat ends. It does not know what it decided last week.
It cannot tell which tasks are open. PetBox fixes this. It gives the agent a notebook, a task
board and a set of tools, all in one service. I built it for my own projects, and I build PetBox
using PetBox: its plan and tasks live on its own boards.

It is a "modular monolith". That means one program made of clearly separated modules. You can
switch each module on or off with a setting. A module that is off costs nothing.

## How the agent remembers

### Sessions

**Problem:** an agent works for hours, and the chat is lost afterward.
**Solution:** the agent saves a transcript of each work session. A background job then writes a
short summary of it, called a digest. Later, the agent can search old sessions to find why it
made a decision.

### Memory

**Problem:** keyword search misses a note when you use different words. Meaning-based search
misses exact names and error codes.
**Solution:** PetBox runs both searches and merges the two result lists. The merge method is
Reciprocal Rank Fusion (RRF): an item that ranks high in both lists wins. A language model can
then reorder the top results.

- Keyword search uses SQLite FTS5. Meaning-based search uses embeddings, which turn text into numbers.
- The merge code is in `src/PetBox.Core/Search/HybridMerge.cs`.

### History and duplicates

**Problem:** agents change their notes, and sometimes an old note was right.
**Solution:** PetBox never overwrites a note. It saves each change as a new revision and marks
one revision as current. You can always see the old versions.

**Problem:** agents save the same fact many times in different words.
**Solution:** before saving a fact, PetBox compares it with the existing facts. It checks the
exact text first. Then it checks the meaning. If the new fact is too close to an old one, PetBox
does not save it again.

### Observations

**Problem:** an agent sees something broken, but it is not part of the current task. The finding
is easy to lose.
**Solution:** each project has a special board called observations. The agent writes the finding
there. If another agent reports the same problem, PetBox does not add a copy. It counts how often
the problem occurs. A person can later turn an observation into a real task. When the task is
done, the observation closes. If the problem comes back, it opens again.

## How the agent stays working

### The LLM router

**Problem:** the agent calls language models from several providers. A provider can be down or
can limit how many requests you send.
**Solution:** the router sends each request to the first provider that works. If that one fails,
it tries the next. A "circuit breaker" stops requests to a failing provider for a short time.

The breaker looks at the type of failure.

- If the server is unreachable, the router skips the whole provider.
- If the provider says "too many requests", the router skips only that one kind of request.

We added this rule after a real incident. A limit on chat requests had silently pushed
embedding requests to a backup server.

### Tools for agents (MCP)

**Problem:** PetBox has more than 100 tools. If you show all of them, the agent gets confused.
Some tools can change data, so not every agent should use them.
**Solution:** MCP is a standard way for an agent to call tools. Each API key gets a list of
permissions, called scopes. For example, the scope `tasks:write` allows changing tasks.

- **Discovery.** The tool list shows only the tools that the key may use. Without `tasks:write`, the agent does not see `tasks_upsert`.
- **Honest answers.** The tool `whoami` lists all modules, all scopes and all tool names. The agent can tell "my key may not use this" from "this tool does not exist". The tool `tool_describe` explains any tool by name.
- **Safety.** Hiding a tool is not the protection. If a key calls a tool without the scope, PetBox refuses the call. Every tool declares its scope once, and a test fails the build if one is missing.
- **Test keys.** A "sandbox key" works only in test projects. PetBox rejects it anywhere else, so a test run cannot touch real data.

### Task boards

**Problem:** tasks need rules, or they get lost. Teams differ in what those rules are.
**Solution:** each board follows a methodology that you define. A methodology is a set of
statuses and allowed moves. Mine is: idea, then spec, then work, then intake for new requests.
The engine blocks moves that break the rules. Details are in [doc/methodology.md](doc/methodology.md).

## What else is inside

PetBox also holds a settings service, a log collector with a query language (KQL), a separate
SQLite database for each project, a health and CI dashboard, and deploy tracking.

| Module | Job |
|---|---|
| `PetBox.Core` | login, data models, migrations, search |
| `PetBox.Web` | the web app and the MCP endpoint |
| `PetBox.Tasks`, `PetBox.Tasks.Engine` | task boards and methodology rules |
| `PetBox.Memory` | notes and memory search |
| `PetBox.Sessions` | transcripts and digests |
| `PetBox.LlmRouter` | the language model router |
| `PetBox.Config`, `PetBox.Log.Core`, `PetBox.Data` | settings, logs, project databases |
| `PetBox.Dashboard`, `PetBox.Deploy` | health checks, deploy tracking |

Client libraries for .NET, TypeScript and Python are in `src/clients-net`, `src/clients-ts` and
`src/clients-py`.

## Stack

- Backend: .NET 10, SQLite with linq2db, FluentMigrator, OpenTelemetry.
- Frontend: Razor Pages rendered on the server, htmx, Alpine.js, Tailwind with daisyUI, TypeScript built by bun.
- Delivery: Docker, Cake with GitVersion, GitHub Actions, Playwright for browser tests.

## Build and run

You need the .NET 10 SDK (see `global.json`), bun, and PowerShell or bash.

```sh
./build.ps1 -Target Test        # or: ./build.sh --target=Test
./build.ps1 -Target Dev         # frontend watchers and `dotnet watch run`
./build.ps1 -Target Verify      # Test, plus checks for the client libraries
```

The container listens on port 8080 and stores data in `/app/data`. More targets are in `build.cs`.

## How I keep quality high

- **One gate.** The `Test` target builds the code, checks formatting, lints the frontend and runs more than 4,000 tests. CI runs the same target. A green local run means a green pipeline.
- **Rules as tests.** Tests check the architecture itself. Modules may not reach into each other. Database connections cannot be injected, because they are not thread-safe. Tenants cannot read each other's data. See `tests/PetBox.Tests/Architecture`.
- **Safe deploys.** Pushing the `deploy` tag runs the tests, builds the image, deploys over SSH and checks health. A code-quality scan can block the deploy.
- **Process.** Every change starts from a task card and happens in its own git worktree. The rules are in [AGENTS.md](AGENTS.md).
