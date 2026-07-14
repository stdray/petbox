# PetBox deployment guide

Walkthrough для деплоя petbox на host через GitHub Actions. PetBox использует ту же shared-host инфру что yobaconf + yobalog (один сервер, один Caddy, отдельные порты).

Если на этом host'е ещё нет Caddy + Docker — следуй сначала `yobalog/doc/deploy.md`, оно ставит базовую инфру. Этот гайд предполагает что shared-host уже готов.

## Prerequisites

- [ ] Host с Docker, reachable через SSH из GitHub Actions
- [ ] Caddy на host'е (поставлен при bootstrap yobalog'а)
- [ ] DNS: A-record `petbox.3po.su` (или твой домен) → public IP сервера
- [ ] **Public GitHub repo + public GHCR package.** Workflow использует ephemeral `${{ secrets.GITHUB_TOKEN }}` с `permissions: packages: write` — PAT не нужен. После первого push в GHCR один раз flip visibility package'а в публичный: GitHub profile → Packages → petbox-web → Package settings → Change visibility.

## Step 1 — GitHub Actions secrets

Settings → Secrets and variables → Actions → New repository secret.

### Обязательные (deploy без них упадёт)

| Secret | Что | Как сгенерировать |
|---|---|---|
| `DEPLOY_HOST` | hostname или IP сервера | например `yoba-apps.3po.su` (тот же что yobaconf/yobalog) |
| `DEPLOY_USERNAME` | SSH user в `docker` group | например `stdray` |
| `DEPLOY_PASSWORD` | SSH password | (SSH сервер должен иметь `PasswordAuthentication yes`) |
| `PETBOX_ADMIN_USERNAME` | имя для login в `/Login` | например `admin` |
| `PETBOX_ADMIN_PASSWORD_HASH` | PBKDF2-SHA256 hash пароля | см. Step 2 ниже |
| `PETBOX_MASTER_KEY` | AES-256 master key для config-secrets шифрования | `openssl rand -base64 32` локально, копировать вывод |

### Опциональные (для observability — можно отложить)

| Secret | Что | Когда нужно |
|---|---|---|
| `PETBOX_OTEL_ENABLED` | `true` / `false` | Если хочешь OTel traces |
| `PETBOX_OTEL_ENDPOINT` | `self` или full external URL | См. ниже |
| `PETBOX_ADMIN_FORCE` | `true` | Только для recovery — см. ниже |

### Bootstrap-admin lockdown

`PETBOX_ADMIN_USERNAME`/`PETBOX_ADMIN_PASSWORD_HASH` — это **bootstrap** аккаунт: он создаётся
только на первом запуске (пока в системе нет ни одного `$system` администратора). Как только ты
создашь собственного админа (`/ui/admin/sys/users` → Create user, затем выдать ему `$system` Admin
в `/ui/admin/ws/$system/members`), вход под bootstrap-аккаунтом **отклоняется** — это штатное
поведение (как в yobaconf). Сам bootstrap-аккаунт при этом не удаляется.

Если заблокировался (потерял доступ к собственному админу) — выставь `PETBOX_ADMIN_FORCE=true` в
env контейнера, перезапусти, войди под bootstrap-аккаунтом, почини доступ, затем убери переменную.

**SELFLOG секреты не нужны.** PetBox пишет собственные ILogger calls **in-process** через `SystemLogger` (Phase 22.2) — напрямую в `IngestionPipeline` → LogDb, без HTTP и без API key. Deploy job устанавливает `Seq__SelfLog__Enabled=true` всегда — это единственный switch.

### Куда petbox шлёт телеметрию

**Логи**: всегда in-process в `$system/petbox-web` LogDb. Видишь через `/ui/$system/petbox-web/logs` (KQL UI). Никакой настройки.

**OTel traces** (опционально):

