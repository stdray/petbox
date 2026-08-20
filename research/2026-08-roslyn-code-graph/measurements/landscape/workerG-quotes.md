# Воркер G — сырые выписки и ссылки (сегмент: модель кода + язык запросов)

Все цитаты получены веб-поиском/фетчем 2026-08-20. Не пересказ — источник для `17-landscape-query-tools.md`.

## CodeQL
- Позиционирование (docs.github.com/writing-codeql-queries/about-codeql-queries): запросы
  "used to analyze code for issues related to security, correctness, maintainability, and
  readability". Два вида: alert queries (проблема в точке), path queries (source→sink flow).
- CI-гейт vs разведка: каждый запрос несёт метаданные `@precision` (very-high/high/medium/low) —
  только высокоточные попадают в дефолтные CI-сьюты (`security-extended`, `security-and-quality`);
  низкоточные остаются исследовательскими, их гоняют вручную/по требованию. Один язык, разные
  сьюты по тегу метаданных.
- database-build шаг добавляет 5–60+ минут — блокирующий гейт на каждый PR непрактичен, поэтому
  типичный паттерн: PR — diff-scoped быстрый проход, nightly — полный.
  Источники: codeql.github.com/docs/writing-codeql-queries/about-codeql-queries/,
  appsecsanta.com/sast-tools/semgrep-vs-codeql, konvu.com/compare/semgrep-vs-codeql

## Joern / Code Property Graph
- joern.io/impact: "The Bug Hunter's Workbench", "the original implementation of the Code
  Property Graph". Аудитория — security researchers, vulnerability discovery at scale, включая
  бинарники/байткод (не только исходники).
  Версия 2.0.1 (joern.readthedocs.io). Язык запросов — Scala DSL.
- 2025-2026: LLMxCPG (USENIX Security 2025) — CPG как контекст для LLM-детектора уязвимостей;
  Codebadger — открытый MCP-сервер поверх Joern CPG (program slicing, taint tracking).
  Источники: joern.io/impact/, github.com/joernio/joern, docs.joern.io/code-property-graph/,
  arxiv.org/html/2603.24837v1

## Glean (Meta)
- engineering.fb.com (2024-12-19, "Indexing code at scale with Glean"): открытая система,
  типизированные факты о коде (определения, ссылки, типы, вызовы, наследование, импорты) в
  запрашиваемой БД. Язык запросов — Angle (анаграмма Glean, "рыбачить"), Datalog-стиль.
  Индексаторы по языкам: C++, Hack, Python, Haskell, Flow + LSIF/SCIP для Go/Java/Rust/TS.
  (WebFetch страницы engineering.fb.com упал по ECONNRESET — цитаты через WebSearch-сводку,
  без прямой выдержки текста; проверить при возможности напрямую.)
  Источник: engineering.fb.com/2024/12/19/developer-tools/glean-open-source-code-indexing/,
  simonmar.github.io/posts/2025-05-22-Glean-Haskell.html

## Kythe (Google)
- Позиционирование (kythe.io): "pluggable, (mostly) language-agnostic ecosystem for building
  tools that work with code" — cross-referencer/индексатор для Google Code Search.
  Статус: команда в США уволена в апреле 2024, заменена индийской командой поддержки
  (en.wikipedia.org/wiki/Google_Kythe) — живо, но не флагманский приоритет.
  Источник: en.wikipedia.org/wiki/Google_Kythe, kythe.io/docs/schema/indexing-generated-code.html

## Semgrep
- Позиционирование (github.com/semgrep/semgrep): "Lightweight static analysis for many
  languages. Find bug variants with patterns that look like source code."
