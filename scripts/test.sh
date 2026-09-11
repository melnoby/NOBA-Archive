#!/usr/bin/env bash
# Offline test runner: the same suites scripts/test.ps1 runs, for a non-Windows build environment.
# See PHASE_1_REPORT.md for why an offline mode exists at all.
set -uo pipefail
cd "$(dirname "$0")/.."

CONFIG="${1:-Release}"

# Pin a non-UTC timezone with a DST history. The container runs UTC, where converting between an
# instant and a local wall clock is the identity - so a whole class of timezone bug is mathematically
# invisible here. Exactly one such bug shipped through 330 green tests and was caught only by the first
# Windows run: ZIP stores MS-DOS local time, and we were writing UTC into it.
# Africa/Cairo was +02:00 in 2021 and +03:00 today, so it exercises both a non-zero offset and a
# transition. Override with NOBA_TZ if a suite ever needs a different one.
export TZ="${NOBA_TZ:-Africa/Cairo}"
declare -a NAMES=(
  "UNIT TESTS (Core)"
  "UNIT TESTS (Security)"
  "UNIT TESTS (Application)"
  "UNIT TESTS (Infrastructure)"
  "UNIT TESTS (Formats)"
  "HOSTILE ARCHIVE + ROUND TRIP (SEC/INT)"
  "ARCHITECTURE TESTS"
  "CLI CONTRACT TESTS"
  "PARITY FOUNDATION"
  # Was missing while the tenth project was already being run: the suite executed and its failures
  # still set the exit code, but the summary printed nine lines and silently dropped the last one.
  "PRESENTATION TESTS"
)
declare -a PROJECTS=(
  tests/NOBA.Archive.Core.Tests
  tests/NOBA.Archive.Security.Tests
  tests/NOBA.Archive.Application.Tests
  tests/NOBA.Archive.Infrastructure.Tests
  tests/NOBA.Archive.Formats.Tests
  tests/NOBA.Archive.Integration.Tests
  tests/NOBA.Archive.Architecture.Tests
  tests/NOBA.Archive.Cli.Tests
  tests/NOBA.Archive.Parity.Tests
  tests/NOBA.Archive.Presentation.Tests
)

# Build first, always. Running --no-build against stale binaries once produced a green architecture
# suite for code that violated it - the assemblies on disk predated the change. A test run that does
# not compile what it is about to assert on is not a test run.
if ! dotnet build NOBA.Archive.sln -c "$CONFIG" -p:NobaOfflineBuild=true --nologo -v:q; then
  echo
  echo 'BUILD: FAIL - not running any suite.'
  exit 1
fi

# Keep every suite's output. A suite that fails once in seven runs and takes its output with it is
# worse than one that fails every time - it cannot be diagnosed, only argued about.
LOGDIR="artifacts/test-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$LOGDIR"

declare -a RESULTS=()
FAILED=0
for i in "${!PROJECTS[@]}"; do
  SUITE=$(basename "${PROJECTS[$i]}")
  if dotnet run --project "${PROJECTS[$i]}" -c "$CONFIG" -p:NobaOfflineBuild=true --no-build 2>&1 \
       | tee "$LOGDIR/$SUITE.log"; then
    RESULTS+=("PASS")
  else
    RESULTS+=("FAIL"); FAILED=1
  fi
done

echo
for i in "${!NAMES[@]}"; do printf '%-30s %s\n' "${NAMES[$i]}" "${RESULTS[$i]}"; done
echo
echo "Per-suite output: $LOGDIR"
echo "Timezone:         $TZ"
exit $FAILED
