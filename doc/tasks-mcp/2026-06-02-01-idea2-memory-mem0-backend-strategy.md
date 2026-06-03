---
timestamp: 2026-06-02T00:00:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: update
target_file: petbox tasks board=ideas node=incoming/task-0b2c90 (projectKey=$system)
---

## What
Попытка обновить тело идеи «Более эффективная память для агентов» (`ideas` board, узел `incoming/task-0b2c90`) выводом обсуждения и перевести статус в `exploring`. **Обновление через MCP заблокировано багом** (см. Outcome) — вывод зафиксирован здесь как durable-копия.

## Why
Пользователь попросил зафиксировать итог обсуждения «нативная память для агентов vs MCP», просто обновив задачу-идею (без реализации). Обсуждение прошло 4 раунда ресёрча (мой агент + сверка Grok/DeepSeek/Gemini) и сошлось на конкретной стратегии.

## Args
**Вывод (НЕ реализовано):** стратегия «быть бэкендом, а не интеграцией» — PetBox клонирует API **mem0** (самый популярный/живой memory-проект, ~57k★, релиз 2026-06-01; чистый REST CRUD+search, self-host, конфигурируемый base URL) и наследует его экосистему клиентов.

Покрытие целевой четвёрки агентов:
- **Claude Code** ✅ — mem0 MCP + skill
- **opencode** ✅ — готовые self-host плагины (`opencode-mem0` и др.)
- **Factory Droid** ✅ — generic MCP (`.factory/mcp.json` → endpoint PetBox)
- **pi** ❌ — принципиально «No MCP», `pi-hermes-memory` локальный; нужен ОДИН тонкий нативный pi-extension поверх того же REST (единственный неунаследуемый клиентский код)

Реализация дёшева: mem0-REST ложится ~1:1 на существующий `memory_*` store (add→upsert, search→search, get_all→list, get→get, delete→delete). Две поверхности: mem0-совместимый **REST** + mem0/OpenMemory-совместимый **MCP** (`add_memory`/`search_memories`/…).

Проактивное всплытие решается ортогонально — нативными session-start хуками агентов (Claude Code/Factory: `SessionStart`→stdout/`additionalContext`; pi: `before_agent_start`/`context`; opencode: `experimental.chat.system.transform`), дёргающими тот же REST. PetBox сам ничего не впрыскивает.

Отвергнуто: memories.sh (API мутный, self-host неясен), Supermemory (cloud-locked), Letta/Zep (не та форма, 0–1/4).

Открытые импл-вопросы: точные сигнатуры pi ExtensionAPI; first-class ли регистрация тулов в opencode.

## Outcome
**FAILED через MCP — выявлен баг PetBox.** Узел `incoming/task-0b2c90` имеет статус `Pending`, которого НЕТ в FSM kind=ideas (`raw|exploring|deferred|accepted|rejected`).
1. Идеи создаются со статусом `Pending` вне FSM (вероятно admin tasks UI, коммит `e56eb63`, проставляет глобальный дефолт вместо initial-статуса kind = `raw`).
2. `ApplyWorkflow` (`src/PetBox.Web/Mcp/TasksTools.cs:491`) ре-валидирует статус узла при КАЖДОМ upsert, даже если он не меняется → узел с «плохим» статусом нельзя отредактировать вообще (ни тело, ни title). Фикс: валидировать только переход (new ≠ prior), не членство неизменного статуса.

Узел остаётся со старым телом и статусом `Pending` до починки FSM/данных. Парная идея `incoming/mcp-ed84a9` (idea 1, «лучше MCP для tasks/plan») ждёт результата второго ресёрча и, вероятно, того же бага при записи.
