#!/usr/bin/env bash
# Soak sampler: every SAMPLE_INTERVAL_SECONDS, append one row to OUTPUT_CSV
# describing the host process. Designed to be cheap (~milliseconds per sample)
# so it doesn't perturb what it's measuring.
#
# Required env:
#   HOST_PID              PID of the B3.Exchange.Host process under load
#   OUTPUT_CSV            Path where samples are appended
#   DURATION_SECONDS      Total runtime of the sampler loop
# Optional env:
#   SAMPLE_INTERVAL_SECONDS  Default 30
#   METRICS_URL              If set, scrape this Prometheus endpoint and
#                            extract a few interesting counters per sample.
set -euo pipefail

: "${HOST_PID:?HOST_PID required}"
: "${OUTPUT_CSV:?OUTPUT_CSV required}"
: "${DURATION_SECONDS:?DURATION_SECONDS required}"
SAMPLE_INTERVAL_SECONDS="${SAMPLE_INTERVAL_SECONDS:-30}"
METRICS_URL="${METRICS_URL:-}"

mkdir -p "$(dirname "$OUTPUT_CSV")"
echo "ts_unix,uptime_s,rss_kb,vm_kb,threads,fd_count,established_total,suspended_total,reaped_total,throttle_accepted_total,throttle_rejected_total" > "$OUTPUT_CSV"

start_ts="$(date +%s)"
end_ts=$((start_ts + DURATION_SECONDS))

scrape_counter() {
    # $1: prom name; emit value or 0 if missing.
    local name="$1"
    if [[ -z "$METRICS_URL" ]]; then echo 0; return; fi
    curl -fs --max-time 2 "$METRICS_URL" 2>/dev/null \
        | awk -v n="$name" '$1 == n { print $2; found=1; exit } END { if (!found) print 0 }'
}

while true; do
    now="$(date +%s)"
    if (( now >= end_ts )); then break; fi
    if ! kill -0 "$HOST_PID" 2>/dev/null; then
        echo "host PID $HOST_PID is gone — aborting sampler" >&2
        exit 2
    fi

    uptime_s=$(( now - start_ts ))

    rss_kb=$(awk '/^VmRSS:/ {print $2}' "/proc/$HOST_PID/status" 2>/dev/null || echo 0)
    vm_kb=$(awk '/^VmSize:/ {print $2}' "/proc/$HOST_PID/status" 2>/dev/null || echo 0)
    threads=$(awk '/^Threads:/ {print $2}' "/proc/$HOST_PID/status" 2>/dev/null || echo 0)
    fd_count=$(ls "/proc/$HOST_PID/fd" 2>/dev/null | wc -l)

    est=$(scrape_counter exch_session_established_total)
    sus=$(scrape_counter exch_session_suspended_total)
    rea=$(scrape_counter exch_session_reaped_total)
    thr_a=$(scrape_counter exch_throttle_accepted_total)
    thr_r=$(scrape_counter exch_throttle_rejected_total)

    echo "${now},${uptime_s},${rss_kb},${vm_kb},${threads},${fd_count},${est},${sus},${rea},${thr_a},${thr_r}" >> "$OUTPUT_CSV"

    sleep "$SAMPLE_INTERVAL_SECONDS"
done

echo "sampler finished after ${DURATION_SECONDS}s ($(wc -l < "$OUTPUT_CSV") lines including header)"
