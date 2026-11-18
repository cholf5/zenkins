#!/usr/bin/env bash
# Summarise one `dotnet test` log so a single run is enough to tell where the time went.
#
# Q-023's AC-5: a slow run must be distinguishable from a regression without reading three logs by hand.
# The runner is Microsoft.Testing.Platform, so per-test timings need `--output detailed`; the VSTest form
# `--logger "console;verbosity=detailed"` is rejected (exit 5). Durations appear as 417ms / 1s / 3s 029ms.
#
# Usage: scripts/suite-duration.sh <log-file> [topN]
#
# Exit: 0 summarised, 1 no per-test timings in the log (reported, never guessed), 2 file missing.
set -euo pipefail

log=${1:?usage: suite-duration.sh <log-file> [topN]}
top=${2:-10}

[[ -f $log ]] || { echo "suite-duration: no such file: $log" >&2; exit 2; }

work=$(mktemp)
trap 'rm -f "$work"' EXIT

# One TSV row per timed test: milliseconds, status, fully qualified name.
awk '
function to_ms(v,   total, parts, n, i, p) {
    total = 0; n = split(v, parts, /[ ]+/)
    for (i = 1; i <= n; i++) {
        p = parts[i]
        if (p ~ /^[0-9]+ms$/)      { sub(/ms$/, "", p); total += p + 0 }
        else if (p ~ /^[0-9]+s$/)  { sub(/s$/,  "", p); total += (p + 0) * 1000 }
        else if (p ~ /^[0-9]+m$/)  { sub(/m$/,  "", p); total += (p + 0) * 60000 }
    }
    return total
}
/^[ \t]*(passed|failed|skipped)[ \t]+/ {
    status = $1
    line = $0
    sub(/^[ \t]*(passed|failed|skipped)[ \t]+/, "", line)
    if (match(line, / \([0-9]+(ms|s|m)( [0-9]+(ms|s|m))*\)$/)) {
        dur  = substr(line, RSTART + 2, RLENGTH - 3)
        name = substr(line, 1, RSTART - 1)
        printf "%d\t%s\t%s\n", to_ms(dur), status, name
    }
}' "$log" > "$work"

if [[ ! -s $work ]]; then
    cat >&2 <<'MSG'
suite-duration: this log carries no per-test timings, so there is nothing to aggregate.

  Re-run with `--output detailed` (Microsoft.Testing.Platform). The VSTest form
  `--logger "console;verbosity=detailed"` is rejected with exit 5.

  Refusing to print zeros or an empty table: a summary of no data is worse than no summary.
MSG
    exit 1
fi

total_ms=$(awk -F'\t' '{ s += $1 } END { print s + 0 }' "$work")
tests=$(wc -l < "$work" | tr -d ' ')
failed=$(awk -F'\t' '$2 == "failed"' "$work" | wc -l | tr -d ' ')

echo "SUMMARY"
echo "  tests with timings : $tests (failed $failed)"
grep -E '^ +(total|succeeded|failed|skipped|duration):' "$log" |
    sed 's/^ */  run summary        : /' || true
awk -v ms="$total_ms" 'BEGIN { printf "  summed test time   : %.1f s (the denominator for the shares below)\n", ms / 1000 }'

if (( failed > 0 )); then
    echo "  failed:"
    awk -F'\t' '$2 == "failed" { print "    " $3 }' "$work"
fi

echo
echo "SLOWEST AREAS (share of summed test time, not of wall clock - collections run in parallel)"
# Area = the third segment of the namespace: Zenkins.Tests.<Area>
awk -F'\t' '
{
    n = split($3, seg, ".")
    area = (seg[1] == "Zenkins" && seg[2] == "Tests" && seg[3] != "") ? seg[3] : seg[1]
    ms[area] += $1; n_of[area]++
}
END { for (a in ms) printf "%d\t%s\t%d\n", ms[a], a, n_of[a] }' "$work" |
    sort -rn | head -n "$top" |
    awk -v total="$total_ms" -F'\t' '
    BEGIN { printf "  %-26s %6s %9s %8s\n", "area", "tests", "sum", "share" }
    { printf "  %-26s %6d %8.1fs %7.1f%%\n", $2, $3, $1 / 1000, (total > 0 ? $1 * 100 / total : 0) }'

echo
echo "SLOWEST TESTS (top $top)"
sort -rn "$work" | head -n "$top" |
    awk -F'\t' '{ printf "  %8.1fs  %-7s %s\n", $1 / 1000, $2, $3 }'
