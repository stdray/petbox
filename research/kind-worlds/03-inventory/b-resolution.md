# 03B — Резолвинг правил

Дата: 2026-08-26. Истина: origin/main 4d956ad5. Автор: petbox-worker.

## Что это за область

Как из доски (или узла через его доску) получается `MethodologyRuntime` — документ, отвечающий
на вопросы про kind доски / statusKind / workflow. Входит: пять функций-резолверов в
`TasksService.cs` (`RuntimeForBoardAsync`, `RuntimeAsync`, `RuntimesByBoardAsync`,
`UtilityRuntimeAsync`, `RuntimeForInstanceAsync`) и их публичные двери
(`GetRuntimeAsync`/`GetRuntimeForBoardAsync`), плюс все внешние вызовы этих дверей. Класс
`MethodologyRuntime` (движок правил) — не сюда, это отдельная область. Хранение
документов правил (`_methodologyInstances`, `_methodologyDefs`) — отдельный инвентарь.
Lifecycle-вербы (`adopt`, `close`) — отдельный.

## Факты

| утверждение | как проверено | источник |
|---|---|---|
| 5 алгоритмов: `RuntimeForBoardAsync`(3 ветки) → `UtilityRuntimeAsync`/`RuntimeForInstanceAsync`/`RuntimeAsync`(3 подветки); плюс `RuntimesByBoardAsync` — то же поштучно с дедупом по bucket'у членства | сам: чтение кода | TasksService.cs:233-310 |
| `RuntimeForBoardAsync` — ровно 3 ветки: utility-сентинел, именованный инстанс, легаси-null (форвард в `RuntimeAsync`) | сам: чтение кода | TasksService.cs:265-279 |
| `RuntimeAsync` (проектный) — 3 подветки: активный указатель, единственный открытый, пресеты | сам: чтение кода | TasksService.cs:239-263 |
| В TasksService.cs 10 вызовов `RuntimeForBoardAsync` с доской в руках — все легитимны (meta уже получена перед вызовом) | сам: построчно | :373,425,829,987,1337,1381,1622,1987,2533,2981 |
| 5 внешних вызовов `GetRuntimeForBoardAsync` (UI+cross-scope) — все с реальной доской в руках, легитимны | сам: чтение кода | ProjectTasks.cshtml.cs:86; TaskBoard.cshtml.cs:752; TaskBoardNode.cshtml.cs:268; Tasks.cshtml.cs:262; CrossScopeTaskSearchService.cs:210 |
| 1 внешний вызов `GetRuntimeAsync` — только когда `meta is null` (доски ещё нет) | сам: тернарник | TaskBoard.cshtml.cs:751-752 |
| 5 внутренних вызовов `RuntimeAsync` без доски-в-руках-для-классификации — все легитимны (см. ниже) | сам: построчно | TasksService.cs:151,350,1234,1344,2045 |
| Единственный врущий МЕХАНИЗМ — `EnsureMetaBackfillAsync`→`ToMetaDoc(kindSlug:null)`, классифицирует StatusKind ВСЕХ досок одним проектным runtime | сам: чтение кода + doc-комментарий, подтверждающий это намеренно | TasksService.cs:3126-3137; TasksSearchDocs.cs:114-121 |
| Этот механизм вызывается из 3 точек, не из 1 | сам: grep+чтение | TasksService.cs:1235, 2111-2112, 2339,2343 |
| `EnsureLexicalBackfillAsync` тоже принимает `runtime`, но параметр НЕ используется — `IsIndexable(n, runtime)` игнорирует его | сам: чтение кода, комментарий "no longer consulted" | TasksSearchDocs.cs:52-59 |
| `RuntimesByBoardAsync` дедуплицирует по bucket'у членства (не по доске) — при 100 досках на 1 инстансе это 1 чтение, не 100 | сам: чтение кода | TasksService.cs:290-303 |
| Хот-путь записи (`UpsertAsync`) делает РОВНО ОДИН резолв на доску (`RuntimeForBoardAsync`), не пачку | сам: чтение кода | TasksService.cs:1622 |
| `MethodologyInstanceService`/`MethodologyDefinitionService`/`WorkDeferredStatusMigrator` строят `new MethodologyRuntime(def)` НАПРЯМУЮ из документа, который сами же пишут — это НЕ вызов резолвера, доски нет или не нужна | сам: чтение кода | MethodologyInstanceService.cs:193,319,487,523; MethodologyDefinitionService.cs:87; WorkDeferredStatusMigrator.cs:292 |
| `GetMethodologyGuideAsync` (669-720) содержит СВОЮ копию 3-ветки active/single-open/ambiguous, не вызывая `RuntimeAsync` — 6-й, отдельный кусок той же логики | сам: чтение кода | TasksService.cs:683-719 |

