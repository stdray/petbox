---
timestamp: 2026-05-29T15:00:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: create
target_file: ~/.claude/plans/parallel-herding-haven.md
---

## What
Создал session-план `parallel-herding-haven.md` — «User-creatable named logs + generic scoped DB
factory + unified entity CRUD». Две фазы. **Фаза 1**: обобщённая фабрика `IScopedDbFactory<TContext>`
(`scope-key → файл + контекст`, переиспользует enum `Scope`), множественные именованные логи на
проект (`logs/{projectKey}/{logName}.db`) по образцу `DataDbs`, path-based ingestion
(`/api/ingest/{projectKey}/{logName}/...`), таблица-метаданных `Logs` (миграция M016), per-log
query/live-tail/share, авто-создаваемый self-log petbox, retention + orphan-cleanup на лог,
admin-UI создания/удаления логов, удаление «Logs (all)», перевод `ConfigDbFactory` на новую фабрику.
**Фаза 2**: обобщённые MCP-инструменты `entity.create/list/delete/describe` с параметром `type`
({project, service, apikey, db, log, config_binding}); type-specific операции (SQL query/exec/
schema_apply, KQL query) остаются отдельными; запрещённые комбинации → ошибка.

## Why
Пользователь через `/plan` попросил дать пользователям (UI + агент) самим создавать логи (сейчас один
лог на проект, создаётся неявно при первой записи). Его рамка: «нет принципиальной разницы между
созданием БД и созданием лога» → лог должен стать сущностью как `DataDb`. Плюс унификация 12 MCP-
инструментов в части CRUD и переход на generic scope-фабрику (Scope уже введён для defaults-страниц).
Discovery (4 Explore-агента) подтвердил факты; четыре уточняющих вопроса дали решения: авто-создаётся
только self-log petbox (в проектах логов нет, ingestion в несуществующий лог → ошибка); адресация
path-based + fallback `default`; MCP — generic `entity.*`; работа разбита на фазы.

## Args
Plan sections: Context (3 follow-ons), Decisions, Scope guard ($system как system workspace/project
НЕ трогаем — меняем только log-routing использования), Phase 1 (1a генерик-фабрика, 1b Logs-таблица,
1c ingestion routing, 1d per-log read/tail/share, 1e self-log, 1f retention+orphan, 1g UI, 1h Config
на фабрику), Phase 2 (entity.* CRUD), Verification, Record-file note. Ключевые находки: 11 src + 2
test call-sites `GetLogDb`; channel/broadcaster keyed по projectKey → станут `{projectKey}/{logName}`;
`RetentionService.cs:51-73` итерирует проекты; шаблоны `M013_DataDbs`, `OrphanCleanupService`,
`Pages/Admin/ProjectData*` для зеркалирования; `Scope.cs:6-14`.

## Outcome
План утверждён пользователем (ExitPlanMode approved). Реализация начинается с Фазы 1 (1a — generic
factory). decision-log.md обновить отдельным шагом.
