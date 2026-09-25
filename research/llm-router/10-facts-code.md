# Факты о коде LlmRouter (текущая реализация)

## Факты

### F1. Архитектура и границы (Contract/Impl)
`PetBox.LlmRouter.Contract` (zero external dependencies): ILlmClient (EmbedAsync, RerankAsync, RerankQueryAsync, ChatAsync, IsAvailableAsync); LlmCapability enum (Embed/Rerank/Chat); LlmRegistry (endpoints, routes); LlmRoute, LlmEndpoint, LlmMessages (request/result types), ServedBy (provenance), ModelIdentity.
`PetBox.LlmRouter` (Impl, depends Contract+Core+Config+DI+FluentValidation+linq2db): CapabilityRouter (ILlmClient), OpenAiCompatibleClient (HTTP client), EndpointBreaker (circuit breaker), LlmRegistryLevelResolver (leveled config), LlmRegistryEditor (admin surface), legacy LlmRegistryStore (ConfigBindings-backed, deprecated).
Boundary enforced: consumers ONLY on Contract; no impl references outside module. (src/PetBox.LlmRouter.csproj:10-14, LlmRouterBoundaryTests)

### F2. Routing: capabilities и fallback chains
CapabilityRouter.RunChainAsync (src:8-23): walks Route list ordered by priority, transient failures = breaker+next, non-transient = next (no abort). Exhausted chain throws LlmRouterException with ALL failures.
RerankQueryAsync (src:75-90): query-affinity invariant — chunks documents, scores ALL chunks on SAME route's model, fallback WHOLE-QUERY (not per-chunk).
TierMatches predicate: null tier = default, matches any requested tier; named tier = exact match.
Thinking mode: LlmThinking enum (Enabled/Disabled), null = provider default, passed to OpenAI-compatible /v1/chat/completions.
ResponseFormat: JsonObject or JsonSchema, optional, retried on 400 if endpoint rejects (warning logged). (LlmRouterApi.cs:115-117, ChatAsync impl)

### F3. Circuit breaker (EndpointBreaker)
src/PetBox.LlmRouter/Routing/EndpointBreaker.cs: per-endpoint state (ConsecutiveFailures, OpenUntil).
FailureThreshold default 2, OpenDuration default 30s, both configurable.
IsOpen(): checks if OpenUntil > now; expired = half-open (next attempt through).
RecordSuccess: removes endpoint state; RecordFailure: increments, opens if >= threshold.
Singleton (shared across requests), thread-safe (lock on state object).

### F4. Registry: leveled, atomic, core.db-backed
src/PetBox.LlmRouter/Registry/LlmRegistryLevelResolver.cs: resolves Project→Workspace→System, FIRST level with routes wins WHOLE (no merge). Level contains endpoints + routes; FK forbids route naming endpoint at another level. Key decryption failure = hard drop (endpoint excluded, warning logged), not fallback to next level. Empty level returns empty route list (honest "no route", not inherited fallback). Workspace inheritance: workspace with no declared registry reads System level, unless InheritAllowed=false.
Stored: LlmRoutes, LlmEndpoints tables (core.db M005, M006 migrations per git history). API keys as encrypted COLUMN on endpoint row (llm-endpoint-security). Old ConfigBindings store (LlmRegistryStore) deprecated, still registered for reads, no longer written. (LlmRouterServiceCollectionExtensions:39-44)

### F5. OpenAI-compatible protocols
OpenAiCompatibleClient: /v1/embeddings (input→vectors[i]), /v1/rerank (query,documents,topN→results[{index,score}]), /v1/chat/completions (messages,model,temperature,maxTokens,thinking,response_format→choices[0].message.content).
Streaming: NOT SUPPORTED (LlmRouterApi line 131: returns 400).
Payload construction: snake_case wire (e.g., max_tokens, top_n), JsonDocument parsing, error classification (transient: timeout/connection; non-transient: 4xx; rate-limit 429 is transient). (Http/OpenAiCompatibleClient.cs:138-200)

### F6. REST endpoint and OpenAI compatibility
POST /v1/chat/completions (LlmRouterApi.cs:22, requires "LlmInvoke" policy).
Input: OpenAI shape (model, messages, temperature, max_tokens, stream).
"model" field maps to ChatRequest.Tier (router resolves to concrete endpoint/model).
Output: OpenAI-compatible ChatCompletionResponse (id, object, created, model, choices).
Project scoped: from API key's project claim, no URL project (CallerTenant.DefaultProjectOf). "*" wildcard key resolves to project_default, or forbidden if no default. (LlmRouterApi.cs:100-162)

### F7. MCP surface (5 tools)
Scope llm:invoke (embed/rerank/chat): llm_embed (inputs:array, tier?), llm_rerank (query, documents:array, topN?, tier?), llm_chat (messages:array, tier?, temperature?, maxTokens?).
All return { …result, model: {model,dim?,version}, servedBy: {endpoint,upstreamModel,attemptCount,degraded} }.
Scope llm:admin (config): llm_config_get (read-only, returns level + servedBy + version), llm_config_upsert (level-atomic PATCH, CAS version, shadow-guard).
Tenant: projectKey argument declares caller's project; level is DERIVED from project's workspace (no per-project level). smoke (sandbox project) writes System:$ like $system. (Mcp/LlmRouterTools.cs:42-230)

