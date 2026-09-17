# 23 — Два каскада и профили. Инвентаризация. 2026-09-10
Автор: petbox-worker · источники: код (D:/my/prj/petbox, origin/main рабочая копия), файл `~/.petbox/roles.json`, `~/.petbox/wire.log`

## Что здесь верно сейчас

- **Серверный каскад — НЕ merge, это НЕЗАВИСИМАЯ КОНКАТЕНАЦИЯ двух чужих друг другу строк.**
  `GET /api/memory/{projectKey}/canon` читает РОВНО `store:"canon", key:"index"` в ДВУХ разных
  контейнерах (project-контейнер и `WorkspaceMemory.ContainerKeyFor(wsKey)`) и возвращает как
  `CanonResponse{project, workspace}` — без слияния на сервере. [код `MemoryApi.cs:44,80-99,131-151`]
- Склейка в один текст происходит в ките, не на сервере: `canon.ts:buildBlock` кладёт
  project-секцию, потом workspace-секцию под своим заголовком; порядок фиксирован (workspace
  всегда последней). [код `canon.ts:109-132`]
- **Проект не может переопределить/удалить workspace (и наоборот)** — не правилом, а физически:
  разные stores/контейнеры (разные SQLite-файлы, разные ключевые пространства), у вызова нет
  общей адресации для "победы" над соседней ногой. [код `MemoryApi.cs:80-99`, `WorkspaceMemory.cs:47`]
- Внутри ОДНОЙ ноги write — temporal upsert ЦЕЛОЙ записи (`UpsertAsync(upserts, deletes, ...)`):
  тело заменяется целиком (PATCH = "поле не прислано → взять текущее", не патч подстроки), есть
  soft-delete по ключу (`MemoryDelete`). Канон-инъекция читает только ключ `index`, так что
  delete/override — возможность API "запись", не механизм МЕЖДУ scope'ами. [код `IMemoryService.cs:75,86`,
  `MemoryService.cs:789-804`]
- User-уровня в серверном каскаде нет нигде: `WorkspaceMemory` знает только project и workspace
  контейнеры, третьей сущности "user" в этом коде не существует. [код `WorkspaceMemory.cs:19-58`]
- **Файловый каскад — 4 операции, задокументированы дословно в заголовке модуля**: ADD (полный
  `.json` для нового slug), CHANGE (частичный `.json`, RFC7396-подобный патч —
  `PATCHABLE_FIELDS=[tier, requiredCapabilities, spawn, escalation]`), REMOVE
  (`{"slug":...,"removed":true}` — настоящий tombstone, с `reason`), REPLACE roster
  (`layer.json` c `"mode":"replace"`, всё нижнее отброшено). [код `layer-cascade.ts:1-70`]
- **Проза в файловом каскаде НЕ мёржится**: `petbox-<slug>.md` полностью ЗАМЕНЯЕТ notes и
  сбрасывает накопленные append-секции; `petbox-<slug>.append.md` ДОБАВЛЯЕТ секцию с атрибуцией
  по слою. Replace+append в одном слое — ошибка E4 (replace побеждает, append теряется, громко).
  [код `layer-cascade.ts:14-24`]
- Порядок: `base` (кит, `DEFAULT_AGENT_DEFINITION`) < `user` (`~/.petbox/agents`) < `project`
  (`<root>/.petbox/agents`), объявлен вызывающим, не выводится из времени. Два режима: BUILD
  (падает громко на broken layer) и RENDER/SessionStart (никогда не падает, маркер+wire.log).
  [код `definition-source.ts:1-33,60-90,132-160`]
- **Где каскады реально сходятся:** `pull-memory.ts` — баннер claude-code собирает protocol
  (файловый каскад) + `fetchCanonBlock` (серверный каскад) + `buildOwnerOnlySkillsBlock`
  (диск, скиллы) + `buildStaleBaseWarning` в ОДНУ лестницу деградации; ничто их не сверяет,
  они просто склеиваются по бюджету байт. [код `pull-memory.ts:1-46`, `session-budget.ts:104-116`]
- **Пятое место прозы сверх названных владельцем четырёх: скиллы + хардкод-строки кита.**
  `SKILL.md` (petbox/petbox-methodology/petbox-agent-factory) — кит-шаблонная проза с
  origin-маркером `petbox: managed`, пишется `wire`/`apply`, живёт отдельно от role-каскада.
  Плюс сам баннер несёт СВОЮ прозу (`buildStaleBaseWarning`, broken-layer маркер,
  `EMPTY_CANON_TEXT` в `canon.ts:56`) — текст, зашитый в TS кита, не прошедший ни через один
  из двух каскадов. [код `skill-files.ts:1-45,831-857`, `canon.ts:56`]
- **AGENTS.md — вне обоих каскадов и вне управления китом**: grep по `wire.ts`/`apply-write.ts`
  на `AGENTS.md` — 0 совпадений. Ручной git-файл (601 строка), харнессы читают нативной
  конвенцией, не через petbox-хук; ничто не сверяет его с канон-записями или role-notes.
  [код-поиск, AGENTS.md прочитан]
