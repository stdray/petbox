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
