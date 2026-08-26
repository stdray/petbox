# 03D — Поверхности чтения

Дата: 2026-08-26. Истина: origin/main 4d956ad5. Автор: petbox-worker.

## Что это за область

Всё, чем спрашивают систему про kind'ы досок, миры и правила: `tasks_methodology_guide`,
`tasks_methodology_list`, `_active_get`, `_rules_get`, `_utility_get`, `tasks_workflow`,
`tasks_board_list`, `tasks_search` (`kind`/`statusKind`), `tasks_node_get`, тексты ошибок
`board_create`, описания MCP-инструментов, UI-страницы досок. НЕ входит: хранение, алгоритм
резолвинга, семантика lifecycle-верб'ов.

## Факты

| утверждение | как проверено | источник |
|---|---|---|
| `guide` без `key` на `$system`: kind доски `wiki` показан только потому, что продублирован в правилах инстанса `quartet` (v22, source:instance) — не потому, что guide читает утилитарный слой | сам: вызов `tasks_methodology_guide($system)` | verb |
| Схема `guide` принимает только `key`/`projectKey` — параметра `board` нет вовсе | сам: `tool_describe`/схема тула | verb schema |
| `guide(key:probe2)` в песочнице (kind доски `wiki` есть только в утилитарном слое проекта) в блоке «Other kinds this server knows» вообще не упоминает `wiki` — ни как объявленный, ни как известный | сам: sandbox call `tasks_methodology_guide(key:probe2)` | verb (lab key) |
| `tasks_methodology_list` не содержит `$utility`; поле `kinds` инстанса — эхо ДЕКЛАРАЦИИ в документе правил, не список kind'ов реально используемых board-членами: инстанс `classic` в песочнице объявляет `["classic","wiki"]`, при этом единственный board-член — kind `classic`, board'ов kind `wiki` в инстансе нет | сам: sandbox `tasks_methodology_list`; код `MethodologyInstanceService.cs:585-592` (`Kinds: def.Kinds...`, независимо от `members`) | verb + код |
| `board_create` c `methodologyInstance:"$utility"` и неизвестным kind: сообщение честно включает утилитарный kind (`wiki`) вместе с пресетами, без дублей | сам: sandbox `tasks_board_create($utility, kind:"nope-kind")` | verb (lab key) |
| `board_create` c `methodologyInstance:probe2` (инстанс) и неизвестным kind: сообщение НЕ включает `wiki` (утилитарный kind), при этом `simple` продублирован (`simple\|spec\|ideas\|intake\|work\|classic\|simple`) | сам: sandbox call, kind=`nope-kind` | verb (lab key) |
| Отсутствие `wiki` в этом сообщении — не лакуна текста, а факт: реальная попытка создать board kind=`wiki`, membership=`probe2` (инстанс) тоже отклонена — утилитарный kind СУБСТАНТИВНО невалиден для инстанс-членства | сам: sandbox call, kind=`wiki`, membership=`probe2` — отклонено | verb (lab key) |
| Корень дубля `simple\|...\|simple`: `KnownKinds()` = `Enum.GetValues<BoardKind>()` конкатенировано с `_kinds.Keys` без Distinct; ветка runtime выбирается `isUtilitySentinel ? Utility : instanceKey!=null ? Instance : legacy` — т.е. instance-ветка НЕ видит utility-kinds принципиально, не по недосмотру текста | код: `TasksService.cs:150-172`, `MethodologyRuntime.cs:287-289` | код |
| `utility_get` на проекте без слоя — жёсткая ошибка (уже установлено в `02-snapshot.md:19`, не переоткрывалось) | со слов снимка | `02-snapshot.md:19` |
| `utility_get` с слоем ($system v2 и lab v1) возвращает только kind'ы слоя; ни намёка на kind'ы, объявленные в инстансах | сам: `tasks_methodology_utility_get($system)` и sandbox `(kind-resolution-lab)` | verb x2 |
| `active_get` вернул `{"key":"alt","version":1}`, хотя `alt` — ЗАКРЫТЫЙ инстанс (methodology_list: `alt.closed:true`) — dangling-указатель воспроизведён заново, без признака `closed` в ответе | сам: sandbox `tasks_methodology_active_get` сверено с `tasks_methodology_list` | verb (lab key) |
| `guide` без `key`, при том что активный указатель — `alt` (закрытый): guide НЕ упоминает `alt` вовсе, репортит «2 OPEN instances (alt2, probe2), none active» — как будто указателя нет. Открытые `alt2`/`probe2` оба открыты одновременно, поэтому «единственный открытый» тоже не сработал бы | сам: sandbox `tasks_methodology_guide()` без key | verb (lab key) |
| UI список досок проекта (`Tasks.cshtml`) и страница одной доски (`TaskBoard.cshtml`) рисуют бейдж «not active» с тултипом «belongs to open methodology instance '$utility'...» для ЛЮБОЙ board с `MethodologyInstance != EffectiveActiveInstance` — включая board'ы членства `$utility`, для которых утверждение «open methodology instance» ложно (слой — не инстанс и не «открыт») | по коду: `TaskBoard.cshtml.cs:513-514`, `TaskBoard.cshtml:30-35`; `Tasks.cshtml.cs` (поле `EffectiveActiveInstance`), `Tasks.cshtml:127-131` | код |
| Само разрешение правил на этих же страницах — честное, per-board (`GetRuntimeForBoardAsync`): `Tasks.cshtml.cs:252-266` (`ClassifyByBoardAsync`), `TaskBoard.cshtml.cs:746-753` (`ResolveProcessAsync`) — баг только в ЛЕЙБЛЕ, не в резолвинге | код | код |
| `ProjectMethodology.cshtml.cs` (админ-редактор правил инстанса) — 0 упоминаний `utility` во всём файле (661 строка); структурно не может показать или редактировать kind'ы утилитарного слоя | по коду: `grep -ci utility` = 0 | код |
| `Doc/Methodology.cshtml.cs` — статичный markdown-чит-лист (`Pages/Doc/content/methodology.md`), без обращения к данным проекта; вне области «врёт/не врёт про конкретный проект» | по коду | код |
| Ни один верб в полном списке МCP-тулов (проверен весь список) не отвечает на «какие kind'ы объявлены в проекте X» (объединённо: слой + все инстансы) и на «кто владеет kind'ом X» — оба вопроса требуют ручного пересечения `utility_get` + `rules_get` по каждому инстансу | сам: сверка полного списка тулов | список тулов |

