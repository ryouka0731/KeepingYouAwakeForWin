#!/usr/bin/env python3
"""kya — command-line driver for KeepingYouAwake for Windows.

Examples:
    kya activate 30m
    kya activate 2h
    kya activate           # indefinite
    kya activate --until-end-of-day
    kya deactivate
    kya toggle
    kya status
    kya status --json

The CLI doesn't poke at KYA's internals: it drives the running app
through the URL scheme it registers (`keepingyouawake:///…`), and reads
`%APPDATA%\\KeepingYouAwake\\activity.jsonl` for state. KYA must be
installed; the URL scheme will launch it on demand if it isn't running.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timezone, timedelta
from pathlib import Path

APP_EXECUTABLE = "KeepingYouAwake.exe"


def _activity_log_path() -> Path:
    """The activity log KYA writes: `%APPDATA%\\KeepingYouAwake\\activity.jsonl`.

    Falls back to `~\\AppData\\Roaming` when `APPDATA` isn't set. Resolved on
    every call so tests (and a changed environment) are honoured.
    """
    appdata = os.environ.get("APPDATA")
    base = Path(appdata) if appdata else Path.home() / "AppData" / "Roaming"
    return base / "KeepingYouAwake" / "activity.jsonl"


def _kya_is_running() -> bool:
    """Whether a KeepingYouAwake.exe process exists. An open log entry while
    it isn't running was left by a crash or kill (KYA repairs it on next launch)."""
    try:
        result = subprocess.run(
            ["tasklist", "/FI", f"IMAGENAME eq {APP_EXECUTABLE}", "/NH", "/FO", "CSV"],
            capture_output=True,
            text=True,
            errors="replace",
            check=False,
            timeout=5,
            # Don't flash a console window when run from a windowless host.
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.SubprocessError):
        return True  # can't tell — don't hide a real session
    return f'"{APP_EXECUTABLE}"'.lower() in (result.stdout or "").lower()


def _open_url(url: str) -> int:
    """Drive KYA via its URL scheme. Returns 0 on success, non-zero on failure."""
    if not hasattr(os, "startfile"):
        print(
            f"kya: cannot open {url}: the keepingyouawake:// URL scheme is only "
            "available on Windows (os.startfile is missing on this platform).",
            file=sys.stderr,
        )
        return 2
    try:
        os.startfile(url)
    except OSError as exc:
        print(
            f"kya: cannot open {url}: {exc}. Is KeepingYouAwake for Windows installed?",
            file=sys.stderr,
        )
        return 1
    return 0


def parse_duration(text: str) -> int:
    """Parse `30m`, `2h`, `45s`, `90` (= 90 sec), `1h30m`. Returns seconds."""
    text = text.strip().lower()
    if not text:
        raise ValueError("empty duration")
    if text.isdigit():
        return int(text)
    pattern = re.compile(r"(\d+)\s*([smh])")
    matches = pattern.findall(text)
    if not matches:
        raise ValueError(f"can't parse duration: {text!r}")
    total = 0
    units = {"s": 1, "m": 60, "h": 3600}
    for value, unit in matches:
        total += int(value) * units[unit]
    if total <= 0:
        raise ValueError(f"non-positive duration: {text!r}")
    return total


def _read_recent_entries(limit: int = 50) -> list[dict]:
    """Newest-first up to `limit`. Bounded deque keeps memory O(limit)."""
    from collections import deque
    log_path = _activity_log_path()
    if not log_path.exists():
        return []
    if int(limit) <= 0:
        return []
    tail: deque[dict] = deque(maxlen=int(limit))
    with log_path.open("r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                tail.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return list(reversed(tail))


def _safe_iso_to_epoch(s) -> float | None:
    """Tolerant ISO-8601 → epoch seconds. Returns None on malformation."""
    if not isinstance(s, str):
        return None
    try:
        return datetime.fromisoformat(s.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return None


def _current_status() -> dict:
    # Only one session is open at a time, so the newest entry decides. An
    # older entry without endedAt was left behind by a quit or crash, and
    # so is the newest one while KYA isn't running.
    if not _kya_is_running():
        return {"active": False}
    entries = _read_recent_entries(1)
    for entry in entries:
        if not entry.get("endedAt"):
            started = entry.get("startedAt")
            requested = entry.get("requestedDuration")
            fire_iso = None
            remaining = None
            started_epoch = _safe_iso_to_epoch(started)
            if started_epoch is not None and isinstance(requested, (int, float)) and requested > 0:
                fire_dt = started_epoch + float(requested)
                now = datetime.now(timezone.utc).timestamp()
                remaining = max(0.0, fire_dt - now)
                fire_iso = datetime.fromtimestamp(fire_dt, tz=timezone.utc).isoformat()
            return {
                "active": True,
                "source": entry.get("source", "unknown"),
                "startedAt": started,
                "requestedDuration": requested,
                "fireDate": fire_iso,
                "remainingSeconds": remaining,
            }
    return {"active": False}


def _format_status_human(s: dict) -> str:
    if not s["active"]:
        return "inactive"
    parts = [f"active (source={s.get('source','?')})"]
    if s.get("requestedDuration") in (None, -1, -1.0):
        parts.append("indefinite")
    elif s.get("remainingSeconds") is not None:
        rem = int(s["remainingSeconds"])
        h, r = divmod(rem, 3600)
        m, sec = divmod(r, 60)
        parts.append(f"remaining={h}:{m:02d}:{sec:02d}")
    return " ".join(parts)


def cmd_activate(args: argparse.Namespace) -> int:
    if args.until_end_of_day:
        now = datetime.now()
        tomorrow = now.replace(hour=0, minute=0, second=0, microsecond=0) + timedelta(days=1)
        seconds = int((tomorrow - now).total_seconds())
        return _open_url(f"keepingyouawake:///activate?seconds={seconds}")
    if args.duration is None:
        # Explicit seconds=0: an activate URL without a duration uses the
        # app's default duration, which is not indefinite.
        return _open_url("keepingyouawake:///activate?seconds=0")
    seconds = parse_duration(args.duration)
    return _open_url(f"keepingyouawake:///activate?seconds={seconds}")


def cmd_deactivate(_args: argparse.Namespace) -> int:
    return _open_url("keepingyouawake:///deactivate")


def cmd_toggle(_args: argparse.Namespace) -> int:
    return _open_url("keepingyouawake:///toggle")


def cmd_status(args: argparse.Namespace) -> int:
    s = _current_status()
    if args.json:
        sys.stdout.write(json.dumps(s) + "\n")
    else:
        sys.stdout.write(_format_status_human(s) + "\n")
    return 0 if s["active"] else 1   # exit code = test-friendly


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="kya", description="Drive KeepingYouAwake for Windows.")
    sub = parser.add_subparsers(dest="cmd", required=True)

    pa = sub.add_parser("activate", help="Start a keep-awake session.")
    pa.add_argument("duration", nargs="?", default=None,
                    help="duration like 30m / 2h / 45s / 90 / 1h30m. Omit for indefinite.")
    pa.add_argument("--until-end-of-day", action="store_true",
                    help="Activate until next local midnight.")
    pa.set_defaults(func=cmd_activate)

    pd = sub.add_parser("deactivate", help="Stop the current session.")
    pd.set_defaults(func=cmd_deactivate)

    pt = sub.add_parser("toggle", help="Toggle activate/deactivate.")
    pt.set_defaults(func=cmd_toggle)

    ps = sub.add_parser("status", help="Print current state.")
    ps.add_argument("--json", action="store_true", help="Emit machine-readable JSON.")
    ps.set_defaults(func=cmd_status)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
