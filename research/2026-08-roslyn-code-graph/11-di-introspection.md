# DI-интроспекция — воркер B — 2026-08-20

## Вердикт в одной строке
Оба варианта развилки работают и не потребовали правок продуктового кода: `Program.ConfigureServices`
и `Program.Configure` уже публичны (тот же сеам, которым пользуется build-time OpenAPI-генератор и
вся тестовая инфраструктура). Вопрос про порядок MCP-фильтров закрыт эмпирически, не по комментарию.

## Что проверено эмпирически
| Что | Команда/код | Результат | Источник (легенда) |
|---|---|---|---|
| No-Build дамп `IServiceCollection` | `dotnet run` в `proto/di/no-build-harness` — зовёт `Program.ConfigureServices(builder)` на свежем `WebApplicationBuilder`, `Build()`/`Run()` НЕ вызываются | 629 дескрипторов | рантайм-дамп (состояние коллекции до `Build()`) |
| Полный хост (TestServer, без Kestrel) | `dotnet run` в `proto/di/full-host-harness` — `ConfigureServices`+`Configure`+`StartAsync`, temp SQLite, `Host:BackgroundServices=false` | 96 `McpServerTool` реально зарегистрировано в живом контейнере — совпадает с No-Build числом день в день | рантайм-дамп |
| Порядок MCP-фильтров (`McpTenantEnforcementFilter` vs `McpProjectExistsFilter`) | 3 живых `tools/call` через `/mcp` тем же полным хостом: (1) свой+существующий проект, опечатка в параметре; (2) чужой, но существующий проект; (3) несуществующий проект | (1) ошибка `ArgumentException` от `McpUnknownParameterFilter` (внутренний фильтр отработал — тенант/existence прошли); (2) и (3) — **идентичный** `UnauthorizedAccessException "Not authorized for project:…"`, для НЕсуществующего проекта тоже — "does not exist" нигде не течёт | рантайм-дамп, сырой лог `proto/di/mcp-order-probe-log.txt` |
| Хук в композиционный корень | не потребовался | оба входа уже `public static` в `Program.cs`; правка продукта = 0 строк | — |

## Замеры
| Операция | Число | Как получено |
|---|---|---|
| Дескрипторов всего (No-Build) | 629 | `proto/di/registrations-no-build.json` |
| …из них `ImplementationFactory` (тип реализации НЕизвестен статически — 100% таких записей имеют `ImplementationType=null`) | 161 / 629 (26%) | тот же дамп, `Kind=Factory` |
| …из них — обёртки MCP-инструментов (`AIFunctionFactory`, каждая — `Factory`) | 96 | `ServiceType` = `ModelContextProtocol.Server.McpServerTool` |
| …factory уровня приложения (`PetBox.*`, без MCP tools) | 17 / 99 записей с `ServiceType` из `PetBox.*` | тот же дамп |
| …`ImplementationType` (пара Service→Impl видна прямо в строке регистрации `AddScoped<TI,TC>()`) | 383 / 629 (61%) | тот же дамп |
| …`ImplementationInstance` | 82 / 629 | тот же дамп |
| …keyed-сервисов | 3 / 629 | тот же дамп |
| No-Build прогон (steady-state, без пересборки) | 0.59 с | `time dotnet run -c Release --no-build` |
| Полный хост: build+migrate+seed+3×HTTP+shutdown (steady-state) | 1.98 с | `time dotnet run -c Release --no-build` |
| Для сравнения: NDepend, весь солюшн | ~9 с анализ / 1–6 мс запрос | `02-snapshot.md` |

## Что НЕ проверено и почему
- **`if (env.IsDevelopment())`/`Features:*`-ветки** — оба харнесса используют один конфиг-профиль
  (`Environment=Testing`, `Features:Tasks=true`, остальные фичи не включались). Дамп = снимок ОДНОЙ
  конфигурации; полное покрытие потребует прогонов по всем ~8 булевым `Features:*` флагам и,
  отдельно, `Environment=Development` (в `Program.cs` есть код, реагирующий на dev-режим — не
  идентифицирован построчно, не хватило бюджета).
- **`TryAdd*` vs `Add*`** — после сборки `IServiceCollection` неотличимы: если `TryAddSingleton<T>`
  был пропущен (T уже зарегистрирован), в дампе остаётся ОДНА запись, как будто пропуска не было.
  `AddGatedHostedService` (`src/PetBox.Web/HostedServiceGate.cs:51`) именно так и работает.
