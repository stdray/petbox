---
timestamp: 2026-06-02T00:00:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: create
target_file: petbox tasks board=ideas node=llm-router (projectKey=$system)
---

## What
Новая top-level идея `llm-router` на борде `ideas`: модуль-маршрутизатор LLM в PetBox с fallback-цепочкой (self-hosted local primary → облако fallback), переиспользуемый дистилляцией и сторонними проектами пользователя.

## Why
Возникла из обсуждения дистилляции: distillation не должна хардкодить DeepSeek/Kimi. У пользователя мощная машина (i9-14900K / 128 GB / RTX 5080 16 GB), на которой локальный summary+embeddings уже отлажены (`D:\my\prj\llama-tooling`: qwen3.5/3.6 27B-35B, ncmoe-offload, summary-сценарии). Через проброс порта домашний ПК = бесплатный эндпойнт; нужен fallback (OpenRouter/DeepSeek), когда ПК выключен/нет интернета. Идея пользователя: модуль-маршрутизатор, начать с простой fallback-цепочки, позже — автовыбор модели и т.п.; плюс бесплатный эндпойнт для других проектов.

## Args
Тело узла: дизайн (capability-routing embed|chat/summary; OpenAI-совместимые провайдеры {baseUrl,apiKey,model,priority,timeout}; circuit-breaker; публичная поверхность `/…/v1/chat/completions`+`/…/v1/embeddings`; конфиг в settings-catalog; лог «кто обслужил»). 3 острых угла: быстрое детектирование local-down, безопасность публичного домашнего эндпойнта (ключ/IP-allowlist), динамический IP (DDNS/self-registration). Build vs adopt: минимальный .NET-роутер (Polly) vs LiteLLM (Python, отдельный рантайм) — рекомендация build-minimal, LiteLLM как референс.

## Outcome
Успех, currentVersion=9. Рекомендация: `llm-router` — пререквизит `memory/distillation` (порядок: роутер v1 → дистилляция поверх). Предложено добавить связь `distillation blockedBy llm-router` (ждёт подтверждения). Дерево `ideas` теперь: `memory` (+mem0-mapping,+distillation), `agent-delivery`, `tasks-search`, `llm-router`, `deliberation-thread`.
