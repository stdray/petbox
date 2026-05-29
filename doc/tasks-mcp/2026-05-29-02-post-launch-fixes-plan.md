---
timestamp: 2026-05-29T12:00:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: update
target_file: ~/.claude/plans/noble-sniffing-bear.md
---

## What
Переписал session-план `noble-sniffing-bear.md`: вместо завершённого Data-module плана
(Wave 1 реализован) — новый план «PetBox post-launch fixes» из шести workstream'ов: WS1 cookie
persistence, WS2 sys-level user CRUD, WS3 bootstrap-admin lockdown, WS4 `/v1/conf` compat
endpoint, WS5 перенос kpvotes-ts на petbox (config + data через plain HTTP), WS6 agentic
keys (Phase 27).

## Why
После первого деплоя petbox.3po.su пользователь поднял пять проблем (`/plan` с 5 пунктами):
нет sys-level управления пользователями; bootstrap-admin не отключается после создания реального
админа (как было в yobaconf); кука логина не переживает закрытие браузера; published config-клиенты
бьют в legacy `/v1/conf` которого нет в petbox; временные agentic-ключи только спроектированы
(Phase 27), кода нет. Discovery подтвердил факты, пользователь выбрал scope (все пять) и подход к
config-gap (добавить `/v1/conf` на сервер).

## Args
Plan sections: Context (5 issues), Reference facts, шесть WS с конкретными файлами/правками,
sequencing, critical files, verification. Ключевые находки: `Login.cshtml.cs:77` без
`AuthenticationProperties`; Smart-схема форвардит на ApiKey только при `X-Api-Key`; клиенты шлют
`X-YobaConf-ApiKey` и ждут bulk-resolve с template'ами flat/dotnet/envvar/envvar-deep; в petbox
нет ни `/v1/conf`, ни template-шейпинга, ни `ApiKey.ExpiresAt`.

## Outcome
План утверждён пользователем (ExitPlanMode approved). Реализация начинается с WS1.
