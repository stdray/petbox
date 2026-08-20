# Воркер H — сырые выписки (агентский сегмент), 2026-08-20

Полный анализ и статусы — в `18-landscape-agent-context.md`. Здесь только необработанные цитаты/ссылки
из web-поиска, для верификации при консолидации.

## Sourcegraph
- sourcegraph.com/deep-search — «Deep Search is an agent that reasons across your entire codebase...
  detailed source list showing exactly which searches it performed and which files it read»
- sourcegraph.com/resources/context-compare — таблица сравнения Cursor/Windsurf/Augment/Sourcegraph и
  др., Sourcegraph сам себе пишет «Exhaustive coverage: every instance across all repos»
- sourcegraph.com/mcp — MCP-сервер, SCIP-powered code intelligence
- Amp = ребрендинг Cody в 2026, та же кодовая база интеллекта

## DeepWiki / Cognition
- cognition.com/blog/deepwiki, docs.devin.ai/work-with-devin/deepwiki
- «indexed over 50,000 of the most popular public GitHub repos... 4 billion lines of code processed»
- `.devin/wiki.json` — конфиг генерации вики под большие репо

## Graphify
- github.com/safishamsi/graphify — «no embeddings, just a graph it can trace and cite», Apache 2.0
- github.com/Howell5/graphify-ts — tree-sitter WASM, 12 языков, graphology
- «71.5x меньше токенов против чтения сырых файлов» — на корпусе 52 файлов (см. также разбор на wiki
  «Индексы кода для агентов»)

## aider repo map
- aider.chat/2023/10/22/repomap.html — оригинальный пост про tree-sitter + PageRank
- deepwiki.com/Aider-AI/aider/4.1-repository-mapping-system — свежее описание механизма
- anishgandhi.com/aider-pagerank-codebase-ranking — персонализированный PageRank, рестарт на символы
  текущего чата

## Cursor / Windsurf / Augment
- cursor.com/docs/context/codebase-indexing, cursor.com/blog/secure-codebase-indexing — Merkle tree
  sync, AST-выровненный chunking, Turbopuffer, «only embeddings and metadata stored in cloud»
- markaicode.com/windsurf-flow-context-engine — «M-Query» вместо cosine similarity, 768-dim embeddings
- augmentcode.com/blog/a-real-time-index-for-your-codebase-secure-personal-scalable — «real-time
  index... indexes commit history, codebase patterns, external sources like docs and tickets, tribal
  knowledge»; docs.augmentcode.com/context-services/mcp/overview — Context Engine MCP, локальный режим
  через Auggie CLI

## Cline
- cline.bot/blog/why-cline-doesnt-index-your-codebase-and-why-thats-a-good-thing — «Code Doesn't Think
  in Chunks» / «Indexes Decay While Code Evolves» / «Security Becomes a Liability»
- news.ycombinator.com/item?id=44106944 — обсуждение на HN (то же, что уже на wiki-странице)

## Claude Code
- vadim.blog/claude-code-no-indexing — Boris Cherny цитата про RAG vs agentic search; 4 причины отказа
  (security/privacy/staleness/reliability); признание ниши, где RAG выигрывает (большие монорепо,
  несогласованные имена)
- WebFetch на этот URL один раз получил 429 (rate limit), повтор прошёл успешно

## Serena
- github.com/oraios/serena — «the IDE for your agent», инструменты find_symbol/find_referencing_
  symbols/get_symbols_overview/rename_symbol/replace_symbol_body
- github.com/oraios/serena/issues/634 — баг про повторную инициализацию LSP, таймауты
- LSP не уведомлялся о внешних правках файлов → чинили file-system polling перед вызовом инструмента

## CodeMap (C#/.NET)
- github.com/bbajt/csharp-code-map — Roslyn-powered MCP-сервер, 28 инструментов, 6 категорий
- «HTTP endpoints, config keys, DB tables, DI registrations, middleware pipeline, retry policies,
  exception throw points, structured log templates» — ВСЁ статика через Roslyn, не рантайм-дамп
- README limitations: multi-target conditional symbols, legacy MVC convention routing, неполный F#,
  нужна сборка для Razor-генераторов
- «One tool call. Precise answer. No context flood»; «90%+ token savings vs reading files directly»

