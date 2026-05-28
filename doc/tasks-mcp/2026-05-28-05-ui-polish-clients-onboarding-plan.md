---
timestamp: 2026-05-28T22:00:00+03:00
agent: claude-code
model: claude-opus-4-7
session: ui-polish-and-clients-roadmap
target: plan
action: update
target_file: D:\my\prj\yobabox\doc\plan.md
---

## What

Обновил `doc/plan.md`:
- **Phase 25.5** (NEW): зафиксировал session-done items от 2026-05-28 — Feature enum рефактор (27 call sites, 9 файлов), CA1848 globally suppressed, UI flag-gating в `_AdminSidebar`/`_ProjectTabs`, workspace switcher dropdown удалён из main sidebar, cookie-sync middleware + `WorkspaceSwitchEndpoint` drops returnUrl
- **Phase 25.6** (NEW): добавил follow-ups outside polish — Services placement в sidebar, CA1711 pattern reminder
- **Phase 26** (NEW): "Clients SDK consolidation" — перенос `YobaConf.Client` (.NET) и `yobaconf-client-ts` (TS) из yobaconf репы в yobabox, добавление тестов (сейчас 0 unit-тестов), модульная архитектура (core + framework integrations: `YobaBox.Client.Config`, `YobaBox.Client.Data.Linq2Db`, `yobabox-client-drizzle`, `yobabox-client-winston`). Phasing 26.1-26.7
- **Phase 27** (NEW): "Agentic pet onboarding" — agent-key infrastructure (ApiKey.ExpiresAt + sysadmin UI), admin MCP tools (declarative `agent.onboard_pet` recommended), onboarding doc + skill text, dogfooding через kpvotes-ts (lightpanda+docker networking учтён). Phasing 27.1-27.5

Параллельно сделаны 2 commit'а в репе (не plan-edit, упоминаю для контекста):
- `1efd1e5 refactor(web): Feature enum, UI flag gating, workspace switch fix`
- `44e37fb chore(dev): enable Data feature + silence HttpClient cleanup debug logs`

## Why

User напомнил "не забывай коммитить и обновлять план". Накопилось 15 модифицированных файлов от UI polish'а + workspace switch fix + CA1848 refactor. Plus обсуждение по сессии охватило 5 пунктов:

1. Services placement (UI polish, sidebar)
2. Workspace removal from sidebar (UX)
3. Agent-key infrastructure для onboarding scenario
4. TS clients validation + KpVotes-ts scenario
5. HttpClient log noise

#1 и #5 — fix'ы (закоммичено). #2 — UX (закоммичено). #3 + #4 — большой scope, требует новых фаз.

User указал что в yobaconf уже есть client libraries — нужно перенести в yobabox, добавить тесты, потом решить модульная vs монолитная архитектура. Phase 26 формализует этот блок работ как prereq для Phase 27 (agentic onboarding) и реального kpvotes-ts integration.

## Args

- File: `doc/plan.md`
- Insertion: Phase 25.5/25.6 после 25.4. Phase 26 и Phase 27 после Phase 25, перед Phase 30 (Tasks deferred).
- Phase 26 phasing: 26.1 (move) → 26.2 (unit tests) → 26.3 (core SDK extend Data/Log) → 26.4 (e2e) → 26.5 (GitHub Packages) → 26.6 (kpvotes-ts migration overlap с Phase 27) → 26.7 (npmjs/nuget stable). Open forks: точный перенос vs сразу core SDK extend (reco: сперва перенос), bun vs npm workspace (reco: bun), bun test vs vitest (reco: bun test), TS yobadata thin helper vs npm package (reco: npm package сразу).
- Phase 27 phasing: 27.1 (agent-key infra: ExpiresAt + migration + auth check + sysadmin UI) → 27.2 (admin MCP tools — declarative `agent.onboard_pet` recommended) → 27.3 (onboarding doc + skill text — recommend static doc) → 27.4 (kpvotes-ts dogfooding с lightpanda+docker networking note) → 27.5 (document gotchas). Open forks: tool granularity (declarative reco), scope model (reco: TTL+admin, not new scope), doc location (static reco), services-per-project (one kpvotes project, two services).

## Outcome

Plan актуален. Сессионные done items зафиксированы. Phase 26 + Phase 27 описаны со всеми open forks для следующей сессии. Tasks-mcp convention соблюдена.

Commits: `1efd1e5`, `44e37fb`. Working tree clean.
