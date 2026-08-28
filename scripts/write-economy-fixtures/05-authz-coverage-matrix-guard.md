## Проблема
Покрытие authz было аудитируемо только разовым multi-agent свипом 57 Razor + все REST (2026-07-06). Нет постоянного guard'а — новый mutation-хендлер без scope-проверки пройдёт незамеченным.

## Предложение
1. **Интроспекция**: тул/эндпоинт, перечисляющий каждую mutation-поверхность (route/PageModel/MCP-tool) → требуемая политика → как резолвится target-scope (route/body/none).
2. **CI-guard**: тест, который FAIL'ит, если mutation-хендлер не несёт project/workspace-scope-проверки (default-deny ассерт) — с allowlist для by-design-open роутов (self-export, docs, share-token).

Превращает ручной аудит в постоянный guardrail. Связано с authz-default-deny-project-scope (если default-deny ляжет — матрица его верифицирует).

## Open questions
- Как перечислить поверхности (reflection по endpoints/PageModels/MCP-tools).
- False-positive для легитимно-открытых роутов.
- Формат матрицы (для человека vs machine-check).
