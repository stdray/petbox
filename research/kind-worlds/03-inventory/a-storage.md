# 03A — Хранение правил

Дата: 2026-08-26. Истина: origin/main 4d956ad5. Автор: petbox-worker.

## Что это за область

Где физически лежат документы правил методологии и всё, что их обслуживает: таблицы и их
схема, `MethodologyDefinitionService`, легаси-таблица `methodology_defs`, шаблоны
(`tasks_methodology_template_*`) и dual-read, версионирование/CAS, судьба документа при
закрытии инстанса. НЕ входит: резолвинг (какой раннтайм видит доска), вербы жизненного
цикла инстанса как процесса, поверхности чтения (MCP-контракт).

## Факты

| утверждение | как проверено | источник |
|---|---|---|
| 4 физические таблицы документов/указателей правил в per-project tasks-файле: `methodology_defs`, `methodology_templates`, `methodology_instances`, `methodology_active_instance` | сам: миграции | `src/PetBox.Tasks/Data/Migrations/M010_MethodologyDefs.cs:14,19`; `M012_MethodologyTemplates.cs:14,19`; `M013_MethodologyInstances.cs:14,19`; `M017_MethodologyActiveInstance.cs:11,16` |
| `methodology_defs` — бывший ЕДИНСТВЕННЫЙ синглтон-документ проекта (до модели инстансов), сейчас реклеймнут под утилитарный слой; тот же shape (kinds/linkKinds/tagAxes), тот же валидатор, тот же live-migration путь | сам: заголовок сервиса | `MethodologyDefinitionService.cs:14-29` |
| Тот же `methodology_defs` ещё и dual-read как "виртуальный шаблон" с ключом `"methodology"` (`source="definition"`), когда нет одноимённого stored-шаблона | сам: код `ListAsync`/`GetAsync` | `MethodologyTemplateService.cs:44-48` (dual-read), `:113-155` (Get), `LegacyDefinitionKey = MethodologyDefRow.SingletonKey` |
| Схемы 4 таблиц РАЗНЫЕ, не копии друг друга: `methodology_defs`/`methodology_templates` без `ClosedAt`; `methodology_instances` с `ClosedAt`; `methodology_active_instance` хранит `InstanceName`, а не `Json` | сам: DDL | `M010_MethodologyDefs.cs:19-27`; `M013_MethodologyInstances.cs:19-28`; `M017_MethodologyActiveInstance.cs:16-24` |
| Закрытие инстанса НЕ удаляет и не архивирует документ: `CloseAsync` пишет новую temporal-ревизию с тем же `Json`, только `ClosedAt` заполнен; строка остаётся `ActiveTo IS NULL`, читается вечно через `GetAsync`/`GetRulesAsync`/`GetDefinitionAsync(allowClosed:true)` | сам: код | `MethodologyInstanceService.cs:248-275` (Close), `:88-96` (GetDefinitionAsync allowClosed) |
| Отдельного архива закрытых инстансов НЕТ — ни таблицы, ни переноса строк; закрытая доска-член тоже не удаляется, только `TaskBoards.ClosedAt` | сам: нет `Delete`/архивной таблицы в этом сервисе; grep по "archive" в области методологии — 0 совпадений | `MethodologyInstanceService.cs` (нет метода delete инстанса); `git grep -in archive` — только UI-тест, не хранилище |
| Проверка уникальности слага kind'а — ТОЛЬКО внутри ОДНОГО валидируемого документа (`seenKinds`), не между документами/таблицами | сам: код валидатора | `Validation/MethodologyDefinitionValidator.cs:70,78,80` |
| `DefineAsync` (utility) и `DefineRulesAsync` (instance) не сверяются друг с другом или с чужими инстансами при записи — отсюда дубль kind `wiki` в `$utility` и в `quartet` (v22), зафиксированный в `02-snapshot.md` | сам: прочитан весь код обоих методов — ни один не читает чужую таблицу перед записью | `MethodologyDefinitionService.cs:65-131`; `MethodologyInstanceService.cs:169-238` |
| Версия/CAS (`version` как watermark) — независимый монотонный курсор НА КАЖДЫЙ `Key` в своей таблице (`TemporalStore.UpsertAsync`); у `methodology_defs` один курсор на проект, у `methodology_instances`/`methodology_templates` — свой курсор на каждый slug-ключ, у `methodology_active_instance` — свой курсор на указатель | сам: `TemporalStore.UpsertAsync` конфликты по `Key`+`ActiveTo` в каждом сервисе отдельно | `MethodologyDefinitionService.cs:99-113`; `MethodologyInstanceService.cs:213-224`; `MethodologyTemplateService.cs:165-176` |
| Членство доски (`TaskBoards.MethodologyInstance`) живёт в Core-каталоге — ФИЗИЧЕСКИ ДРУГОЙ БД, не в той, где лежат сами документы правил (per-project tasks-файл) | сам: комментарий модели + класс backfill | `TaskBoardMeta.cs:40-52`; `MethodologyInstanceBackfill.cs:14-16` ("Spans Core catalog... + per-project tasks files... FluentMigrator cannot own this alone... different DBs") |
| Cross-document миграции для контента правил уже существуют и явно сканируют ВСЕ ТРИ document-таблицы отдельно (не через общий каталог) | сам: заголовок мигратора | `LinkKindsDeclaredMigrator.cs:22-24` ("the project-singleton methodology_defs row + every active methodology_instances row + every active methodology_templates row") |
| Namespace шаблонов уже пересекается с легаси-ключом: `LegacyDefinitionKey == "methodology"` — теоретически можно создать stored-шаблон с ключом `"methodology"`, и он молча затенит dual-read (код это допускает, отдельно не проверяет) | сам: код `ListAsync`/`GetAsync`, нет `RejectBuiltinWrite`-аналога для этого ключа | `MethodologyTemplateService.cs:34-37,44-48,113-136` |

