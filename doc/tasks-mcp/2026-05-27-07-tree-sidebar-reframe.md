---
timestamp: 2026-05-28T00:30:00+03:00
agent: claude-code
model: claude-opus-4-7
session: phase24-admin-ia-tree
target: memory
action: update
target_file: ~/.claude/projects/D--my-prj-yobabox/memory/feedback_explicit_over_implicit.md
---

## What
Расширил memory `feedback-explicit-over-implicit` параграфом "Когда не применять" — добавил пункт что tree navigation сам считается "более explicit чем context-block".

## Why
В phase 24 я в первой итерации проголосовал ЗА плоский+контекст сайдбар, ссылаясь на этот же memory rule (smart-поведения избегаем). Пользователь reframed: "более явного чем полное дерево, ничего нет — давай делать полное дерево". То есть для navigation/IA пользователь считает что full tree более явное (вся структура сразу видна, ничего не появляется/исчезает в зависимости от контекста), чем мой "context-block" вариант (где project block появляется только когда вы в проекте).

Это важный nuance для будущих сессий: когда обсуждаем navigation, не использовать `feedback_explicit_over_implicit` против tree-структур. Tree это и есть explicit для navigation.

## Args
File: `feedback_explicit_over_implicit.md`
Section: "Когда не применять"
Added: один bullet про tree navigation = extension правила, не исключение. С прямой цитатой пользователя.

## Outcome
Memory обновлена. Будущая Claude Code сессия при обсуждении navigation для YobaBox получит правильный сигнал: для IA tree предпочтительнее, чем context-blocks или collapsible sections с auto-show/hide.
