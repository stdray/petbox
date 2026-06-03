---
timestamp: 2026-06-02T00:00:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: update
target_file: petbox tasks board=ideas (projectKey=$system)
---

## What
Реструктуризация борда `ideas`: самодостаточные идеи подняты из-под `incoming/` в top-level (l1), выводы обсуждения продублированы в тела узлов (раньше жили только в record-файлах, т.к. правка тел была заблокирована багом `Pending` — теперь баг починен).

## Why
Пользователь: «надо всё продублировать в petbox ideas, и там сейчас дерево, некоторые самодостаточные задачи надо поднять на верхний уровень». Баг `Pending` (см. record 01) к этому моменту был исправлен на стороне PetBox, узлы перешли в `raw`, правки applied.

## Args
**Переименования (nodeId сохранён, старые пути в record 01/02 стухли):**
- `incoming/task-0b2c90` → **`memory`** (l1, exploring) — стратегия mem0-backend
  - **`memory/mem0-mapping`** (new, raw) — детальный маппинг mem0 API → `memory_*`
  - `incoming/memory-distillation` → **`memory/distillation`** (raw)
- `incoming/mcp-ed84a9` → **`agent-delivery`** (l1, raw) — «лучше MCP»/tasks: нативные хуки + per-agent коннекторы + Beads verb-фасад
- `incoming/tasks-search-fts` → **`tasks-search`** (l1, raw) — FTS по spec/work борам

Не тронуто: `deliberation-thread` (l1, узел пользователя). `incoming` — пустой инбокс.

Тела заполнены: memory (решения: mem0-клон, агенты CC/Droid/opencode, pi отложен, agentmemory отвергнут как замена), mem0-mapping (эндпоинт-таблица, id→(store,key) кодирование, type-дефолт, infer-шов, gaps, форма адаптеров), distillation (infer=true на DeepSeek/Kimi), agent-delivery (5 пунктов вывода по tasks-доставке), tasks-search (дизайн `tasks_search`).

## Outcome
Успех. Борд `ideas` currentVersion=8. Дерево: 4 top-level идеи (`memory`, `agent-delivery`, `tasks-search`, `deliberation-thread`) + 2 ребёнка под `memory`. По памяти (idea 2) — готово к graduation: mem0-адаптер `infer=false` сразу + дистилляция следом. Tasks — отложено по решению пользователя («потом будем про tasks думать»).
