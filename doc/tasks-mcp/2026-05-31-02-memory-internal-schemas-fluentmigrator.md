---
timestamp: 2026-05-31T12:05:00+03:00
agent: claude-code
model: claude-opus-4-8
target: memory
action: create
target_file: C:\Users\stdray\.claude\projects\D--my-prj-petbox\memory\project_internal_schemas_fluentmigrator.md
---

## What
Создана memory-запись с инвариантом: внутренние схемы petbox (Core/Tasks/Memory/Sessions, в перспективе Logs/Config) версионируются через FluentMigrator с Core-инвариантами (WAL/busy_timeout/partial-unique-active-index); DbUp зарезервирован только за пользовательским Data-тиром.

## Why
В ходе планирования Фазы 30 пользователь поправил: DbUp был выбран как компромисс для внешних авторов сервисов, а внутри petbox 100% сценариев должны идти через FluentMigrator. Это неочевидно из кода (где Tasks/Memory/Sessions сейчас на ручном Ensure DDL) — durable архитектурное решение.

## Args
name: project-internal-schemas-fluentmigrator; type: project; body: правило + Why (хрупкий ручной DDL, риск порчи по критику) + How-to-apply (новая миграция в сборке тира, per-file MigrateUp). Линки: [[project-data-module-plan]], [[reference-linq2db-clone]].

## Outcome
Успех. Добавлена строка в MEMORY.md (Project context).
