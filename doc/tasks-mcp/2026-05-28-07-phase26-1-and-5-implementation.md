---
timestamp: 2026-05-29T01:00:00+03:00
agent: claude-code
model: claude-opus-4-7
session: phase26-clients-implementation
target: plan
action: update
target_file: D:\my\prj\yobabox\doc\plan.md
---

## What

Отметил Phase 26.1, 26.2, 26.5 как DONE в `doc/plan.md` с привязкой к коммитам.

Сделанное (4 коммита):

1. `57c6601 feat(clients): port yobaconf clients → src/clients-{net,ts}/`
   - .NET: `src/clients-net/YobaBox.Client.Config/` — 7 файлов (.csproj + AssemblyInfo + JsonFlattener + 4 YobaBoxConfig*.cs). Renames: namespace + classes + extension method `AddYobaConf` → `AddYobaBoxConfig`. PackageId `YobaBox.Client.Config`.
   - TS: `src/clients-ts/yobabox-client/` — 8 файлов (package.json + tsconfig + biome + 4 src/*.ts + 2 tests/*.ts). Package `@stdray/yobabox-client` (GitHub Packages scope = owner). Class `YobaBoxConfigClient`, error `YobaBoxConfigError`.
   - Wire format strings preserved (X-YobaConf-ApiKey, /v1/conf path) — adaptation to yobabox `/api/config/{ws}/resolve` deferred к Phase 26.3.
   - Unit tests: 14 .NET (JsonFlattener × 8, Provider × 6) + 24 TS (ResolvedConfig × 16, Client × 8). Все pass.
   - Solution updated.

2. `7680fd7 build(publish): Pack/NuGetPush + TsSdk tasks + GitHub Packages CI jobs`
   - build.cs ports yobaconf Cake tasks: Pack, NuGetPush, TsSdkInstall/Typecheck/Lint/Test/Build/Pack/NpmPublish.
   - NuGetPush target: `https://nuget.pkg.github.com/{OWNER}/index.json` via GITHUB_TOKEN + GITHUB_REPOSITORY_OWNER. --skip-duplicate для retries.
   - NpmPublish target: scoped `.npmrc` (`@{owner}:registry=npm.pkg.github.com` + `_authToken=GITHUB_TOKEN`). .npmrc cleanup в finally.
   - ci.yml: tags + `nuget`/`npm`. New jobs `nuget-publish` + `npm-publish`. Existing docker `publish` job gated не запускается на nuget/npm tags.

3. `e28def3 build: include TsSdk lint/typecheck/test in Verify (CI) target`
   - Verify task теперь зависит от TsSdkLint + TsSdkTypecheck + TsSdkTest. CI на PR прогоняет оба языка.

4. `<this commit> docs(plan): mark Phase 26.1, 26.2, 26.5 DONE`
   - Plan checkmarks + commit refs.

## Why

User приказал делать 26.1 (перенос клиентов) и 26.5 (publish infra) — готовится выкладка yobabox + пакетов в GitHub Packages. User создаёт server параллельно, я работал автономно.

Решения по ходу (записал в plan):
- Bun workspace отложен — каждый TS-клиент со своим node_modules, проще, мiграция в workspace неблокирующая если понадобится shared deps
- Wire format остаётся yobaconf-совместимым — port-as-is, адаптация в 26.3
- GitHub Packages scope `@stdray/...` вместо `@stdray-npm/...` (GitHub Packages enforce scope = github owner)
- Один `GITHUB_TOKEN` авторизует и NuGet, и npm GitHub Packages — пользователю не нужен отдельный NPM_TOKEN/NUGET_API_KEY до публичных registries (Phase 26.7)

## Args

- File: `doc/plan.md`
- Marked sections 26.1, 26.2, 26.5 from `[ ]` to `[x]` with implementation references
- `doc/clients.md` deferred (not blocker for first publish)

## Outcome

Plan актуален. Ветка готова к git tag push (`nuget` / `npm`) для проверки первого publish в GitHub Packages. Pet (kpvotes-ts) сможет добавить package после успешной публикации.

Next phase items: 26.3 (Core SDK extend Data/Log), 26.4 (e2e tests), 26.6 (kpvotes-ts migration). Будут после того как первая публикация и установка в pet'е подтвердятся.