**Option A — self-hosted ("self" sentinel):**
```
PETBOX_OTEL_ENABLED = true
PETBOX_OTEL_ENDPOINT = self
```
Deploy script резолвит `self` → `http://localhost:8080/v1/traces`. Auth через `yb_key_system_internal` ApiKey (auto-seeded миграцией M004 в `$system` project) — никаких ключей выпускать руками не нужно. Плюс: unified observability через один UI, нет внешних зависимостей. Минус: если petbox упадёт — некуда писать диагностику падения; слабый риск feedback-loop.

**Option B — external yobalog:**
```
PETBOX_OTEL_ENABLED = true
PETBOX_OTEL_ENDPOINT = https://yobalog.3po.su/v1/traces
```
External URL без sentinel'а. Auth по-прежнему через `yb_key_system_internal` (yobalog тоже принимает этот key если seeded там). Плюс: yobalog независим — увидишь spans даже падения petbox'а. Минус: external dependency.

**Option C — выключено:**
Не set'ить `PETBOX_OTEL_ENABLED` → OTel не активируется. Логи всё равно работают (in-process), просто без traces.

Для первого деплоя рекомендую **Option C** (минимум moving parts), потом **Option A** (`OTEL_ENDPOINT=self`) когда подтвердишь стабильность.

**OTel metrics** (ingest-only): PetBox принимает OTLP **метрики** на `/v1/metrics/{projectKey}/{logName}`
(ApiKey, как traces — `X-Service-Key` не нужен) и на bare `/v1/metrics` self-export (`X-Seq-ApiKey`),
складывает в per-log MetricPoints и делает их queryable через KQL-корень `metrics`. Это приёмная
сторона — любой pet со стоковым OTLP-metrics-экспортёром может слать метрики в PetBox, указав
`{endpoint}/v1/metrics`. Сам **petbox-web метрики о себе НЕ экспортирует**: его self-export (`PETBOX_OTEL_*`
выше) настроен только на traces (`.WithTracing`, без `.WithMetrics`), поэтому OTel-переменные деплоя
по-прежнему касаются только спанов.

## Step 2 — сгенерировать admin password hash

Локально (нужен .NET 10 SDK):

```bash
dotnet run --project src/PetBox.Web -- --hash-password 'your-strong-password'
```

Вывод: `pbkdf2$100000$<salt>$<hash>`. Скопировать **всю** строку (включая `pbkdf2$...`) в GitHub secret `PETBOX_ADMIN_PASSWORD_HASH`.

Plaintext **никуда** больше не сохраняется — hash PBKDF2-SHA256 безопасно класть в secret, plaintext нет.

**Альтернатива** (если нет локального .NET): запустить контейнер с тем же бинарём:
```bash
docker run --rm ghcr.io/stdray/petbox:latest --hash-password 'your-strong-password'
```
(Требует чтобы образ уже был опубликован — для первого деплоя локальный `dotnet run` неизбежен.)

## Step 3 — обновить Caddy config

Боевой Caddy запущен через systemd drop-in как `caddy run --config /etc/caddy/caddy.json` —
`/etc/caddy/Caddyfile` и `conf.d/*.caddy` **не читаются вообще**, править их бесполезно.
Единственный источник истины — `/etc/caddy/caddy.json`, эталон которого лежит в репе как
`infra/caddy.json`.

Цепочка для petbox: `:443` → app `layer4` (SNI-мультиплексор, матчит `petbox.3po.su`) →
`127.0.0.1:8444` → http-сервер `petbox_l7` (TLS) → `reverse_proxy` → `localhost:8083`
(контейнер petbox).

`flush_interval: -1` на этом route НЕ ставим. Он был в мёртвом Caddyfile fragment, то есть
на проде не применялся никогда; попытка перенести его в caddy.json — непроверенное изменение
горячего пути (отключает буферизацию ответа) ради SSE live-tail, который и без него работает.
Если он всё же понадобится — вводить отдельно и с замерами, а не прицепом.

