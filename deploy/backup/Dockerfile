# Tiny wrapper over the official restic image: adds the backup script + a cron loop.
# restic/restic is Alpine-based, so busybox `crond` and `/bin/sh` are already present.
# curl is NOT preinstalled — needed for the optional Telegram alert / healthcheck ping.
FROM restic/restic:0.18.1

RUN apk add --no-cache curl

COPY backup.sh /usr/local/bin/backup.sh
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
# Shared egress path selection, sourced by BOTH scripts — without it the sidecar boots and then
# fails at the first probe with ".: egress.sh: not found". Asserted by BackupSidecarScriptTests.
COPY egress.sh /usr/local/bin/egress.sh
RUN chmod +x /usr/local/bin/backup.sh /usr/local/bin/entrypoint.sh /usr/local/bin/egress.sh

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
