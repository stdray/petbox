# YobaBox deployment guide

Walkthrough для деплоя yobabox на host через GitHub Actions. Yobabox использует ту же shared-host инфру что yobaconf + yobalog (один сервер, один Caddy, отдельные порты).

Если на этом host'е ещё нет Caddy + Docker — следуй сначала `yobalog/doc/deploy.md`, оно ставит базовую инфру. Этот гайд предполагает что shared-host уже готов.

## Prerequisites

- [ ] Host с Docker, reachable через SSH из GitHub Actions
- [ ] Caddy на host'е (поставлен при bootstrap yobalog'а)
- [ ] DNS: A-record `yobabox.3po.su` (или твой домен) → public IP сервера
- [ ] **Public GitHub repo + public GHCR package.** Workflow использует ephemeral `${{ secrets.GITHUB_TOKEN }}` с `permissions: packages: write` — PAT не нужен. После первого push в GHCR один раз flip visibility package'а в публичный: GitHub profile → Packages → yobabox-web → Package settings → Change visibility.

## Step 1 — GitHub Actions secrets

Settings → Secrets and variables → Actions → New repository secret.

### Обязательные (deploy без них упадёт)

| Secret | Что | Как сгенерировать |
|---|---|---|
| `DEPLOY_HOST` | hostname или IP сервера | например `yoba-apps.3po.su` (тот же что yobaconf/yobalog) |
| `DEPLOY_USERNAME` | SSH user в `docker` group | например `stdray` |
| `DEPLOY_PASSWORD` | SSH password | (SSH сервер должен иметь `PasswordAuthentication yes`) |
| `YOBABOX_ADMIN_USERNAME` | имя для login в `/Login` | например `admin` |
| `YOBABOX_ADMIN_PASSWORD_HASH` | PBKDF2-SHA256 hash пароля | см. Step 2 ниже |
| `YOBABOX_MASTER_KEY` | AES-256 master key для config-secrets шифрования | `openssl rand -base64 32` локально, копировать вывод |

### Опциональные (для observability — можно отложить)

| Secret | Что | Когда нужно |
|---|---|---|
| `YOBABOX_SELFLOG_URL` | URL Seq-protocol ingest endpoint'а куда yobabox шлёт собственные логи | См. ниже про self-hosted vs external |
| `YOBABOX_SELFLOG_KEY` | API key для того endpoint'а | то же |
| `YOBABOX_OTEL_ENABLED` | `true` / `false` | Если хочешь OTel traces |
| `YOBABOX_OTEL_ENDPOINT` | full OTLP traces endpoint URL | URL ДОЛЖЕН быть endpoint целиком — OTel exporter не добавляет path |

### Куда yobabox шлёт телеметрию

Yobabox умеет хостить **сам себя** для observability — он экспонирует `/api/ingest/clef` (Seq protocol для логов) и `/v1/logs` + `/v1/traces` (OTLP). То есть destination — это либо external yobalog/Seq, либо сам yobabox.

**Option A — self-hosted (yobabox шлёт в себя):**
```
YOBABOX_SELFLOG_URL  = http://localhost:8080/api/ingest/clef
YOBABOX_SELFLOG_KEY  = <ApiKey с scope logs:ingest для $system project — выдать через UI после первого старта>
YOBABOX_OTEL_ENABLED = true
YOBABOX_OTEL_ENDPOINT = http://localhost:8080/v1/traces
```
`localhost:8080` — yobabox-web listener внутри своего же контейнера. Плюс: unified observability через один UI, нет внешних зависимостей. Минус: если yobabox упадёт — некуда писать диагностику падения; и слабый риск feedback-loop (логи о приёме логов).

**Option B — external yobalog:**
```
YOBABOX_SELFLOG_URL  = https://yobalog.3po.su/compat/seq
YOBABOX_SELFLOG_KEY  = <ApiKey из yobalog admin UI с ingest scope>
YOBABOX_OTEL_ENABLED = true
YOBABOX_OTEL_ENDPOINT = https://yobalog.3po.su/v1/traces
```
Плюс: yobalog независим — увидишь логи даже падения yobabox'а. Минус: external dependency, два места смотреть.

