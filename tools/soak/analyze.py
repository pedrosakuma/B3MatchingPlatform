#!/usr/bin/env python3
"""
Soak analyzer: read the CSV produced by sample.sh and emit a markdown summary
to stdout. Exits non-zero if any threshold assertion fails.

Thresholds are intentionally lax for the first run — issue #120 explicitly
calls for establishing a baseline before tightening. They can be overridden
via env vars to make the workflow easy to tune without code changes.
"""
from __future__ import annotations

import csv
import os
import statistics
import sys
from pathlib import Path


def linear_slope(xs: list[float], ys: list[float]) -> float:
    """Ordinary least squares slope of ys vs xs. Returns 0 if degenerate."""
    n = len(xs)
    if n < 2:
        return 0.0
    mean_x = sum(xs) / n
    mean_y = sum(ys) / n
    num = sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys))
    den = sum((x - mean_x) ** 2 for x in xs)
    if den == 0:
        return 0.0
    return num / den


def main(csv_path: str) -> int:
    rss_slope_max_mb_per_h = float(os.environ.get("RSS_SLOPE_MAX_MB_PER_H", "50"))
    fd_growth_max = int(os.environ.get("FD_GROWTH_MAX", "100"))
    thread_growth_max = int(os.environ.get("THREAD_GROWTH_MAX", "20"))
    min_samples = int(os.environ.get("MIN_SAMPLES", "5"))
    # Discard the first WARMUP_SAMPLES rows from the slope calculation —
    # JIT compilation, tier-PGO, and initial multicast-socket setup cause a
    # large RSS jump in the first ~30s that would otherwise dominate the
    # slope of any short soak.
    warmup_samples = int(os.environ.get("WARMUP_SAMPLES", "4"))

    rows = list(csv.DictReader(Path(csv_path).open()))
    if len(rows) < min_samples:
        print(f"### Soak summary\n\n**FAIL** — only {len(rows)} samples (need ≥{min_samples}).")
        return 1

    times = [float(r["uptime_s"]) for r in rows]
    rss_mb = [float(r["rss_kb"]) / 1024.0 for r in rows]
    fds = [int(r["fd_count"]) for r in rows]
    threads = [int(r["threads"]) for r in rows]

    # For slope assertions, drop warmup window. Last/peak/first stats
    # still report on the whole run for visibility.
    slope_start = min(warmup_samples, max(0, len(rows) - min_samples))
    slope_times = times[slope_start:]
    slope_rss = rss_mb[slope_start:]
    slope_fds = [float(x) for x in fds[slope_start:]]
    slope_thr = [float(x) for x in threads[slope_start:]]

    duration_h = (times[-1] - times[0]) / 3600.0 if times[-1] > times[0] else 0
    rss_slope_mb_per_s = linear_slope(slope_times, slope_rss)
    rss_slope_mb_per_h = rss_slope_mb_per_s * 3600.0
    rss_first, rss_last = rss_mb[0], rss_mb[-1]
    rss_peak = max(rss_mb)
    fd_first, fd_last = fds[0], fds[-1]
    fd_peak = max(fds)
    thr_first, thr_last = threads[0], threads[-1]
    thr_peak = max(threads)

    failures: list[str] = []

    if rss_slope_mb_per_h > rss_slope_max_mb_per_h:
        failures.append(
            f"RSS slope {rss_slope_mb_per_h:.2f} MB/h exceeds threshold {rss_slope_max_mb_per_h} MB/h"
        )
    if (fd_last - fd_first) > fd_growth_max:
        failures.append(
            f"FD count grew by {fd_last - fd_first} (first={fd_first}, last={fd_last}) — exceeds {fd_growth_max}"
        )
    if (thr_last - thr_first) > thread_growth_max:
        failures.append(
            f"Thread count grew by {thr_last - thr_first} (first={thr_first}, last={thr_last}) — exceeds {thread_growth_max}"
        )

    status = "PASS" if not failures else "FAIL"
    print(f"### Soak summary — **{status}**\n")
    print(f"- Samples: **{len(rows)}** (warmup discarded from slope: {slope_start})")
    print(f"- Duration: **{duration_h*60:.1f} min** ({duration_h:.2f} h)")
    print()
    print("| metric | first | last | peak | mean | slope (post-warmup) |")
    print("|---|---|---|---|---|---|")
    print(
        f"| RSS (MB) | {rss_first:.1f} | {rss_last:.1f} | {rss_peak:.1f} | "
        f"{statistics.mean(rss_mb):.1f} | **{rss_slope_mb_per_h:+.2f} MB/h** |"
    )
    print(
        f"| FD count | {fd_first} | {fd_last} | {fd_peak} | "
        f"{statistics.mean(fds):.1f} | {linear_slope(slope_times, slope_fds) * 3600:+.2f}/h |"
    )
    print(
        f"| Threads | {thr_first} | {thr_last} | {thr_peak} | "
        f"{statistics.mean(threads):.1f} | {linear_slope(slope_times, slope_thr) * 3600:+.2f}/h |"
    )

    print()
    print("### Thresholds (override with env vars)")
    print(f"- `RSS_SLOPE_MAX_MB_PER_H` = {rss_slope_max_mb_per_h}")
    print(f"- `FD_GROWTH_MAX` = {fd_growth_max}")
    print(f"- `THREAD_GROWTH_MAX` = {thread_growth_max}")

    if failures:
        print()
        print("### Failures")
        for f in failures:
            print(f"- ❌ {f}")
        return 1

    print()
    print("All thresholds satisfied.")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("usage: analyze.py <samples.csv>", file=sys.stderr)
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