- **Декораторы** — не искал специально; в 629 дескрипторах паттерн не бросился в глаза, но
  целенаправленной проверки (напр. двух регистраций одного интерфейса подряд) не делал.
- **Открытые generic'и** (`typeof(IFoo<>)`) — не встретились в `Program.cs` буквально; не проверял
  остальные `Add*Module()`-методы (`AddLlmRouter()` и т.п.) на этот счёт — они регистрируют свои
  сервисы ВНУТРИ себя, и мой дамп их учитывает по факту (629 включает их), но я не разбирал их
  исходники построчно.
- **`ImplementationFactory` → реальный тип без выполнения** — не пробовал: IL-инспекция тела лямбды
  (`sp => new CoreDbFactory(...)`) на предмет `newobj`-опкода технически возможна, но не делал —
  дешевле оказалось один раз выполнить (полный хост) и прочитать факт.
- **Полный хост vs `dotnet run` продакшн-профиля** — не поднимал ничего похожего на прод (реальный
  порт/Kestrel/реальный `./data`); `TestServer` — in-memory, отдельный temp-каталог на прогон,
  удостоверился по логам (`petbox-di-dump-host-*`).

## Противоречия с известным
Постановка цитирует «вопрос "кто из 96 инструментов не вызывает guard" вернул бессмысленные 96» —
число **96** совпало один-в-один в обоих независимых замерах (No-Build дескрипторов и живой
`GetServices<McpServerTool>()`), так что «96» — не случайная синтаксическая находка, а стабильный
факт композиции. Расхождений с `02-snapshot.md` не нашёл.

## Оценка трудозатрат по своей области
Диапазон 3–5 человеко-дней до пригодного источника рёбер (не «идеального», а «дающего честные ServiceType→Impl рёбра там, где это вообще возможно без выполнения»):
- **1 день** — No-Build харнесс: сейчас прототип; довести до устойчивого CLI/CI-шага (обработка
  профилей `Features:*`, стабильный JSON-схема, TryAdd-разметка через сравнение "до/после" по
  каждой строке `Program.cs` синтаксически — где TryAdd, там дамп не гарантирует истину).
  Правок продукта — 0.
- **1–1.5 дня** — Full-host харнесс: сейчас прототип (ручная сборка `WebApplicationBuilder` в обход
  `WebApplicationFactory`, т.к. харнесс живёт вне солюшна). Нужно: перебор `Features:*`, аккуратный
  teardown temp-директорий (сейчас накапливаются в `%TEMP%`), кросс-чек имя-инструмента ↔
  `TenantDeclarationAttribute` (уже есть готовый паттерн — `AuthzDeclarationRatchetTests.cs:274`,
  просто не задействован в харнессе). Правок продукта — 0 (оба входа уже публичны).
- **0.5 дня** — Порядок MCP-фильтров как МАШИНОЧИТАЕМОЕ ребро: сейчас порядок = комментарий в
  `Program.cs:472-494` + один эмпирический прогон. Дешёвый путь — синтаксический (Roslyn) обход
  тела лямбды `WithRequestFilters(filters => { ... })` и фиксация порядка вызовов `*.Register()`
  как последовательности рёбер `filter[i] wraps filter[i+1]`; рантайм нужен только для ПРОВЕРКИ
  инварианта (как я сделал вручную), не для извлечения самого порядка.
- **1 день** — свести три источника (No-Build дескрипторы + Full-host имена/decl + синтаксис
  фильтров) в единый граф рёбер `ServiceType → Impl | Factory(opaque) | Instance` с явным флагом
  "требует рантайма, чтобы узнать реализацию" на 161 из 629 узлов — это и есть честный ответ на
  «кто на самом деле обработает вызов», а не тихая деградация до `IFoo`.

## Артефакты
- `proto/di/no-build-harness/` (`Harness.cs`, `DiDump.csproj`) + `proto/di/registrations-no-build.json`
- `proto/di/full-host-harness/` (`Harness.cs`, `DiDumpHost.csproj`) + `proto/di/mcp-tools-registered.json`
- `proto/di/mcp-order-probe-log.txt` — сырые ответы 3 живых `tools/call`, доказывающие порядок фильтров
