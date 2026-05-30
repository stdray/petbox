# Architectural Decision Log

Newest decisions on top. Each entry: short title, date, context, decision, consequences.

---

## 2026-05-30 — Temporal-store движок: rename, дельта-курсор, порядок, идентичность

**Context.** Прототип generic temporal-upsert движка (`PetBox.Core/Data/Temporal`) обкатан на LINQPad и портирован в репу с тестами (ветка `feat/temporal-store`). Это переиспользуемая инфраструктура под plan/session/memory; по ходу проектирования закрыли набор решений по семантике upsert. Движок строится до pilot-gate как изолированная инфра (не вплетён в миграции/MCP/Program).

**Decisions.**

1. **Sparse partial upsert, не «весь план».** Клиент шлёт только изменённые ноды; отсутствующий в батче Key = не трогаем. Неизменный трафик не гоняем.
2. **Оптимистичная конкуренция по baseline автора** (не по перечитанной версии): close по `(Key, baselineVersion)`. Truth table: insert (`Version==0`) / absorb (совпал payload) / edit / Stale / Vanished / CloseRace.
3. **Идентичность = человекочитаемый путь (path-as-key).** Метка — идентичность, не атрибут. Клиентский id — `record TaskNodeId(PhaseKey, WaveKey?, TaskKey?)`, канонизируется в строковый `Key`; движок остаётся string-keyed, домен владеет своим id-типом.
4. **Rename/move = `PrevKey`** (nullable на `TemporalRow`): «закрыть старый узел + создать новый, связанные ребром PrevKey» — решает невыразимость rename в чистом upsert. Новый конфликт `TargetOccupied` (rename на занятый путь). `PrevKey` вне `SamePayload` (lineage, не payload). История переименований — рекурсивный CTE по рёбрам PrevKey (read-слой).
5. **Move узла с детьми — слоями.** Движок плоский (про детей не знает). Клиент либо шлёт потомков явно, либо ставит `MoveChildren` на rename-строке → доменный слой плана разворачивает в батч rename по префиксу `oldPath/`. Если ни то ни другое, а активные дети есть → **orphan-guard** (доменный) отдаёт ошибку. `MoveChildren`/orphan-guard — доменные (PlanStore), не в generic-движке.
6. **`ActiveTo = nextVersion`** при закрытии (а не fromVersion) — чтобы дельта-курсор ловил и рождения (`Version > N`), и смерти (`ActiveTo > N`).
7. **Дельта-since-cursor в ответе.** Запрос несёт plan-level `sinceVersion`; результат — `Added`/`Updated` (активные с `Version > sinceVersion`) + `Removed` (ключи, умершие с N и без активной строки) + `CurrentVersion`. Один round-trip: клиент шлёт частичное, получает всё, что сдвинулось с его курсора (свои и чужие), двигает единственный курсор без перечитывания. Added/Updated делятся по инварианту «в рамках одного upsert Created==Updated у новых строк; у правок Created перенесён из прошлой ревизии».
8. **Порядок = разреженный `Priority` (payload-поле) + path-sort.** `ORDER BY Priority, Key`. Приоритет живёт на узле → меняется через ту же пер-нодовую конкуренцию и не перенумеровывает соседей (плотный `Order` рушился бы параллельными агентами).

**Layering.** Generic-движок (`TemporalStore`, `TemporalRow`) — string-keyed, payload-агностичен, знает только identity/temporal-колонки + `SamePayload`/`AsRevision`/`PrevKey`. Доменное (TaskNodeId, MoveChildren, orphan-guard, lineage-CTE, тумбстоун-статусы) — в будущих PlanStore/MemoryStore, за pilot-gate.

**Consequences.** В движке реализовано с тестами: baseline-concurrency, PrevKey-rename + TargetOccupied, ActiveTo=nextVersion, дельта-результат (Added/Updated/Removed), sparse priority. Доменные обёртки — отдельной фазой, когда/если pilot подтвердит модуль. Generic результат стал `TemporalUpsertResult<TRow>`.