- **Версии — bitemporal SCD-2, не git.** `TemporalRow` (ActiveFrom/ActiveTo/Version, soft-close,
  ничего физически не удаляется) даёт: полную историю ревизий на диске, монотонный Version на
  запись (файл 17: index v25/v13), delta-курсор `DeltaAsync(sinceVersion)`. **Не даёт** на
  MCP/REST-поверхности: ни point-in-time чтения, ни restore — `GetAsync` читает только активную
  ревизию. [код `TemporalRow.cs:1-48`, `IMemoryService.cs:28,86`; grep `AsOf|Restore` по
  `PetBox.Memory`+`PetBox.Web`+`clients-ts` — 0 совпадений вне комментариев]
- Git даёт то, чего нет у temporal-таблиц: построчный diff, ручной checkout/revert, offline-
  историю, ветвление. Temporal-таблицы дают то, чего нет у git: историю без отдельного
  commit-действия (каждый upsert уже версия), point-in-time снимок по ВСЕЙ БД без репозитория
  поверх памяти. "Восстановить прошлую канон-запись" не выставлено ни одним инструментом.
- **Профили — 3 реально определены, 1 активен.** `~/.petbox/roles.json` (`formatVersion:2`):
  `opencode-main` (`activeProfile` сейчас), `opencode-go-max`, `opencode-direct`. Профиль =
  `{agent-харнесс → {role → {model, origin, provider}}}` — только модели, ни поля прозы.
  [файл `~/.petbox/roles.json`]
- Переключение: `wire profile use <name>` — ставит `activeProfile`, создаёт пустую полку, если
  имя новое. [код `wire.ts:478,595,2060`, `roles.ts:271-279`]
- **"Реально используется больше одного" — НЕПРОВЕРЕНО**: `wire.log` не содержит ни
  `profile use`, ни `activeProfile` (0 совпадений) — лог пишет только SessionStart-деградации,
  не CLI-вызовы. Единственное свидетельство — сам файл: 3 профиля существуют, 1 активен;
  история переключений не установлена (файл вне git). [файл, grep, НЕПРОВЕРЕНО далее]

## Где механизм на самом деле живёт
`src/PetBox.Web/Memory/MemoryApi.cs:44-151` — обе ноги серверного канона, без слияния.
`src/clients-ts/petbox-wire/src/canon.ts:56,95-132` — склейка project+workspace в кит-коде.
`src/clients-ts/petbox-wire/src/layer-cascade.ts:1-70` — все 4 файловые операции, схема патча.
`src/clients-ts/petbox-wire/src/definition-source.ts:1-160` — порядок base<user<project, 2 режима.
`src/clients-ts/petbox-wire/src/pull-memory.ts:1-46` — точка схождения обоих каскадов + скиллы + баннер-проза.
`src/PetBox.Core/Data/Temporal/TemporalRow.cs`, `TemporalStore.cs` — bitemporal-движок без AsOf/Restore.
`~/.petbox/roles.json`, `src/clients-ts/petbox-wire/src/roles.ts:69-90,271-313` — профили, только модели.

## Что НЕ проверено
- Даёт ли `restic`-бэкап окольный "restore" канона вне продуктовой поверхности — отдельная
  область (файл 18) — [НЕПРОВЕРЕНО].
- История реальных переключений `wire profile use` до сегодняшнего `opencode-main` — файл вне
  git, `wire.log` её не пишет — [НЕПРОВЕРЕНО], нужен внешний лог оболочки владельца.
- Прямой SQL/CLI-доступ к temporal-таблицам в обход продуктовой поверхности (для оператора БД) —
  в коде MCP/REST его нет, внешние пути не проверялись — [НЕПРОВЕРЕНО].

## Противоречия
- Формулировка владельца «серверный каскад — merge only» технически неточна: слияния (в смысле
  разрешения конфликта одного ключа из двух источников) НЕТ ВООБЩЕ — есть склейка двух
  независимых, никогда не пересекающихся по ключам легов. «Merge only» ближе к «append only, без
  пересечения» — сильнее, чем «нет override»: override между легами структурно НЕВОЗМОЖЕН, а не
  запрещён политикой.

## Что отсюда следует
По осям п.2: **override** — нет (write одной ноги не видит другую); **delete/tombstone** — да,
но только внутри своей ноги, для целой записи, не между project/workspace; **per-field patch** —
нет на уровне прозы (Body — непрозрачный текст, апсерт заменяет целиком; PATCH есть только для
того, какие ПОЛЯ конверта присланы); **user-уровень** — не поддержан ни одной структурой
(`WorkspaceMemory` знает 2 контейнера) — добавление требует третьего типа контейнера, нового
резолва в `CanonAsync`, новой ветки в `canon.ts` и решения по auth (`SandboxContainment`): это
новая ось контейнеризации, а не конфиг-флаг. Файловый каскад решает то же самое уже сегодня,
потому что строился как патч-система с начала (`RolePatch`, tombstone); серверная память
строилась как CRUD над целыми записями в двух фиксированных контейнерах — разница архитектурная,
не конфигурационная.