**Option C — выключено:**
Не set'ить `YOBABOX_SELFLOG_*` → Seq__SelfLog не включится, логи только в `docker logs yobabox`. Аналогично с OTel.

Для первого деплоя рекомендую **Option C** (минимум move parts), потом **Option A** когда подтвердишь что yobabox стабильно работает.

## Step 2 — сгенерировать admin password hash

Локально (нужен .NET 10 SDK):

```bash
dotnet run --project src/YobaBox.Web -- --hash-password 'your-strong-password'
```

Вывод: `pbkdf2$100000$<salt>$<hash>`. Скопировать **всю** строку (включая `pbkdf2$...`) в GitHub secret `YOBABOX_ADMIN_PASSWORD_HASH`.

Plaintext **никуда** больше не сохраняется — hash PBKDF2-SHA256 безопасно класть в secret, plaintext нет.

**Альтернатива** (если нет локального .NET): запустить контейнер с тем же бинарём:
```bash
docker run --rm ghcr.io/stdray/yobabox:latest --hash-password 'your-strong-password'
```
(Требует чтобы образ уже был опубликован — для первого деплоя локальный `dotnet run` неизбежен.)

## Step 3 — добавить Caddy fragment

SSH на сервер, открыть central Caddyfile:

```bash
sudo nano /etc/caddy/Caddyfile
```

Append блок из `infra/Caddyfile.fragment` репы. Port 8083 — yobabox slot из shared allocation:

```caddy
yobabox.3po.su {
    # Container publishes to 127.0.0.1:8083 (shared-host convention: yobaconf=8081, yobalog=8082, yobabox=8083).
    reverse_proxy 127.0.0.1:8083 {
        flush_interval -1
    }

    encode gzip zstd

    log {
        output file /var/log/caddy/yobabox.access.log {
            roll_size 50mb
            roll_keep 5
        }
        format json
    }
}
```

Заменить `yobabox.3po.su` на реальный домен если другой.

Validate + reload:
```bash
sudo caddy validate --config /etc/caddy/Caddyfile
sudo systemctl reload caddy
```