---

## 2026-05-29 — Редизайн навигации /ui (дерево) + «сервис» → Health/Status (tag-отчёты)

**Context.** После named-logs обнаружилось, что из основного UI пропал переключатель воркспейсов
(бэкенд `WorkspaceSwitchEndpoint` цел — выпилен только UI-контрол в IA-рефакторинге Phase 24). Пользователь
решил задать полный визуальный скелет `/ui`. Прогнаны 3 adversarial-критика (UX/IA, перформанс, продукт);
их тех-замечания приняты, скоуп-возражения отклонены пользователем (all-in-one консоль ≠ узкий Seq/Grafana;
скелет намеренный + задел под мульти-юзер). Параллельно пользователь переосмыслил сущность «сервис».

**Decision (навигация).** Единое дерево-сайдбар `/ui`: workspace-selector → workspace dashboard →
project → {memory, logs→логи, databases→db→таблицы, config, tasks, agent session}. Глубина ≤5,
**shallow+lazy** (глубокие узлы — htmx по раскрытию, load-once; localStorage для раскрытия без авто-GET).
Дашборды — один параметризованный партиал; **счётчики live только из `petbox.db`**, размеры — `FileInfo.Length`
(+`-wal`/`-shm`, не `PRAGMA`), биндинги — одно открытие `config/{ws}.db` **на странице** (не в сайдбаре);
**отдельный фоновый StatsService не вводим** (метрики on-page). Клик по счётчику → страница-список раздела.
config-дерево из чипов отложено → панель «сохранённые фильтры» (новая `SavedConfigFilter`, зеркало `SavedQuery`).
Инварианты: рендер сайдбара трогает только `petbox.db`; открытие user/лог-файлов — только lazy/на leaf;
единый UI-authz для lazy-партиалов (против IDOR).

**Decision (Health).** Сущность `Service` (контейнер) **удаляется полностью** — её единственное per-service
(логи) стало per-project именованными логами. Вводятся **tag-идентифицируемые health-отчёты**: структура
`{svc, name?, tags{project,region,env,…}, version, sha, buildDate, status}`, идентичность = `(svc + tags)`.
Приём: **push** `POST /api/health` (scope `health:write`) или **pull** (`HealthPoller` опрашивает
`HealthEndpoint`-URL). История — **append-all + retention** (sweep как у логов). Статус-страница (workspace
dashboard) показывает последний отчёт по `(svc,tags)`, группировка по проекту, stale-детекция.
`LogEntries.ServiceKey` остаётся свободным тегом-эмиттером; `project.create_service` MCP и тип `service`
из named-logs Phase 2 `entity.*` убираются; `HealthPoller` перепрофилируется со `Service.Url` на `HealthEndpoint`.

**Consequences.**
- Новые модели: `HealthReport`, `HealthEndpoint`, `SavedConfigFilter` + миграции (drop `Services`).
- Дашборд/админка переезжают со `Service.Health` на `HealthReport`; админ-CRUD сервисов → конфиг `HealthEndpoint`.
- Затрагивает logs (services-эндпоинт → distinct из лога), ingestion (не создаёт Services), MCP, HealthPoller.
- План: `~/.claude/plans/parallel-herding-haven.md`; запись `doc/tasks-mcp/2026-05-29-05-…md`.

---

## 2026-05-29 — Логи стали именованными сущностями per-project + обобщённая scope-фабрика (Phase 1)

**Context.** Был один лог на проект (`logs/{projectKey}.db`), создавался неявно при первой записи; роутинг только по `X-Service-Key → Service → ProjectKey`; self-логи petbox и неизвестные сервисы сваливались в псевдо-проект `$system`; workspace-страница «Logs (all)» читала этот агрегат. Пользователь захотел, чтобы логи создавались пользователями (UI + агент) как именованные сущности — по образцу `DataDb` (`db/{projectKey}/{dbName}.db` + таблица `DataDbs` + CRUD). Рамка: «нет принципиальной разницы между созданием БД и созданием лога».