## Где модель протекает

- **Единственная реальная ложь**: `EnsureMetaBackfillAsync` классифицирует StatusKind всех
  досок проекта одним runtime (project-level или utility, смотря что подвернётся первым),
  не зная реальный kind каждой доски (`kindSlug: null`). Последствие: `search_meta.StatusKind`
  для доски из "чужого" мира может быть неверным до первой записи в эту доску (self-heal per
  write, комментарий сам это признаёт: TasksService.cs:3122-3125). Ровно это и есть карточка
  `work/meta-backfill-classifies-with-project-runtime`.
- Побочный эффект: `EnsureLexicalBackfillAsync` таскает `runtime` как параметр, который давно
  ничего не решает (`IsIndexable` его игнорирует) — мёртвый параметр, не баг, но шум,
  затрудняющий чтение (выглядит как второй врущий путь, пока не проверишь `IsIndexable`).

## Противоречия

- Аудит: «ровно один врущий путь». Уточнение — верно на уровне МЕХАНИЗМА (один метод,
  `EnsureMetaBackfillAsync`, всегда с `kindSlug: null`), но не на уровне call site: этот
  механизм вызывается из ТРЁХ мест (`ExactIdentifierHitsAsync`:1235, explicit-statusKind
  listing:2111-2112, `HybridCandidatesAsync`:2339,2343). Если считать «путь» как call site —
  их три, не один. Не противоречие по сути, противоречие по счёту при буквальном чтении фразы
  аудита.
- Аргумент «резолвинг на хот-пути ударит по объединению хранилищ» — проверено и НЕ
  подтвердилось на уровне TasksService: запись (`UpsertAsync`) уже делает 1 резолв на доску;
  массовое чтение (`RuntimesByBoardAsync`) уже дедуплицирует по bucket'у, не по доске. Под
  целью C бакетов вообще не будет — один проектный каталог, дешевле, а не дороже. Оговорка:
  сама стоимость чтения `_methodologyInstances`/`_methodologyDefs` из БД — отдельный
  инвентарь (хранение), здесь не измерялась.
- С остальным аудитом (5 путей переведены на per-board, см. 02-snapshot.md) — не обнаружено.

## Что умрёт под целью C

- Вся 3-ветка `RuntimeForBoardAsync` (utility-сентинел / инстанс / легаси-null) — схлопнется
  в один безусловный проектный резолв.
- `RuntimeForInstanceAsync`, `UtilityRuntimeAsync` как отдельные функции резолвинга правил
  (роль конфигурации процесса у инстанса может остаться, но не как источник правил).
  `RuntimeAsync`'s 3-подветка (активный указатель/единственный открытый/пресеты) — умирает
  вся, вопрос «чей мир» больше не задаётся.
  `RuntimesByBoardAsync`'s дедуп по bucket'у — не нужен, один каталог на все доски разом.
- `EnsureMetaBackfillAsync`'s `kindSlug: null` перестаёт быть ложью по построению — не
  потому что чинится, а потому что вопрос "runtime какого мира" исчезает.
- `GetMethodologyGuideAsync`'s active/single-open/ambiguous ветка (683-719) — тоже умирает,
  guide перестаёт зависеть от того, какой инстанс активен.

## Открытые вопросы

- Что заменяет `AssertProcessRoleSingletonAsync`'s поиск "мира" (сейчас — instanceKey/
  utility-сентинел/null bucket, MethodologyInstanceService.cs:487-523) под C — вопрос
  инстанс-сервиса, не резолвинга, но резолвинг сейчас передаёт ему `runtime` как аргумент,
  и если правила становятся проектными, сигнатура этого вызова меняется у ВСЕХ 3 колл-сайтов.
