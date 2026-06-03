---
timestamp: 2026-06-03T00:00:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: update
target_file: petbox tasks board=ideas (projectKey=$system)
---

## What
Три правки: (1) `agent-delivery` переписан под чистый tasks-only (хуки убраны); (2) новый `memory/autocapture` — хук/автозахват вынесены в линию памяти; (3) новый `tasks-md-render` — markdown-рендер задач в UI.

## Why
Пользователь верно заметил: автоинжект плана задач бесполезен — план релевантен только при намерении с ним работать, а это намерение = команда («покажи, что взять в работу»); для сессий про код/идеи инжект = шум. Значит хуки — концерн памяти (фоновое всплытие), не задач. tasks-доставка = только pull (MCP/CLI + Beads-verbs). Плюс: тела задач в UI показываются сырым markdown — после Obsidian нечитаемо.

## Args
- **`agent-delivery`** (v14): tasks-only. Вывод: не клонировать (local-file/closed); MCP — универсальный носитель, PetBox богаче; доставка = только callable-tools pull (MCP baseline / опц. CLI+skill); Beads-verb фасад серверно (`ready` = «что взять»); AGENTS.md/skill = статичная доку. Хуков нет.
- **`memory/autocapture`** (new, nodeId 731b1ac8…): нативные non-MCP хуки у всех агентов (CC/Droid `SessionStart`, opencode `chat.system.transform`, pi `before_agent_start`/`context`); два направления — инъекция-на-старте (read, полезна памяти, НЕ задачам) и захват (write → сырой архив). Шаг №1 линии памяти; предшествует distillation/memory-tiers/workspace-memory.
- **`tasks-md-render`** (new, l1, nodeId df5305f5…): UI-рендер md тел задач (GFM, code-fence, кликабельные ссылки на узлы), клиентский md→html; формат не меняем.

## Outcome
Успех, борд `ideas` v14. Чистое разделение: хук/автозахват = линия памяти (`memory/autocapture`), tasks-доставка = pull-only (`agent-delivery`). Tasks-тред закрыт. Связи blocks (autocapture → distillation/memory-tiers/workspace-memory) НЕ заводили — зависимости текстом; по запросу проставлю рёбра.