**Decision.**
- Обобщил `LogDbFactory`/`ConfigDbFactory` в `IScopedDbFactory<TContext>` (`PetBox.Core/Data`): scope-ключ (+опц. name) → кэшируемый lazy-schema linq2db-контекст. Путь: `{moduleDir}/{scopeKey}.db` или `{moduleDir}/{scopeKey}/{name}.db`. Переиспользует enum `Scope`. Отличия от наброска в записи ниже: метод назван `GetDb` (CA1716 запрещает `Get`), scope привязан в конструкторе (`ScopedDbFactory<LogDb>("logs", Scope.Project, …)`), а не передаётся на каждый вызов. Общий `ScopedDbFiles` (path/delete/list) шарится с `DataDbFactory`. `DataDbFactory` остаётся исключением (connection-string, user-owned schema).
- Логи теперь именованные per-project: `logs/{projectKey}/{logName}.db`, метаданные в таблице `Logs` (миграция M016, зеркало `DataDbs`), фасад `ILogStore` (GetContext/Create/Delete/List/Exists). Без auto-vivify: ingestion в несуществующий лог → ошибка (explicit over implicit).
- Ingestion path-based: `/api/ingest/{projectKey}/{logName}/clef`, `/v1/logs/{projectKey}/{logName}`, `/v1/traces/{projectKey}/{logName}`; legacy без logName резолвит проект по `X-Service-Key` и пишет в лог `default`. Pipeline/broadcaster ключуются по `{projectKey}/{logName}`. Query/live-tail/share — per-log; REST lifecycle `/api/logs/{projectKey}/logs` (create/list/delete).
- Системный лог: авто-создаётся только petbox self-log `($system, petbox)` на старте; SystemLogFlusher + seq self-log пишут туда. Проектные логи создаются явно.
- «Logs (all)» удалён (нет дешёвого cross-project merge): убраны nav-пункт, роуты `/ui/{ws}/logs|traces`, `Routes.WorkspaceLogs/Traces`. Viewer стал per-log (`/ui/{ws}/{projectKey}/logs/{logName}` + селектор), добавлены admin-страницы создания/удаления логов. Retention и новый orphan-cleanup идут по строкам `Logs`.

**Consequences.**
- Миграции M016 (Logs), M017 (ShareLinks.LogName). `ILogDbFactory`/`LogDbFactory` удалены — все вызовы на `ILogStore`/`IScopedDbFactory<LogDb>`.
- Phase 1 проверена: 315/315 unit/integration зелёные, TS typecheck зелёный, E2E обновлены под новую модель.
- Phase 2 (унификация MCP в generic `entity.*` CRUD) — отдельным заходом; требует переписать `McpDataToolsTests` + `ProvisioningToolsTests` под новые имена.
- `$system` как system workspace/project (admin/auth/config) НЕ затронут — менялись только его log-routing использования.

---

## 2026-05-29 — Tasks + Memory пересмотрены под MCP-only (критика частично снята, перед стройкой — пилот)

**Context.** Модуль Tasks + Agent Memory был отложен 2026-05-27 (запись ниже) с вердиктом «в честном — не делать вообще». Главный провал — синхронизация серверного store ↔ локального `doc/plan.md`. С тех пор три новых вводных: (1) в petbox появился **рабочий MCP** (HTTP `/mcp`, инструменты через рефлексию, auth по scopes, 12 живых tools); (2) появился **реальный мульти-агентный мульти-разработческий кейс** — 2 проекта по 4 разработчика, у всех разные агенты, локальный `plan.md` вести нельзя; (3) сменилась рамка на **MCP-only** (не MCP-first) — MCP единственный интерфейс, локального файла нет вообще, адоптация навязывается средой (конфиг + хуки). Повторный анализ — `doc/proposals/tasks-memory-modules/proposal-v2.md`.

