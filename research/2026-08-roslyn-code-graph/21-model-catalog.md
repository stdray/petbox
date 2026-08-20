# Каталог типов модели — воркер J — 2026-08-20

## Вердикт в одной строке
Ключ соединения — `ISymbol.GetDocumentationCommentId()`, и он сходится с IL/рантайм **только для
обычных членов**; для сконструированных generic-методов, лямбд, анонимных типов и локальных функций
он либо расходится с определением, либо коллизирует (см. §3). `edge-kinds.json` воркера I **ещё не
существует** (`measurements/callgraph/` нет в дереве на момент написания) — раздел «вызовы» помечен
явно, не выдуман.

## 1. Типы узлов
Замер: `KeyProbe` (мой прототип, см. «Артефакты»), обход `GetMembers()` по `GlobalNamespace` всех 22
компиляций — та же схема, что у воркера A (тип + его члены), без вложенных типов дважды.
**Итого 47 793** объявленных символа (у воркера A наивный счётчик — 48 173, дельта — двойной счёт
вложенных типов как «член» и как «тип», не расхождение источника).

| Узел | Число | Источник | Во всех 3? |
|---|---:|---|---|
| Сборка (проект) | 22 | семантика (`.slnx`) + IL (`bin/**`, по одной на сборку) | семантика, IL; рантайм — только косвенно (`Assembly.FullName` загруженных сборок хоста) |
| Класс | 2249 | семантика/IL/рантайм (`Type`) | да, если тип реально скомпилировался и загрузился |
| Интерфейс | 69 | семантика/IL/рантайм | да |
| Struct | 47 | семантика/IL/рантайм | да |
| Enum | 73 | семантика/IL/рантайм | да |
| Delegate | 3 | семантика/IL/рантайм | да |
| Метод (Ordinary) | 14 379 | семантика/IL/рантайм (`MethodInfo`) | да |
| Конструктор / статический конструктор | 2877 / 431 | семантика/IL/рантайм | да |
| Property (+ get/set методы) | 6066 (+6066/4443) | семантика/IL/рантайм | да |
| Поле | 9569 | семантика/IL/рантайм | да, но `const`-поле не оставляет ребра в IL (см. `30-decisions.md`, кейс 1) |
| Событие | 0 (честный ноль, сверено grep'ом) | — | в кодовой базе не встретилось ни разу |
| Оператор (`UserDefinedOperator`) | 1414 | семантика/IL/рантайм | да |
| Explicit interface impl | 104 | семантика/IL/рантайм | да |
| Локальная функция | не тарифицирована census'ом (не член типа); найдена точечно, см. §3 | **только семантика** | IL видит как обычный `MethodDefinition` с именем-мусором (`<Method>g__Local\|0_0`), рантайм — так же по `MethodInfo`, но имя не то, что в исходнике |
| Лямбда | не тарифицирована; точечно, см. §3 | **только семантика** (как «использование»); в IL/рантайм — синтетический метод/делегат-класс | id в IL/рантайм не совпадает по имени с семантикой вообще |
| Анонимный тип | не тарифицирован; точечно, см. §3 | **только семантика** по имени; IL/рантайм видят синтетический `<>f__AnonymousType0\`3` | имя стабильно **внутри одной сборки одной компиляции**, не переносится как понятие «анонимный тип X» |
| Параметр | не узел в текущей наивной схеме (атрибут ребра/сигнатуры) | семантика | IL хранит как `Parameter` метаданных; рантайм — `ParameterInfo` |
| Razor-страница (класс) | 115 (в `PetBox.Web`, из 207 `.cshtml`-документов / 118 сген. деревьев) | семантика (сген. `SyntaxTree`) + IL/рантайм (обычный класс после компиляции) | да, после компиляции — обычный класс, см. §3.6 |
| DI-регистрация | 629 (`ServiceType`→импл./фабрика/инстанс/keyed) | **только рантайм-дамп** (`IServiceCollection` до `Build()`) | синтаксис видит `Add*<T,U>()`-вызов, но не факт финальной регистрации (`TryAdd*` перекрывается — Р-B из `11-di-introspection.md`) |
| MCP-эндпоинт (`McpServerTool`) | 96 продукт. + 2 тестовых зонда (Р2) | семантика (носитель атрибута) **и** рантайм (`GetServices<McpServerTool>()`, число совпало день-в-день) | синтаксис — по строке `[McpServerTool(Name="...")]`; строки — по `mcp-tools-registered.json` (имена инструментов) |
| Фильтр MCP-пайплайна | 5, порядок из `Program.cs:472-486` (`McpErrorEnvelopeFilter → McpToolScopeFilter → McpTracingFilter → McpProjectDefaultFilter → McpTenantEnforcementFilter → McpProjectExistsFilter`) | синтаксис (порядок вызовов `*.Register()`) + рантайм (3 живых `tools/call`, `11-di-introspection.md`) | IL/семантика видят вызовы, но не «кто кого оборачивает» — это порядок в лямбде, не тип отношения |

## 2. Типы рёбер

| Ребро | Число | Источник | Пример |
|---|---:|---|---|
| Использование символа (идентификатор/член/дженерик) | 503 019 (наивная перепись, воркер A) | семантика | `System.Security.Claims.ClaimsPrincipal` — 505 строк в SQLite-выгрузке |
| Ссылка `FindReferencesAsync` (уточнённая, отличает `case`-метку) | напр. 67 на `PetBoxClaims` (§6), 127 на `ClaimsPrincipal` | семантика | `measurements/sample-answers.json` |
| Носитель атрибута (тип/метод несёт `[Attr]`) | 98 (`McpServerToolAttribute`, все Method) + 95 (`TenantFromAttribute`: 56 Type + 39 Method) | семантика, сверено строками 1:1 | `measurements/attribute-sites.txt`: `McpServerToolAttribute Method .../AgentDefTools.cs:[1525..1690) PetBox.Web.Mcp.AgentDefTools.ListAsync(...)` |
| Ребро из Razor-тела в символ `PetBox.*` | 3248 | семантика (обход сген. деревьев) | обход `GetSymbolInfo` по `.cshtml`→C# |
| Member-ref в IL (межсборочный) | 3 / 2 / 1 (контрольные на `ProjectScope.EvaluateAsync`/`ApiKeyScopes.Granted`/`TenantAuthorizer.AuthorizeAsync`); **0** на `const`-поля и члены enum при том же паттерне использования | IL (`System.Reflection.Metadata`, `proto/ILProbe`) | `measurements/il-control.txt` |
| Строковое совпадение (grep-ребро, не доказывает связь) | 69 сырых вхождений `PetBoxClaims\.` против 67 настоящих (2 — комментарий/строковый литерал) | строки | `tests/PetBox.Tests/.../ScopeClaimTransportEquivalenceTests.cs:128,151` |
| DI-регистрация `ServiceType → Impl\|Factory\|Instance\|Keyed` | 383 Type / 161 Factory / 82 Instance / 3 Keyed из 629 | рантайм-дамп | `{"ServiceType":"...IHostApplicationLifetime","Kind":"Type","ImplementationType":"...ApplicationLifetime"}` |
| DI keyed-ребро | 3 | рантайм-дамп | `{"ServiceType":"...OpenApiDocumentService","Kind":"Keyed","ServiceKey":"v1"}` |
| Порядок фильтров пайплайна (`filter[i] wraps filter[i+1]`) | 5 рёбер (6 узлов) | синтаксис (порядок вызовов), рантайм — проверка инварианта | `Program.cs:472-486` |
| Вызов метода (call-граф, разбивка по видам: прямой/виртуальный/через интерфейс/делегат/рефлексия) | **заполняется воркером I** | семантика/IL (по видам) | `measurements/callgraph/edge-kinds.json` — файла ещё нет на момент написания; не ждал, раздел оставлен пустым намеренно |

## 3. Ключ соединения — эмпирика (`KeyProbe`, `dotnet restore` выполнен перед прогоном)
`GetDocumentationCommentId()` сравнивался с (а) символом того же члена, полученным через
`CSharpCompilation` из **PE**-ссылки на `bin/Debug/net10.0/PetBox.Core.dll` (это и есть источник
«IL» с точки зрения имени, не байткода) и (б) вручную собранной строкой из `System.Reflection`
(`Type`/`FieldInfo`) — прокси источника «рантайм».

| Вид символа | Семантика (source) | PE (IL) | Reflection (рантайм) | Сходится? |
|---|---|---|---|---|
| Обычный тип | `T:PetBox.Core.Auth.PetBoxClaims` | тот же | `T:`+`Type.FullName` — тот же | **да, точно** |
| `const`-поле | `F:PetBox.Core.Auth.PetBoxClaims.UserId` | тот же (поле в метаданных ЕСТЬ) | тот же | да по имени; НО ребро-использование в IL всё равно 0 (см. §2) — ключ сходится, **рёбер к нему через IL нет** |
| Сконструированный generic-метод (вызов `db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)`) | `M:...FirstOrDefaultAsync``1(...{PetBox.Core.Models.User,System.Boolean},...)` — **подставлен конкретный тип аргумента** | id определения (метода-шаблона) — `M:...FirstOrDefaultAsync``1(...{``0,System.Boolean},...)` | — | **НЕ сходится**: id конструированного вызова ≠ id определения в PE. Нужна нормализация через `.OriginalDefinition.GetDocumentationCommentId()` ДО сравнения с IL/рантайм |
| Локальная функция (`TemporalStore.BuildBatch`, объявлена внутри generic-метода `UpsertAsync<T>`) | `M:PetBox.Core.Data.Temporal.TemporalStore.BuildBatch(...)` — id **не содержит** объемлющий метод `UpsertAsync` | синтетическое имя `<UpsertAsync>g__BuildBatch\|17_0` в метаданных — не совпадает буквально | то же имя, что в IL | **коллизия по конструкции**: два одноимённых локальных метода в разных перегрузках/instantiations того же класса получат ОДИН И ТОТ ЖЕ семантический doc-id |
| Лямбда (в `AccountSelfService`) | `M:PetBox.Core.Auth.AccountSelfService.(PetBox.Core.Models.User)` — **имя метода пустое** | синтетическое имя (`<>c.<Method>b__N_0`) | то же синтетическое имя | **не сходится вообще и не уникален**: любые две лямбды в одном классе с одинаковым типом параметра дают идентичный id |
| Анонимный тип (в `WorkspaceMembershipService`) | `T:` — **буквально пустая строка после двоеточия** | синтетическое `<>f__AnonymousType0\`3` | то же | **не сходится и не различим**: ВСЕ анонимные типы решения имеют один и тот же doc-id `"T:"` |
| Razor-страница (`Pages_Admin_SysDefaults`) | `T:PetBox.Web.Pages.Admin.Pages_Admin_SysDefaults` | тот же (обычный класс после генерации) | тот же | **да** — расхождение снято на этапе компиляции, ключ надёжен |

**Что делать.** (1) Для generic-методов/типов — ключ соединения всегда `OriginalDefinition`, конкретные
типы-аргументы — отдельное поле ребра, не часть узлового ключа. (2) Для локальных функций и лямбд —
doc-id **не первичный ключ**; нужен составной `(containing method doc-id, syntax span)` — это
единственное, что различает два тела с одинаковым текстом. (3) Анонимные типы — не адресуемый узел
по doc-id вообще; ключ — `(containing method doc-id, span объявления)`. (4) DI/рефлексия/рантайм-дамп
не знают этих трёх видов в принципе (нет типа во время выполнения, пока делегат/тип не создан) —
частичное покрытие ключа, не полное.

## 4. Провенанс — поля
**Ребро:** `{ Source: семантика|IL|рантайм|строки|синтаксис, Kind: <строка>, File, Line, Span,
IsGenerated: bool, BuildLabel: product|test|e2e }`. Найдено эмпирически, не в Р1–Р9: рантайм-ребро
обязано нести ещё `ConfigProfile` (`Environment`, значения ~8 флагов `Features:*`) — оба харнесса
воркера B сняли дамп на **одном** профиле (`Environment=Testing`, только `Features:Tasks=true`);
ветки `if (env.IsDevelopment())` и остальные 7 флагов не проходились НИ РАЗУ, то есть 629
дескрипторов — это честный ответ про ОДНУ точку конфигурационного пространства, а не про все.
**Ответ:** `{ CommitSha, SourcesSearched[], NotSearched[] (выводится, не пишется руками),
Completeness: exhaustive-within-sources|partial|UNTRUSTWORTHY-model-does-not-compile,
ModelHealth: { Projects, Documents, GeneratedDocuments, CompilationErrors, WorkspaceLoadFailures } }`
— уже реализовано в `measurements/sample-answers.json`, добавить только `ConfigProfile` в
рантайм-часть `SourcesSearched`.

## 5. Три модели или одна
**Одна модель с размеченными по источнику рёбрами**, не три отдельные. Аргумент — устройство, не вкус:
узел (тип/метод/поле) один и тот же физический символ независимо от того, каким источником его
увидели (§3 подтверждает это для ≥80% видов узлов); отдельные модели заставили бы решать задачу
слияния заново на каждый запрос. Но узел без разметки источника **лжёт**: у трёх источников разные
классы ошибок — IL молча теряет `const`/enum-рёбра при живом определении поля (ложный ноль); строки
находят рёбра там, где связи нет (`grep` без якоря `^\s*` дал 164 вместо 95 — ложная единица);
рантайм видит только пройденную конфигурацию (161 фабрика непрозрачна, 8 флагов `Features:*` не
охвачены). Поэтому «одна модель» верно только при условии Р1 — провенанс живёт НА КАЖДОМ РЕБРЕ,
а не на модели целиком; без этого «одна модель» — это как раз способ спрятать три разных класса лжи
под одной непрозрачной цифрой.

## 6. Наглядность

```sql
-- узлы (наивная схема воркера A, 47 793 строки в этом прогоне)
CREATE TABLE node(doc_id TEXT PRIMARY KEY, kind TEXT, display TEXT, assembly TEXT, build_label TEXT);
-- рёбра — провенанс на строке, не на таблице
CREATE TABLE edge(src_doc_id TEXT, dst_doc_id TEXT, source TEXT, kind TEXT,
                   file TEXT, line INT, is_generated BOOL, build_label TEXT, config_profile TEXT NULL);
```

Реальные строки (не выдуманные):
- `node`: `('T:PetBox.Core.Auth.PetBoxClaims','Type.Class','PetBox.Core.Auth.PetBoxClaims','PetBox.Core','product')`
- `node`: `('M:...AgentDefTools.ListAsync(...)','Member.Method.Ordinary','AgentDefTools.ListAsync','PetBox.Web','product')`
- `edge` (семантика): `('F:...PetBoxClaims.UserId','M:...AccountSelfService.SomeMethod','semantics','read', 'AccountSelfService.cs', 41, false, 'product', NULL)`
- `edge` (рантайм, DI): `('T:...IHostApplicationLifetime','T:...ApplicationLifetime','runtime-dump','registers','n/a',0,false,'product','Environment=Testing')`
- `edge` (IL, отсутствует по конструкции): `const`-чтение `PetBoxClaims.UserId` — **строки в таблице нет**, это и есть честный ноль источника IL, а не «нет связи»

### Сквозной пример: `PetBoxClaims` (67 семантика / 0 IL)
Узел `T:PetBox.Core.Auth.PetBoxClaims` + 4 узла полей (`UserId`,`IsSysAdmin`,`WorkspaceRoles`,
`ActiveWorkspace`, все `const`). 67 рёбер `source=semantics, kind=read` от вызывающих методов к этим
4 узлам (26+21+12+8). Запрос «кто ссылается по IL» отдаёт **0 строк**, но провенанс ответа несёт
`SourcesSearched:["IL"], Completeness:"exhaustive-within-sources"` — то есть **честный ноль**: IL
структурно не может выразить чтение `const` (сворачивается в `ldstr`/`ldc.i4`, поле в метаданных при
этом объявлено — `il-control.txt`). Ложный ответ был бы «0 использований» БЕЗ указания источника;
честный — «67 по семантике, 0 по IL (ожидаемо для const), не искал по строкам/рантайму».

## Артефакты
`C:\...\scratchpad\KeyProbe\{Program.cs,KeyProbe.csproj}` — прогонялся против
`agent-aba4247bcd0590fd4\PetBox.slnx` после `dotnet restore` + `dotnet build src/PetBox.Core`.
Продуктовый код не менялся, в общий чекаут не писал.
