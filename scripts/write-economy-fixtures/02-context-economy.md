Оценка 2026-06-04 (на ожидании деплоя `tag-grouping-view`): сколько контекста жрёт PetBox-MCP и где резать. Доказательства — из этой же сессии (собственные tool-результаты) + два read-only прохода по коду. Канон-черновик в плане `parallel-roaming-cosmos.md`. **Статусы/мнения — к делиберации, владелец думает.**

## Главный сток: write-echo
`tasks.upsert`/`tasks.delta` возвращают `Added/Updated` как `PlanNodeDelta` с ПОЛНЫМ `Body` КАЖДОГО узла, у которого `Version > sinceVersion` — то есть «что изменилось на борде», а не «что я записал». Якоря: `PlanNodeDelta` (полное `Body`, без среза) `src/PetBox.Tasks/Contract/TaskViews.cs:37-39`; дельта `x.Version > sinceVersion` в `TemporalStore.DeltaAsync`; сериализация в `TasksTools` (Upsert/Delta, дефолт `sinceVersion=0`).

**Видела вживую в этой сессии:** accept одной идеи (передала `sinceVersion:64`) вернул полные тела `config-bindings-ui` и `logs-share-events` (чужие, version 65); spec-запись вернула тела ВСЕХ ~23 spec-узлов; флип одного статуса work вернул полные тела `node-edit-ui-impl` и `llmrouter-module-v1`. Каждое лишнее тело ~1–10 КБ. Footgun: дефолт `sinceVersion=0` + естественная ошибка передать версию *узла* (она < версии борда) → передампливается вся недавняя дельта.

## Read-path: тела везде, но один тул уже решил это
- `tasks.get` (без `groupBy`) — полное `Body` каждого узла (`PlanNodeView.Body`). Борд на 60 узлов = один большой блоб.
- Memory: `recall/list/search/get` — полное `body`; `recall` дефолт `limit=20`, у `search`/`list` лимита НЕТ; сниппет-режима нет (`MemoryTools.cs`).
- Sessions: `session.get` — весь append-only блоб, без tail/offset.
- **Уже компактно (шаблон для копирования):** `methodology_get` → `PlanNodeHeader` (без тела) + opt-in `bodyLen`-срез (`…` при обрезке); `tasks.get?groupBy` → только ключи (проверено вживую: вложенные `subGroups`, тел нет).

## Каталог тулов — НЕ проблема
56 `[McpServerTool]`, имена впереди, схемы по требованию (ToolSearch). 5 самых длинных `[Description]` (upsert ~21 стр, get ~18, methodology_get ~14, memory.upsert ~13, recall ~10) — нагружающие, но on-demand. Низкий приоритет.

## Рычаги (приоритет)
**P1 — компактный write-echo (макс выигрыш, мин риск).** Вызывающий почти никогда не нуждается в теле узлов, которые сам же отправил, ни в телах чужих изменившихся. (1) Выключить тела в echo по умолчанию: `Added/Updated` несут `key/nodeId/status/type/title/version`, тело — по opt-in `bodyLen` (переиспользовать срез из `methodology_get`); реализовать как опц. срезанное тело в `PlanNodeDelta`/header-проекция, форма — в адаптере `TasksTools`/`TasksService`. (2) Усилить docstring+дефолт: «передавай `currentVersion` из прошлого ответа как `sinceVersion`», чтобы версию узла не путали с версией борда. Зеркалить в `memory.upsert`/`delta`. Ожидание: −50–90% на каждый write-ход.

**P2 — сниппет на read-path (расширить доказанный паттерн).** `bodyLen` на `tasks.get` (дефолт 0 = полное, ради обратной совместимости; ВАЖНО: Razor-борд рендерит `n.Body` и режет на клиенте — поэтому срез только в MCP-адаптере, сервисный `GetAsync` тела UI оставляет). `bodyLen` на `memory.recall`/`list` (recall уже отдаёт `description` → description+snippet, полное тело через `memory.get`). `limit` на `memory.search`/`list` (как у recall=20). `tail`/offset на `session.get`.

**P3 — методология по чтению (уже хорошо).** `methodology_get`-индекс — правильная высота, не трогать. Остаток — тела идей/спеки + skill-файл, это inherent. Мелочь: `keysOnly`/индекс-режим на `comments.list` (нити растут) — отложить.

## Связи
[[read-index-altitude]] (spec, concern:economy) — эти рычаги РАСШИРЯЮТ его принцип «компактно по умолчанию, полное тело opt-in» на write-path и memory/session. [[methodology-read-altitude]] (уже отгружено — index methodology_get).

## Открытые вопросы (решить до выхода из raw)
1. Есть ли реальный SYNC-потребитель echo с телами (можно ли резать ДЕФОЛТ, или только добавить opt-in)? На практике агент синкается переспросом через methodology_get/tasks_get, echo как курсор почти не используется.
2. Что доминирует: долгие рабочие сессии (→ P1 write-echo первым) или дорогой старт сессии (memory_recall полные тела ×20 зовётся КАЖДУЮ сессию через SessionStart-хук → snippet-режим recall первым)?

_Источник: оценка по запросу владельца, сессия tag-grouping-view._
