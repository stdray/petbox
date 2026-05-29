---
timestamp: 2026-05-29T16:00:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: update
target_file: doc/decision-log.md
---

## What
Зафиксировал повторный анализ модуля Tasks + Agent Memory под рамкой **MCP-only**.
Создан `doc/proposals/tasks-memory-modules/proposal-v2.md` (исходные proposal.md /
critique.md оставлены как история) и добавлена запись в `doc/decision-log.md`
сверху (2026-05-29). `doc/plan.md` в этот заход не трогался (узкий scope по
просьбе пользователя). Сопутствующий session-план анализа —
`~/.claude/plans/polished-churning-koala.md`.

## Why
Модуль был отложен 2026-05-27 с вердиктом «в честном — не делать вообще»,
главный провал — sync серверного store ↔ локального `doc/plan.md`. Пользователь
попросил переисследовать модуль под новые вводные: появился рабочий MCP в petbox,
есть 2 реальных проекта (по 4 разработчика, разные агенты), где локальный
`plan.md` вести нельзя — то есть подход не MCP-first, а **MCP-only**. Цель захода —
только анализ (что меняют вводные + сложность реализации), без кода.

## Args
- **proposal-v2.md**: переигровка критики (из ~12 претензий 4 сняты, 1 перевёрнута,
  остальные — точечные правки, остаётся compliance); три сдвига (MCP-only убивает
  sync-слой; гетерогенная команда переворачивает «велосипед» — MCP единственный
  общий знаменатель; compliance стал измеримым); правки дизайна (server sessionId +
  optimistic concurrency, memory scope global/workspace/project, свободные tags,
  глубина дерева ~3, гибрид project-plan + session-blob); размещение БД через
  generic `IScopedDbFactory<TContext>` (Tasks → `tasks/{projectKey}.db`, Memory →
  multi-scope; logs+tasks НЕ объединять — SQLite single-writer); черновик
  MCP-контракта (tasks.*, memory.*); вывод по сложности (ниже исходной) и пилот.
- **decision-log запись 2026-05-29**: context (3 новых вводных), переигровка
  критики, правки дизайна, размещение БД, decision (рамка жизнеспособна, но перед
  стройкой — пилот с mock-инструментами и замером compliance на реальной команде).

## Outcome
Успешно. Анализ зафиксирован в proposals + decision-log. Следующий шаг (отдельной
сессией) — либо пилот (mock-инструменты `tasks.session_save`/`node_upsert` +
замер compliance на 2 проектах), либо обновление Phase 30 в `doc/plan.md` на
статус «pilot». Реализация модуля не начиналась.
