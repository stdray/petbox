# Architectural Decision Log

Newest decisions on top. Each entry: short title, date, context, decision, consequences.

---

## 2026-05-28 — Data module design approved (multi-DB, REST pass-through, DbUp)

**Context.** Phase 16 был BLOCKED по нерешённым вопросам: storage layout, API shape, schema ownership. Memory: "Data — не сделан, возможно не строить (Turso/PocketBase делают лучше)". Dispatcher текущий — заглушка (stub endpoints, misleading DataTable model). Запрос /plan через несколько раундов discovery + 2 раунда critique привёл к утверждённой архитектуре.

**Decision.** Build своего Data модуля по плану `C:\Users\stdray\.claude\plans\noble-sniffing-bear.md`. Ключевые архитектурные решения:

1. **Storage**: per-project-per-db SQLite `data/db/{projectKey}/{dbName}.db` через `DataDbFactory`. Multiple DBs per project, явно создаются через lifecycle endpoint (не auto-create on first touch). Operational defaults в Wave 1: WAL mode + `max_page_count` quota (~1GB default) + 30s `CommandTimeout` + PRAGMA whitelist на /exec.
2. **Read/Write protocol**: raw SQL pass-through через `POST /api/data/{p}/{db}/{query|exec}`. Без KQL (двойной dialect — UX-ошибка). Без statement classifier (pet знает свой SQL через ORM, SQLite errors прокидываются). Parameter binding через Microsoft.Data.Sqlite.
3. **Schema management**: DbUp (`dbup-core` + `dbup-sqlite` 6.0.4) + custom `SqliteHashingJournal` (~100-150 LOC, subclass `SqliteTableJournal`, добавляет hash в `__SchemaVersions`). Idempotency по (name, hash) с 409 на mismatch. `__SchemaVersions` живёт внутри каждой DataDb файла.
4. **Migration entry**: API (scope `data:schema`) + UI-paste (workspace-admin). Liquibase XML через `DbUp.Extensions` — Wave 5+ optional.
5. **Auth**: ApiKey scopes `data:read/write/schema`. ApiKey project-level видит все DataDbs данного project'а. **Никаких per-DB/per-table flags** — только scope-level enforcement.
6. **gRPC + linq2db.Remote.Grpc — drop** (архитектурный mismatch с DDL-driven схемой).
7. **Pet client MVP**: thin send-helper, native ORM SQL extraction. **Future Wave 5+**: yobabox-side обёртка над `LinqToDB.Remote.HttpClient.Server` (готовое в linq2db), pet-side использует `LinqToDB.Remote.HttpClient.Client` NuGet — full linq2db UX end-to-end. Не custom IDataProvider с нуля.
8. **MCP отложен в Wave 4** (не MVP). Когда подключится — единый `/mcp` endpoint на весь yobabox через `ModelContextProtocol.AspNetCore` SDK, не модуль-специфичный.
9. **Driver pet — kpvotes-net** (Wave 3 dogfooding gate).

**Phasing**: Wave 0 (critique + linq2db PoC gate) → Wave 1 (foundation + APIs combined) → Wave 2 (UI + dogfood через REST) → Wave 3 (real pet integration) → Wave 4 (MCP) → Wave 5+ (deferred).

**Consequences.**
- Phase 16 в `doc/plan.md` разблокирован, будет заменён фазами из noble-sniffing-bear плана при имплементации.
- Существующая `DataTables` table остаётся untouched в M013 (data disabled, dead schema — drop отдельной cosmetic миграцией позже).
- Backup pre-Wave-5 требует service stop (hot-backup через `VACUUM INTO` — Wave 5 feature, не обещаем "cp достаточно").
- Wave 0 gate с rubric (0 RED + ≤3 YELLOW → build, 1+ RED → defer, ≥4 YELLOW → refactor) — защита от premature implementation.
- Plan отправлен в Ultraplan параллельно для remote refinement; локальный план — canonical source of truth.

---

## 2026-05-27 — Tasks + Agent Memory modules deferred

**Context.** Обсуждался дизайн двух тесно связанных модулей: **Tasks** (project-plan + session-plan со структурированным деревом, MCP-каноном и skill-синком) и **Agent Memory** (общий store по claude-code модели с 4 типами + agent-tag). Полная фактура — `doc/proposals/tasks-memory-modules/proposal.md`.

Запущен сторонний reviewer (general-purpose agent) с инструкцией дать жёсткую критику. Результат — `doc/proposals/tasks-memory-modules/critique.md`. Verdict: "не строить как сейчас, в честном — не делать вообще".

**Главные проблемы по критике:**
1. **Compliance-парадокс.** claude-code уже сейчас игнорирует более лёгкое требование (записи в `doc/tasks-mcp/`). MCP-tools с auth и upsert будут вызываться ещё реже, не чаще.
2. **Велосипед.** Существуют: mcp-server-memory (Anthropic), Linear/GitHub Issues + sub-issues, Obsidian+git, Logseq. Дублирование без явного выигрыша для single-user-pet-project.
3. **Дублирование с git.** `PlanNodeRef.commit` дублирует `git log -S`/blame; ревизионная история plan.md — git и так это делает.
4. **Multi-machine — реальные дыры.** SessionPlan slug-collision между machines, перезапись локальных правок plan.md при pull, last-write-wins без vector clock.
5. **Bidirectional sync как write-only mirror.** plan.md становится disposable. Хуже чем сейчас, где plan.md — source of truth.
6. **Per-project memory ломает user-level кейсы.** "Ты senior dev в Go" дублируется в каждом проекте и расходится.

**Decision.** Модуль **отложен в конец roadmap** до проведения двух экспериментов:
- **(a) Stop-hook эксперимент.** Поставить hook на claude-code, который при завершении turn'а пишет mock-запись (echo в файл) о правках `doc/plan.md` / `~/.claude/plans/*.md`. Поработать 2 недели. Посчитать compliance vs реальные правки. Если <70% — модуль не построим, даже когда напишем.
- **(b) Honest bench.** Дать claude-code / factory droid / opencode / pi одну и ту же задачу. Сравнить artifacts. Понять, нужна ли унификация или у всех уже git'абельный markdown.

После экспериментов — решить заново: строить как пропозал, строить минимальный Notes-модуль (один тип, markdown+tags+upsert, git как history), или не строить вовсе и обойтись Stop-hook + git auto-commit.

**Consequences.**
- `doc/plan.md` — добавлена `Phase 30: Tasks + Memory modules [DEFERRED]` в конце с ссылкой на пропозал и критику.
- Все упоминания Tasks как "next priority module" в memory (`project_overview.md`) остаются — модуль не отменён, отложен.
- Roadmap впереди: `Phase 16: Data module` (BLOCKED → разблокировка), kpvotes интеграция, dogfooding. Tasks/Memory — после.
- Bench в `doc/tasks-mcp/` остаётся как journal, но требование записывать туда снимается до момента когда модуль реально начнём строить (тогда вернётся вместе с design'ом).