**Переигровка критики (из ~12 претензий):**
1. **Sync hell (главный аргумент) — СНЯТ.** Держался на двойном представлении (сервер-канон + локальный рендер). Нет файла → нет pull→render→правка→push→конфликт, нет write-only mirror, нет неразрешимого merge-конфликта markdown. Исчезает целая фаза работы (bidirectional parser) и класс рисков.
2. **«Дублирование 4 мест / SoT неясен» — СНЯТ.** Один источник истины (DB).
3. **«Велосипед, нет смысла для single-user» — ПЕРЕВЁРНУТ.** Предпосылка (single-user) ушла. Локальные файлы НЕ являются общим знаменателем между разными агентами (каждый кладёт по-своему). Единственная общая шина у гетерогенных агентов — MCP. mcp-server-memory (file-based, per-machine, без multi-tenant), GitHub Issues (не моделирует session-plan, не интегрирован), Obsidian+git (локально-файловые) — кейс не закрывают.
4. **Multi-machine — частично СНЯТ**, остаток трансформируется из «data corruption» в обычный concurrent-write (server-generated sessionId + optimistic concurrency). Multi-machine стал аргументом *за* централизацию.
5. **Compliance-парадокс — ОСТАЁТСЯ риском №1**, но меняет форму: лёгкой альтернативы (локальный файл) больше нет — выбор «MCP или ничего»; адоптация навязывается средой; и впервые измерим на настоящей команде.

**Правки дизайна для v2:** убрать sync-слой; server `sessionId` + `Version`/etag вместо slug-natural-key; memory scope `global|workspace|project`; типы памяти → свободные tags; глубина дерева ~3 уровня (prose в session-blob и `Body`); гибрид (structured project-plan + markdown session-plan); dogfood с первого дня.

**Размещение БД.** Обобщить три существующие фабрики (`LogDbFactory`/`ConfigDbFactory`/`DataDbFactory`) в `IScopedDbFactory<TContext>.Get(scope, key, name?)`. Tasks → `tasks/{projectKey}.db` (project-scope, все таблицы модуля в одном файле). Memory → multi-scope (`memory/$global.db`, `memory/{workspaceKey}.db`, `memory/{projectKey}.db`). **Не объединять logs+tasks+memory в один `project.db`**: SQLite single-writer — hot log-ingest заблокировал бы запись плана; разные нагрузочные профили, миграции, feature-toggle. Физическое разделение per-module = изоляция writer-lock, generic-фабрика делает его бесплатным.

**Decision.** Рамка пересмотрена: дизайн под MCP-only жизнеспособен, сложность **ниже** исходной (удалён sync-слой, остальное ложится на готовый MCP/auth/data стек). **Но перед стройкой — дешёвый пилот** на реальной команде: тонкие mock-инструменты `tasks.session_save`/`tasks.node_upsert` + навязать средой у 4×2 разработчиков + 1–2 недели мерить compliance (порог ~70%) + честный бенч (одна задача → разные агенты). Проходит → полноценный план реализации отдельной сессией. Не проходит → не строим даже при готовой инфраструктуре.

**Consequences.**
- `doc/proposals/tasks-memory-modules/proposal-v2.md` — новый документ; исходные `proposal.md`/`critique.md` сохранены как история.
- `doc/plan.md` Phase 30 в этот заход **не трогался** (узкий scope правки). Обновление статуса Phase 30 на «pilot» — отдельным шагом.
- Главный труд/риск модуля переехал из «написать код» в «заставить агентов пользоваться» — и это теперь проверяемо до стройки.

---

## 2026-05-28 — Data module Wave 0 gate passed (refactored architecture, build approved)

**Context.** Phase 16 Wave 0 gate (critique + 2 PoCs) запущен. Изначальная plan rule: 0 RED + ≤3 YELLOW → build; 1+ RED → defer; ≥4 YELLOW → refactor. Skeptical critique вернулся с **3 RED + 8 YELLOW** — formally defer territory. Но после разбора каждого RED оказались либо solvable, либо неактуальными при пересмотре scope.

**Wave 0 results:**

