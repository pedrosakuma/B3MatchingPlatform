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
#
# Issue #608 follow-up: the Workstation-GC fix (PR #609) only marginally
# reduced the soak RSS slope, so RSS growth alone no longer localizes the
# problem. Two extra column groups disambiguate "managed heap actually
# grows" from "RSS grows but the managed heap doesn't" (native/off-heap
# growth — mmap'd buffers, socket buffers, page-cache-like effects, etc.):
#   - pss_anon_kb / pss_file_kb / pss_shmem_kb come from
#     /proc/$HOST_PID/smaps_rollup (Pss_Anon / Pss_File / Pss_Shmem):
#     splits resident memory by backing type.
#   - dotnet_heap_bytes is scraped from the existing
#     dotnet_total_memory_bytes gauge (GC.GetTotalMemory) already exposed
#     on METRICS_URL: the managed live-heap size at sample time.
set -euo pipefail

: "${HOST_PID:?HOST_PID required}"
: "${OUTPUT_CSV:?OUTPUT_CSV required}"
: "${DURATION_SECONDS:?DURATION_SECONDS required}"
SAMPLE_INTERVAL_SECONDS="${SAMPLE_INTERVAL_SECONDS:-30}"
METRICS_URL="${METRICS_URL:-}"

mkdir -p "$(dirname "$OUTPUT_CSV")"
echo "ts_unix,uptime_s,rss_kb,vm_kb,threads,fd_count,established_total,suspended_total,reaped_total,throttle_accepted_total,throttle_rejected_total,pss_anon_kb,pss_file_kb,pss_shmem_kb,dotnet_heap_bytes" > "$OUTPUT_CSV"

start_ts="$(date +%s)"
end_ts=$((start_ts + DURATION_SECONDS))

scrape_counter() {
    # $1: prom name; emit value or 0 if missing.
    # The `|| true` shields a transient curl failure (timeout, connection
    # refused, non-2xx via -f) from `pipefail`: without it, `set -e` would
    # abort the whole sampler loop on one bad scrape instead of falling
    # back to 0 for this sample, per the doc comment above.
    local name="$1"
    if [[ -z "$METRICS_URL" ]]; then echo 0; return; fi
    { curl -fs --max-time 2 "$METRICS_URL" 2>/dev/null || true; } \
        | awk -v n="$name" '$1 == n { print $2; found=1; exit } END { if (!found) print 0 }'
}

scrape_gauge() {
    # Same as scrape_counter but for a bare (unlabeled) gauge line, e.g.
    # "dotnet_total_memory_bytes 12345". Kept distinct in case counters and
    # gauges ever need different parsing (labeled vs bare).
    scrape_counter "$1"
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

    # smaps_rollup: fast (O(1) w.r.t. VMA count) resident-memory breakdown.
    # Falls back to 0/0/0 if the kernel or process doesn't expose it (older
    # kernels, or PID already gone between the kill -0 check and this read).
    if [[ -r "/proc/$HOST_PID/smaps_rollup" ]]; then
        pss_anon_kb=$(awk '/^Pss_Anon:/ {print $2}' "/proc/$HOST_PID/smaps_rollup" 2>/dev/null || echo 0)
        pss_file_kb=$(awk '/^Pss_File:/ {print $2}' "/proc/$HOST_PID/smaps_rollup" 2>/dev/null || echo 0)
        pss_shmem_kb=$(awk '/^Pss_Shmem:/ {print $2}' "/proc/$HOST_PID/smaps_rollup" 2>/dev/null || echo 0)
    else
        pss_anon_kb=0
        pss_file_kb=0
        pss_shmem_kb=0
    fi
    dotnet_heap_bytes=$(scrape_gauge dotnet_total_memory_bytes)

    echo "${now},${uptime_s},${rss_kb},${vm_kb},${threads},${fd_count},${est},${sus},${rea},${thr_a},${thr_r},${pss_anon_kb:-0},${pss_file_kb:-0},${pss_shmem_kb:-0},${dotnet_heap_bytes:-0}" >> "$OUTPUT_CSV"

    sleep "$SAMPLE_INTERVAL_SECONDS"
done

echo "sampler finished after ${DURATION_SECONDS}s ($(wc -l < "$OUTPUT_CSV") lines including header)"