- Честная оговорка: Community Edition — taint-анализ только в границах ОДНОЙ функции/файла,
  "will miss many true positives". Interprocedural + cross-file — только в платном Pro Engine
  (30+ языков interprocedural, cross-file для C/C++/C#/Go/Java/JS-TS/Kotlin/Python).
- CI vs разведка: правило с severity ERROR блокирует CI, WARNING/INFO — информационно;
  `semgrep scan` локально = разведка тем же языком правил.
  Источники: github.com/semgrep/semgrep, endorlabs.com/learn/semgrep-alternatives,
  konvu.com/compare/semgrep-vs-codeql

## ast-grep
- ast-grep.github.io/advanced/tool-comparison.html: "does not have deep semantic information...
  only operates on the syntactic level of the code, which may miss some matches". Позиция:
  для разработки (codemod/lint/rewrite), не для security — в отличие от Semgrep.
  Источник: ast-grep.github.io/advanced/tool-comparison.html, news.ycombinator.com/item?id=38594457

## jQAssistant
- jqassistant.github.io/jqassistant/current: "a QA tool which allows the definition and
  validation of project specific rules on a structural level", построен на Neo4j, плагин
  Maven/CLI, "plugged into the build process to automate detection of constraint violations".
- Источник рёбер: байткод JVM (аналог IL для .NET — точный структурный аналог NDepend, но
  граф+Cypher вместо IL+LINQ).
- Пример правила (constraint, детектирует тест без ассерта):
  ```cypher
  MATCH (t:Test:Method) WHERE NOT (t)-[:INVOKES]->(:Assert:Method) RETURN t AS TestWithoutAssertion
  ```
- CI vs разведка — ключевой документированный водораздел ОДНИМ языком (Cypher): `<concept>` —
  просто выводит новые виртуальные факты (разведка/отчёт), `<constraint>` — тот же Cypher, но
  провал = красный Maven build (гейт). Один файл правил, один язык, разные теги.
- Оговорка: не резолвит динамический диспетчинг, внешние (несканированные) типы видны только
  по FQN без деталей атрибутов.
  Источник: jqassistant.github.io/jqassistant/current/

## NetArchTest / ArchUnitNET / ReSharper SSR (.NET, без графа)
- NetArchTest: последний релиз 1.3.2 (2021-05-23) — фактически заброшен, есть форки.
  ArchUnitNET (TNG, .NET-порт ArchUnit) — активнее поддерживается на 2026.
  Источники: github.com/BenMorris/NetArchTest, github.com/TNG/ArchUnitNET,
  code-maze.com/csharp-architecture-tests-with-netarchtest-rules/
- Источник рёбер обоих: чтение скомпилированных сборок (reflection/Mono.Cecil) — тот же класс,
  что IL у NDepend, но без метрик/языка запросов — только фиксированные fluent-правила,
  оформленные как unit-тест. Транзитивности как явного примитива нет.
- ReSharper SSR (jetbrains.com/help/resharper/.../Structural_Search_and_Replace.html):
  паттерны с плейсхолдерами `$X` по PSI (семантическая модель ReSharper), find+replace,
  quick-fix по всему решению. "There isn't much documentation" — сами отмечают скудность доков.
  Не язык запросов в смысле произвольных предикатов — фиксированная форма "паттерн → замена",
  без транзитивных предикатов и без агрегаций.
  Источники: jetbrains.com/help/resharper/Navigation_and_Search__Structural_Search_and_Replace.html,
  github.com/hmemcpy/AgentMulder/wiki/ReSharper's-Structural-Search

## Sourcetrail — мёртвый ориентир
- Проект остановлен в сентябре 2021 (news.ycombinator.com/item?id=29646519,
  en.wikipedia.org/wiki/Sourcetrail). Форк petermost/Sourcetrail жив (коммиты по декабрь 2025,
  поддержка Clang/LLVM 20, Qt 6.9) — сообщество тянет, вендор нет.
- Важно для этого сегмента: Sourcetrail НЕ имел языка запросов вообще — чисто визуальный
  граф-эксплорер по семантическому индексу (clang/java indexer). Контрпример «граф без
  запросов» — крайняя точка нашей оси.
  Источники: en.wikipedia.org/wiki/Sourcetrail, news.ycombinator.com/item?id=29646519

## SciTools Understand
- Позиционирование: dependency/control-flow/call графы, метрики, compliance (MISRA и т.п.) для
  многоязычных legacy/embedded кодовых баз. Собственный мультиязычный парсер (не компилятор) —
  ближе к «синтаксис» с частичным разрешением, чем к «семантика».
  Язык запросов в декларативном смысле НЕ найден — скриптинг через Perl/Python API поверх модели,
  не декларативный язык запросов уровня CQLinq/Angle/Cypher/QL. Коммерческая лицензия,
  maintenance 18%/год.
  Источники: scitools.com/features, blog.scitools.com/dependency-networks-with-understand/,
  support.scitools.com/support/solutions/articles/70000587628-understand-2-5-release-notes

## NDepend / CQLinq — эталон (данные воркера D, `13-ndepend-baseline.md`, не переоткрывать замеры)
- Маркетинг (simpleorientedarchitecture.com со ссылкой на ndepend.com/features/cqlinq): "query
  your code base... the same way as relational data is queried with SQL" — C#/LINQ поверх IL-модели.
  Замеры воркера D: 3 из 3 известных провалов (const инлайнится, enum→int, атрибут на
  типе невидим по методам) подтверждены ИМЕННО как следствие источника IL, не бага NDepend.
  Полное время вызова ~1.2с (холодный dotnet), не 1-6мс из README (это только внутренний замер).
- CI vs разведка: NDepend Quality Gates — тот же CQLinq, baseline diff ("code smells introduced
  since baseline") гейтит билд; ad-hoc запросы в IDE — разведка. Тот же паттерн, что у jQAssistant
  и CodeQL — язык один, режим определяется тем, привязан ли запрос к baseline/сьюту.

## Синтез: инвариант-в-CI vs разведка — общий паттерн
Все три зрелых продукта с языком запросов (CodeQL, jQAssistant, NDepend) решают этот водораздел
ОДНИМ языком запросов, не двумя инструментами: разница — метаданные/классификация запроса
(precision-тег, concept/constraint-тег, привязка к baseline), а не другой синтаксис. NetArchTest/
ArchUnitNET — противоположный полюс: они CI-инвариант И ТОЛЬКО ЭТО, языка для разведки нет вообще
(хочешь новый вопрос — пиши новый C#-метод-тест). ReSharper SSR и ast-grep — тоже без разделения
на «гейт», потому что оба не гейт по умолчанию (SSR — quick-fix в IDE, ast-grep — CLI/lint шаг,
опционально в CI, но без встроенного различия «гейт vs проводник»).
