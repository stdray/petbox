# Замер связанности: можно ли собрать «наблюдение» композицией

Только чтение кода. Пути — от `D:/my/prj/petbox`.

> **Замер сделан под форму «свой тип строки/свой модуль», ОТМЕНЁННУЮ владельцем
> 2026-08-28.** Главный вывод — «связь наблюдение↔узел структурно невозможна из-за FK» и
> «350-450 строк нового кода» — БОЛЬШЕ НЕ ДЕЙСТВУЕТ: в принятой форме оба конца ребра
> сами стали строками `plan_nodes`, и `RelationStore` заработал бесплатно, без
> переизобретения модели рёбер. См. `70-implementation-2026-08-28.md`. Частные измерения
> (сигнатура `FindDuplicateKeyAsync`, приваренность `CollapseAsync`) остались верны и
> пригодились как есть.

## Таблица

| # | способность | обобщённость (а) | зависимости хозяина (б) | вердикт (в) |
|---|---|---|---|---|
| 1 | `AutocaptureDedup.FindDuplicateKeyAsync` | Дженерик факт: сигнатура — `IReadOnlyList<(string Key, string Text)> existing` (`src/PetBox.Web/Search/AutocaptureDedup.cs:75-77`), никакой `MemoryEntryRow` не видит | только `ILlmClient` (embed) + опциональный `EmbeddingCache` (тот же файл) | **переиспользуема как есть** — ядро сравнения (нормализация + косинус) уже параметризовано парой (ключ, текст), не типом строки |
| 1b | `AutocaptureDedup.CollapseAsync` (полный sweep стора) | Приварен: сигнатура берёт `IMemoryService memory` напрямую (`AutocaptureDedup.cs:108`), внутри — `memory.ListAsync(...)` → `IReadOnlyList<MemoryEntryView>` (`Contract/IMemoryService.cs:27`) и `memory.UpsertAsync(..., MemoryEntryInput, MemoryDelete, ...)` (`IMemoryService.cs:86`) | `IMemoryService` целиком; `ClusterAsync`/`PickCanonical`/`MergeMetadata` читают поля `MemoryEntryView.Description/Metadata/Version/Key` и JSON-свойства `sources/seenIn/sessionId/messages` (`AutocaptureDedup.cs:143-217`) | **приварена, писать заново** (или параметризовать интерфейсом уровня «list+upsert над TRow»); собственно merge-политика (provenance union) тоже специфична для memory-метаданных |
| 2 | `RelationStore` | Не дженерик по типу узла — `Relation.FromNodeId/ToNodeId` это FK-колонки на `plan_node_ids` (`src/PetBox.Tasks/Data/Relation.cs:19-30`), реестр наполняется ТОЛЬКО триггерами от `plan_nodes` (`Migrations/M014_Relations.cs:47,79,88,98`) | `IScopedDbFactory<TasksDb>` — файл `tasks/{project}.db`; `AssertNodeExistsAsync` бьёт конкретно в `TaskNodeId`/`plan_node_ids` (`RelationStore.cs:222-228`) | **приварена, писать заново** — связь наблюдение↔узел структурно невозможна: оба конца обязаны быть строками `plan_nodes` (FK ON DELETE CASCADE), а наблюдение живёт не в этой SQLite-таблице (и физически не в этом файле — `MemoryDb`≠`TasksDb`, см. `Data/MemoryDb.cs:9`, `Data/TasksDb.cs:9`) |
| 3 | `CommentService` | Приварен к узлу доски по конструкции: `CommentRow.NodeId` — «stable TaskNode.NodeId владеющего узла» (`src/PetBox.Tasks/Data/Comment.cs:6-19`), `Board` — партиция как у `TaskNode.Board` | `IScopedDbFactory<TasksDb>`, `TemporalStore`, `SqliteFtsIndex`, `SearchService`, `TasksSearchDocs.CommentToDoc` (`Services/CommentService.cs:16-27,111-119`) | **приварен, писать заново** — «узел доски» не параметр, а поле идентичности строки; дерево `ParentId`/теги (`CommentTag`) переносимы по идее, но код завязан на `Board`+`NodeId` как обязательные колонки |
| 4 | usage-слой (`MemoryUsageRecorder`/`entry_usage`/`delivery_events`) | Схема таблиц обобщена: `entry_usage` PK = `(Store, Key)` — просто строки, без FK на `MemoryEntry` (`Data/EntryUsage.cs:12-13`); `delivery_events.Store/Key` тоже голые строки (`Data/DeliveryEvent.cs:19-20`) | Класс жёстко привязан к файлу: конструктор берёт `IScopedDbFactory<MemoryDb>` (`Services/MemoryUsageRecorder.cs:27,33`), т.е. пишет строго в БД памяти | **переиспользуема после параметризации** — сама схема сущность-агностична (`store`+`key` как у любой другой временной строки), но реализация нужно параметризовать по `IScopedDbFactory<TDb>`/фабрике подключения, чтобы писать в стор наблюдений |
| 5 | `TemporalStore` (сам движок) | см. ниже | — | база уже дженерик-класс |

## Что обязана предоставить новая строка (`TemporalRow`, `src/PetBox.Core/Data/Temporal/TemporalRow.cs:10-46`)