## Где модель протекает

- Дубль kind'а между utility-слоем и инстансом пишется беспрепятственно: `DefineAsync`/
  `DefineRulesAsync` валидируют только «свой» документ (`seenKinds` в `MethodologyDefinitionValidator.cs:70-80`)
  — последствие: маскировка бага (см. `02-snapshot.md`, дубль `wiki`), которую нельзя
  обнаружить на записи, только вручную сравнив документы.
- Закрытие инстанса — это НЕ конец жизни документа, а просто флаг: правила остаются
  читаемыми (`GetDefinitionAsync(allowClosed:true)`) НАВСЕГДА, без TTL и без переноса в
  архив — «архив закрытых инстансов» из задания физически не существует как отдельная
  сущность, это тот же ряд той же таблицы.
- Ключевое пространство шаблонов и легаси-ключ синглтона делят один и тот же слаг
  (`"methodology"`) без явной защиты — потенциальный второй источник тихой путаницы,
  отдельной от дубля kind'ов.
- Слияние в «один каталог проекта» (цель C) требует не просто объединить строки: 4 разные
  схемы (defs/templates без ClosedAt, instances с ClosedAt, active_instance без Json), 4
  независимых курсора версии, и членство доски в ДРУГОЙ БД (Core), которую уже сегодня
  трогает `MethodologyInstanceBackfill` через два прохода (Core + per-project) именно
  потому что FluentMigrator не видит обе БД разом.

## Противоречия

Не обнаружено расхождений между собственной проверкой и `02-snapshot.md`/`01-legend.md` —
факты снапшота (дубль `wiki`, невидимость `$utility` в списке миров) подтверждены кодом
без затрат на пересчёт: valve отсутствия cross-document проверки объясняет ИМЕННО тот
дубль, который снапшот наблюдал вживую.

Со слов аудита: «MethodologyDefinitionService — бывшее singleton-хранилище methodology_defs,
реклеймнутое под утилитарный слой, плюс dual-read как шаблон» — **подтверждено дословно**,
это пересказ собственного заголовка сервиса (`MethodologyDefinitionService.cs:14-29`), не
домысел аудита.

«Три поколения хранилищ» — **подтверждено по духу, неточно по счёту**. Физически ЧЕТЫРЕ
таблицы возникали в четыре разных момента (M010 defs → M012 templates → M013 instances →
M017 active_instance), а не три. Если считать «поколениями» только хранилища ПОЛНОГО
документа правил (JSON kinds/linkKinds), их действительно три: legacy-синглтон (defs,
реклеймнутый), instances (сменившие его для процессных kind'ов) и templates (не
поколение-замена, а параллельный инертный снимок, никогда никого не вытеснявший —
`M012_MethodologyTemplates.cs:14-17` прямо пишет «independent of… future instance entities»).
`methodology_active_instance` — четвёртая таблица, но она НЕ хранит документ правил вообще,
только имя указателя; включать её в «поколения хранилищ правил» — категориальная ошибка
аудита, а не альтернативный, но верный счёт.

## Что умрёт под целью C

- Само понятие «utility-слой» как ОТДЕЛЬНОЕ хранилище (`methodology_defs`/`MethodologyDefinitionService`)
  — если kind принадлежит проекту напрямую, синглтон-документ-реклейм больше не нужен как
  отдельная сущность.
- Dual-read `MethodologyTemplateService` → `LegacyDefinitionKey`/`SourceDefinition` (весь
  блок `ListAsync`/`GetAsync`, помеченный `SourceDefinition`) — исчезает вместе с legacy-документом.
- Разделение «open-инстанс хранит правила / closed-инстанс хранит правила навсегда как
  зомби-документ» — под целью C правила у проекта, а инстанс(процесс) закрывается не
  замораживая ничего.
- Необходимость сканировать 3 таблицы по отдельности в cross-document мигаторах
  (`LinkKindsDeclaredMigrator`) — заменяется одним проходом по каталогу.

## Открытые вопросы

- Каталог kind'ов проекта под целью C — это пятая новая таблица, или реюз одной из
  существующих четырёх (например, `methodology_templates` с иной семантикой)? От ответа
  зависит, нужна ли ещё одна миграция схемы или только перепривязка чтения.
- При слиянии — что происходит с версионным курсором закрытых инстансов: он гасится или
  участвует в едином курсоре каталога? Это меняет контракт CAS для `tasks_methodology_rules_upsert`.
- Переносится ли `TaskBoards.MethodologyInstance` (Core DB) в единую модель, или граница
  Core/per-project остаётся и после C — если остаётся, cross-DB часть стоимости C не
  уменьшается вообще, только схлопывается документная часть.
