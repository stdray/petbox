# 66 — Требования реальных потребителей роутера (вне PetBox)

Присланы соседней сессией (petbox-95) 2026-09-25. Решений нет — это вход для выбора K1/K2/K3 (70-verdict).

## archive (заметки + мемы) — `D:\my\prj\archive\research\36-llm-router-needs.md`
1. Vision content-parts (`image_url`/base64) в `/v1/chat/completions` — сейчас `Content` = string.
2. Мультимодальный embed (картинка+текст в одном пространстве), маршрут дом→облако.
3. Embed/rerank по REST (сейчас MCP-only).
4. Закрепление модели эмбеддинга — частично есть (`EmbedSpaceId`), мультимодального пространства нет.

## chat-agent (Telegram, свой) — `D:\my\prj\chat-agent\research\inv\4-llm-backend.md`
1. Tool calling через `/v1/chat/completions`: pass-through `tools`/`tool_choice` и `tool_calls` в ответе
   (OpenAI shape). Сейчас (origin/main, по словам потребителя) в DTO нет `tools`/`tool_choice`,
   `IOpenAiCompatibleClient.ChatAsync` возвращает `string` и теряет `tool_calls`.
2. Стриминг SSE — желательно.
3. Capability-aware маршрутизация: маршрут, не умеющий tools, пропускается для запросов с tools.
Без этого агент ходит мимо роутера напрямую (deepseek-flash → deepseek-v4-pro → OpenRouter) и теряет
fallback и учёт usage.

## Что это значит для вердикта (вывод оркестратора, не проверено стендом)
- Оба потребителя просят именно то, что у внешних шлюзов уже есть в chat-пути (tools/stream/vision
  pass-through — Bifrost/agentgateway сквозные), а у нашего модуля нет. Это аргумент ЗА вынос chat во
  внешний шлюз (K2/K1), а не за достройку chat в своём модуле.
- Мультимодальный embed и `EmbedSpaceId` — трек внутреннего CapabilityRouter (K2), шлюзы его не дают.
