#!/usr/bin/env bash
# DROP 11 (Performance and Scale): the Linux counterpart to scripts/benchmark.ps1.
#
# scripts/benchmark.ps1 measures R17 ("competes with 7-Zip") and R18 (solid-7z O(n^2) extraction)
# on Windows, against the native 7Z backend. Neither is reachable from this build machine: 7z.dll
# is not present here (L33/L34), so noba-ar cannot open or create .7z at all on Linux. What IS
# reachable, and what this script measures instead, is the managed ZIP provider — the format every
# machine this engine runs on actually has — compared against the native `7z` CLI's own ZIP support
# (present on this image at /usr/bin/7z), so "competes with 7-Zip" gets a real Linux-side answer
# for the one format both sides can open.
#
# Times create, list, search, extract and test at escalating entry counts, and samples each noba-ar
# process's peak resident set size (/proc/<pid>/status VmHWM) while it runs — there is no `time -v`
# on this image, so this is the portable substitute. One machine, one run each, no warm-up: the
# numbers below are real but indicative, exactly like scripts/benchmark.ps1 says about its own.
set -uo pipefail
cd "$(dirname "$0")/.."

ENTRY_COUNTS="${1:-200 2000 20000}"
ENTRY_BYTES="${2:-4096}"
CONFIG="${3:-Release}"

CLI_DLL="src/NOBA.Archive.Cli/bin/$CONFIG/net10.0/noba-ar.dll"
SEVENZIP="$(command -v 7z || true)"

echo "NOBA Archive - Linux benchmark (DROP 11)"
echo "  config   : $CONFIG"
echo "  entries  : $ENTRY_COUNTS   payload $ENTRY_BYTES bytes each"
echo "  7-Zip    : ${SEVENZIP:-not found}"
echo

if [ ! -f "$CLI_DLL" ]; then
  echo "Building $CLI_DLL ..."
  if ! dotnet build src/NOBA.Archive.Cli -c "$CONFIG" --nologo -v:q; then
    echo "BUILD FAILED - not running any measurement."
    exit 1
  fi
fi

WORK_ROOT="$(mktemp -d)"
trap 'rm -rf "$WORK_ROOT"' EXIT

STAMP="$(date +%Y%m%d-%H%M%S)"
OUT_DIR="artifacts"
mkdir -p "$OUT_DIR"
CSV="$OUT_DIR/benchmark-linux-$STAMP.csv"
echo "entries,noba_create_s,noba_create_peak_kb,noba_list_s,noba_search_s,noba_extract_s,noba_extract_peak_kb,noba_test_s,7z_create_s,7z_list_s,7z_extract_s,7z_test_s" > "$CSV"

# Runs a command in the background, waits for it, and prints "<elapsed-seconds> <peak-rss-kb>" on
# stdout. Peak RSS is sampled from /proc while the process is alive, since VmHWM is only readable
# until the process exits.
measure() {
  local start end peak sample pid
  start=$(date +%s.%N)
  "$@" >/dev/null 2>&1 &
  pid=$!
  peak=0
  while kill -0 "$pid" 2>/dev/null; do
    sample=$(grep -m1 VmHWM "/proc/$pid/status" 2>/dev/null | grep -o '[0-9]\+' || true)
    if [ -n "$sample" ] && [ "$sample" -gt "$peak" ]; then
      peak=$sample
    fi
    sleep 0.02
  done
  wait "$pid" 2>/dev/null
  end=$(date +%s.%N)
  awk -v s="$start" -v e="$end" -v p="$peak" 'BEGIN { printf "%.3f %d", (e - s), p }'
}

for count in $ENTRY_COUNTS; do
  source="$WORK_ROOT/src-$count"
  mkdir -p "$source"
  payload=$(head -c "$ENTRY_BYTES" /dev/zero | tr '\0' 'A')
  for ((i = 0; i < count; i++)); do
    printf '%s' "$payload" > "$source/file-$i.txt"
  done

  archive="$WORK_ROOT/noba-$count.zip"
  dest="$WORK_ROOT/out-$count"

  read -r createS createPeak <<< "$(measure dotnet "$CLI_DLL" create "$archive" "$source")"
  read -r listS _ <<< "$(measure dotnet "$CLI_DLL" list "$archive" --json)"
  read -r searchS _ <<< "$(measure dotnet "$CLI_DLL" search "$archive" --ext .txt --sort size --json)"
  read -r extractS extractPeak <<< "$(measure dotnet "$CLI_DLL" extract "$archive" -o "$dest")"
  read -r testS _ <<< "$(measure dotnet "$CLI_DLL" test "$archive")"

  sevenCreateS="" sevenListS="" sevenExtractS="" sevenTestS=""
  if [ -n "$SEVENZIP" ]; then
    sevenArchive="$WORK_ROOT/7z-$count.zip"
    sevenDest="$WORK_ROOT/out-7z-$count"
    read -r sevenCreateS _ <<< "$(measure "$SEVENZIP" a -tzip -mx=1 "$sevenArchive" "$source")"
    read -r sevenListS _ <<< "$(measure "$SEVENZIP" l -ba "$sevenArchive")"
    read -r sevenExtractS _ <<< "$(measure "$SEVENZIP" x -y "-o$sevenDest" "$sevenArchive")"
    read -r sevenTestS _ <<< "$(measure "$SEVENZIP" t "$sevenArchive")"
  fi

  echo "$count,$createS,$createPeak,$listS,$searchS,$extractS,$extractPeak,$testS,$sevenCreateS,$sevenListS,$sevenExtractS,$sevenTestS" >> "$CSV"

  printf '%6s entries  create %7ss (peak %7s KB)  list %7ss  search %7ss  extract %7ss (peak %7s KB)  test %7ss' \
    "$count" "$createS" "$createPeak" "$listS" "$searchS" "$extractS" "$extractPeak" "$testS"
  if [ -n "$SEVENZIP" ]; then
    printf '   [7z: create %ss, list %ss, extract %ss, test %ss]' "$sevenCreateS" "$sevenListS" "$sevenExtractS" "$sevenTestS"
  fi
  echo
done

echo
echo "Caveats, so these numbers are not read as more than they are:"
echo "  - one machine (this build container), one run each, no warm-up, no repetition."
echo "  - ZIP only: 7z.dll is not present on this build machine, so the native 7Z/RAR backend and"
echo "    R17/R18's own solid-archive question are unmeasured here - see scripts/benchmark.ps1 for"
echo "    that instrument, which still needs a Windows run."
echo "  - 7-Zip writes files itself; NOBA routes every byte through the hardened extraction writer"
echo "    (path validation, handle re-verification, guarded stream). Some of the gap is that work."
echo "  - peak RSS is sampled every 20ms from /proc, not read from a kernel-reported maximum; a very"
echo "    short-lived process can be under-sampled. Treat it as indicative, not exact."
echo
echo "Full results: $CSV"
