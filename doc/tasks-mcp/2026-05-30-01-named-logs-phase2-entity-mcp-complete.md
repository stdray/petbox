---
timestamp: 2026-05-30T12:00:00+03:00
agent: claude-code
model: claude-opus-4-8
session: entity-mcp-phase2-autonomous
target: plan
action: update
target_file: doc/plan.md
---

## What
Реализована named-logs **Phase 2** — обобщённые MCP-инструменты `entity.*`, последняя открытая задача
плана `parallel-herding-haven.md`. Новый `src/PetBox.Web/Mcp/EntityTools.cs`:
`entity.create / entity.list / entity.delete / entity.describe` с дискриминатором `type` и реестром
хендлеров. Типы: `project`, `apikey`, `config_binding`, `db`, `log`. Удалён `ProvisioningTools.cs`
(3 tool'а), из `DataTools.cs` вырезаны `list/create/delete/describe_db` (остались
`data.query/exec/schema_apply`). Обновлён `doc/plan.md` §27.2, `doc/agent-onboarding.md`,
`doc/data-client-pattern.md`, описание scope `admin:provision`.

## Why
Единственная незакрытая задача (#10). Дизайн утверждён ещё в плане named-logs (запись
`2026-05-29-04`): «нет принципиальной разницы между сущностями» → единый CRUD-фасад вместо 6+
per-entity tool'ов. Пользователь: «продолжай / делай всё кроме критических вопросов».

## Args / решения реализации
- **Тип `service` отброшен** — сущность `Service` удалена в W2 (Health-редизайн); в реестре её нет.
- **Auth per-handler, две семьи:** provisioning (`project`/`apikey`/`config_binding`) → только
  `admin:provision`, без project-claim (cross-project onboarding); project-scoped (`db`/`log`) →
  project-claim cross-check + module scope (`data:*` / `logs:*`). Зеркалит REST/прежние tools.
- **Матрица запрещённых `(type, op)`:** `project` — без `delete` (иначе осиротит logs/dbs/keys →
  `NotSupportedException`); `describe` только для `db` (таблицы+колонки); остальные describe → forbidden.
- **Форма tool'ов:** `type` + JSON-объект (`props`/`filter`/`key`) — generic-фасад теряет per-field
  schema, но это явная цель «generic entity.*»; Description перечисляет поля по типам.
- **Gotcha:** `JsonElement filter = default` ломал старт MCP-сервера (сериализация default-значения
  параметра в схему → `InvalidOperationException` на `Undefined`). Чинится `JsonElement? filter = null`
  (как `data.exec`'s `@params`). До фикса падал ВЕСЬ `/mcp` (все 18 тестов коллекции, включая
  нетронутый `log.query`).
- **Breaking, без shim:** старые имена tool'ов удалены (соло-юзер, `feedback_no_legacy_redirects`).

## Outcome
Полный прогон: **325/325** зелёных (+4 `EntityToolsTests` — log CRUD, db describe, forbidden-op,
unknown-type; +1 `CreateProject_DeleteIsForbidden`; обновлены `McpDataToolsTests` +
`ProvisioningToolsTests` на `entity.*`). `dotnet format --verify-no-changes` чисто. Коммит
`951d4a5`. Этим закрыт весь план named-logs (Phase 1 отгружен ранее) и единственная остававшаяся
задача backlog'а — открытых задач плана больше нет.