`petbox_l7` также несёт заглушку на деплой: если backend недоступен (502/504), Caddy
вместо сырого 502 отдаёт `503` с `Retry-After: 60` — JSON на `/api/*`, `/mcp`, `/health`,
HTML на всё остальное. 60 секунд — не круглое число для красоты, а замер простоя за 14 дней
по логам Caddy + docker events: медиана 37s, p90 ≈ 55s, максимум 114s.

Применение на сервере:

```bash
scp infra/caddy.json <host>:/tmp/caddy.json
ssh <host>
sudo caddy validate --config /tmp/caddy.json
sudo cp /etc/caddy/caddy.json /etc/caddy/caddy.json.bak
sudo cp /tmp/caddy.json /etc/caddy/caddy.json
sudo systemctl reload caddy
```

`caddy validate` **до** подмены файла — если конфиг битый, `reload` не должен даже
пытаться его подхватить. Бэкап перед подменой — откат при необходимости — это
`sudo cp /etc/caddy/caddy.json.bak /etc/caddy/caddy.json && sudo systemctl reload caddy`.

Caddy получит TLS-cert автоматически на первый HTTPS-запрос (Let's Encrypt) — manual certbot не нужен. После reload в логах будет видно `obtaining certificate` → `served key authentication certificate` → `certificate obtained successfully`.

Если reload завис >2 минут или systemd state в `reloading` дольше нескольких секунд — это corrupt state systemd:

```bash
sudo systemctl reset-failed caddy
sudo systemctl restart caddy
```

`reset-failed` сбрасывает failure-counter unit'а, `restart` форсит полный stop→start cycle (вместо reload).

## Step 4 — подготовить data directory

Container монтирует `/opt/petbox/data` с host'а для SQLite + DataProtection keys:

```bash
sudo mkdir -p /opt/petbox/data
sudo chown 1654:1654 /opt/petbox/data    # uid chiseled `app` user'а
```

UID 1654 — это `app` user в chiseled .NET runtime image. Без правильного chown контейнер не сможет писать в volume → SQLite "unable to open database file" при старте.

`DEPLOY_USERNAME` должен быть в `docker` group (один раз):
```bash
sudo usermod -aG docker stdray
# logout/login чтобы группа применилась к SSH session'у
```

(Обычно уже сделано при bootstrap'е yobalog'а — здесь как напоминание.)

## Step 5 — trigger deploy

Из локального clone:

```bash
git tag -f deploy
git push origin deploy --force
```

GitHub Actions подхватывает tag:
1. **`publish` job** — Cake DockerPush target: build образа + DockerSmoke + push в `ghcr.io/stdray/petbox:<FullSemVer>`.
2. **`deploy` job** — SSH на host, `docker pull/stop/rm/run` нового container'а с port `127.0.0.1:8083:8080`, volume `/opt/petbox/data:/app/data`, всеми env vars из secrets.

Force-push на tag намеренно — `deploy` это "moving pointer" tag. Каждый push логируется в Actions tab вместе с SHA.

## Step 6 — verify deploy

Из любого места:

```bash
curl https://petbox.3po.su/health
# → {"status":"healthy"}

curl https://petbox.3po.su/version
# → {"semVer":"0.1.X-ci.N+<sha>","shortSha":"abc1234","commitDate":"..."}
```

Открыть `https://petbox.3po.su/Login` в браузере → форма sign-in. Login с username + password из Step 2. Должен попасть на dashboard `/ui/$system`.

## Step 7 — self-log (ничего не делаем)

PetBox пишет собственные ILogger calls **автоматически** через `SystemLogger` (Phase 22.2) — in-process прямо в `$system/petbox-web` LogDb. Deploy job выставляет `Seq__SelfLog__Enabled=true`. Никаких секретов, никаких ключей.

После первого деплоя логи смотреть через:
- `/ui/$system/petbox-web` — Logs UI с KQL
- Или прямо в `docker logs petbox` для startup-time сообщений до того как LogDb инициализировалась

## Step 8 (опционально) — OpenTelemetry traces

PetBox emit'ит spans (ASP.NET Core root + ingestion pipeline + KQL queries + Data exec/query). Если хочешь — добавь GitHub secrets:

**Option A — self-hosted (рекомендую):**
```
PETBOX_OTEL_ENABLED  = true
PETBOX_OTEL_ENDPOINT = self
```
Deploy script резолвит `self` → `http://localhost:8080/v1/traces`. ApiKey для аутентификации берётся из appsettings `Seq:SelfLog:ApiKey` = `yb_key_system_internal` (auto-seed'ится M004). После deploy spans видны в petbox waterfall UI.

**Option B — external collector:**
```
PETBOX_OTEL_ENABLED  = true
PETBOX_OTEL_ENDPOINT = https://yobalog.3po.su/v1/traces
```
ApiKey тот же — auto-seeded. Подходит если хочешь spans в external yobalog для post-mortem.

`PETBOX_OTEL_ENABLED` должен быть **literal** `true` (не `1`, не `True`).

## Rollback

Если deploy сломал prod — раскат предыдущим commit'ом:

```bash
git checkout <prev-known-good-sha>
git tag -f deploy
git push origin deploy --force
```

CI прогонит полный pipeline на старом commit'е — tests + smoke + deploy. Никакого special "rollback" workflow.

## Port allocation table (для будущих сервисов)

| Service | Loopback port | Domain |
|---|---|---|
| yobaconf | 8081 | yobaconf.3po.su |
| yobalog | 8082 | yobalog.3po.su |
| petbox | 8083 | petbox.3po.su |
| (free) | 8084+ | для будущих |

При добавлении нового сервиса:
1. Зарезервировать port в **всех** репозиториях `infra/caddy.json` (header table sync'ить руками)
2. DNS A-record для нового субдомена
3. Добавить route в `infra/caddy.json` (layer4 SNI match + http server); применить как в Step 3
4. Deploy через свой `deploy` tag flow
4. Deploy через свой `deploy` tag flow

## Что лежит в `/opt/petbox/data/`

После первого старта container создаст внутри:
- `petbox.db` — main SQLite (Workspaces, Projects, Services, ApiKeys, Settings, ShareLinks, etc.)
- `keys/` — ASP.NET DataProtection keys (для cookies, antiforgery)
- `logs/{projectKey}.db` — per-project LogDb (когда Features:Logging=true)
- `config/{workspaceKey}.db` — per-workspace ConfigDb (когда Features:Config=true)
- `db/{projectKey}/{dbName}.db` — pet's DataDbs (когда Features:Data=true)

Резервная копия: достаточно `tar czf petbox-data-$(date +%Y%m%d).tar.gz /opt/petbox/data` после `docker stop petbox` (для consistency SQLite WAL). После — `docker start petbox`.

## Troubleshooting

**Container не стартует** → `docker logs petbox`:
- `unable to open database file` → `/opt/petbox/data` не chown'ен на 1654:1654
- `BaseUrl is required` / `ApiKey is required` от PetBoxConfigProvider → не должно быть на первом деплое (petbox не использует свой Config-client на себе; см. invariant "No PetBox self-config через ConfigModule")
- `Admin__Username is required` / login form не принимает credentials → проверь secrets `PETBOX_ADMIN_USERNAME` + `PETBOX_ADMIN_PASSWORD_HASH`

**503 "PetBox is being redeployed"** дольше пары минут при открытии `petbox.3po.su`
(это ожидаемая заглушка на время деплоя — см. Step 3 — но не должна висеть долго):
- Container не работает: `docker ps -a | grep petbox` — exited?
- Port не пробрасывается: `ss -tlnp | grep 8083` — должен слушать loopback
- `caddy.json` не reload'нут после правки: `sudo systemctl reload caddy`

**Login принимает но redirect на `/Login` снова**:
- DataProtection keys не персистятся: `docker exec petbox ls /app/data/keys` пусто
- Вероятно `/opt/petbox/data` не mount'нут или chown неправильный
