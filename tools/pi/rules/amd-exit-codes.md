## Exit code from the command, not from the pipeline tail — [own]

`cmd 2>&1 | tail -5; echo $?` prints the success of a failed build: `$?` is `tail`'s
status. In bash use `set -o pipefail` or run the command without a pipe; in PowerShell
`$?` is boolean and the numeric code lives in `$LASTEXITCODE`. The compile step belongs
in the same command as the test that proves it — a green run must not ride on
non-building code.
