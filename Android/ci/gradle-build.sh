#!/usr/bin/env bash
# Builds the OLI Mobile APKs; on failure the log is turned into check annotations.
set -uo pipefail
cd "$(dirname "$0")/../OLI-Mobile" || exit 1
LOG="${TMPDIR:-/tmp}/oli-gradle.log"

build() {
    gradle --no-daemon "$@" > "$LOG" 2>&1
    local rc=$?
    if [ $rc -ne 0 ]; then
        python3 ../ci/annotate.py "$LOG"
        exit $rc
    fi
    tail -3 "$LOG"
}

build assembleDebug
build assembleRelease "-POLI_DEFAULT_SERVER=${OLI_DEFAULT_SERVER:-}" "-POLI_DIRECTORY_URL=${OLI_DIRECTORY_URL:-}"
