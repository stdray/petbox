---
timestamp: 2026-05-27T22:30:00+03:00
agent: claude-code
model: claude-opus-4-7
session: settings-taxonomy-design
target: plan
action: update
target_file: C:\Users\stdray\.claude\plans\proud-waddling-naur.md
---

## What
Перепланировал session-plan под новую задачу: дизайн taxonomy для всех текущих и будущих settings в YobaBox. Прошёл три итерации (rev 1 / rev 2 / rev 3) после двух раундов правок от пользователя.

Финальный план: создать `doc/settings-taxonomy.md` (~450 строк), обновить `AGENTS.md`. Никаких code-changes в этой итерации — только дизайн-документ.

## Why
Пользователь запросил `/plan` про настройки: "нарисуй табличку, что конфигурируется, к какой сущности относится, где находится. надо продумать настроечки. а то потом появятся настройки пользователя или настройки retension лога через пользовательские фильтры и какие-нибудь провайдеры ИИ для tasks или что-нибудь еще. надо как-то спланировать заранее".

Боль: ~150 settings сегодня живут в 6 storage-классах без явной таксономии. Каждый новый параметр → ad-hoc решение "куда положить". Без правил это сломается при росте (user prefs, AI providers для Tasks, filter-based retention).

## Args
План правился 3 раза в ответ на правки пользователя:

**Rev 1**: исходно сделал storage-first таксономию (S1-S6 классы × scope axes). Пользователь возразил: "ты сделал storage, я хотел entity-first таблицу + UI consistency".

**Rev 2**: переделал entity-first. Пользователь возразил: "слишком много настроек, не дублировать на каждом уровне defaults; админка должна быть полностью отделена со своим sidebar; убей старые таблицы без миграции".

**Rev 3** (approved): per-entity таблицы; cascade с per-setting `TopLevel` cap (не unbounded); каталог как C# records с `[Setting(TopLevel, Key)]` атрибутами (пользователь сам предложил pattern); reflection-генерируемые Defaults pages; admin area с собственным sidebar; project Settings уезжает из tab-strip в админку.

Ключевой архитектурный выбор:
- **L1**: typed entity tables (Workspaces, Projects, ... — без изменений).
- **L2 NEW**: `Settings(Scope, ScopeKey, Path, Type, Value)` — generic key-value для всего behavioral. Catalog в коде через records.
- **L3**: ConfigBindings — без изменений, только для external consumers.

## Outcome
Plan утверждён. Документ создан в `doc/settings-taxonomy.md`. AGENTS.md содержит ссылку.

Implementation (Settings table, SettingAttribute, resolver, generic form partial, admin sidebar rework, retention migration, /ui/me/* pages) — отдельными PR-ами в порядке из §"Out of scope" финального плана. В этой итерации НЕ делается.