### F8. Embed identity decoupling
EmbedSpaceId (LlmRoute optional field, embed-only): canonical vector index key, decoupled from Model.
Two embed routes naming SAME EmbedSpaceId (e.g., home model + OpenRouter fallback) declare vectors live in ONE space, mutually comparable.
Null EmbedSpaceId → falls back to Model (backward compatible: existing indices keyed by model name stay valid).
Not used for Chat/Rerank (their identity is always Model). Last parameter on purpose (source-compatible positional calls). (LlmRegistry.cs:33-48)

### F9. Consumers inside PetBox
Memory module: LlmClientEmbedder (ILlmClient → embed), LlmClientReranker (ILlmClient → rerank).
Sessions module: LlmClientEmbedder, LlmClientReranker, DuckDbSessionEpisodicIndex (episodic memory).
Tasks module: LlmClientEmbedder, LlmClientReranker.
SearchService (hybrid search): uses embedder/reranker via IScopedDbFactory.
Session digest job: likely uses llm_chat (MCP, not direct ILlmClient).
All resolve ILlmClient via DI (singleton CapabilityRouter).

### F10. Tests (11 classes, coverage gaps)
LlmRouterBoundaryTests: contract-only dependence, NetArchTest.
LlmRegistryEditorTests: PATCH semantics, CAS, shadow guard, levels.
LlmRegistryImportTests, LlmRegistryJsonTests: JSON serialization (camelCase property, PascalCase enum).
LlmRegistryLevelsTests: level resolution cascade, inheritance, empty level.
LlmRegistryValidatorTests: validation rules.
LlmRouterRegistryFlipTests: flip from old ConfigBindings store to new core.db levels.
LlmChatEndpointAuthzTests: auth (llm:invoke scope, "*" key default-project).
LlmRegistryResolverRaceReproTests: concurrent resolution (fan-out parallel search).
LlmAdminUiTests (E2E), LlmAdminPageTests: UI admin surface (not shown here).
NO tests for fallback chain walk, breaker state machine, RerankQueryAsync chunking, response_format retry, streaming rejection, IsAvailableAsync liveness.

## Пробелы/противоречия

1. **Состояние маршрутизатора:** EndpointBreaker имеет состояние (open/half-open/closed), но нет ЯВНОГО автомата над маршрутом в целом — нет состояния типа "Chat недоступен во всём проекте", "переключился на fallback", "все провайдеры исчерпаны". Состояние отражается только в логах и ServedBy.degraded флаге.

2. **Fallback цепи vs конфигурация:** цепь заказана (priority), но нет автоматического переключения конфигурации при отвале первого провайдера — fallback ТРАНСПОРТНЫЙ (walk to next), не конфигурационный (reload registry). Нет "привязки" к fallback на время (временной маршрут).

3. **RerankQueryAsync и размеры:** ChunkSize по всей цепи одинаков (не настраивается per-route), и нет теста на тот случай, когда все чанки одного падают, но другая цепь не падает (whole-query fallback внутри одной цепи, но цепь целиком падает ещё раньше).

4. **IsAvailableAsync:** объявлен в контракте, но нет входящих вызовов в поиске кода (калл-сайтов). Лiveness чек инверсировался где-то или не используется.

5. **Trimming/AOT совместимость:** JsonSerializer source gen частичный (Web defaults), FluentValidation не trimmed-friendly, linq2db reflection на mappings. Автономный dotnet tool / служба потребует настройки.

## Что это значит для вопросов владельца

**Q1 (автомат):** код не выражает маршрутизацию как явный FSM; состояние разбросано (breaker, logs, degraded flag). Q1 требует выявить состояния: "idle" (работает первый провайдер), "fallback" (первый вышел, на втором), "exhausted" (все вышли), переходы по failure/success.

**Q2 (codex: разные провайдеры):** роутер готов — для каждого субагента ОДНА настройка `llm_config_*` с собственной цепью (embedder, reranker, chat). codex выбирает провайдер через model parameter (tier), роутер идентифицирует his route и fallback. Нет препятствий.

**Q3 (бесплатные модели + автопереключение):** fallback цепь уже работает транспортный уровень, но нет КОНФИГУРАЦИОННОГО автопереключения — не переписывает registry при отвале. DLQ или очередь из настроек (ждёт + tries) нужна отдельно.

**Q4 (конфигурация):** llm_config_upsert уже есть, уровни + наследование; ключи как шифрованные колонки. Чувство полноты достаточное.

**Q5 (пользовательский шлюз в PetBox):** REST /v1/chat/completions есть, MCP llm_chat есть. Интеграция с petbox-wire — через MCP инструменты. Отделимость: CapabilityRouter зависит от core.db (проекты, рабочие пространства, аутентификация) → нужна копия core.db или отсоединение.

**Q6 (автономный локальный шлюз):** Contract зависимо-свободная, Impl зависит от Core. Dotnet tool / служба требует либо встроенное core (in-process SQLite, seed $system), либо переделка CapabilityRouter на context-free interface (только ILlmRegistryResolver, никакого project→workspace lookup).

**Q7 (переписывание заголовков):** сейчас нет; это транспортный уровень (MCP/HTTP).

