# 02 — Снимок состояния

**Дата: 2026-08-26.** Истина, против которой сверялось: `origin/main` = `4d956ad5`;
живой сервер `https://petbox.3po.su/version` → `{"semVer":"0.1.0-ci.1936","shortSha":"4d956ad"}`.
Совпадают — дрейфа между репозиторием и стендом нет.

Термины — по `01-legend.md`. Голый `classic` здесь не употребляется (см. легенду, раздел B).

---

## Что выложено сегодня

Коммит `deca00bb`, смержен и выложен как `4d956ad5`. Карточка
`$system/client-issues/custom-kind-route-undiscoverable` — `Done`.

| правка | проверено на живом |
|---|---|
| `tasks_board_create` описание называет вербы объявления kind'а | `tool_describe` → содержит `declare one with tasks_methodology_utility_upsert...` |
| ошибка `utility_get` называет действие | вызов на проекте без слоя → `...; create one with tasks_methodology_utility_upsert (version: 0)` |
| гайд: секция `## Declaring your own kind`, безусловная | `tasks_methodology_guide` → секция присутствует |
| резолвинг kind'а: 5 путей переведены на per-board | `tasks_search(board)` в петсондовской форме → `"kind":"wiki"` вместо `"simple"` |

Пять починенных путей: поле `kind` в листинге `tasks_search`; тиринг `statusKind` в query-режиме;
`TaskSearchFilter.ResolveStatusAcross`; классификация terminal-cancel в query-режиме;
`ValidateWiredBoardAsync` (за `board_create.wiredBoard` и `board_set_wire`).

Гейты, прогнанные оркестратором лично: `./build.ps1 -Target Test` → exit 0, 4199+96+143+19+6+1,
ноль падений; `dotnet run scripts/inspect-gate.cs` → `clean — 0 findings`. Охранное свойство
тестов проверено откатом исходников при сохранённых тестах: два новых теста падают, первый —
`"simple"` вместо `"wiki"`.

---

## Что известно открытым

| дефект | карточка | статус |
|---|---|---|
| `adopt` без валидации живых узлов и без `migration` | `work/adopt-skips-live-node-validation` | Pending |
| нет инварианта «открытая доска ⇒ открытый `rule source`» | `work/open-board-in-closed-world-zombie` | Pending |
| `active_get` отдаёт закрытый инстанс без `closed` | `work/active-get-dangling-closed-pointer` | Pending |
| ошибка `board_create` порождает дубли | `work/board-create-valid-kinds-breeds-duplicates` | Pending |
| гайд врёт про доску чужого `rule source` | `work/guide-lies-about-foreign-world-board` | Pending |
| meta-backfill классифицирует проектным runtime | `work/meta-backfill-classifies-with-project-runtime` | Pending |
| снять дубль `wiki` из правил инстанса `quartet` | `work/drop-wiki-kind-duplicate-from-quartet` | Pending, **blockedBy** backfill |

Аналитика: `work/kind-worlds-model-audit` — `Review` (два комментария).
Пути: `ideas/kind-worlds-decision-paths` — `review`, артефакт `spec_plan` положен.
Оба ждут владельца; `Review → Done` и `review → accepted` — OWNER-ONLY.

---

## Живые данные `$system`

Источник: `tasks_board_list`, `tasks_methodology_list`, `tasks_methodology_rules_get(quartet)`,
`tasks_methodology_utility_get` — все на 2026-08-26.

8 досок: `classic`(kind classic, `$utility`), `client-issues`(simple, `$utility`),
`ideas`/`intake`/`spec`/`work`(quartet), `roadmap`(simple, `$utility`, **закрыта**),
`wiki`(kind wiki, `$utility`).

`Rule source`: инстанс `quartet` (открыт, v22) + три закрытых smoke-инстанса без досок + утилитарный
слой (v2). Досок с пустым членством **нет** — легаси-null в `$system` пуст.

**Дубль на месте:** kind `wiki` объявлен и в утилитарном слое (v2), и в правилах инстанса
`quartet` (v22). Проверено повторно после выкладки. Доска `wiki` состоит в `$utility`,
значит резолвится из слоя; копия в quartet поведения не меняет, но маскирует два открытых
дефекта.

**Коллизий слагов в `$system` шесть, а не одна** (уточнено 2026-08-26 инвентарём
`03-inventory/e-live-data.md`; ранее здесь и в `04-summary.md` стояла «одна»). Живую доску
задевает только `wiki`. Остальные пять дремлют в ЗАКРЫТЫХ smoke-инстансах без досок:
`intake`/`ideas`/`spec`/`work` против `smoke-verdict-gate`, kind `classic` против
`smoke-classic-818` и `smoke-clone-818`.

> Это меняет формулировку вехи A. Правило «слаг уникален в проекте поверх всех домов
> объявления» будет нарушено существующими данными в момент включения. Требуется решение:
> распространяется ли уникальность на закрытые инстансы (тогда их надо переименовать или
> вычистить) или закрытый инстанс — архив и из каталога исключён. См. `05-decisions.md`.

**Невидимость:** `tasks_methodology_list($system)` не содержит строки `$utility`, и доска
`wiki` не входит в список досок инстанса `quartet`. Доска отсутствует в перечне `rule source` вовсе.

---

## Живые данные прочих проектов

`petsonde`: известно из лога владельца — инстанс `classic`, доска `wiki` (kind `wiki`,
`$utility`), kind объявлен только в слое; две страницы. **Оркестратору недоступен** — ключ
сессии привязан к `$system`.

Остальные 8 проектов workspace `stdray` (`agent-relay`, `animemov`, `infra`, `kek-devices`,
`kpvotes`, `pochtar`, `yoba-summarizer`, `yobapub`): **не смотрел никто**. Коллизии слагов,
доски с пустым членством, дубли — неизвестны.

---

## Песочница

`kind-resolution-lab` (workspace `stdray`, флаг `sandbox`), ключ `sandboxOnly`, TTL до 2026-09-02.
Оставлена в конечном состоянии экспериментов — это **живая репродукция**, не мусор:

- инстанс `classic` — закрыт; инстанс `alt` — закрыт; инстанс `probe2` — открыт;
- доска `wiki` в `$utility`, kind `wiki` только в слое;
- доска `wiki2` — **уже НЕ зомби**: `adopt` перевёл её членство в `$utility` (всегда открыт).
  Зомби-состоянием она была в середине цепочки, не в конце;
- узел `n1` (тип `note`, статус `final`) — **застрял навсегда**, эталон необратимости;
  проверено эмпирически после переезда доски: типа `note` в текущем kind'е `wiki` нет;
- на `probe2` воспроизводится дефект сообщения `board_create`.

> **Песочница живая и её меняют параллельно работающие агенты.** Любое утверждение
> «в песочнице сейчас X» протухает за минуты. Исправлено 2026-08-26 по факту:
> снимок утверждал зомби-состояние `wiki2` и не знал про 4-й инстанс `alt2` (открыт,
> kind `simple`, доска `alt2`). Проверять перед употреблением, а не цитировать отсюда.

---

## Чего в снимке НЕТ

- Состояния UI-поверхностей — руками не гонялись.
- Реального срабатывания meta-backfill — триггер version-gated, песочным ключом не достаётся;
  вывод сделан из кода.
- Данных о коллизиях за пределами `$system`.