1. **Critique (0.2)** — 3 RED: hash canonicalization undefined, WAL+per-call connection не даёт claimed benefit, thin send-helper рушится на transactions. 8 YELLOW (dialect concerns, refcount on DELETE, per-project quota, `__SchemaVersions` exposure, dogfooding semantics, log-only-instance failover, journal LOC underestimate, PRAGMA whitelist maintenance, request body size limit).

2. **linq2db SQL extraction PoC (0.3)** — `LinqExtensions.ToSqlQuery(query)` верифицирован end-to-end. Возвращает `QuerySql { Sql, Parameters: IReadOnlyList<DataParameter> }`. Артефакт в `.tmp/linq2db-poc/`. Gotchas: literal constants инлайнятся (pet оборачивает в captured locals), `query.ToString()` бесполезен, .NET 10 `AsyncEnumerable.ToListAsync` коллидирует.

3. **Explore Remote.HttpClient.Server (0.4)** — **NO-GO для end-to-end use**. Ships expression tree (требует shared assemblies/MappingSchema), не поддерживает cross-request transactions (только batch non-query внутри `ExecuteBatchAsync`). Per-DB routing через `configuration` URL segment + `IDataContextFactory` хороший, но недостаточно. Wave 5+ option если потребуется.

4. **Re-investigate Remote base namespace (0.6)** — confirms NO-GO для Remote-based dumb server. `LinqService` + `LinqServiceSerializer` (3209 LOC) coupled к `SqlStatement` AST + MappingSchema. **Нашли правильный server-side primitive: `DataConnection.Execute(sql, DataParameter[])` из `LinqToDB.Data` namespace** — ADO.NET binding + error wrapping встроены, AST не требуется. Client-side primitive обновлён: `IExpressionQuery.GetSqlQueries(SqlGenerationOptions?)` вместо `ToSqlQuery` (multi-statement support для InsertOrReplace/CreateTable).

**Decision: BUILD with refactored Wave 1 + DROP transactions from MVP.**

Resolution of REDs:
- **RED1 hash canonicalization → solved**: `SQL.Formatter` NuGet нормализует whitespace/case/CRLF; sha256 от formatted output. Спец фиксируется в Wave 1.
- **RED2 WAL+connection lifetime → solved**: Wave 1 добавляет `IHostedService` который раз в N минут вызывает `PRAGMA wal_checkpoint(TRUNCATE)` per active DataDb. Не блокер для MVP single-pet.
- **RED3 transactions → DROPPED**: KpVotes (`D:\my\prj\KpVotes` — verified by user) не использует BeginTransaction. Если появятся pets с tx needs — Wave 5+ server-side session protocol с TTL token. Не sweep'им под ковёр: explicitly documented limitation.

Resolution of YELLOWs absorbed into Wave 1 update:
- Migration history endpoint (`GET /api/data/{p}/{db}/migrations`) — не coupling pets к `__SchemaVersions` internal layout
- Refcount on DataDb DELETE (отказ если query in-flight)
- PRAGMA **deny**-list (не allow-list) — раз раз в release переписывать
- Explicit request body size limit per endpoint
- Cross-platform hash test (CRLF, BOM, comments)
- Journal LOC реалистично 200-300 (sync + async paths) — просто корректирую estimate, не блокер
- Per-project quota aggregate → Wave 5+ (single-pet не нужно)
- Log-only-instance 503 → уже в плане

**Decision rule re-application after refactor**: 0 RED + 2 absorbed-YELLOW + 6 fixed-YELLOW = build approved.

**Architecture clarified (overrides original plan):**

- **Server is dumb pass-through**. No `MappingSchema`, no `SqlStatement` AST, no Remote.HttpClient. Just:
  ```csharp
  using var db = new DataConnection(sqliteProvider, $"Data Source={dbPath}");
  db.Execute(request.Sql, request.Parameters.Select(p => new DataParameter(p.Name, p.Value, p.DataType)).ToArray());
  // or db.ExecuteReader for /query
  ```
