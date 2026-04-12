#!/usr/bin/env python3
"""BotNexus log search tool — search gateway logs efficiently."""

import argparse
import os
import re
import sys
from datetime import datetime, timedelta
from pathlib import Path


def resolve_profile() -> Path:
    custom = os.environ.get("BOTNEXUS_HOME")
    if custom:
        return Path(custom)
    return Path.home() / ".botnexus"


def resolve_logs_dir(profile: Path) -> Path:
    return profile / "logs"


def get_log_files(logs_dir: Path, last_hours: int | None) -> list[Path]:
    """Get log files, optionally filtered to last N hours."""
    if not logs_dir.exists():
        return []

    files = sorted(logs_dir.glob("botnexus-*.log"), reverse=True)

    if last_hours is not None and last_hours > 0:
        cutoff = datetime.now() - timedelta(hours=last_hours)
        filtered = []
        for f in files:
            # Parse timestamp from filename: botnexus-yyyyMMddHH.log
            stem = f.stem  # botnexus-2026041212
            try:
                ts_str = stem.replace("botnexus-", "")
                file_time = datetime.strptime(ts_str, "%Y%m%d%H")
                if file_time >= cutoff:
                    filtered.append(f)
            except ValueError:
                filtered.append(f)  # include if can't parse
        return filtered

    return files[:24]  # default: last 24 files (24 hours)


def search_logs(
    files: list[Path],
    pattern: str,
    level: str | None = None,
    context: int = 0,
    max_results: int = 50,
):
    """Search log files for a pattern."""
    regex = re.compile(pattern, re.IGNORECASE)
    level_filter = f"[{level.upper()}]" if level else None
    results = []

    for log_file in reversed(files):  # oldest first
        try:
            lines = log_file.read_text(encoding="utf-8", errors="replace").splitlines()
        except OSError as e:
            print(f"  Warning: Could not read {log_file}: {e}", file=sys.stderr)
            continue

        for i, line in enumerate(lines):
            if len(results) >= max_results:
                break

            if level_filter and level_filter not in line:
                continue

            if regex.search(line):
                result_lines = []
                if context > 0:
                    start = max(0, i - context)
                    end = min(len(lines), i + context + 1)
                    for j in range(start, end):
                        prefix = ">>>" if j == i else "   "
                        result_lines.append(f"{prefix} {lines[j]}")
                else:
                    result_lines.append(line)

                results.append({
                    "file": log_file.name,
                    "line": i + 1,
                    "content": "\n".join(result_lines),
                })

        if len(results) >= max_results:
            break

    return results


def summarize_errors(files: list[Path]) -> dict:
    """Count errors and warnings across log files."""
    counts = {"INF": 0, "WRN": 0, "ERR": 0, "FTL": 0, "DBG": 0}
    error_samples = []

    for log_file in files:
        try:
            content = log_file.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue

        for level in counts:
            counts[level] += content.count(f"[{level}]")

        if counts["ERR"] > 0 or counts["FTL"] > 0:
            for line in content.splitlines():
                if "[ERR]" in line or "[FTL]" in line:
                    error_samples.append({"file": log_file.name, "line": line.strip()})
                    if len(error_samples) >= 10:
                        break

    return {"counts": counts, "error_samples": error_samples[:10]}


def main():
    parser = argparse.ArgumentParser(description="BotNexus log search tool")
    parser.add_argument("pattern", nargs="?", help="Search pattern (regex)")
    parser.add_argument("--last-hours", "-t", type=int, default=4, help="Search logs from last N hours (default: 4)")
    parser.add_argument("--level", "-l", help="Filter by log level (INF, WRN, ERR, FTL, DBG)")
    parser.add_argument("--context", "-C", type=int, default=0, help="Lines of context around matches")
    parser.add_argument("--max", "-m", type=int, default=50, help="Max results (default: 50)")
    parser.add_argument("--summary", "-s", action="store_true", help="Show error/warning summary instead of search")
    parser.add_argument("--profile", help="Override .botnexus profile path")

    args = parser.parse_args()
    profile = Path(args.profile) if args.profile else resolve_profile()
    logs_dir = resolve_logs_dir(profile)

    if not logs_dir.exists():
        print(f"Logs directory not found: {logs_dir}")
        sys.exit(1)

    files = get_log_files(logs_dir, args.last_hours)
    if not files:
        print(f"No log files found in {logs_dir} for the last {args.last_hours} hours")
        sys.exit(0)

    print(f"Scanning {len(files)} log file(s) from {logs_dir}\n")

    if args.summary:
        summary = summarize_errors(files)
        counts = summary["counts"]
        print(f"Log level counts (last {args.last_hours}h):")
        print(f"  INFO:    {counts['INF']:>6,}")
        print(f"  WARNING: {counts['WRN']:>6,}")
        print(f"  ERROR:   {counts['ERR']:>6,}")
        print(f"  FATAL:   {counts['FTL']:>6,}")
        print(f"  DEBUG:   {counts['DBG']:>6,}")

        if summary["error_samples"]:
            print(f"\nRecent errors (up to 10):")
            for sample in summary["error_samples"]:
                print(f"  [{sample['file']}] {sample['line'][:200]}")
        return

    if not args.pattern:
        print("Provide a search pattern or use --summary for an overview")
        sys.exit(1)

    results = search_logs(files, args.pattern, args.level, args.context, args.max)

    if not results:
        print(f"No matches for '{args.pattern}'" + (f" at level [{args.level.upper()}]" if args.level else ""))
        return

    print(f"Found {len(results)} match(es) for '{args.pattern}':\n")
    for r in results:
        print(f"  [{r['file']}:{r['line']}]")
        print(f"  {r['content']}")
        print()


if __name__ == "__main__":
    main()
