## Tool usage — [opencode]

- Use `read` for file contents, `grep` for content search, `find` for path search, `ls`
  for listing — not `cat`, `rg`/`grep`, `find`/`ls` in bash. Reach for `bash` when a tool
  cannot do the job, not as the default route.
- Use `edit` for targeted changes to existing files; `write` for new files or a full
  rewrite. Do not create a file that the task did not ask for.
- **Never write a file through a shell.** `sed -i`, `perl -pi`, `python - <<EOF`,
  `cat > file <<EOF`, `echo >> file`: none of them is a substitute for `edit`/`write`.
  They carry no uniqueness check, no diff anyone can review, and a `str.replace` on text
  that did not match leaves the file untouched while the script exits 0 and prints
  success. `edit` reporting "did not match" is not a reason to reach for a script — it is
  a reason to re-`read` the file and retry with the exact fragment.
- **The one exception: content that must not enter the transcript.** A password, a token,
  anything read out of a secrets file. `edit` and `write` put the file body into the tool
  call, and the tool call IS the transcript — so there a script is right: it reads the
  secret and writes the file without echoing either. It must print nothing, the script
  source must not contain the secret, and the command must not carry it on the command line
  either (that is argv, and argv is shown and logged). Say why you did it — the next reader
  otherwise sees a `python` call where `write` belonged and files it as the habit this rule
  forbids.
- **Never print a secret.** It comes out of a file, an environment variable or a config:
  name the path or the key instead of the value, and keep it out of your answer, out of
  `echo`, out of a diff and out of a tool argument. Once printed it is in the transcript,
  and a transcript is not something you can retract from.
- A replacement bigger than a screenful — a whole section, or a paragraph with real
  newlines and quotes — is a `write` of the file, not a fight with `edit`'s exact match.
- Make independent tool calls in parallel; dependent ones sequentially.
- Use absolute file paths when you refer to files.
- Do not chain shell commands with `;` or `&&` just to print separators — the output
  becomes noise for the reader.

## Two shells — pick by the job

You have `bash` (Git Bash on Windows) and PowerShell — exposed under one tool name, `pwsh`
or `powershell`, or under both; check the tool list. **Prefer `pwsh` when both are listed**:
same tool, and the name resolved first.

- **`pwsh` / `powershell`** — use it for Windows-native work: `Get-*`/`Start-Process`,
  services, the registry, event logs, `New-Item`, paths with backslashes and spaces, anything
  whose only documentation is PowerShell. A repo command documented as
  `./build.ps1 -Target X` is native here.
- **`bash`** — POSIX tooling and everything the repo already scripts that way: `grep`,
  `sed`, `awk`, pipelines, `find`, `rg`/`fd` (both ship with pi), `./build.sh`, `git`
  output parsing. `jq` is not shipped — it is there only if the machine has it. A repo
  command documented as `./build.sh --target=X` runs in bash unchanged.

Rules that follow from the split:

- **Never invoke bare `powershell` from bash** — it may select a different engine from the
  tool. Use the PowerShell tool, or invoke `pwsh` explicitly.
- Do not translate a working bash command into cmdlets just because PowerShell exists, and
  do not run bash to reach something Windows exposes only through PowerShell. The exit-code
  rule differs per shell — see `amd-exit-codes` (`$?` is `tail`'s status in a bash pipeline;
  `$LASTEXITCODE` in PowerShell).

## Project rules outrank these

Before acting in a repository, read the nearest `AGENTS.md` (or `CLAUDE.md`) walking up
from your working directory. It is project canon and outranks everything in this skill
set; when the two disagree, follow the project file and say so in your report.
