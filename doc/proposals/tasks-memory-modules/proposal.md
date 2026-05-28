# Proposal: Tasks + Agent Memory modules

**Status:** proposed, **deferred** pending validation experiments (see [decision-log](../../decision-log.md) entry 2026-05-27).
**Author:** обсуждение со stdray 2026-05-27.
**Critique:** [critique.md](critique.md) — жёсткий разбор от стороннего ревьюера.

## Цель

Объединить ведение планов и памяти разных coding-агентов (claude-code, factory droid, opencode, oh-my-pi/pi) в один store с историей и UI для ревью.

## Pain

- Каждый агент пишет план по-своему: `~/.claude/plans/{slug}.md`, `doc/plan.md`, `.factory/...`. У oh-my-pi файл перезатирается.
- Session-plan'ы claude-code теряются при rewrite в течение сессии.
- Память у Claude per-repo + 4 типа (user/feedback/project/reference); у других агентов либо нет, либо иное.
- Между агентами нет общей точки.

## Два модуля

### Tasks

**Назначение:** project-plan (структурированное дерево) + session-plan (markdown blob с ревизиями).

Сущности:
- `ProjectPlan` — 1:1 к petbox Project.
- `PlanNode` — self-referencing tree неограниченной глубины. Поля: `Id, ProjectKey, ParentId?, Order, Title, Status, Body, CreatedAt, UpdatedAt, CompletedAt?`. Статусы: `Pending / InProgress / Done / Blocked / Deferred / Cancelled`.
- `PlanNodeHistory` — append-only ops log (create/update/complete/reorder/delete).
- `PlanNodeRef` — junction-table: `Kind` (Commit/GithubIssue/GithubPr/GitlabIssue/GitlabMr/Jira/Url), `Value`, `Label?`.
- `SessionPlan` — `(workspaceKey, projectKey, agent, slug)` как natural key + `StartedAt, Title?, Model?`.
- `SessionPlanRevision` — `(SessionId, Revision)` + `Content` (markdown blob).

Storage: `data/tasks/{projectKey}.db` через `TasksDbFactory` (паттерн `LogDbFactory`).

### Agent Memory

**Назначение:** общая память агентов с общей 4-типовой схемой + agent-tag.

Сущности:
- `Memory` — `Id, ProjectKey, Type (user/feedback/project/reference), Name (slug), Description, Body (markdown), AgentTags (CSV), CreatedAt, UpdatedAt`.
- `MemoryHistory` — как `PlanNodeHistory`.
- `MemoryRef` — junction по той же схеме что `PlanNodeRef`.
- `MemoryLink` — распарсенные `[[name]]` ссылки, dangling links допустимы.

Storage: `data/memory/{projectKey}.db` через `MemoryDbFactory`.

## MCP-контракт

Endpoints `/mcp/tasks` и `/mcp/memory` внутри `PetBox.Web`. Реализация — отдельные проекты `PetBox.Tasks` / `PetBox.Memory` по шаблону `PetBox.Log.Core`. Auth — существующая X-Api-Key с новыми scope'ами `tasks:read/write`, `memory:read/write`.

**Tasks tools:**
- `plan_get(projectKey)`, `plan_diff(projectKey, since)`, `plan_recent(projectKey, limit)`, `plan_search(query, projectKey?)`
- `node_get(nodeId)`, `node_upsert({parentId?, title, status, body, refs[]})`, `node_complete(nodeId, refs[]?)`, `node_reorder(parentId, orderedIds[])`
- `session_save({workspaceKey, projectKey, agent, slug, model?, content})` → upsert по natural key, возвращает `{sessionId, revision}`
- `session_list(projectKey, limit)`, `session_get(sessionId)`, `session_revision_get(sessionId, revision)`

**Memory tools:**
- `list({projectKey, types?, agentTag?})`, `get(projectKey, name)`, `upsert(projectKey, {name, type, description, body, refs[]})`, `delete(projectKey, name)`, `search(projectKey, query)`, `history(projectKey, name)`

## Sync архитектура

**petbox — канон.** Skill устанавливается агенту:
- **Pull**: при старте сессии → `tasks.plan_get` → render markdown → пишет `doc/plan.md`.
- **Push (MVP write-only)**: агент вызывает MCP-tools при каждой правке. Локальный plan.md/memory dir — устаревший mirror.
- **Stop-hook**: при завершении turn'а `tasks.session_save` для `~/.claude/plans/{slug}.md` + `memory.upsert` для свежеправленных memory-файлов.
- **Bidirectional parser (plan.md → структура)** — НЕ MVP, follow-up.

## Что НЕ делается

- AI-фичи (auto-summarize, suggest-next) — никаких `AiSettings`. Ноль конфигурации модулей.
- Cross-workspace cascade memory — per-project, дублирование user-level памяти принимается.
- Graph view memory-links — list view достаточно.
- Markdown-парсер plan.md → структура — follow-up.

## Фазовая последовательность (sketch)

- Phase A: Tasks backend + MCP + read-only UI
- Phase B: Memory backend + MCP + read-only UI
- Phase C: Claude Code skill + Stop-hook + render plumbing
- Phase D: Data модуль (независимый prerequisite для kpvotes)
- Phase E: kpvotes интеграция через Data
- Phase F: petbox-self dogfooding — `doc/plan.md` мигрирует через MCP

## Открытые вопросы

См. [critique.md](critique.md) — список рисков. Главные:
- Compliance паттерн: claude-code уже сейчас игнорирует более лёгкое требование (записи в `doc/tasks-mcp/`). Tools вызывать тем более не будет автоматически.
- Multi-machine: SessionPlan slug-collision, перезапись локальных правок plan.md при pull, last-write-wins без vector clock.
- Дублирование с git: `PlanNodeRef.commit` дублирует `git log -S`/blame.
- Per-project memory ломает user-level кейсы ("ты senior dev в Go").

## Что делать вместо (рекомендации критики)

1. **Stop-hook + git auto-commit** на правки plan/memory. 50 строк bash вместо двух C# модулей.
2. Готовый **mcp-server-memory** (Anthropic) для памяти.
3. Если строить — минимальный **Notes** модуль (один тип, markdown+frontmatter+tags), версионирование через git.