- **Client extracts compiled SQL on its side** via `GetSqlQueries()`, ships `{statements: [{sql, params}]}` JSON. ~30 LOC pet-side wrapper.
- **Server side**: ~150-200 LOC (controller + DTOs + SQLite error → HTTP code mapping). Pulls `linq2db` for `DataConnection.Execute` (or just `Microsoft.Data.Sqlite` directly — even thinner).

**Consequences:**

- Phase 16 Wave 0 gate **closed: build approved**. Wave 1 starts next.
- `Wave 5+` теперь содержит explicit list: transaction sessions, batch endpoint, per-project quota, Liquibase XML, full Remote.HttpClient (если когда-нибудь нужен — dispatch hook известен).
- Memory `feedback-discovery-before-design` validated again: 2 PoCs + 2 critiques + 2 re-investigations спасли нас от потенциального Wave 5 path-of-no-return (полная Remote.HttpClient infra с MappingSchema sharing).
- KpVotes integration (Wave 3) — единственный pet до конца phase. tasks-mcp records 2026-05-28-{03+04+05+06} (Wave 0 артефакты + PoC report + plan revisions).

---

## 2026-05-28 — Data module design approved (multi-DB, REST pass-through, DbUp)

**Context.** Phase 16 был BLOCKED по нерешённым вопросам: storage layout, API shape, schema ownership. Memory: "Data — не сделан, возможно не строить (Turso/PocketBase делают лучше)". Dispatcher текущий — заглушка (stub endpoints, misleading DataTable model). Запрос /plan через несколько раундов discovery + 2 раунда critique привёл к утверждённой архитектуре.

**Decision.** Build своего Data модуля по плану `C:\Users\stdray\.claude\plans\noble-sniffing-bear.md`. Ключевые архитектурные решения:

1. **Storage**: per-project-per-db SQLite `data/db/{projectKey}/{dbName}.db` через `DataDbFactory`. Multiple DBs per project, явно создаются через lifecycle endpoint (не auto-create on first touch). Operational defaults в Wave 1: WAL mode + `max_page_count` quota (~1GB default) + 30s `CommandTimeout` + PRAGMA whitelist на /exec.
2. **Read/Write protocol**: raw SQL pass-through через `POST /api/data/{p}/{db}/{query|exec}`. Без KQL (двойной dialect — UX-ошибка). Без statement classifier (pet знает свой SQL через ORM, SQLite errors прокидываются). Parameter binding через Microsoft.Data.Sqlite.
3. **Schema management**: DbUp (`dbup-core` + `dbup-sqlite` 6.0.4) + custom `SqliteHashingJournal` (~100-150 LOC, subclass `SqliteTableJournal`, добавляет hash в `__SchemaVersions`). Idempotency по (name, hash) с 409 на mismatch. `__SchemaVersions` живёт внутри каждой DataDb файла.
4. **Migration entry**: API (scope `data:schema`) + UI-paste (workspace-admin). Liquibase XML через `DbUp.Extensions` — Wave 5+ optional.
5. **Auth**: ApiKey scopes `data:read/write/schema`. ApiKey project-level видит все DataDbs данного project'а. **Никаких per-DB/per-table flags** — только scope-level enforcement.
6. **gRPC + linq2db.Remote.Grpc — drop** (архитектурный mismatch с DDL-driven схемой).
7. **Pet client MVP**: thin send-helper, native ORM SQL extraction. **Future Wave 5+**: petbox-side обёртка над `LinqToDB.Remote.HttpClient.Server` (готовое в linq2db), pet-side использует `LinqToDB.Remote.HttpClient.Client` NuGet — full linq2db UX end-to-end. Не custom IDataProvider с нуля.
8. **MCP отложен в Wave 4** (не MVP). Когда подключится — единый `/mcp` endpoint на весь petbox через `ModelContextProtocol.AspNetCore` SDK, не модуль-специфичный.
9. **Driver pet — kpvotes-net** (Wave 3 dogfooding gate).

