---
timestamp: 2026-06-02T00:00:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: update
target_file: petbox tasks board=ideas (projectKey=$system)
---

## What
Два изменения на борде `ideas`: (1) новая идея `tasks-get-headers`; (2) связь `blocks` между `llm-router` и `memory/distillation`.

## Why
(1) Пользователь заметил, что `tasks_get` (и ответы `upsert`/`delta`) возвращают полные тела всех узлов — огромный payload; нужен opt-in флаг на headers-only. (2) Подтверждено решение: `llm-router` — пререквизит дистилляции (distillation не хардкодит провайдеров, а зовёт роутер).

## Args
- **Идея `tasks-get-headers`** (l1, raw, nodeId 9b1eedd9…): флаг `tasks_get(..., bodies=false)`/`view=titles` → узлы без `body` (key/nodeId/path/status/type/title/priority/links); тот же флаг для ответа `upsert`/`delta`; дефолт с телами ради обратной совместимости, лёгкий режим — opt-in.
- **Связь** (relations_create, kind=`blocks`, edge 58631ef3…): fromNodeId=`llm-router` (562be3b4…) → toNodeId=`memory/distillation` (4f232321…). Семантика: distillation blockedBy llm-router.

## Outcome
Успех. Борд `ideas` currentVersion=10. Текущее дерево top-level: `memory` (+`mem0-mapping`, +`distillation`), `agent-delivery`, `tasks-search`, `llm-router`, `tasks-get-headers`, `deliberation-thread`. Связь blocks отражает порядок реализации: роутер v1 → дистилляция поверх.
