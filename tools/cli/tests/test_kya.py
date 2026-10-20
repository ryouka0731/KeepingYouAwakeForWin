"""Tests for the ``kya`` CLI module (``tools/cli/kya.py``).

These exercise the pure helpers (``parse_duration``, ``_safe_iso_to_epoch``,
``_read_recent_entries``, ``_current_status``) plus the ``status`` subcommand
end-to-end via ``subprocess`` with ``APPDATA`` pointed at a temp directory.
Nothing is ever launched: ``os.startfile`` and ``tasklist`` are mocked.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

import kya


# --------------------------------------------------------------------------
# parse_duration
# --------------------------------------------------------------------------

@pytest.mark.parametrize(
    "text,expected",
    [
        ("45s", 45),
        ("30m", 1800),
        ("2h", 7200),
        ("90", 90),               # bare integer = seconds
        ("0090", 90),
        ("1h30m", 5400),
        ("1H30M", 5400),          # case-insensitive
        ("  2h  ", 7200),         # surrounding whitespace tolerated
        ("1h 30m", 5400),         # internal whitespace tolerated
        ("2m30s", 150),
        ("1h1m1s", 3661),
    ],
)
def test_parse_duration_valid(text, expected):
    assert kya.parse_duration(text) == expected


@pytest.mark.parametrize("text", ["abc", "", "   ", "1x", "-5", "h", "m30", "0s", "0m0s"])
def test_parse_duration_invalid_raises_valueerror(text):
    with pytest.raises(ValueError):
        kya.parse_duration(text)


def test_parse_duration_bare_zero_is_zero_seconds():
    # Documented contract: a bare digit string is taken verbatim as seconds,
    # so "0" returns 0 (the non-positive guard only covers the suffix forms).
    assert kya.parse_duration("0") == 0


# --------------------------------------------------------------------------
# _safe_iso_to_epoch
# --------------------------------------------------------------------------

def test_safe_iso_to_epoch_valid_utc_z():
    # 2024-01-02T03:04:05Z  ->  known epoch
    got = kya._safe_iso_to_epoch("2024-01-02T03:04:05Z")
    expected = datetime(2024, 1, 2, 3, 4, 5, tzinfo=timezone.utc).timestamp()
    assert got == pytest.approx(expected)


def test_safe_iso_to_epoch_valid_with_offset():
    got = kya._safe_iso_to_epoch("2024-01-02T03:04:05+00:00")
    expected = datetime(2024, 1, 2, 3, 4, 5, tzinfo=timezone.utc).timestamp()
    assert got == pytest.approx(expected)


@pytest.mark.parametrize("bad", ["not-a-date", "", "2024-13-99", "garbage", "T::"])
def test_safe_iso_to_epoch_malformed_returns_none(bad):
    assert kya._safe_iso_to_epoch(bad) is None


@pytest.mark.parametrize("bad", [None, 123, 12.5, [], {}, object()])
def test_safe_iso_to_epoch_non_string_returns_none(bad):
    assert kya._safe_iso_to_epoch(bad) is None


# --------------------------------------------------------------------------
# _read_recent_entries / _current_status — synthetic activity.jsonl
# --------------------------------------------------------------------------

def _write_log(tmp_path: Path, lines) -> Path:
    p = tmp_path / "activity.jsonl"
    with p.open("w", encoding="utf-8") as f:
        for line in lines:
            if isinstance(line, (dict, list)):
                f.write(json.dumps(line) + "\n")
            else:
                f.write(str(line) + "\n")
    return p


def _log_path_in(appdata: Path) -> Path:
    return appdata / "KeepingYouAwake" / "activity.jsonl"


@pytest.fixture(autouse=True)
def _appdata(tmp_path, monkeypatch):
    """Point %APPDATA% at a temp dir so a real log on the dev machine stays out."""
    appdata = tmp_path / "AppData" / "Roaming"
    monkeypatch.setenv("APPDATA", str(appdata))
    return appdata


@pytest.fixture(autouse=True)
def _kya_running(monkeypatch):
    """Pretend KYA is running; tests about a stopped app override this."""
    monkeypatch.setattr(kya, "_kya_is_running", lambda: True)


@pytest.fixture()
def patched_log(_appdata):
    """Return a callable that installs a synthetic log where kya looks for it."""

    def _install(lines):
        log_dir = _log_path_in(_appdata).parent
        log_dir.mkdir(parents=True, exist_ok=True)
        return _write_log(log_dir, lines)

    return _install


def test_read_recent_entries_missing_file():
    assert not kya._activity_log_path().exists()
    assert kya._read_recent_entries(10) == []


def test_read_recent_entries_empty_file(patched_log):
    patched_log([])
    assert kya._read_recent_entries(10) == []


def test_read_recent_entries_newest_first_and_skips_garbage(patched_log):
    patched_log([
        {"startedAt": "2024-01-01T00:00:00Z", "n": 1},
        "this is not json",
        "",
        {"startedAt": "2024-01-02T00:00:00Z", "n": 2},
        {"startedAt": "2024-01-03T00:00:00Z", "n": 3},
    ])
    entries = kya._read_recent_entries(50)
    assert [e["n"] for e in entries] == [3, 2, 1]


@pytest.mark.parametrize("limit", [0, -1, -100])
def test_read_recent_entries_nonpositive_limit_returns_empty(patched_log, limit):
    patched_log([{"startedAt": "2024-01-01T00:00:00Z"}])
    assert kya._read_recent_entries(limit) == []


def test_read_recent_entries_maxlen_bounds_result(patched_log):
    patched_log([{"n": i} for i in range(20)])
    entries = kya._read_recent_entries(5)
    assert len(entries) == 5
    # newest-first => last 5 written, reversed
    assert [e["n"] for e in entries] == [19, 18, 17, 16, 15]


def test_current_status_inactive_when_empty(patched_log):
    patched_log([])
    assert kya._current_status() == {"active": False}


def test_current_status_inactive_when_last_entry_closed(patched_log):
    patched_log([
        {"startedAt": "2024-01-01T00:00:00Z", "endedAt": "2024-01-01T01:00:00Z", "source": "url"},
    ])
    assert kya._current_status() == {"active": False}


def test_current_status_active_open_entry_with_future_duration(patched_log):
    started = datetime.now(timezone.utc) - timedelta(seconds=10)
    patched_log([
        {
            "startedAt": started.isoformat().replace("+00:00", "Z"),
            "source": "url-scheme",
            "requestedDuration": 3600,
        },
    ])
    s = kya._current_status()
    assert s["active"] is True
    assert s["source"] == "url-scheme"
    assert s["requestedDuration"] == 3600
    # ~3590 s remaining; allow slack for test runtime.
    assert s["remainingSeconds"] == pytest.approx(3600 - 10, abs=30)
    assert s["fireDate"] is not None


def test_current_status_active_open_entry_no_duration(patched_log):
    patched_log([
        {"startedAt": "2024-01-01T00:00:00Z", "source": "url"},  # no requestedDuration
    ])
    s = kya._current_status()
    assert s["active"] is True
    assert s["remainingSeconds"] is None
    assert s["fireDate"] is None


def test_current_status_open_entry_with_bad_started(patched_log):
    # Malformed startedAt must not crash; still reported active.
    patched_log([
        {"startedAt": "not-a-real-timestamp", "source": "url", "requestedDuration": 600},
    ])
    s = kya._current_status()
    assert s["active"] is True
    assert s["remainingSeconds"] is None
    assert s["fireDate"] is None


def test_current_status_picks_most_recent_open_entry(patched_log):
    # newest entry (last line) is open => active even though an older one is closed
    patched_log([
        {"startedAt": "2024-01-01T00:00:00Z", "endedAt": "2024-01-01T01:00:00Z", "source": "old"},
        {"startedAt": "2024-01-02T00:00:00Z", "source": "new"},
    ])
    s = kya._current_status()
    assert s["active"] is True
    assert s["source"] == "new"


# --------------------------------------------------------------------------
# `kya status [--json]` subcommand via subprocess (APPDATA -> temp dir)
# --------------------------------------------------------------------------

_KYA_DIR = str(Path(kya.__file__).resolve().parent)

# Runs `kya.main` in a child interpreter with `_kya_is_running` stubbed, so
# the result doesn't depend on `tasklist` or KYA running on this host (and
# works the same on Windows, macOS, and Linux CI runners).
_CHILD = (
    "import sys; sys.path.insert(0, sys.argv[1]); import kya; "
    "kya._kya_is_running = lambda: sys.argv[2] == '1'; "
    "raise SystemExit(kya.main(sys.argv[3:]))"
)


def _run_status(appdata: Path, *args, kya_running: bool = True):
    # Copy the real environment and only override APPDATA so the child keeps
    # PATH / SYSTEMROOT / etc. — a bare env dict breaks Python on Windows.
    env = os.environ.copy()
    env["APPDATA"] = str(appdata)
    return subprocess.run(
        [sys.executable, "-c", _CHILD, _KYA_DIR, "1" if kya_running else "0", "status", *args],
        capture_output=True,
        text=True,
        env=env,
        timeout=10,  # a regressed/blocking `kya status` must not hang CI
    )


def _write_child_log(appdata: Path, entry: dict) -> None:
    log = _log_path_in(appdata)
    log.parent.mkdir(parents=True)
    log.write_text(json.dumps(entry) + "\n", encoding="utf-8")


def test_status_subprocess_inactive_exit_1(_appdata):
    # Empty / nonexistent log => inactive => exit 1.
    res = _run_status(_appdata)
    assert res.returncode == 1
    assert res.stdout.strip() == "inactive"


def test_status_subprocess_json_inactive(_appdata):
    res = _run_status(_appdata, "--json")
    assert res.returncode == 1
    payload = json.loads(res.stdout)
    assert payload == {"active": False}


def test_status_subprocess_json_active_exit_0(_appdata):
    started = datetime.now(timezone.utc) - timedelta(seconds=5)
    _write_child_log(_appdata, {
        "startedAt": started.isoformat().replace("+00:00", "Z"),
        "source": "user",
        "requestedDuration": 1800,
    })
    res = _run_status(_appdata, "--json")
    assert res.returncode == 0
    payload = json.loads(res.stdout)
    assert payload["active"] is True
    assert payload["source"] == "user"
    assert payload["requestedDuration"] == 1800
    assert isinstance(payload["remainingSeconds"], (int, float))


def test_status_subprocess_human_active(_appdata):
    _write_child_log(_appdata, {"startedAt": "2024-01-01T00:00:00Z", "source": "schedule",
                                "requestedDuration": -1})
    res = _run_status(_appdata)
    assert res.returncode == 0
    assert res.stdout.strip() == "active (source=schedule) indefinite"


def test_status_subprocess_open_entry_but_kya_not_running(_appdata):
    _write_child_log(_appdata, {"startedAt": "2024-01-01T00:00:00Z", "source": "user"})
    res = _run_status(_appdata, "--json", kya_running=False)
    assert res.returncode == 1
    assert json.loads(res.stdout) == {"active": False}


# --------------------------------------------------------------------------
# Regression tests (autoresearch debug findings)
# --------------------------------------------------------------------------

def test_current_status_ignores_stale_open_entry_behind_newer_closed(patched_log):
    # KYA quit (or crashed) mid-session, leaving an open entry; a later
    # session was opened and closed normally. Only one session can be open
    # at a time, so the newest entry decides the state.
    patched_log([
        {"startedAt": "2024-01-01T00:00:00Z", "source": "watched-app"},
        {"startedAt": "2024-01-02T00:00:00Z", "endedAt": "2024-01-02T01:00:00Z",
         "source": "user", "requestedDuration": 3600, "endedReason": "expired"},
    ])
    assert kya._current_status() == {"active": False}


def test_current_status_inactive_when_kya_not_running(patched_log, monkeypatch):
    # Crash/kill leaves the newest entry open until KYA relaunches and
    # repairs it; meanwhile nothing keeps the PC awake.
    patched_log([{"startedAt": "2024-01-01T00:00:00Z", "source": "user"}])
    monkeypatch.setattr(kya, "_kya_is_running", lambda: False)
    assert kya._current_status() == {"active": False}


# --------------------------------------------------------------------------
# URL-scheme commands — `_open_url` captured, nothing launched
# --------------------------------------------------------------------------

@pytest.fixture()
def captured_urls(monkeypatch):
    urls: list[str] = []
    monkeypatch.setattr(kya, "_open_url", lambda url: urls.append(url) or 0)
    return urls


def test_activate_without_duration_requests_indefinite(captured_urls):
    # `activate` with no query falls back to the app's default duration,
    # not indefinite; the CLI must send an explicit seconds=0.
    assert kya.main(["activate"]) == 0
    assert captured_urls == ["keepingyouawake:///activate?seconds=0"]


@pytest.mark.parametrize("arg,seconds", [("30m", 1800), ("2h", 7200), ("90", 90), ("1h30m", 5400)])
def test_activate_with_duration(captured_urls, arg, seconds):
    assert kya.main(["activate", arg]) == 0
    assert captured_urls == [f"keepingyouawake:///activate?seconds={seconds}"]


def test_activate_until_end_of_day(captured_urls):
    assert kya.main(["activate", "--until-end-of-day"]) == 0
    assert len(captured_urls) == 1
    prefix = "keepingyouawake:///activate?seconds="
    assert captured_urls[0].startswith(prefix)
    assert 0 < int(captured_urls[0][len(prefix):]) <= 86400


def test_deactivate_and_toggle(captured_urls):
    assert kya.main(["deactivate"]) == 0
    assert kya.main(["toggle"]) == 0
    assert captured_urls == ["keepingyouawake:///deactivate", "keepingyouawake:///toggle"]


def test_command_propagates_open_url_failure(monkeypatch):
    monkeypatch.setattr(kya, "_open_url", lambda url: 2)
    assert kya.main(["toggle"]) == 2


# --------------------------------------------------------------------------
# _open_url — os.startfile (Windows) mocked
# --------------------------------------------------------------------------

def test_open_url_uses_startfile(monkeypatch):
    opened = []
    monkeypatch.setattr(os, "startfile", lambda url: opened.append(url), raising=False)
    assert kya._open_url("keepingyouawake:///toggle") == 0
    assert opened == ["keepingyouawake:///toggle"]


def test_open_url_startfile_oserror_is_nonzero(monkeypatch, capsys):
    def _boom(url):
        raise OSError("No application is associated with the specified file")

    monkeypatch.setattr(os, "startfile", _boom, raising=False)
    assert kya._open_url("keepingyouawake:///toggle") != 0
    assert "keepingyouawake:///toggle" in capsys.readouterr().err


def test_open_url_without_startfile_errors(monkeypatch, capsys):
    # Non-Windows: no os.startfile -> stderr message + non-zero, nothing spawned.
    monkeypatch.delattr(os, "startfile", raising=False)
    monkeypatch.setattr(kya.subprocess, "run", lambda *a, **k: pytest.fail("must not spawn"))
    assert kya._open_url("keepingyouawake:///toggle") != 0
    assert "Windows" in capsys.readouterr().err


# --------------------------------------------------------------------------
# _activity_log_path — %APPDATA% resolution
# --------------------------------------------------------------------------

def test_activity_log_path_uses_appdata(tmp_path, monkeypatch):
    monkeypatch.setenv("APPDATA", str(tmp_path / "Roaming"))
    assert kya._activity_log_path() == tmp_path / "Roaming" / "KeepingYouAwake" / "activity.jsonl"


@pytest.mark.parametrize("value", [None, ""])
def test_activity_log_path_falls_back_to_home_roaming(tmp_path, monkeypatch, value):
    if value is None:
        monkeypatch.delenv("APPDATA", raising=False)
    else:
        monkeypatch.setenv("APPDATA", value)
    monkeypatch.setattr(kya.Path, "home", classmethod(lambda cls: tmp_path))
    assert kya._activity_log_path() == (
        tmp_path / "AppData" / "Roaming" / "KeepingYouAwake" / "activity.jsonl"
    )


# --------------------------------------------------------------------------
# _kya_is_running — `tasklist` mocked
# --------------------------------------------------------------------------

def _real_is_running():
    # The autouse fixture stubs kya._kya_is_running; reach the real one.
    return _ORIGINAL_IS_RUNNING()


_ORIGINAL_IS_RUNNING = kya._kya_is_running


def _fake_tasklist(monkeypatch, stdout="", exc=None):
    calls = []

    def _run(cmd, *a, **k):
        calls.append((cmd, k))
        if exc is not None:
            raise exc
        return subprocess.CompletedProcess(cmd, 0, stdout=stdout, stderr="")

    monkeypatch.setattr(kya.subprocess, "run", _run)
    return calls


def test_kya_is_running_true_when_tasklist_lists_process(monkeypatch):
    calls = _fake_tasklist(
        monkeypatch, stdout='"KeepingYouAwake.exe","4242","Console","1","52,340 K"\r\n'
    )
    assert _real_is_running() is True
    cmd, kwargs = calls[0]
    assert cmd == ["tasklist", "/FI", "IMAGENAME eq KeepingYouAwake.exe", "/NH", "/FO", "CSV"]
    assert kwargs["capture_output"] is True
    assert kwargs["check"] is False
    assert kwargs["timeout"] == 5


def test_kya_is_running_case_insensitive(monkeypatch):
    _fake_tasklist(monkeypatch, stdout='"keepingyouawake.EXE","4242","Console","1","52,340 K"\r\n')
    assert _real_is_running() is True


def test_kya_is_running_false_when_no_match(monkeypatch):
    _fake_tasklist(
        monkeypatch,
        stdout="INFO: No tasks are running which match the specified criteria.\r\n",
    )
    assert _real_is_running() is False


@pytest.mark.parametrize(
    "exc",
    [FileNotFoundError("tasklist"), OSError("boom"), subprocess.TimeoutExpired("tasklist", 5)],
)
def test_kya_is_running_true_when_tasklist_unavailable(monkeypatch, exc):
    # Can't tell -> don't hide a real session.
    _fake_tasklist(monkeypatch, exc=exc)
    assert _real_is_running() is True