Проверено эмпирически: 9 поверхностей (guide×3 варианта, methodology_list, board_create×3
ветки, utility_get×2, active_get, board_list). По коду (без живого вызова): 5 UI/серверных
поверхностей (Tasks.cshtml[.cs], TaskBoard.cshtml[.cs], ProjectMethodology.cshtml.cs,
Doc/Methodology.cshtml.cs, TasksService/MethodologyRuntime).

## Где модель протекает

- `guide` структурно не принимает `board` — при N инстансах + слое НЕТ ни одного вызова,
  дающего честный сводный каталог; агент обязан звать guide на каждый `key` плюс `utility_get`
  и сводить руками — источник тех самых дублей (`wiki` в quartet и в слое одновременно).
- `guide` без `key`, если активный указатель битый (нацелен на закрытый инстанс), тихо
  ПЕРЕСЧИТЫВАЕТ «нет активного», не называя битый указатель — агент, следующий совету guide
  «вызови set_active», не узнает, что указатель вообще существует и его надо чистить.
- `methodology_list.kinds` — эхо документа, не факт использования: дубль-объявление без единой
  board-члена выглядит неотличимо от дубля, который реально маскирует баг (ровно случай
  `quartet`/`wiki` в `$system`, но подтверждено и в песочнице на инстансе `classic`).
- Ошибка `board_create` в instance-ветке никогда не намекает, что kind существует в другом мире
  (слое) — подталкивает объявить дубль вместо `$utility`-членства; сообщение при этом
  субстантивно верно (kind реально невалиден там), лжёт только умолчанием.
- `KnownKinds()` без Distinct — косметический дубль в том же сообщении, что уже неполно;
  создаёт иллюзию полноты списка.
- UI-бейдж «not active» на двух страницах называет `$utility` «open methodology instance» —
  прямое противоречие словарю (слой ≠ инстанс, у него нет open/closed). Это ошибка ЛЕЙБЛА
  поверх уже честного per-board резолвинга — per-board резолвинг её не чинит, чинится только
  явным исключением сентинела `$utility` из сравнения.
- `ProjectMethodology.cshtml.cs` — админ, которому нужно увидеть kind'ы проекта, никаким путём
  через эту страницу не увидит утилитарный слой.

## Противоречия

Не обнаружено. Новые детали (guide молчит про битый указатель; UI путает `$utility` с
инстансом; методология_list дублирует декларацию без использования) дополняют, а не
противоречат `01-legend.md`/`02-snapshot.md`.

## Что умрёт под целью C

- `MethodologyInstanceView.Kinds` как per-инстанс поле (`MethodologyInstanceService.cs:591`) —
  под C kind'ы принадлежат проекту, не инстансу; колонка теряет смысл.
- Ветвление `isUtilitySentinel ? Utility : instanceKey!=null ? Instance : legacy`
  (`TasksService.cs:150-158`) и сам сентинел `$utility` — единый каталог убирает выбор мира.
  `KnownKinds()`-дубль (`MethodologyRuntime.cs:287-289`) уходит вместе с этим форком.
  Ошибки `board_create`, различающиеся по ветке членства, схлопываются в одну.
  Бейдж «not active»/`EffectiveActiveInstance`-сравнение (`Tasks.cshtml.cs:513`,
  `TaskBoard.cshtml.cs:513-514`) отмирает — сравнивать больше нечего.
- Ветка guide «N open, none active» (`source:"ambiguous"`) — при плоском каталоге вопрос
  «какой инстанс активен» отделяется от вопроса «какие есть kind'ы» и перестаёт блокировать
  чтение kind'ов.

## Открытые вопросы

- Кто отвечает на «какие kind'ы объявлены в проекте X» и «кто владеет kind'ом X» сегодня —
  никто. Добавляется ли под C новый верб, или это становится телом guide/board_create без
  `key`/`instance` вовсе? От ответа зависит объём цели C.
- `guide` не принимает `board` структурно — почему это не в списке «5 починенных путей»
  02-снимка? Это единственная поверхность из списка задачи, где per-board патч органически
  невозможен без смены сигнатуры — стоит ли заводить это отдельно от per-board починок A.
