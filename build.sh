#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

TARGET="Default"
CONFIGURATION="Release"
QUIET=0
EXTRA_ARGS=()

for ARG in "$@"; do
	case "$ARG" in
		--target=*) TARGET="${ARG#*=}" ;;
		--configuration=*) CONFIGURATION="${ARG#*=}" ;;
		--quiet) QUIET=1 ;;
		--ci) TARGET="CI" ;;
		*) EXTRA_ARGS+=("$ARG") ;;
	esac
done

dotnet tool restore >/dev/null

# Cake's GitVersionRunner uses Tool<T> which probes PATH for `dotnet-gitversion`.
# Local tools (.config/dotnet-tools.json) aren't on PATH — they're invoked as
# `dotnet gitversion`, which Tool<T> doesn't try. Install as a global tool and
# put ~/.dotnet/tools on PATH so the runner finds the binary directly.
if ! command -v dotnet-gitversion >/dev/null 2>&1; then
	dotnet tool install --global GitVersion.Tool --version 6.4.0 >/dev/null 2>&1 || \
	dotnet tool update --global GitVersion.Tool --version 6.4.0 >/dev/null 2>&1 || true
fi
export PATH="$PATH:$HOME/.dotnet/tools"

if [ "$QUIET" -eq 1 ]; then
	dotnet run build.cs \
		--target="$TARGET" \
		--configuration="$CONFIGURATION" \
		"${EXTRA_ARGS[@]}" 2>&1 | grep -E '^(Totals|Task|Error|Warning|[0-9]+ (passed|failed))'
	exit "${PIPESTATUS[0]}"
fi

if [ ${#EXTRA_ARGS[@]} -gt 0 ]; then
	dotnet run build.cs --target="$TARGET" --configuration="$CONFIGURATION" "${EXTRA_ARGS[@]}"
else
	dotnet run build.cs --target="$TARGET" --configuration="$CONFIGURATION"
fi