**Phasing**: Wave 0 (critique + linq2db PoC gate) → Wave 1 (foundation + APIs combined) → Wave 2 (UI + dogfood через REST) → Wave 3 (real pet integration) → Wave 4 (MCP) → Wave 5+ (deferred).

**Consequences.**
- Phase 16 в `doc/plan.md` разблокирован, будет заменён фазами из noble-sniffing-bear плана при имплементации.
- Существующая `DataTables` table остаётся untouched в M013 (data disabled, dead schema — drop отдельной cosmetic миграцией позже).
- Backup pre-Wave-5 требует service stop (hot-backup через `VACUUM INTO` — Wave 5 feature, не обещаем "cp достаточно").
- Wave 0 gate с rubric (0 RED + ≤3 YELLOW → build, 1+ RED → defer, ≥4 YELLOW → refactor) — защита от premature implementation.
- Plan отправлен в Ultraplan параллельно для remote refinement; локальный план — canonical source of truth.

---

## 2026-05-27 — Tasks + Agent Memory modules deferred

**Context.** Обсуждался дизайн двух тесно связанных модулей: **Tasks** (project-plan + session-plan со структурированным деревом, MCP-каноном и skill-синком) и **Agent Memory** (общий store по claude-code модели с 4 типами + agent-tag). Полная фактура — `doc/proposals/tasks-memory-modules/proposal.md`.

Запущен сторонний reviewer (general-purpose agent) с инструкцией дать жёсткую критику. Результат — `doc/proposals/tasks-memory-modules/critique.md`. Verdict: "не строить как сейчас, в честном — не делать вообще".

**Главные проблемы по критике:**
1. **Compliance-парадокс.** claude-code уже сейчас игнорирует более лёгкое требование (записи в `doc/tasks-mcp/`). MCP-tools с auth и upsert будут вызываться ещё реже, не чаще.
2. **Велосипед.** Существуют: mcp-server-memory (Anthropic), Linear/GitHub Issues + sub-issues, Obsidian+git, Logseq. Дублирование без явного выигрыша для single-user-pet-project.
3. **Дублирование с git.** `PlanNodeRef.commit` дублирует `git log -S`/blame; ревизионная история plan.md — git и так это делает.
4. **Multi-machine — реальные дыры.** SessionPlan slug-collision между machines, перезапись локальных правок plan.md при pull, last-write-wins без vector clock.
5. **Bidirectional sync как write-only mirror.** plan.md становится disposable. Хуже чем сейчас, где plan.md — source of truth.
6. **Per-project memory ломает user-level кейсы.** "Ты senior dev в Go" дублируется в каждом проекте и расходится.

**Decision.** Модуль **отложен в конец roadmap** до проведения двух экспериментов:
- **(a) Stop-hook эксперимент.** Поставить hook на claude-code, который при завершении turn'а пишет mock-запись (echo в файл) о правках `doc/plan.md` / `~/.claude/plans/*.md`. Поработать 2 недели. Посчитать compliance vs реальные правки. Если <70% — модуль не построим, даже когда напишем.
- **(b) Honest bench.** Дать claude-code / factory droid / opencode / pi одну и ту же задачу. Сравнить artifacts. Понять, нужна ли унификация или у всех уже git'абельный markdown.

После экспериментов — решить заново: строить как пропозал, строить минимальный Notes-модуль (один тип, markdown+tags+upsert, git как history), или не строить вовсе и обойтись Stop-hook + git auto-commit.

**Consequences.**
- `doc/plan.md` — добавлена `Phase 30: Tasks + Memory modules [DEFERRED]` в конце с ссылкой на пропозал и критику.
- Все упоминания Tasks как "next priority module" в memory (`project_overview.md`) остаются — модуль не отменён, отложен.
- Roadmap впереди: `Phase 16: Data module` (BLOCKED → разблокировка), kpvotes интеграция, dogfooding. Tasks/Memory — после.
- Bench в `doc/tasks-mcp/` остаётся как journal, но требование записывать туда снимается до момента когда модуль реально начнём строить (тогда вернётся вместе с design'ом).
