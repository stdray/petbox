## Tool usage — [opencode]

- Use `read` for file contents, `grep` for content search, `find` for path search, `ls`
  for listing — not `cat`, `rg`/`grep`, `find`/`ls` in bash. Reach for `bash` when a tool
  cannot do the job, not as the default route.
- Use `edit` for targeted changes to existing files; `write` for new files or a full
  rewrite. Do not create a file that the task did not ask for.
- Make independent tool calls in parallel; dependent ones sequentially.
- Use absolute file paths when you refer to files.
- Do not chain shell commands with `;` or `&&` just to print separators — the output
  becomes noise for the reader.

## Two shells — pick by the job

You have `bash` (Git Bash on Windows) and `powershell` (`pwsh.exe`). This is a choice per
command, not a session setting:

- **`bash`** — POSIX tooling and everything the repo already scripts that way: `grep`,
  `sed`, `awk`, pipelines, `find`, `./build.sh`, `git` output parsing. A repo command
  documented as `./build.sh --target=X` runs in bash unchanged.
- **`powershell`** — Windows-native work: `Get-*`/`Start-Process`, services, the registry,
  event logs, paths with backslashes and spaces, anything whose only documentation is
  PowerShell. A repo command documented as `./build.ps1 -Target X` is native here.

Rules that follow from the split: do not translate a working bash command into cmdlets
just because PowerShell exists, and do not run bash to reach something Windows exposes
only through PowerShell. The exit-code rule differs per shell — see
`amd-exit-codes` (`$?` is `tail`'s status in a bash pipeline; `$LASTEXITCODE` in PowerShell).

## Project rules outrank these

Before acting in a repository, read the nearest `AGENTS.md` (or `CLAUDE.md`) walking up
from your working directory. It is project canon and outranks everything in this skill
set; when the two disagree, follow the project file and say so in your report.
