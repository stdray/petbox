# PetBox disaster recovery — restore on a clean host

What the offsite backup contains (restic repos, encrypted with `RESTIC_PASSWORD`):

- **compact** → Cloudflare R2 (`s3:$R2_S3_ENDPOINT/$R2_BUCKET/compact`)
- **full** → FirstVDS S3 (`s3:$FVDS_S3_ENDPOINT/$FVDS_BUCKET/full`)

Both repos now carry the **same** contents — data only. The tags are historical.

Each snapshot holds the newest consistent `backups/*-auto` set **plus** `data/keys/` (the
DataProtection key ring). `PETBOX_MASTER_KEY` is **not** in the backup — it lives in GitHub
Actions secrets / KeePass and is required to decrypt config secrets.

In the set: `petbox.db`, `deploy.db`, `db/**` (user schemas + user data), `memory/**`,
`tasks/**`, `sessions/**`, `config/**`.

**Not** in the set: `logs/**`. PetBox's own logs are telemetry, not data — a restore comes
back with **no log/metric history**, and that is intentional (owner decision 2026-07-11: logs
were 79% of every set). Nothing else is excluded.

## What you need before starting

- A clean host with Docker.
- From KeePass: `RESTIC_PASSWORD`, `PETBOX_MASTER_KEY`, the admin username + password hash,
  and the S3 keys for whichever repo you restore from.
- The petbox image tag to run (any recent `ghcr.io/<repo>:<tag>`).

## 1. Restore the data volume

Pick either repo (same contents; use whichever is reachable). Example with R2:

```sh
export RESTIC_PASSWORD='<from KeePass>'
export AWS_ACCESS_KEY_ID='<R2 access key>'
export AWS_SECRET_ACCESS_KEY='<R2 secret key>'
export AWS_DEFAULT_REGION=us-east-1
REPO="s3:https://<account>.r2.cloudflarestorage.com/petbox/compact"   # or the FirstVDS repo

restic -r "$REPO" snapshots                 # find the snapshot to restore
restic -r "$REPO" restore latest --target /tmp/pb-restore
```

The restored tree contains `backups/<stamp>-auto/` (the module DB set) and `keys/`. Lay the
live data dir out from it:

```sh
sudo mkdir -p /opt/petbox/data
snap="$(ls -1d /tmp/pb-restore/opt/petbox/data/backups/*-auto/ | sort | tail -1)"
sudo cp -a "$snap"/. /opt/petbox/data/          # module DBs (config, tasks, memory, sessions, deploy.db, db/, …)
sudo cp -a /tmp/pb-restore/opt/petbox/data/keys /opt/petbox/data/keys
sudo chown -R 1654:1654 /opt/petbox/data        # chiseled `app` uid the container writes as
```

## 2. Bring PetBox up

Provide the deploy env (same vars the CI deploy uses) and start compose:

```sh
cd deploy   # this repo checked out on the host, or copy deploy/ over
export PETBOX_IMAGE='ghcr.io/<repo>:<tag>'
export ADMIN_USER='<admin>' ADMIN_PASS_HASH='<hash>' MASTER_KEY='<PETBOX_MASTER_KEY>'
export OTEL_ENABLED=false OTEL_ENDPOINT=self
# backup sidecar vars (RESTIC_PASSWORD, R2_*, FVDS_*) so it resumes pushing
docker compose up -d --build
curl -fsS http://localhost:8083/health && echo OK
```

## 3. Repoint DNS

Point `petbox.3po.su` at the new host once `/health` is green. Clients with the config disk
cache ride through the gap; MCP/UI resume when DNS propagates.

## Verify a backup is restorable (drill)

`restic check` runs after every push, but to prove an end-to-end restore, periodically:

```sh
restic -r "$REPO" restore latest --target /tmp/pb-drill --include '*/config'
sqlite3 /tmp/pb-drill/.../config/main.db 'pragma integrity_check;'
```

## Failure alerting (optional)

The backup sidecar can send a Telegram alert on leg failure (and a "recovered" message
once it's healthy again), plus ping a dead-man's-switch URL on success. Both are entirely
optional and env-gated: unset `TELEGRAM_BOT_TOKEN`/`TELEGRAM_CHAT_ID`/`HEALTHCHECK_URL`
(see `deploy/backup/secrets.local.env.example`) means no alerting, no heartbeat — the
backups themselves are unaffected either way.
