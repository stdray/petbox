---
timestamp: 2026-06-02T00:00:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: update
target_file: petbox tasks board=ideas node=incoming/mcp-ed84a9 (projectKey=$system)
---

## What
Вывод обсуждения по идее «более эффективные способы работы чем MCP» (`ideas` board, узел `incoming/mcp-ed84a9`) для слоя tasks/plan. **Запись в саму задачу заблокирована тем же багом `Pending`** (см. record `2026-06-02-01`), поэтому вывод зафиксирован здесь.

## Why
Пользователь попросил повторить «clone-the-API, be-the-backend» ресёрч (как для памяти), но для управления задачами: есть ли популярный task-плагин, под чей API PetBox мог бы подстроиться, унаследовав экосистему клиентов.

## Args
**Вывод (НЕ реализовано): клонировать НЕ надо — кейс зеркален памяти.**

Нет проекта, годного под «переставь клиента на PetBox». Популярные task-менеджеры:
- **локально-файловые** (Task Master ~27k★ `tasks.json`; Beads ~24k★ `.beads/` Dolt; spec-kit ~71–90k★ `.specify/`; OpenSpec ~52k★; Backlog.md; mcp-shrimp) — данные в репо, base-URL-ручки нет; стать бэкендом = форкать клиента. Противоположность mem0.
- **удалённые закрытые** (Linear MCP фикс-хост; Task Master Team = Hamster cloud) — не self-host.

Единственный универсальный носитель для четвёрки — **сам протокол MCP**, который PetBox уже умеет (`tasks_*`, `relations_*`, `tasks_workflow`). Модель PetBox строго богаче кандидатов (Phase>Wave>Task + board kinds + spec-link + approve-gate FSM) — клонирование = деградация.

**Рекомендация (лёгкий гибрид):**
1. Оставить свой MCP+CLI+skill как source of truth.
2. Позаимствовать у **Beads** словарь глаголов (`ready`/`blocked`/`dep`/`close`) alias-фасадом над движком (Beads ближе всех: граф зависимостей + auto-ready ≈ `blockedBy`/FSM; единственный с first-party `setup claude`/`setup factory`). Берём идиому, не хранилище.
3. Реальный deliverable — **per-agent коннекторы**: MCP-entry для Claude Code/opencode/Factory (через cross-project key, коммит `3c53ce9`); для **pi** — тонкий `mcp.json` + skill (pi без встроенных todo/MCP; формат `mcp.json` как у Claude Code). Нужны при любом раскладе.
4. spec-kit/OpenSpec — валидация спроса на методологию spec→plan→tasks+approve, не цель интеграции; опционально отдавать борды как read-вью `tasks.md`/`spec.md`.

**Контраст с idea 2:** память → клонируем mem0 (есть configurable-base-URL сервер); tasks → свой MCP+CLI+коннекторы (экосистема локально-файловая, бэкендом стать нельзя).

## Outcome
Вывод зафиксирован в этом record-файле. Запись в задачу `incoming/mcp-ed84a9` отложена до фикса бага `Pending` (FSM kind=ideas + ре-валидация неизменного статуса в `TasksTools.cs:491`; детали в record `2026-06-02-01`). Оба узла идей (`task-0b2c90`, `mcp-ed84a9`) ждут починки данных/кода, чтобы принять обновлённое тело и статус `exploring`.