Caddy получит TLS-cert автоматически на первый HTTPS-запрос (Let's Encrypt) — manual certbot не нужен.

## Step 4 — подготовить data directory

Container монтирует `/opt/yobabox/data` с host'а для SQLite + DataProtection keys:

```bash
sudo mkdir -p /opt/yobabox/data
sudo chown 1654:1654 /opt/yobabox/data    # uid chiseled `app` user'а
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
1. **`publish` job** — Cake DockerPush target: build образа + DockerSmoke + push в `ghcr.io/stdray/yobabox:<FullSemVer>`.
2. **`deploy` job** — SSH на host, `docker pull/stop/rm/run` нового container'а с port `127.0.0.1:8083:8080`, volume `/opt/yobabox/data:/app/data`, всеми env vars из secrets.

Force-push на tag намеренно — `deploy` это "moving pointer" tag. Каждый push логируется в Actions tab вместе с SHA.

## Step 6 — verify deploy

Из любого места:

```bash
curl https://yobabox.3po.su/health
# → {"status":"healthy"}

curl https://yobabox.3po.su/version
# → {"semVer":"0.1.X-ci.N+<sha>","shortSha":"abc1234","commitDate":"..."}
```

Открыть `https://yobabox.3po.su/Login` в браузере → форма sign-in. Login с username + password из Step 2. Должен попасть на dashboard `/ui/$system`.

## Step 7 (опционально) — self-log

yobabox умеет писать собственные ILogger calls через Seq protocol — либо в себя (Option A), либо в external yobalog (Option B). См. секцию "Куда yobabox шлёт телеметрию" выше.

**Self-hosted setup (Option A):**
1. После первого деплоя — login в `/Login`, открыть `/ui/admin/sys/projects/$system` (или `/ui/admin/ws/$system/projects/...`), найти service `yobabox-web` или создать.
2. Issue ApiKey с scope `logs:ingest` — plaintext шоу один раз, скопировать.
3. Set GitHub secrets:
   - `YOBABOX_SELFLOG_URL` = `http://localhost:8080/api/ingest/clef`
   - `YOBABOX_SELFLOG_KEY` = plaintext из шага 2
4. Следующий `deploy` tag push подхватит. Логи появятся в `/ui/$system/yobabox-web/logs` (через KQL).

**External yobalog setup (Option B):**
1. В yobalog admin UI (`https://yobalog.3po.su/admin/api-keys`) создать API key с ingest scope. Copy plaintext.
2. Set GitHub secrets:
   - `YOBABOX_SELFLOG_URL` = `https://yobalog.3po.su/compat/seq`
   - `YOBABOX_SELFLOG_KEY` = plaintext
3. Deploy.

Troubleshoot:
- `docker logs yobabox` — startup error от Seq provider'а?
- API key scope/visibility — неправильный workspace = silent 403 на ingest
- Manual probe (Option B): `curl -X POST https://yobalog.3po.su/compat/seq/api/events/raw -H "X-Seq-ApiKey: <key>" -H "Content-Type: application/vnd.serilog.clef" --data '{"@t":"2026-05-29T00:00:00Z","@l":"Information","@m":"probe"}'` → 201

## Step 8 (опционально) — OpenTelemetry traces

Yobabox emit'ит spans (ASP.NET Core root + ingestion pipeline + KQL queries + Data exec/query). Destination — либо own OTLP endpoint, либо external collector.

**Self-hosted (Option A):**
- `YOBABOX_OTEL_ENABLED` = `true`
- `YOBABOX_OTEL_ENDPOINT` = `http://localhost:8080/v1/traces`

**External yobalog (Option B):**
- `YOBABOX_OTEL_ENABLED` = `true`
- `YOBABOX_OTEL_ENDPOINT` = `https://yobalog.3po.su/v1/traces`

После deploy spans видны в yobabox или yobalog waterfall UI (зависит куда указал). `YOBABOX_OTEL_ENABLED` должен быть **literal** `true` (не `1`, не `True`).

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
| yobabox | 8083 | yobabox.3po.su |
| (free) | 8084+ | для будущих |

При добавлении нового сервиса:
1. Зарезервировать port в **всех** репозиториях `infra/Caddyfile.fragment` (header table sync'ить руками)
2. DNS A-record для нового субдомена
3. Append Caddyfile block; reload
4. Deploy через свой `deploy` tag flow

## Что лежит в `/opt/yobabox/data/`

После первого старта container создаст внутри:
- `yobabox.db` — main SQLite (Workspaces, Projects, Services, ApiKeys, Settings, ShareLinks, etc.)
- `keys/` — ASP.NET DataProtection keys (для cookies, antiforgery)
- `logs/{projectKey}.db` — per-project LogDb (когда Features:Logging=true)
- `config/{workspaceKey}.db` — per-workspace ConfigDb (когда Features:Config=true)
- `db/{projectKey}/{dbName}.db` — pet's DataDbs (когда Features:Data=true)

Резервная копия: достаточно `tar czf yobabox-data-$(date +%Y%m%d).tar.gz /opt/yobabox/data` после `docker stop yobabox` (для consistency SQLite WAL). После — `docker start yobabox`.

## Troubleshooting

**Container не стартует** → `docker logs yobabox`:
- `unable to open database file` → `/opt/yobabox/data` не chown'ен на 1654:1654
- `BaseUrl is required` / `ApiKey is required` от YobaBoxConfigProvider → не должно быть на первом деплое (yobabox не использует свой Config-client на себе; см. invariant "No YobaBox self-config через ConfigModule")
- `Admin__Username is required` / login form не принимает credentials → проверь secrets `YOBABOX_ADMIN_USERNAME` + `YOBABOX_ADMIN_PASSWORD_HASH`

**Caddy 502 Bad Gateway** при открытии `yobabox.3po.su`:
- Container не работает: `docker ps -a | grep yobabox` — exited?
- Port не пробрасывается: `ss -tlnp | grep 8083` — должен слушать loopback
- Caddyfile fragment не reload'нут: `sudo systemctl reload caddy`

**Login принимает но redirect на `/Login` снова**:
- DataProtection keys не персистятся: `docker exec yobabox ls /app/data/keys` пусто
- Вероятно `/opt/yobabox/data` не mount'нут или chown неправильный