## Академия
- github.com/gersteinlab/LocAgent (ACL 2025) — TraverseGraph tool, SWE-Bench-Lite Acc@k по файлам/
  модулям/функциям (94.16% / 87.59% / 77.37% с Claude-3.5)
- RepoGraph — ICLR 2025, graph-based repository understanding
- MCGA (Make Call Graph Agent) — static + dynamic call-site discovery, различает theoretically
  possible vs practically reachable через рантайм-трассы
- DAIRA — dynamic analysis как callable tool в issue-resolution агенте
- Оба (MCGA/DAIRA) — 2026 препринты, замер на бенчмарках, не продукты с ценой

## Devin Agentic MapReduce
- devin.ai/blog/agentic-map-reduce — «selectors are an inspectable, version-controlled artifact that
  can be tested and tuned, whereas a search agent's "I've looked everywhere" is unfalsifiable»
- Map worker получает «which selector fired and on what evidence» — provenance по правилу отбора,
  не по графу

## Общие источники по теме "не индексируем"
- zerofilter.medium.com/why-claude-code-is-special... — «outperformed everything, by a lot» (Boris,
  Latent Space podcast, May 2025)
- harrisonsec.com/blog/agent-retrieval-cost-curve-claude-code-grep-vs-rag — «Windsurf, Cline, Devin,
  and Sourcegraph Amp dropped vectors for tool-driven search» — ПРОТИВОРЕЧИТ находкам про Windsurf/
  Augment/Sourcegraph выше (у них RAG/векторы прод-механизм). НЕ ПРОВЕРЕНО, вероятно неточность автора
  статьи или речь о другом узком компоненте — в главный файл не включил как непроверенное

## Добор по запросу владельца: CodeMap deep-dive + roslyn-codelens-mcp (2026-08-20)

Источники (прочитаны напрямую, не по README):
- github.com/bbajt/csharp-code-map — LICENSE.MD (полный текст декодирован через raw.githubusercontent),
  README.MD, docs/CODEMAP-AGENT-GUIDE.MD, docs/API-SCHEMA.MD (частично устарел относительно README —
  не описывает codemap.summarize/export/guide, index.diff и др. новые инструменты)
- src/CodeMap.Roslyn/Extraction/DiRegistrationExtractor.cs — полный текст, 7 паттернов детекции DI
- src/CodeMap.Roslyn/Extraction/ReferenceExtractor.cs + RefKindClassifier.cs — полный текст,
  подтверждает: (а) generic-классификация по SemanticModel без спецкейса для switch/case → const и
  enum-в-switch НЕ воспроизводят провал NDepend; (б) AttributeSyntax явно исключён из построения
  рёбер → атрибуты не отслеживаются вообще для произвольного атрибута
- src/CodeMap.Roslyn/Extraction/EndpointExtractor.cs — только 4 захардкоженных имени атрибутов
- src/CodeMap.Roslyn/IncrementalCompiler.cs — кэш MSBuildWorkspace+Solution между вызовами
- src/CodeMap.Git/GitService.cs — RepoId = SHA-256(origin URL)[:16] или "local-"+hash(abs path);
  это ключ шаринга baseline-кэша между git-worktree одного репозитория
- GitHub issues (open): #7 (нет текст-поиска по не-.NET), #5 (монорепо не поддержан), #4 (TFS/cache
  на git-commit истории)
- github.com/MarcelRoozekrans/roslyn-codelens-mcp — README (67 тулов), LICENSE (MIT, декодирован),
  src/RoslynCodeLens/Tools/FindAttributeUsagesLogic.cs (полный текст, ISymbol.GetAttributes()-based),
  src/RoslynCodeLens/Analysis/DiRegistrationScanner.cs (grep на factory/lambda — сентинел
  `UnresolvedFactory = "(factory)"`, идентичная логика CodeMap)
- gh repo view / gh api для метаданных живости обоих репозиториев + carquiza/RoslynMCP,
  egorpavlikhin/roslyn-mcp, ndepend/NDepend.MCP.Server, pzalutski-pixel/sharplens-mcp,
  MadQ/RoslynMcp, JoshuaRamirez/RoslynMcpServer (метаданные только, не разобраны глубоко)

Не проверено экспериментально (нет прогона на PetBox): индексация tests/** на реальном PetBox.slnx,
поведение при реальном создании git worktree (логика прочитана по коду, не воспроизведена в песочнице).
