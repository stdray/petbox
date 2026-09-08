# Windows-поддержка: Codex CLI vs Qwen Code

Клоны: `D:/my/prj/_analysis/repos/{codex,qwen-code}`. ПО ДОКАМ/коду; живых прогонов на
Windows не было (только Docker=Linux, здесь нулевые). GitHub-выборки — через `gh api`,
живой сетевой доступ был; даты свежие на 2026-09-08.

## Codex CLI (openai/codex)

**Вывод: нативно с оговорками.** Реальная нативная поддержка есть и не свежая
косметика: `codex-rs/**` содержит 246 файлов с `cfg(windows)`/`cfg(target_os =
"windows")`, есть отдельный процессор `app-server/src/request_processors/
windows_sandbox_processor.rs` и бинарь `codex-windows-sandbox-setup` — то есть
песочница на Windows не «просто выключена», у неё свой setup-flow (`windows_sandbox_
readiness`, `windows_sandbox_setup_start`). CI (`rust-ci-full.yml`) гоняет
`tests_windows_x64` и `tests_windows_arm64` на self-hosted `windows-x64`/`windows-arm64`
рядом с ubuntu/macos, и оба обязательны в итоговом gate-джобе (строки ~538-569) — это
не витрина, а полноценная нога матрицы.

**Но docs/install.md прямо противоречит этому:** таблица требований гласит
`Windows 11 **via WSL2**` (`docs/install.md:7`), тогда как README тут же даёт нативный
PowerShell-инсталлятор (`irm .../install.ps1 | iex`, README.md:22-26) и весь
Rust-код целится на `x86_64-pc-windows-msvc`/`aarch64-pc-windows-msvc` (не WSL). Вывод:
документация не обновлена вслед за кодом — практический риск для владельца в том, что
официальный текст говорит «WSL2», а фактическая поставка — нативный `.exe`/MSI и
собственная песочница. Не проверено, какой путь реально рекомендуют в актуальном
GitHub Release notes (там ссылка на releases page, не в клоне).

**Shell на Windows:** `cmd.exe`/`powershell.exe` нативно, без обязательного bash —
`core/exec_policy.rs:77-114` явно разрешает/классифицирует и `cmd.exe /c`, и
`powershell.exe -Command`, есть отдельный `COMSPEC`-fallback (`exec-server/src/server/
processor.rs:613`, `hooks/engine/command_runner.rs:430`). Реэкспортируется даже
Unicode-путь через `.bat`-алиасы (`arg0/src/lib.rs`, тест `windows_batch_alias_
preserves_unicode_executable_paths`) — признак, что путями на Windows реально
занимались, не просто «POSIX и авось сработает».

**Open issues (не полностью WSL/CLI-специфично):** репозиторий openai/codex — это
монолит CLI + Desktop-app + Computer Use, не только CLI. Заголовочный поиск
`"Windows" in:title` даёт 2918 открытых (из 15985 всего) — подавляющее большинство
это Desktop/ChatGPT-приложение и Computer Use (`cua.getApp is not a function`,
«Pets do not respond to clicks», зависания композера), НЕ относится к CLI-харнессу.
После ручной фильтрации Desktop/ChatGPT/Computer-Use/Pets остаётся ~15 CLI/sandbox-
релевантных, свежих на 2026-09-08: `#42264` (Windows setup loop, sandbox-setup.exe
падает молча при elevation), `#40158` (codex exec на Windows попадает в
restricted-token sandbox вопреки `windows.sandbox="elevated"`), `#37599`
(codex-code-mode-host открывает видимое окно Windows Terminal), `#41018` (экранирование
спецсимволов на Windows). Свежесть подтверждена (все updated_at = 2026-09-08) —
не устаревший полугодовой тред. Открытых issues с точным лейблом `windows`: 8.

## Qwen Code (QwenLM/qwen-code)

