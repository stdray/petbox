# 17 — canon как STORE: инвентаризация и проверка предложения владельца. 2026-09-10
Автор: petbox-worker · источники: память (memory_search/memory_get/memory_delta/memory_store_list), код

## Что здесь верно сейчас
- Обе области канона содержат РОВНО ОДИН ключ — `index`. `totalEntries:1` в обоих
  `memory_store_list(store excluded, scope:project/workspace, includeUsage:true)` [замер 2026-09-10].
  Небазовых (не-`index`) ключей — **0** в project, **0** в workspace. Предположение задачи
  ("establish how many such keys exist") имеет пустой ответ: неиндексных ключей нет вообще.
- Store `canon` создан: project `2026-07-03T19:24:43.036`, workspace `2026-07-03T19:50:19.199`
  [память: memory_store_list.createdAt].
- Per-entry created/updated даты НЕ экспонируются ни одним read-инструментом (`memory_search`,
  `memory_get`, `memory_delta` — только `version`, `lastHitAt`=последнее ЧТЕНИЕ, не запись)
  [НЕПРОВЕРЕНО за пределами MCP-поверхности]. Прокси на частоту правок — номер версии:
  project `index` version 25, workspace version 13 за ~69 дней с создания стора → нет
  признаков "protokol staleness"; оба живые.
- Использование `index`: project — surfaced 971 / opened 64, workspace — surfaced 1046 / opened 29
  [память: memory_search includeUsage:true]. По `memory_store_list`: project canon
  deliberateDeliveries 36 vs machineDeliveries 757; workspace 50 vs 748 — то есть `index`
  почти целиком доставляется АВТОИНЪЕКЦИЕЙ сессии, а не деliberate-поиском агента.
- `[код]` MemoryApi.cs:44-50,85,106,140-151 — инъекция читает ТОЛЬКО `store:"canon"`,
  `key:"index"` в каждом контейнере (project и caller's workspace). Других ключей эндпоинт
  не видит и видеть не может.
- `[код]` MemoryService.cs:794-804 `EnforceCanonBudget` — гейт применяется к КАЖДОМУ upsert
  в store `canon` вне зависимости от ключа (`if (!string.Equals(store, CanonStore...)) return;`
  дальше цикл по всем upserts). Раз неиндексных ключей нет — гейт сегодня применяется
  фактически только к `index`; риск "платного, но неинжектируемого ключа" сейчас НЕ
  реализован в данных, только в коде-возможности.
- Project `canon/index` body [память, full read] САМ утверждает: "Role/agent prose lives
  ONLY in git (`src/common/default-agents.json`)... layered over by files: base <
  `~/.petbox/agents` < `<root>/.petbox/agents`. The server holds none of it; AGENTS.md and
  this canon only POINT at the git basis." — канон уже документирует и соблюдает разделение,
  которое предлагается стереть слиянием.
- Workspace `canon/index` body [память, full read]: "Owner / Single user (stdray)... Working
  style... Traps: Memory is NOT a secret store". Полностью про ВЛАДЕЛЬЦА и кросс-проектные
  правила, ни одной репозиторной привязки.

## Где механизм на самом деле живёт
- `src/PetBox.Web/Memory/MemoryApi.cs:44-56` — GET `/api/memory/{projectKey}/canon`,
  единственный consumer инъекции, хардкод store/key.
- `src/PetBox.Memory/Services/MemoryService.cs:794-804` — write-гейт по ВСЕМУ store.
- `src/PetBox.Web/Memory/ProjectCanonSeeder.cs:30-31,75-76` — сидирует `canon/index` при
  создании проекта (skeleton), других ключей не создаёт.
- `src/clients-ts/petbox-wire/src/layer-cascade.ts:4,428-485` — каскад `base <
  ~/.petbox/agents < <root>/.petbox/agents`, файловый, per-machine, без сетевого слоя.
- `src/PetBox.Web/Search/SessionFactsJob.cs:518-534` — автосбор фактов пишет в свои же
  project-стораны (`autocaptured` и др.), но НЕ в `canon`; комментарий явно фиксирует, что
  judge раньше был "structurally blind to canon" и теперь просто ВИДИТ его как соседа для
  дедупа наблюдений — не как источник для промоушена туда.

## Сравнение canon/index vs default-agents.json (п.3)
- Разный жанр: canon/index — компактная КАРТА фактов проекта (что это, порядок idea→spec→
  work, где что искать, деплой, кириллица-в-JSON) с явными указателями "детали — там-то".
  default-agents.json `.roles[].notes` — построчный ПОВЕДЕНЧЕСКИЙ КОНТРАКТ роли (нумерованные
  императивы: self-intro, leaf-запрет, foreground-верификация и т.д.), на роль ~2-3К символов.
- Прямое пересечение по содержанию ЕСТЬ: canon/index пункт "Observations are board nodes,
  not memory facts... Promote explicitly via `tasks_observation_promote`" почти дословно
  повторяет правило 9 worker/worker-highstakes notes в default-agents.json ("On a finding,
  route — don't file — except a defect... `tasks_observation_promote`"). Это ОДНО и то же
  правило, продублированное в двух хранилищах разными словами.
- Вывод: это НЕ один и тот же тип артефакта (карта фактов проекта vs поведенческий контракт
  роли), но они не изолированы — как минимум одно правило (observations routing) живёт
  в обоих одновременно, канон при этом явно ссылается на git-файл как на источник правды
  для ролей и не дублирует остальные 9-12 пунктов каждой роли.

## Промоушен-механизм (п.4)
- Аналога `tasks_observation_promote` для памяти НЕТ. `memory_remember`/`memory_upsert`
  — общие write-глаголы, без спец-пути "candidate → curated". `autocaptured` (project,
  831 записей [store_list]) — фактический пул кандидатов (сессионные факты, dedup через
  SessionFactsJob.cs), но переход в `canon` — обычный ручной `memory_upsert` тем же
  инструментом, что и любая другая запись; кода, который бы двигал запись из
  `autocaptured` в `canon`, не найдено (grep по `promote`/`candidate.*curat` в
  MemoryTools.cs/SessionFactsJob.cs — 0 совпадений).

## Что НЕ проверено
- Существует ли где-то вне репозитория (документация, CI) синхронизация `~/.petbox` между
  машинами — по коду `petbox-wire` её не нашёл (только резолюция путей), но не проверял
  внешние скрипты/дотфайлы владельца [НЕПРОВЕРЕНО].
- Точные даты последней ЗАПИСИ (не чтения) `index` в обоих скоупах — MCP-поверхность их не
  отдаёт; нужен прямой доступ к БД/аудиту, которого у read-only агента нет [НЕПРОВЕРЕНО].

## Что отсюда следует
Хранилище `canon` сегодня — не "полка с множеством записей, из которых читается одна", а
буквально ОДНА запись на скоуп; вопрос о "мёртвых неинжектируемых ключах" снят фактами: их
нет. Слияние с ролями противоречит собственному тексту канона, который явно делегирует роли
git-файлу; overlap в одном правиле — повод синхронизировать формулировки, не повод сливать
хранилища. Самый сильный контраргумент предложению: `workspace/canon` не про репозиторий и не
про машину — он про ВЛАДЕЛЬЦА кросс-проектно (буквально текст записи), а кассейд-механизм кита
целиком file+repo+machine-scoped; перенос workspace-канона в `~/.petbox` меняет его с
кросс-машинного (сервер, один HTTP-эндпоинт с любой машины) на привязанный к одной машине без
какого-либо обнаруженного sync-слоя — это конкретная потеря, а не абстрактный риск.