Обязательные унаследованные колонки: `Key` (string), `Version` (long), `ActiveFrom`/`ActiveTo` (SCD-2), `PrevKey` (rename lineage), `Created`/`Updated`.
Обязательные хуки, которые реализует ПОТОМОК:
- `abstract bool SamePayload(TemporalRow other)` — сравнение только payload-полей (`TemporalRow.cs:34`);
- `abstract TemporalRow AsRevision(long version, DateTime created, DateTime updated)` — `this with {...}` (`TemporalRow.cs:45`);
- опционально `virtual ChangedPayloadFields(...)` для информативного Stale-конфликта (default — пустой список, `TemporalRow.cs:41`).

Дальше `TemporalStore.UpsertAsync<TRow>` (`TemporalStore.cs:140-185`) сам делает classify/apply/delta по `db.GetTable<TRow>()` через LinqToDB — новому типу нужен маппинг `[Table]`/`[Column]` (LinqToDB.Mapping) и, если несколько сторов делят файл, `Expression<Func<TRow,bool>> partition`.

## Уже есть третий вид строки — не память, не узел доски

Да, и не один:

**`CommentRow`** (`src/PetBox.Tasks/Data/Comment.cs:13-43`) — прямым текстом в комментарии к классу: «structurally a degenerate spec node... NOT a TaskNode, so it never enters tasks_search / the workflow FSM». 44 строки на тип строки + `CommentTag` (SCD-2 open-теги, тот же файл, ещё 14 строк) + `CommentService` 427 строк (`Services/CommentService.cs`) — но там же FTS-индексация, поиск, теги, реплаи — заведомо больше, чем понадобится наблюдению.

**`AgentDefinitionRow`** (`src/PetBox.Core/Data/AgentDefinitionRow.cs:13-28`) — ещё дальше от обоих хозяев: портативный JSON-документ, партиционированный по `ProjectKey`, живёт в `PetBox.Core`, а не в `PetBox.Memory`/`PetBox.Tasks`. Реализация — 28 строк на строку + `AgentDefinitionService` 186 строк (`Services/AgentDefinitionService.cs`), и это ГОРАЗДО ближе к тому минимуму, который нужен наблюдению: чистый CRUD через `TemporalStore.UpsertAsync`, без dedup/relations/comments/search — только `ListAsync`/`GetAsync`/`UpsertAsync`/`DeleteAsync` плюс валидация слага и конфликт-сообщения (`AgentDefinitionService.cs:40-186`).

Итог: третий вид уже дважды собран, причём `AgentDefinitionService` и есть почти готовый шаблон «TemporalStore + новый тип строки без готовых способностей хозяина» — минималистичный CRUD-каркас, к которому нужно добавить дедуп-примитив (переиспользуемый, п.1) и типизированные рёбра (заново, п.2).

## Оценка: что придётся написать заново

- **Тип строки `ObservationRow : TemporalRow`** — по образцу `AgentDefinitionRow`/`CommentRow`: ~30-50 строк (Key/Version/статус-поле/payload + `SamePayload`/`AsRevision`).
- **Дедуп для наблюдений**: `FindDuplicateKeyAsync` переиспользуется как есть (п.1) — 0 строк нового кода на само сравнение; НО `CollapseAsync`-эквивалент (периодический sweep + merge провенанса) нужно переписать под `ObservationRow`/новый стор — по объёму сопоставимо с текущим `CollapseAsync`+`ClusterAsync`+`MergeMetadata` (строки 108-217 файла) — **≈110 строк новых**, если merge-семантика такая же (union источников); меньше, если статус-поле `seen/promoted/declined` упрощает merge.
- **Сервис CRUD** (list/upsert/get/delta) — по образцу `AgentDefinitionService`: **≈150-190 строк**.
- **Два типизированных ребра (промоушен, прибитие)** — НЕ через `RelationStore` (FK-барьер, п.2 доказывает это конструктивно): нужна отдельная таблица эджей между сторами наблюдений и `plan_node_ids`/задачами, либо эдж хранится СТРОКОЙ в самой `ObservationRow` (denormalized ссылка на NodeId, без FK) — второе дешевле и соответствует «минимальной версии» из постановки (документ 24, раздел «Минимальная версия»). Оценка: **≈30-60 строк**, если без FK.
- **Usage-слой**: параметризовать `MemoryUsageRecorder` по `IScopedDbFactory<TDb>` или завести аналог с той же (Store,Key) схемой в сторе наблюдений — **≈40-60 строк** (в основном копия существующего класса с заменой типа фабрики).

**Итого нового кода** (без UI/FSM/комментариев, как и просит «минимальная версия» из документа 24): ориентировочно **350-450 строк**, из которых готовый чужой код переиспользуется практически без изменений только в п.1 (`FindDuplicateKeyAsync`). Остальные три способности (`RelationStore`, `CommentService`, `MemoryUsageRecorder`) — не composable "как есть"; ближе всего к переиспользованию usage-слой (схема уже дженерик, нужна только параметризация фабрики подключения), дальше всего — `RelationStore` (реальный DB-level FK делает связь наблюдение↔узел структурно невозможной без переписывания модели рёбер).

Не проверено: содержимое `MemoryEntryView`/`MemoryEntryInput` целиком (только сигнатуры интерфейса), таблицы `M007_EntryUsage`/`M011_DeliveryEvents` (файлы миграций не найдены по этим именам — возможно объединены в другую миграцию, не искал глубже), нет ли других мест, вызывающих `AutocaptureDedup.CollapseAsync` кроме `BehaviorPatternJob`/`SessionFactsJob` (не проверял вызывающий код).
