# Proposal v2: Tasks + Agent Memory — пересмотр под MCP-only

**Status:** повторный анализ 2026-05-29 (stdray). Заменяет рамку
[proposal.md](proposal.md); исходные [proposal.md](proposal.md) и
[critique.md](critique.md) сохранены как история.
**Решение:** см. [decision-log](../../decision-log.md) запись 2026-05-29.
**Вердикт:** критика частично снята новой рамкой; остаётся один стратегический
риск (compliance), который теперь можно проверить дёшево на реальной команде.

## Что изменилось с 2026-05-27

Исходный проект был отложен после жёсткой критики. Главный провал — синхронизация
серверного store ↔ локального `doc/plan.md`. С тех пор три новых вводных:

1. **Рабочий MCP в petbox.** HTTP transport на `/mcp`, инструменты через
   рефлексию (`[McpServerToolType]`/`[McpServerTool]` в `src/PetBox.Web/Mcp/*Tools.cs`),
   auth через `ApiKeyAuthenticationHandler` (X-Api-Key → claims `project`+`scopes`),
   хелперы `AssertProject`/`AssertScope`. Уже живут 12 инструментов
   (data.* ×7, log.query, provisioning ×4).
2. **Реальный мульти-агентный мульти-разработческий кейс.** 2 проекта, по 4
   разработчика, у всех разные coding-агенты. В этих проектах локальный `plan.md`
   вести **нельзя** — только MCP.
3. **Смена рамки: MCP-only** (не MCP-first). MCP — единственный интерфейс к
   плану/памяти; локального файла нет вообще. Адоптацию навязываем средой
   (конфиг MCP-сервера + хуки на ExitPlanMode/Stop + инструкции в CLAUDE.md и
   аналогах).

## Переигровка исходной критики

| # | Аргумент критики | Статус под MCP-only |
|---|---|---|
| 3 | Markdown как render → write-only mirror, bidirectional sync | **СНЯТ.** Нет локального файла → нет рендера и обратного парсинга. Это было ядро провала. |
| 3/5 | merge-конфликт rendered markdown, «markdown не парсится назад» | **СНЯТ.** Нет git-копии плана. |
| 5 | pull перетирает несинхронизированные локальные правки | **СНЯТ.** Нет правок мимо store. |
| 3 | Дублирование petbox.db ← MCP ← agent ← mirror ← git, SoT неясен | **СНЯТ.** Один SoT — DB. |
| 2 | Велосипед; «ничем не лучше для single-user» | **ПЕРЕВЁРНУТ.** Предпосылка (single-user) исчезла. |
| 1 | Compliance: агенты игнорируют даже лёгкое требование | **ОСТАЁТСЯ** риском №1, но измеримым и смягчаемым. |
| 5 | slug-collision / upsert race / last-write-wins | **ТРАНСФОРМИРУЕТСЯ** в обычный concurrent-write: server-generated sessionId + optimistic concurrency. |
| 3 | Дерево неограниченной глубины = UI-ад | **СМЯГЧАЕТСЯ.** UI вторичен; глубина ~3 уровня; prose в session-blob и `Body`. |
| 3 | Per-project memory ломает user-level | **ПРАВКА.** scope памяти global/workspace/project. |
| 3 | 4-типовая schema — buckets claude-code | **ПРАВКА.** свободные tags + опциональный type. |
| 3 | `PlanNodeRef.commit` дублирует git | **ОСЛАБЛЕН.** План не в git; явная привязка узел↔commit/PR полезна для ревью. |
| 8 | Per-project `.db` overkill | **ПРАВКА.** см. размещение БД ниже. |

Из ~12 претензий: **4 сняты полностью**, **1 перевёрнута в нашу пользу**,
**остальные — точечные правки**, и лишь **compliance** остаётся стратегическим.

## Три ключевых сдвига

### 1. MCP-only убивает sync-слой целиком

Исходный дизайн ([proposal.md](proposal.md) «Sync архитектура») держал самый
хрупкий механизм: pull→render в `plan.md`→write-only push→Stop-hook→
**bidirectional parser (отложен)**. Без обратного парсера это «хуже текущего
состояния». В MCP-only этого слоя **нет**: единственное представление живёт в DB.
Исчезает не один баг, а целая фаза работы и класс рисков (merge/mirror/parser).

