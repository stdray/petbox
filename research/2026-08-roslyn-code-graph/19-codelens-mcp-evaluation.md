# roslyn-codelens-mcp — воркер (MIT-конкурент, углублённое изучение) — 2026-08-20

Продолжение `18-landscape-agent-context.md` и `50-verdict.md` (Шаг 0). Прогнан РЕАЛЬНО на
`PetBox.slnx` в собственном worktree (не сравнение по докам). **Ревизия**: по требованию
оркестратора главная находка (`find_references` теряет `tests/**`) прошла попытку опровержения —
опровержение УДАЛОСЬ, это была наша ошибка вызова, детали в разделе ниже.

## Вердикт в одной строке

Работает и закрывает D2/рефлексию как обещано; при ПРАВИЛЬНОМ вызове (`dotnet restore` перед
стартом) числа по `find_references`/`get_di_registrations` сходятся с ожиданием почти точно. Не
«граф с оговоркой» по устройству — это IDE-инструментарий (рефакторинги пишут на диск) поверх
честного графа; графовая часть сама по себе — честная, не дырявая, если решение восстановлено.

## Граф или OmniSharp через MCP — прямой ответ владельцу

Устройство (семантика, `Program.cs`, `Tools/*.cs`): сервер оборачивает Roslyn `SymbolFinder`,
`SemanticModel`, а ещё `Renamer`, `CodeFixService`, `ChangeSignatureService` — ТЕ ЖЕ сервисы, что
OmniSharp/VS/Rider для Find Usages И для Rename/Refactor. Интуиция владельца верна по УСТРОЙСТВУ:
`apply_code_action`, `rename_symbol`, `change_signature`, `get_code_actions` (4 тула) — буквально
OmniSharp внутри MCP, **пишут в файлы на диске** (preview-режим есть, но путь записи реален). По
ЧИСЛУ перевес не в их сторону — см. раскладку ниже.

## Раскладка 67 инструментов (моя классификация, есть пограничные случаи)

| Корзина | Число | Примеры |
|---|---|---|
| нужное нам (D1-D8 / слепые зоны) | 37 | `find_references`, `find_callers`, `find_attribute_usages`, `find_reflection_usage`, `get_di_registrations`, `get_type_hierarchy`, `find_circular_dependencies`, `check_architecture`, `find_unused_symbols`, `analyze_change_impact`, `get_call_graph`, `search_symbols`, инфраструктура (`list_solutions`/`load_solution`/`set_active_solution`/`unload_solution`/`rebuild_solution`, фоновые задачи) |
| безразличное (работает, наш спрос не покрывает) | 26 | линтинг/метрики (`get_complexity_metrics`, `find_naming_violations`, `find_async_violations`, `find_disposable_misuse`, `find_large_classes`, `find_god_objects`, `get_project_health`), диагностика компилятора (`get_diagnostics`, `get_code_fixes`, `trust_solution`+`list_trusted_paths`+`revoke_trust`), поток данных/управления, генераторы, `find_breaking_changes`, `resolve_stack_trace` |
| вредное/лишнее (вторая рука мимо гейта) | 4 | `apply_code_action`, `rename_symbol`, `change_signature` — реально пишут на диск (preview-режим есть, но путь записи реален); `get_code_actions` — read-only, но существует только чтобы кормить `apply_code_action` |

Отключить на стороне сервера НЕЛЬЗЯ (семантика, код): в `CliOptions.cs`/`Program.cs` нет
`--disable-tools`/allowlist/env-var — единственная рантайм-настройка во всём README —
`ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS` (таймаут MSBuild, не про список тулов); grep по
`src/RoslynCodeLens` на allowlist/denylist/ToolFilter — отрицательный результат. Фильтровать нужно
НА СТОРОНЕ ХАРНЕССА (Claude Code умеет allowlist per-server/per-tool в MCP-конфиге) — либо форкать
и добавлять флаг самим (тулы регистрируются типизированными классами через DI, дёшево, но это уже
«допилить», не конфигурация).

## Поведение в worktree

- **Выбор дерева:** явный позиционный CLI-параметр (путь к `.sln`/`.slnx`), либо автопоиск вверх от
  cwd; переключение `load_solution`/`set_active_solution`/`unload_solution` без рестарта процесса.
- **Параллельные worktree:** stdio-транспорт по умолчанию — каждый MCP-клиент поднимает СВОЙ
  процесс, свой `MSBuildWorkspace`. Единственное общее состояние — `%APPDATA%\roslyn-codelens\
  trust.json` (доверие анализаторам, ключ = путь решения), НЕ модель кода (`TrustStore.cs:23`,
  grep по общим путям — отрицательный). Коллизия только при осознанном `--http` (README прямо
  предупреждает: общее состояние на всех клиентов) — при своём процессе на агента риска нет.
- **Внешняя правка файла:** `FileSystemWatcher` (НЕ polling, как у Serena), дебаунс, помечает
  только stale проекты (`FileChangeTracker.cs`). Архитектурно верно; end-to-end не проверял
  (бюджет) — апстрим-issue #282 (закрыт) описывал именно этот путь молчащим сбоем, класс бага был
  реальным.
