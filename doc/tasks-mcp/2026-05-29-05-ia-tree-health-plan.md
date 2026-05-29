---
timestamp: 2026-05-29T19:30:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: update
target_file: ~/.claude/plans/parallel-herding-haven.md
---

## What
Перезаписал session-план под новую задачу — **редизайн навигации `/ui` (дерево-сайдбар) + пересмотр
концепции «сервис» → Health/Status**. Два workstream'а. **W1 (IA):** свитчер воркспейса + единое
дерево workspace→project→{logs→логи, databases→db→таблицы, config, memory/tasks/agent-session},
глубина ≤5, shallow+lazy (глубокие узлы — htmx по раскрытию, load-once), дашборды (один партиал,
счётчики live из petbox.db + размеры/биндинги on-page через FileInfo/одно открытие config-db, без
фонового StatsService), leaf-вью (log табы Logs/Traces, db/table SQL-вью, config Manage/Preview +
панель «сохранённые фильтры» = новая модель SavedConfigFilter), disabled-плейсхолдеры. **W2
(Health):** сущность `Service` удаляется полностью; вводятся tag-идентифицируемые `HealthReport`
(svc+tags, push `POST /api/health` scope `health:write` или pull через HealthPoller по
`HealthEndpoint`), история = append-all + retention, статус-страница = последний отчёт по (svc,tags).

## Why
После отгрузки named-logs пользователь обнаружил пропавший из UI переключатель воркспейсов (бэкенд
`WorkspaceSwitchEndpoint` цел) и решил заодно задать визуальный скелет всего `/ui`. Я прогнал три
adversarial-критика (UX/IA, перф, продукт). Пользователь перебил часть критики: all-in-one консоль
оправдывает единое дерево (Seq/Grafana — одна задача каждый), скелет намеренный (не YAGNI), плейсхолдеры
= якоря под одобренный Tasks/Memory. Тех-корректность критики вшита как инварианты (FileInfo не PRAGMA,
сайдбар трогает только petbox.db, lazy-таблицы, единый authz, MAX(Id) не COUNT). Отдельно пользователь
переосмыслил «сервис»: единственное per-service было логами, а они стали per-project именованными →
сервис как контейнер не нужен, остаётся health-check как push/pull tag-отчёты с историей.

## Args
Plan sections: Контекст, Критика(учтено/отклонено), Структура дерева, W1 Фазы A-E, W2 Services→Health
(модели HealthReport/HealthEndpoint, push/pull, удаление Service, решения: append-all+retention,
полное удаление сразу), Инварианты производительности, Верификация. Уточнённые развилки: tree depth =
shallow+lazy; stats = live-счётчики + on-page размеры; config tree → панель saved filters; stubs =
disabled-плейсхолдеры; health история = append-all+retention; Service = удалить сразу полностью.

## Outcome
План утверждён (ExitPlanMode approved). Реализация: запись/decision-log → W1 Фаза A (свитчер+скелет) →
W2 (Health + удаление Service) → W1 Фазы C/B/D/E. Named-logs Phase 2 (entity.* MCP) — отдельная
незакрытая задача; тип `service` оттуда выпадает вместе с удалением сущности.