Цена: теряются «бесплатные» свойства git (история, `git diff/blame`), которыми
критика предлагала заменить модуль. Но в MCP-only план не в git, поэтому модуль
обязан дать это сам: append-only history (он и так был) + `plan.diff`/`history`/
`search`. Это и есть содержательная ценность.

### 2. Гетерогенная команда переворачивает «велосипед»

Готовые решения под новой рамкой кейс не закрывают:

- **mcp-server-memory** — file-based, per-machine, без multi-tenant/auth/
  workspace-изоляции. Не шарится между 4 разработчиками и разными агентами.
- **GitHub Issues + gh** — конкурент для *tasks*, но не интегрирован с
  workspace/logs/config petbox, не моделирует session-plan (эфемерный
  markdown-черновик ≠ issue), каждому агенту нужна своя gh-интеграция.
- **Obsidian/Logseq + git** — снова локально-файловые.

Ключевой новый аргумент: **локальные файлы НЕ являются общим знаменателем** —
каждый агент кладёт план по-своему (`~/.claude/plans/*.md`, `.factory/...`,
oh-my-pi затирает файл). Единственная общая шина у гетерогенных агентов — **MCP**.
MCP-only здесь не ограничение, а единственное, что работает. Это differentiator,
которого не было у single-user.

Для **Memory** «велосипед» частично остаётся валиден, но не блокирует: свой store
оправдан отсутствием multi-tenant у готовых; 4-типовую схему заменяем на
свободные tags + scope.

### 3. Compliance: из «фатально и недоказуемо» в «измеримо и смягчаемо»

Единственный аргумент, который MCP-only сам не закрывает. Но меняет форму:

- Раньше: «агент выбирает лёгкий локальный файл вместо трудного MCP-вызова».
  Под MCP-only **лёгкой альтернативы нет** — выбор «MCP или ничего».
- Адоптацию навязываем средой (конфиг + хуки + инструкции) — compliance свойство
  настройки, а не доброй воли агента.
- Честный остаток: лишённый файла агент может **не вести план вообще**, если
  хуки не покрывают все точки. Отсюда требования к дизайну:
  - инструменты совпадают с нативным поведением: `session.save(markdown blob)` —
    ровно то, что claude-code пишет в `~/.claude/plans/*.md`, только через MCP;
  - устойчивость к пропускам: пропущенный вызов = пробел, не порча данных.
- Критика била по фейковому бенчу («это твой journal, других агентов нет»).
  Теперь есть **настоящая** команда → эксперимент «2 недели, порог 70%»
  (из [critique.md](critique.md) §7) можно провести по-честному.

## Правки дизайна (обязательны для v2)

1. **Убрать sync-слой** (pull/render/mirror/bidirectional parser).
2. **Concurrency:** server-generated `sessionId` вместо natural-key
   `(ws,project,agent,slug)`; `Version`/etag на `PlanNode`/`Memory`, optimistic
   concurrency на upsert.
3. **Memory scope:** `global | workspace | project`.
4. **Типы памяти → свободные tags** (+ опциональный `type`).
5. **Глубина дерева ~3 уровня**; prose в `SessionPlanRevision.Content` и
   `PlanNode.Body`, не «между узлами».
6. **Гибридная модель:** structured project-plan (узлы/статусы/связи) для
   трекинга **+** session-plan (markdown-blob с ревизиями) для черновика.
7. **dogfooding с первого дня** — petbox ведёт свой план через свой MCP.

## Размещение БД (scope-aware)

Текущая карта БД (по факту кода):

```
./data/
├── petbox.db                    GLOBAL    Core (Workspaces, Projects, Services, ApiKeys…)
├── config/{workspaceKey}.db     WORKSPACE ConfigBinding(+history,+tags)
├── logs/{projectKey}.db         PROJECT   LogEntries, Spans
└── db/{projectKey}/{dbName}.db  PROJECT/named  user-data
```

Три фабрики (`LogDbFactory`, `ConfigDbFactory`, `DataDbFactory`) следуют одной
форме «scope-ключ → файл». Предлагается обобщить:

```csharp
enum DbScope { Global, Workspace, Project, Service }
interface IScopedDbFactory<TContext> where TContext : DataConnection
{   TContext Get(DbScope scope, string scopeKey, string? name = null); }
// путь: {moduleDir}/{scopeKey}.db | {moduleDir}/{scopeKey}/{name}.db
```

Размещение модулей:

- **Tasks → project-scope, одна БД модуля:** `tasks/{projectKey}.db`
  (ProjectPlan, PlanNode+History+Ref, SessionPlan+Revision — все таблицы вместе).
- **Memory → multi-scope:** `memory/$global.db`, `memory/{workspaceKey}.db`,
  `memory/{projectKey}.db` — scope выбирает фабрика. Это чинит претензию
  «per-project ломает user-level».

**Не объединять logs+tasks+memory в один `project.db`.** SQLite — один writer на
файл; hot log-ingest и запись плана блокировали бы друг друга. Плюс разные
нагрузочные профили, миграции и feature-toggle. Физическое разделение per-module
— изоляция writer-lock (не карго-культ), а generic-фабрика делает его бесплатным.
Объединяем таблицы внутри модуля, не между модулями.

## Черновик MCP-контракта

**Tasks** (scopes `tasks:read`/`tasks:write`):
- `plan.get(projectKey)`, `plan.diff(projectKey, since)`,
  `plan.search(query, projectKey?)`
- `node.upsert({projectKey, parentId?, title, status, body, refs[], expectedVersion?})`,
  `node.complete(nodeId, refs[]?)`, `node.reorder(parentId, orderedIds[])`
- `session.save({projectKey, agent, model?, content, sessionId?})`
  → `{sessionId, revision}`, `session.list(projectKey, limit)`, `session.get(sessionId)`

**Memory** (scopes `memory:read`/`memory:write`):
- `memory.upsert({scope, scopeKey, name, description, body, tags[], expectedVersion?})`,
  `memory.get`, `memory.list({scope, scopeKey, tags?})`, `memory.search`,
  `memory.delete`, `memory.history`

Статусы узла: `Pending / InProgress / Done / Blocked / Deferred / Cancelled`.

## Сложность реализации с учётом текущего MCP

**Готово/переиспользуется (низкая стоимость):** MCP-сервер (transport +
рефлексия-discovery), auth + `AssertProject`/`AssertScope`, linq2db +
FluentMigrator, паттерн DbFactory. Новый модуль = `src/PetBox.Web/Mcp/TasksTools.cs`
(+ `MemoryTools.cs`) + новые scopes в `ApiKeyScopes.cs`; ядро MCP не трогаем.

**Строить (много механики, мало риска):** ~8 таблиц (по модулю), сервис
upsert+history+search, ~18 MCP-инструментов по готовому шаблону, read-only UI
(вторично).

**Реально сложное (не код petbox):** интеграция в гетерогенных агентов
(хуки/skill в репах тех проектов) — это работа адоптации и источник
compliance-риска. Внутри модуля нетривиальны только concurrency и memory-scope.

**Вывод:** относительно исходного проекта сложность **ниже** — удалён самый
хрупкий слой (sync). Главный труд и риск переезжают из «написать модуль» в
«заставить агентов им пользоваться», и это теперь дёшево проверяемо.

## Следующий шаг — пилот, не стройка

Прежде чем строить модуль, прогнать дешёвый пилот на реальной команде (чего не
было в 2026-05-27):

1. Поднять **тонкие mock-инструменты** `tasks.session_save` / `tasks.node_upsert`
   (пишут в таблицу/лог, без UI и истории) на dev-MCP.
2. Навязать средой у 4 разработчиков × 2 проекта: MCP в конфиге + хук на
   ExitPlanMode/Stop + строка в CLAUDE.md/аналогах.
3. 1–2 недели измерять compliance: доля сессий, где план попал в store **сам**,
   vs прошёл мимо. Порог жизнеспособности — ~70%.
4. Параллельно — честный бенч: одна задача → разные агенты → сравнить, что каждый
   шлёт в `session.save`, зафиксировать минимальный общий контракт.

Проходит порог → правки дизайна выше превращаются в полноценный план реализации
(отдельная сессия). Не проходит → не строим даже при готовой инфраструктуре, и
это доказано на реальной среде, а не на догадке.