- **ОБЯЗАТЕЛЬНЫЙ препроцесс — restore:** свежий `git worktree` не содержит `obj/project.assets.json`
  — без `dotnet restore` решение грузится ЧАСТИЧНО («ready», но отдельные проекты с `CS0246`), и
  `find_references`/`get_di_registrations` молча врут. Общее требование MSBuildWorkspace, не баг
  инструмента — разбор в «Попытке опровержения» ниже.
- **Запись в дерево:** снимок `find . -printf '%T@ %s %p'` до/после — **112 новых файлов, 84 481
  байт, 0 вне `obj/`, 0 изменённых** (отдельно от restore). Меньше базлайна MSBuildWorkspace
  (208/1.6 МБ). `git status` после прогона — 0 (obj/ в `.gitignore`).

## Попытка опровержения главной находки (по требованию оркестратора)

Изначальная находка («`find_references` систематически теряет `tests/PetBox.Tests`, DI-фабрики
дают тихий 0 хуже CodeMap») получена в worktree `agent-a39c77a3f5683a733`, который между турами
исчез из `git worktree list` (снесён снаружи) — поднял новый (`codelens-verify`) для повтора.
Шаги по порядку, останавливаясь на первом объяснении:

1. **Сигнатура.** `inputSchema` `find_references`: только `symbol`/`kinds`/`limit` (default 500),
   без `scope`/`includeTests`/`projectFilter`; ответ был `truncated: false`, повтор с `limit:
   10000` — то же. Пагинация исключена.
2. **Арифметика.** Точный подсчёт (Python regex `PetBoxClaims\.\w+`, без строк-комментариев) по
   22 проектам: Core 13, Web 30, `PetBox.Tests` 24 — **итого 67**, ровно ожидание. Пропавшее
   (44 вместо 67) совпало РОВНО с одним проектом; в остальных 5 тестовых — 0 реальных упоминаний.
   Чистая граница по проекту, не рваное распределение.
3. **Другой инструмент.** `analyze_change_impact` («combines find_references and find_callers») на
   том же решении дал ТОТ ЖЕ `directReferenceCount: 44` — дефект не в одном тулле, а на уровне
   компиляции/символов.
4. **Здоровье модели (решающий шаг).** `list_solutions` показывал `status: "ready"`,
   `skippedProjects: []`. Но `get_file_overview` на `PetBox.Tests/Auth/TenantAuthorizerTests.cs`
   вскрыл `CS0246` на `IProjectCatalog`/`TheoryAttribute`/`InlineDataAttribute`. Причина: во всех
   проверенных проектах свежего worktree отсутствовал `obj/project.assets.json` — `dotnet restore`
   не запускался (`obj/` в `.gitignore`). `PetBox.Core`/`PetBox.Web` резолвились частично и без
   restore (source-only типы), `PetBox.Tests` — нет (нужен `xunit.v3` + цепочка до Core).
5. **Свежесть версии.** Клон уже на HEAD апстрима сегодняшним числом (`f8ddca14`, `2.17.0`) —
   новее некуда. `gh issue list --search` нашёл два релевантных ЗАКРЫТЫХ: **#263**
   «SolutionLoader silently accepts degraded MSBuildWorkspace loads» — ровно класс из гипотезы
   оркестратора, закрыт ретраями (`SolutionLoader.GetLoadRetryCount()`, уже видели в коде);
   **#282** «find_references return 0 permanently after the watcher's auto-rebuild» — другой
   механизм (рассинхрон `ProjectId`), тоже закрыт. Класс «тихий неверный `find_references`»
   апстриму известен — наш случай объяснился проще, шагом 4.

**Решающий эксперимент:** `dotnet restore PetBox.slnx` — 22/22 проекта, 0.95 с (тёплый кеш),
`project.assets.json` появился. Тот же вызов в свежем процессе: `find_references PetBoxClaims` →
**totalCount 67** (Core:13, Web:31, Tests:23); `get_file_overview` на том же файле → `"diagnostics":
[]`.

**Вывод: опровержение УДАЛОСЬ.** Находка была нашей ошибкой вызова. Верный способ звать: **всегда
`dotnet restore <solution>` до первого запроса**, особенно в свежем worktree. Остаточное (мягче,
уже не «нечестный ноль», а провенансный пробел): `status: "ready"` не гарантирует, что решение
КОМПИЛИРУЕТСЯ — деградацию видно только явным `get_diagnostics`, а `find_references` молчит.

## Три наших кейса на `PetBox.slnx` (числа — ПОСЛЕ `dotnet restore`, решающий прогон)

`dotnet build -c Release` (сборка самого MCP-сервера): 39.3 с, 230 warnings/0 errors. `PetBox.slnx`
открывается без проблем — 22/22 проекта, `skippedProjects: []` (в `.slnx` тоже 22 `<Project Path>`).

