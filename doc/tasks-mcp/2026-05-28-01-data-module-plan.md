---
timestamp: 2026-05-28T18:00:00+03:00
agent: claude-code
model: claude-opus-4-7
session: data-module-design
target: plan
action: create
target_file: C:\Users\stdray\.claude\plans\noble-sniffing-bear.md
---

## What

Создан session-plan для дизайна Data модуля YobaBox: `C:\Users\stdray\.claude\plans\noble-sniffing-bear.md`. План прошёл несколько итераций discovery → design → critique → revision и был approved.

Параллельно отправлен в Ultraplan для удалённого refinement (PR landing separately).

## Why

Пользователь запросил `/plan` для Data модуля. Текущий Data-скелет в репо — заглушка (stub endpoints, misleading DataTable model). Memory автора указывала "yobabox хостит sqlite per pet через linq2db.remote.grpc для .NET и postgres-like REST для python/ts", Phase 16 в `doc/plan.md` был BLOCKED по нерешённым вопросам.

В процессе:
1. Прошёл discovery (3 раунда вопросов + ответы): build vs buy, API surface, schema ownership, driver pet, storage scope, auth granularity, migration format, transactions, MCP standard
2. Запустил Plan agent для technical review — выявил dynamic schema mismatch с linq2db.Remote.Grpc, дублирование с git/PocketBase
3. Запустил критика agent #1 — выявил KQL UX-ошибку, dogfooding trap
4. Применил pivot: KQL → raw SQL pass-through, drop gRPC, multi-DB per project, DbUp+hash journal
5. Запустил критика agent #2 (финальный) — verdict YELLOW с 8 правками
6. Применил все 8 правок + новую информацию от пользователя про `LinqToDB.Remote.HttpClient` как future provider base

## Args

Final plan structure:
- Context: pet-infra escape from local SQLite pain, multi-DB per project, agentic eventual
- Storage: per-project-per-db SQLite via DataDbFactory, operational defaults (WAL + quota + timeout + PRAGMA whitelist)
- Schema: DbUp + custom SqliteHashingJournal (subclass SqliteTableJournal, ~100-150 LOC)
- Query/Exec: raw SQL pass-through, statement classifier dropped, SQLite errors прокидываются
- Pet client: MVP thin send-helper (linq2db `.ToString()` PoC required in Wave 0), Future LinqToDB.Remote.HttpClient
- Phasing: Wave 0 (critique + PoC) → Wave 1 (foundation + APIs combined) → Wave 2 (UI + dogfood) → Wave 3 (real pet) → Wave 4 (MCP) → Wave 5+ (out of scope)
- Wave 0 rubric: 0 RED + ≤3 YELLOW → build, 1+ RED → defer, ≥4 YELLOW → refactor wave

## Outcome

Plan approved через ExitPlanMode. Implementation запланирована через Ultraplan PR + локальные Wave 0 gates (PoC + critique). Plan file сохранён на диск как канонический локальный source-of-truth.
