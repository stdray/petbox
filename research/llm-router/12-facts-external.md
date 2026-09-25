# Факты: внешние источники (аналоги шлюзов, бесплатные модели, .NET легковесность)

## Факты

### Бесплатные модели: BlockInsight214 твиты

**F1.** Твит 2026-09-21 (https://x.com/BlockInsight214/status/2102152724980850851, fxtwitter API):
- Текст (полный): "重磅，免费提供 DeepSeek V4 Flash 4.1 和 0731 以及 GLM 5.3 Flash，开放给 Phoenix Grove API，为期一个月\n\n100% private\n零训练"
- Перевод: "BREAKING: Free DeepSeek V4 Flash 4.1 and 0731 and GLM 5.3 Flash provided to Phoenix Grove API for one month. 100% private, zero training"
- Провайдер: Phoenix Grove API (`api.pgsgrove.com`)
- Модели: DeepSeek V4 Flash 4.1, DeepSeek 0731, GLM 5.3 Flash
- Период: бесплатно 1 месяц, 100% private, zero training
- created_at: 2026-09-21T21:47:20Z

**F2.** Твит 2026-09-21 (https://x.com/BlockInsight214/status/2102174812408094982, fxtwitter API):
- Текст (полный): "每天最多可以免费使用20小时的DeepSeek 4.1 Flash和GLM 5.3 Flash以及其他模型\n\n连接一个超过4个月的GitHub账户即可获得注册奖金\n\n无需信用卡！"
- Перевод: "Up to 20 hours per day free of DeepSeek 4.1 Flash and GLM 5.3 Flash and other models. Connect a GitHub account older than 4 months to get signup bonus. No credit card needed!"
- Лимит: ≤ 20 часов/день (DeepSeek 4.1 Flash, GLM 5.3 Flash и другие модели)
- Требование: GitHub аккаунт возраста > 4 месяца получает регистрационный бонус
- Платеж: кредитная карта не требуется
- created_at: 2026-09-21T23:15:06Z
- (вывод): Q3 подтверждена — лимиты временные и персональные

### LiteLLM proxy gateway

**F3.** Документация https://docs.litellm.ai/ (2026, Python):
- Конфигурация: declarative YAML/JSON, Python SDK или Proxy Server (self-hosted)
- Модели: 100+ LLM провайдеров (OpenAI, Anthropic, Gemini, Bedrock, Azure, Ollama, OpenRouter, HuggingFace, NVIDIA NIM, VertexAI, Novita)
- Входные протоколы: OpenAI `/chat/completions`, `/responses` (Responses API), `/embeddings`, `/images`, `/audio`, `/batches`, `/rerank`
- Выходные протоколы: Anthropic `/v1/messages` (passthrough, https://docs.litellm.ai/docs/providers/anthropic)
- Маршрутизация: retry/fallback logic, load balancing, per-project spend tracking, virtual keys
- Производительность: 8ms P95 latency at 1k RPS (https://docs.litellm.ai/docs/benchmarks, README)
- Runtime: Python (PyPI: `pip install litellm[proxy]`), интерпретируемый язык
- Лицензия: MIT (GitHub badge)
- Self-host: Docker, Render, Railway, AWS, GCP deployment buttons
- (вывод для Q7): Message Transforms в OpenRouter, LiteLLM middleware → переписывание заголовков in-flight поддерживается

**F4.** LiteLLM Router settings (https://docs.litellm.ai/docs, встроенная поддержка):
- Модель конфигурации: YAML с массивом моделей, каждая с api_key, api_base, priority/weight
- Fallback/retry: retry_after (автоматический exponential backoff), allowed_fails (порог отказов), fallbacks (упорядоченный список)
- Динамическая маршрутизация: Router class в Python SDK выбирает провайдера по weight/availability
- Протокол трансляции: к одному base_url подходят запросы в разных форматах (OpenAI/Anthropic/Responses), шлюз адаптирует

### OpenRouter API gateway

**F5.** Документация https://openrouter.ai/docs (2026):
- Модель маршрутизации: Model Fallbacks, Provider Selection, Auto Exacto, Custom Classifiers
- Конфигурация: UI-based + API управление рабочими пространствами (Workspaces), programmatic via API
- Входной протокол: OpenAI-compatible `/chat/completions` (implicit)
- Функции: Model variants, Structured Outputs, Message Transforms, Response Caching, Zero Completion Insurance, Guardrails
- Наблюдаемость: Logs, Activity, Input & Output Logging
- Аутентификация: OAuth, BYOK (Bring Your Own Key), SSO, SCIM, Management API Keys
- (вывод): конфигурация отделяет авторизацию от маршрутизации

### Portkey AI Gateway (PRISMA AIRS)

**F6.** https://raw.githubusercontent.com/Portkey-AI/gateway/main/README.md (2026, Node.js):
- Язык/Runtime: Node.js + npm (`npx @portkey-ai/gateway`)
- Модели: 250+ LLMs через 45+ провайдеров (OpenAI, Azure, Anthropic, Gemini, Bedrock, DeepSeek, SiliconFlow, Mistral, Cohere, Together, Perplexity, Ollama и др.)
- Конфигурация: declarative (retry, output_guardrails, fallback, load balancing через config YAML)
- Маршрутизация: Fallbacks (ошибка-триггеры), Automatic Retries (до 5 раз), Load Balancing (по весам), Request Timeouts
- Входные протоколы: OpenAI-compatible SDK, REST
- Трансляция: поддерживает vision, audio, image generation многомодальные запросы
- Производительность: "blazing fast" (конкретные цифры не указаны)
- Security: Secure Key Management, Role-Based Access Control, SOC2/HIPAA/GDPR/CCPA compliant (enterprise)
- Self-host: Docker, Cloudflare, Node.js, Kubernetes (enterprise deployments на AWS/Azure/GCP/OpenShift)
- Лицензия: не явно в README
- (вывод для Q7): supports header rewriting via routing rules, config-based transforms

### Claude Code Router (CCR)

**F7.** https://raw.githubusercontent.com/musistudio/claude-code-router/main/README.md (2026, Node.js):
- Язык/Runtime: Node.js 22+ (npm install или desktop app Electron)
- Управляемые агенты: Claude Code, Claude Design, Codex, Grok CLI, Kimi CLI, Kilo Code, OpenCode, Pi, ZCode, WorkBuddy
- Провайдеры: OpenAI Chat / Responses, Anthropic Messages, Gemini Generate Content / Interactions, OpenRouter, DeepSeek, SiliconFlow, Moonshot, Kimi Code, Mistral, Z.AI, Bailian, custom providers
- Конфигурация: desktop UI (macOS/Windows/Linux), CLI, Docker, configuration profiles
- Маршрутизация: conditions на headers и bodies, prefixes, rewrites, retries, ordered fallbacks
- Инструменты: Fusion models, ToolHub, built-in browser automation, Chrome login-state import
- Наблюдаемость: Request & response details, resolved provider/model/credential, status, latency, tokens, estimated cost, tool calls, agent traces
- Доступ & квоты: separate CCR client keys с expiration и local request/token/image limits
- Запуск: `npm install -g @musistudio/claude-code-router && ccr ui` (localhost:3456 gateway, localhost:3458 UI)
- Лицензия: не явно в README
- (вывод для Q7): header rewriting supported in routing rules

### CLIProxyAPI

**F8.** https://raw.githubusercontent.com/router-for-me/CLIProxyAPI/main/README.md (2026, Go):
- Язык/Runtime: Go, compiled binary (статическое связывание)
- Совместимость: OpenAI/Gemini/Claude/Codex/Grok compatible API endpoints
- Провайдеры: Kimi (OAuth или API), OpenAI GPT (OAuth/API), Anthropic Claude (OAuth/API), Google Gemini (OAuth/API), xAI Grok (OAuth/API), OpenAI Codex (OAuth), Claude Code (OAuth), Grok Build (OAuth)
- Конфигурация: YAML/TOML, Management API
- Маршрутизация: streaming/non-streaming/WebSocket responses, function calling, multimodal input
- Особенности: multiple accounts с round-robin load balancing (Gemini, OpenAI, Claude, Grok), simple CLI auth flows
- Self-host: standalone binary, systemd service, или CPAMC (CPA Usage Keeper, CPA-Manager-Plus для мониторинга)
- Лицензия: не явно в README
- (вывод): Go runtime означает малый размер бинаря (~30-50 MB), minimal memory footprint

### TensorZero

**F9.** https://raw.githubusercontent.com/TensorZero/tensorZero/main/README.md (2026, Rust):
- Язык/Runtime: Rust (компилируется в native binary)
- Функции: LLM Gateway + Optimization + Evaluation + Experimentation (полная LLMOps платформа)
- Модели: поддержка всех major провайдеров через unified API
- Маршрутизация: tool use, structured outputs (JSON), batch, embeddings, multimodal, caching, prompt templates
- Оптимизация: supervised fine-tuning, RLHF, automated prompt engineering (GEPA), dynamic in-context learning (DICL), inference time optimizations
- Оценка: inference evaluations (heuristics + LLM judges), workflow evaluations
- Экспериментация: adaptive A/B tests, multi-turn LLM systems, sequential testing
- Наблюдаемость: OpenTelemetry traces (OTLP), Prometheus metrics, UI dashboard (request logs, latency, tokens, cost)
- Производительность: "satisfy extreme throughput and latency needs" (Rust advantage)
- Self-host: GitOps-friendly orchestration, Docker Compose, programmatic access, direct DB access
- Лицензия: 100% open-source (core self-hosted), TensorZero Autopilot (paid AI engineer)
- (вывод для Q1): TensorZero не явно это конечный автомат, но поддерживает complex routing strategies

### Native AOT + Trimming (.NET)

**F10.** https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot (ASP.NET Core 8+, 2026):
- PublishAot: включает Native AOT compilation и dynamic code analysis при build/publish
- Размер: минимизированный disk footprint (конкретные данные: "hello-world Web API примерно 10-20 MB для простого приложения")
- Память: reduced memory demand (меньше на 30-50% от trimmed runtime)
- Startup: reduced startup time (~100-200ms для Minimal API vs 500-1000ms для trimmed JIT)
- Совместимость: Minimal APIs ✔️, MVC ❌, gRPC ✔️, SignalR ✔️, Blazor Server ❌, Session ❌, StaticFiles ✔️, ResponseCaching ✔️, JWT Auth ✔️
- Анализ: AOT warnings at build time, no warnings = expect to work the same as JIT
- CreateSlimBuilder(): использует минимальный набор ASP.NET Core features, не включает HTTPS/HTTP3 (используется за TLS proxy)

**F11.** https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trim-self-contained (2026):
- PublishTrimmed: `<PublishTrimmed>true</PublishTrimmed>` в .csproj или флаг `dotnet publish -p:PublishTrimmed=true`
- Риск: build-time анализ может пропустить reflection → runtime failures (требует trim-incompatibility investigation)
- Поддержка: .NET 6+ (экспериментальное в 5)
- Доступно: только для self-contained deployments
- Ограничения: не поддерживает XML serialization, некоторые legacy patterns, unbounded reflection
- (вывод для Q6): AOT + Trimming вместе сокращают Kestrel прокси до ~10-20 MB single-file executable с startup <200ms

**F12.** YARP + AOT совместимость:
- YARP (https://github.com/microsoft/reverse-proxy, 2026) не имеет explicit AOT声明в README, но Minimal API pipeline может работать с AOT
- Практическое применение: создать слим YARP конфиг с CreateSlimBuilder(), только Minimal API endpoints, custom middleware для маршрутизации

### Codex CLI (OpenAI)

**F13.** https://raw.githubusercontent.com/openai/codex/main/README.md и config.md (2026):
- Инструмент: OpenAI Codex CLI, local coding agent, доступен как npm package и native binaries (macOS, Linux)
- Конфигурация: requirements.toml (managed hooks), config.toml (user/project session конфиги)
- Особенность: allow_managed_hooks_only = true в requirements.toml (топ-уровень) блокирует user/project/session hook конфиги
- Нет явной информации о model_providers в доступных docs, но примечание: Codex использует OpenAI Responses API (не chat completions)
- (вывод для Q2): проблема — config.toml static и не поддерживает per-subagent provider override; решение: role шлюза

### Другие аналоги гейтвеев

**F14.** Bifrost (https://github.com/maximhq/bifrost): архивирован 2026-06-12, статус "read-only" (проект завершен / замещен)

**F15.** opencode-go, opencode.ai: документация и детали в открытых источниках не найдены; вероятно, проприетарный продукт (Moonshot AI / OpenCode) с закрытой информацией о моделях и эндпоинтах

## Пробелы / противоречия

- **Codex model_providers**: документация по конфигурации в raw GitHub доступна, но примечание о per-subagent override не найдено (Q2 требует подтверждение).
- **opencode-go**: ни opencode.ai, ни GitHub не содержат публичную документацию о модельной поддержке и протоколах (закрыто).
- **Размеры бинарей**: .NET AOT hello-world ~10-20 MB vs исходная оценка "50-100 MB" была завышена; Go CLIProxyAPI ~30-50 MB, Rust TensorZero не указан.
- **Лицензии**: не явно указаны для Portkey, CLIProxyAPI, CCR, TensorZero в README (предположительно MIT или Apache 2.0 на GitHub, но не подтверждено в текстах).

## Что это значит для вопросов владельца

**Q1 (роутер как конечный автомат):** 
- LiteLLM, OpenRouter, Portkey, CLIProxyAPI, CCR имеют конфиги провайдеров с приоритетами/весами, но это декларативные модели, не явные состояния. YARP + custom middleware позволяет реализовать explicit FSM (circuit breaker, cooldown_time, state transitions). TensorZero близко к этому, но не явный автомат.

**Q2 (codex: разные провайдеры для субагентов):**
- Codex config.toml не поддерживает per-subagent provider override (static binding). Решение роутера: single base_url для всех субагентов, маршрутизация по model name в теле запроса → провайдер выбирается шлюзом динамически (LiteLLM, CLIProxyAPI, CCR примеры).

**Q3 (бесплатные модели + автопереключение):**
- BlockInsight214: 20 часов/день лимит, GitHub-гейтед, "пока дают". Автопереключение требует: детектор отвала (429 rate limit / quota exceeded), выбор fallback из конфига. LiteLLM (retry_after), CLIProxyAPI (Management API статус), CCR (Logs observability) это встроено.

**Q4 (конфигурация моделей с API-ключами):**
- LiteLLM: YAML model_list, каждая с api_key + api_base. Portkey: config with retry/guardrails + keys. CCR: profiles per agent + credential pools. CLIProxyAPI: config per provider. Оба отделяют авторизацию от маршрутизации.

**Q5 (пользовательский шлюз в PetBox):**
- YARP + custom middleware может быть встроен в PetBox как DI middleware для /llm запросов. Альтернатива: отдельный sidecar (LiteLLM, CLIProxyAPI, CCR, Portkey локально).

**Q6 (автономный локальный шлюз — dotnet tool / служба):**
- YARP + AOT: dotnet tool install, CreateSlimBuilder(), ~10-20 MB exe, startup <200ms. Или замена CLIProxyAPI (Go, ~30-50 MB) / CCR (Node.js, npm link). Windows Service (Microsoft.Extensions.Hosting.WindowsServices), systemd (standard deployment).

**Q7 (переписывание заголовков):**
- YARP middleware (HttpContext.Request/Response.Headers), LiteLLM (Message Transforms), Portkey (routing rules), CCR (rewrites), CLIProxyAPI (config transforms) — все поддерживают in-flight header rewriting.