| Кейс | Инструмент/аргумент | Результат | Ожидание | Источник/примечание |
|---|---|---|---|---|
| `PetBoxClaims` | `find_references` | **67** (Core:13, Web:31, Tests:23) | 67 | семантика; сходится точно после restore |
| `ProjectAccess.SandboxContainment` | `find_references` | **7** (Core:2, Web:2, Tests:3), kind `"read"` (kind `case` в таксономии нет) | 7 (3 switch-места) | семантика; сходится точно после restore |
| `TenantFromAttribute` | `find_attribute_usages` | **93** (Web:72, Log.Core:11, Config:3, Data:7) | 56 типов+37 методов=93 | семантика; сумма сходится (worktree без ошибки restore); types/methods в `summary` нет |
| `McpServerToolAttribute` (полное имя) | `find_attribute_usages` | **0** | — | семантика; README «with/without suffix» не подтвердилось для этого атрибута |
| `McpServerTool` (короткое имя) | `find_attribute_usages` | **98** (Web:96, Tests:2) | 96+2=98 | семантика; сходится ТОЛЬКО без суффикса `Attribute` |

**Razor:** `search_symbols("AgentKeysModel")` нашёл класс по адресу
`src/PetBox.Web/Pages/Admin/AgentKeys.cshtml.cs:31` — code-behind виден. Артефакт: второй элемент
с тем же именем и `file:"", line:0` (Razor-сгенерированная частичная часть без пути) — не мешает
основному ответу, но провенанса, откуда взялся, нет; тот же паттерн у `LlmRegistryStore` (DI-раздел).

## Провенанс

На уровне решения — есть (`skippedProjects`, `LoadDiagnostics`), но НЕ покрывает «проект загружен,
но с нерезолвленными ссылками» — видно только через `get_diagnostics`, см. выше. На уровне
ОТДЕЛЬНОГО запроса — нет: конверт (`items`/`totalCount`/`truncated`/`summary`) не говорит, какие
проекты просмотрены.

## Рефлексия/рантайм DI — наша слепая зона, подтверждена (без ошибки — то же, что у CodeMap)

`get_di_registrations`, проверено дважды (до/после restore): `IProjectCatalog` (прямая
`AddScoped<I,C>`) и `ICoreDbFactory` (фабрика-лямбда с `new CoreDbFactory(...)`) резолвятся верно
в обоих прогонах. `ILlmRegistryAdmin` (`sp => sp.GetRequiredService<LlmRegistryStore>()`, косвенная
фабрика, `LlmRouterServiceCollectionExtensions.cs:43`) — ДО restore: `totalCount 0`. **ПОСЛЕ:
`totalCount 1`, `implementation:"(factory)"`, `line:43`** — сентинел ЕСТЬ, точно как у CodeMap.
**Отзываю «хуже CodeMap, тихий ноль» — тоже ошибка нереставрированного решения**; то же на
`ILlmRegistryResolver` (`line:44`). Слепая зона (тип за косвенной фабрикой неизвестен) подтверждена
— метаданные у обоих конкурентов одинаковы, не хуже.

## Что НЕ проверено и почему

- End-to-end «правка файла → следующий запрос видит её» — гарантирует код (`FileSystemWatcher`),
  вживую не гонял (бюджет); #282 показывает, что для этого пути был реальный баг (уже закрыт).
- Разбивка types/methods у `find_attribute_usages` (56/37) — тул не отдаёт, вручную не считал.
- 26 «безразличных» тулов — не гонял, классификация по описанию, не по прогону.
- Параллельный запуск двух процессов НА ОДНОМ дереве (не разных worktree) — вывод по коду.
- `apply_code_action`/`rename_symbol`/`change_signature` не вызывались (рамки задачи запрещают).

## Замеры (числа, команда)

| Операция | Число | Как получено |
|---|---|---|
| `dotnet build -c Release` (сам MCP-сервер) | 39.3 с | `time dotnet build ... -c Release` |
| `dotnet restore PetBox.slnx` (22 проекта, кеш тёплый) | 0.95 с | `time dotnet restore` в свежем worktree |
| Холодный старт `initialize` / первый tool-call (блокирует до готовности) | 6.2–15.7 с / 10.4–29.5 с (7 прогонов) | JSON-RPC, питон-клиент, `time.time()` |
| Тёплый повторный `list_solutions` | 0.001 с | тот же клиент, второй вызов подряд |
| Память процесса (WorkingSet64) | 343→484→689 МБ, рост по ходу вызовов | PowerShell `Get-Process -Id <pid>`, 3 момента |
| Запись в дерево (без restore) | 112 файлов / 84 481 байт, всё в `obj/` | `find . -printf '%T@ %s %p'` до/после |
| Проектов загружено | 22/22, 0 skipped (не значит «0 деградировавших») | `list_solutions` + подсчёт в `.slnx` |

## Оценка трудозатрат / Артефакты

Трудозатраты не применимы — чужой продукт; интеграция (allowlist, restore-препроцесс, провенанс
снаружи) не оценивалась. Артефакты: клон, скрипты, логи, `tools_list.json` (схемы всех 67) —
`C:\Users\stdray\AppData\Local\Temp\claude\...\scratchpad\` (вне обоих чекаутов petbox). Worktree
для перепроверки — `D:\my\prj\petbox\.claude\worktrees\codelens-verify` (создан заново взамен
исчезнувшего; продуктовый код не тронут, только `obj/` от restore).