**Вывод: нативно, с честно задокументированной нестабильностью CI.** README даёт
нативный PowerShell-инсталлятор (README.md:40-44), без WSL-требования в докax
вообще. Shell — нативный `cmd.exe`/`powershell.exe`/`pwsh.exe`
(`packages/core/src/tools/shell.ts:5060-5137`, есть даже проза для модели про
экранирование `&|<>^` в cmd.exe и что одинарные кавычки в cmd.exe не работают
как quote — это код, писавшийся человеком, знающим Windows-shell не понаслышке).
Платформенных веток (`process.platform === 'win32'`) — 60 файлов в `packages/`,
включая PTY-хост (`cli/src/agent-view/pty-host.ts`), пути (`serve/fs/paths.ts`),
символические ссылки, drive-letter casing в MCP approval keys.

**CI-матрица — честный и редкий случай самораскрытия:** `ci.yml` действительно
гоняет `test_windows` (`windows-latest`/self-hosted `ecs-win`, windows-2022 fallback)
и `test_macos`, НО с 2026-хх (см. комментарий `ci.yml:26-56`) их триггер `pull_request`
**выключен** — за 18 часов до отключения Windows-нога дала «13 failures и 0 successes»
на PR, устойчивый паттерн падений на путях и симлинках (`read_many_files`,
`releaseWorktree` через symlink-предка, review-worktree по SHA-256). Сейчас обе ноги
живут только на `schedule` (ночной прогон на main) и потенциальном `merge_group`
(очередь не включена с 2026-07-02) — то есть Windows реально тестируется, но НЕ на
каждом PR, только раз в сутки на main. Это прямое признание нестабильности от самого
проекта, не домысел из issues.

**CHANGELOG:** 89 упоминаний "Windows" (vs 0 у codex — там changelog это просто ссылка
на releases page, содержательного сравнения не даёт). Постоянный поток
Windows-специфичных фиксов: verbatim path prefixes, drive-letter casing, IME/paste,
process tree reaping на POSIX/Windows раздельно, дважды включали/выключали PR-триггер
Windows-ноги (`#9370` включили → `#10059` снова выключили — совпадает с текущим
состоянием в коде).

**Открытые issues по Windows (28 с "Windows" в заголовке, все свежие на
2026-09-08/09-05):** заметная кластеризация вокруг ConPTY/PTY-утечек — `#11303`
(347 процессов conhost.exe, ~2.8 ГБ за 12ч в VS Code Companion), `#11352` (node-pty не
закрывает ConPTY-хост при штатном выходе шелла — race до `onExit`), `#11353`
(WebTerminalRegistry держит PTY-ресурсы до 15-минутного idle-таймаута). Плюс
`#8649` (command hooks ломаются в `cmd.exe` на квочённых командах), `#8278`
(детект кодировки берёт chardet раньше системной codepage — CP-866 всегда
декодируется как windows-1252, актуально для кириллицы), `#8385` (мерцание вывода в
ConEmu/Cmder). Закрытых с "Windows" в заголовке — 60, то есть поток чинится, но
не иссякает.

## Сравнение с Claude Code (по памяти владельца, не перепроверялось в этой сессии)

Оба кандидата, в отличие от WSL-only харнесса, дают именно нативный `.exe`/PowerShell-
путь и нативный шелл — в этом смысле ближе к Claude Code, чем «поставь WSL». Разница
не в факте нативности, а в ЗРЕЛОСТИ: у codex документация (`install.md`) отстаёт от
кода и всё ещё пугает WSL2, у qwen-code нативность есть, но её собственный CI открыто
признаёт, что Windows-нога нестабильна настолько, что её сняли с PR-гейта — то есть
регресс на Windows у qwen-code может долежать в main до ночного прогона, а не
поймается до мержа, чего на практике Claude Code (судя по опыту владельца) не
демонстрирует.

## Если всё же WSL

Ни один из кандидатов не требует WSL по факту нативной поддержки — WSL всплывает
только (а) в устаревшей формулировке codex `install.md`, (б) в частных багах
Windows↔WSL peredачи проекта (`#43628`, `#41290`, `#42984` — все про смену
Agent Environment между Windows-native и WSL, что говорит о том, что даже сам codex
поддерживает ОБА режима и переключение между ними — источник багов, но не
единственный путь). Уходить в WSL целиком владельцу для этих двух харнессов
не требуется по докам/коду; цена ухода в WSL здесь не считалась отдельно, т.к.
основной вывод — нативный путь рабочий (с оговорками выше).
