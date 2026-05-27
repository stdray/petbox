---
timestamp: 2026-05-27T17:45:00+03:00
agent: claude-code
model: claude-opus-4-7
session: ui-redesign-plan
target: memory
action: create
target_file: ~/.claude/projects/D--my-prj-yobabox/memory/feedback_record_plan_memory_actions.md
---

## What
Создал feedback memory `feedback-record-plan-memory-actions.md` фиксирующий правило: каждое изменение плана или memory должно сопровождаться записью в `doc/tasks-mcp/`. Память отсылает к канонической инструкции в `AGENTS.md` ("Recording plan/memory actions").

Параллельно создан индекс `doc/tasks-mcp/README.md` и добавлена секция в `AGENTS.md` — это не memory-операции, не записываются здесь отдельно.

## Why
Пользователь попросил завести инструкцию: "при работе с планом или memory писать в doc/tasks-mcp какой-то файл или несколько файлов, что и зачем и с какими аргументами ты делал. типо пока пишем накопить примеров для Tasks".

Цель — собрать реальные примеры plan/memory операций как design input для будущего Tasks/Plans модуля YobaBox (см. roadmap в `~/.claude/plans/proud-waddling-naur.md`, секция "Module roadmap"). Tasks — главный differentiator продукта и следующий приоритет после IA рефакторинга.

## Args
Memory file frontmatter:
- name: `feedback-record-plan-memory-actions`
- description: "Every plan/memory edit must produce a record in doc/tasks-mcp/ per AGENTS.md instruction. Accumulates examples for the future Tasks module."
- metadata.type: `feedback`

Body lead: правило, **Why** ссылается на эту запись и на Tasks roadmap, **How to apply** описывает формат файла (см. README этого каталога).

## Outcome
Memory создан. Индекс `MEMORY.md` обновлён. Текущий файл — первый пример формата, дальше пишутся по тому же шаблону.
