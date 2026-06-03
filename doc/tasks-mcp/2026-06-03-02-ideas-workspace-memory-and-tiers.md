---
timestamp: 2026-06-03T00:00:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: create
target_file: petbox tasks board=ideas (projectKey=$system)
---

## What
Две новые идеи-ребёнка под `memory`: `workspace-memory` (каскад workspace→project) и `memory-tiers` (облегчённые тиры: сырой архив + дистиллят + provenance + DuckDB-drilldown, с итеративным планом).

## Why
Из обсуждения 2026-06-03: (1) сессия = append-only blob → её можно хранить тупым JSONL-архивом, поиск/дистилляция — это работа memory, не session; (2) векторы хранят provenance-метаданные → из дистиллята дотягиваешься до исходной сессии, DuckDB FTS по сырью = «одна мегасессия»; (3) пользователь хочет память на уровне workspace (кросс-проектные правила пишутся раз, видны везде) с merge workspace+project. Вопрос «workspace-memory+хуки ≈ файл+AGENTS.md» — для статики да, разница операционная (центральный источник vs копия в каждом репо).

## Args
- **`memory/workspace-memory`** (raw, nodeId 18601a76…): скоуп выше проекта на базе многоуровневого mem0-скоупа; SessionStart-хук мёрджит workspace+project; выигрыш над файлом = write-once-видно-везде, merge, авто-рост. Реализует «workspace first-class». Зависит от `mem0-mapping`, `agent-delivery`.
- **`memory/memory-tiers`** (raw, nodeId f230fc92…): двухслойка working/episodic=сырой JSONL-архив (DuckDB FTS) + semantic=дистиллят + provenance-мостик; procedural подрезан. Итеративный план 1–5 (автозахват → infer=false → infer=true дистилляция → DuckDB-drilldown → опц. decay), сравнение с mem0 по сложности/поиску/размеру. Caveat: дорого качество LLM-консолидации, не плумбинг; UX-буст №1 = автозахват.

## Outcome
Успех, борд `ideas` v13. Дерево `memory`: `mem0-mapping`, `distillation`, `workspace-memory`, `memory-tiers`. Сквозная мысль сессии: приоритет — автозахват (hooks, `agent-delivery`), он разблокирует и наполнение, и тиры, и workspace-каскад. Связи blocks между новыми узлами и `agent-delivery` НЕ заводили (зависимости пока текстом в телах).
