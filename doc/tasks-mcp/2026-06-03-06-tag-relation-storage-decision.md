---
timestamp: 2026-06-03T00:00:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: update
target_file: petbox tasks board=ideas node=spec-flat-tags (projectKey=$system)
---

## What
Дописана тех-деталь хранения в тело идеи `spec-flat-tags`: теги — отдельной таблицей (не JSON-колонкой), зеркально существующей edge-таблице связей; обе soft-closed (SCD-2).

## Why
Пользователь добивал технические детали: теги и связи — JSON-колонка или отдельная таблица? Решение принято по критерию главной операции (group-by/фильтр/roll-up по тегу = реляционно/индексируемо) и governance (FK-энфорс namespace, rename/синонимы как UPDATE — JSON это не может).

## Args
Решение:
- **Теги → отдельная таблица** `node_tag(node_id, tag, valid_from, valid_to)` (SCD-2 soft-close, key = стабильный nodeId; INDEX(tag),(node_id)) + `tag_vocab(tag PK, namespace)` с FK `node_tag.tag→tag_vocab.tag` (энфорс namespace в БД). Теги цепляются к identity, не к контент-версии. FTS5 — денормализованная копия в строке узла (производный индекс).
- **Связи** уже отдельная edge-таблица (`relations_*`, soft-close, closedAt) — оставить; никогда не JSON (теряется reverse-traversal/kind-фильтр/lifecycle). Это шаблон для node_tag.
- **Единая форма:** тег = node→term, связь = node→node — типизированные рёбра. (a) выбрано: две параллельные soft-closed таблицы; (b) опц. end-state: одна triple-таблица `edge(from_node, predicate, to_ref, to_kind={node|term}, …)` — один group-by-движок на теги/связи/декомпозицию.

## Outcome
Успех, борд `ideas` v17. Тех-решение зафиксировано в `spec-flat-tags`. Выбран прагматичный путь (a); triple-store отмечен как возможный end-state.
