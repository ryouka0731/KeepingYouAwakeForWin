"""Tests for the KYA MCP server module (``tools/mcp-server/kya_mcp_server.py``).

Importing the module unconditionally requires the ``mcp`` package (the module
calls ``raise SystemExit(2)`` at import time when it's missing), so the whole
module is gated behind ``pytest.importorskip("mcp")``. Within that, we test:

  * the pure helpers (``_safe_iso_to_epoch``, ``_read_recent_entries``,
    ``_current_status``) — same contracts as the CLI;
  * the ``ALLOWED_DEFAULT_KEYS`` allow-list enforcement in ``kya_set_default``
    (allowed keys answer "unsupported" on Windows — nothing is shelled out);
  * ``%APPDATA%`` log-path resolution, the ``tasklist`` running check, and
    ``_open_url`` via ``os.startfile`` — all mocked, nothing is launched;
  * the ``tools/list`` surface (6 tools with name/description/inputSchema);
  * dispatching every tool via ``call_tool`` with ``_open_url`` stubbed out,
    asserting the returned payload is JSON-serializable
    and matches the declared top-level shape.
"""

from __future__ import annotations

import asyncio
import json
import os
import subprocess
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

pytest.importorskip("mcp", reason="kya_mcp_server imports the 'mcp' package at module load")

import kya_mcp_server as srv


def _run(coro):
    return asyncio.run(coro)


# --------------------------------------------------------------------------
# Pure helpers — mirror the CLI contracts.
# --------------------------------------------------------------------------

def test_safe_iso_to_epoch_valid_and_malformed():
    expected = datetime(2024, 1, 2, 3, 4, 5, tzinfo=timezone.utc).timestamp()
    assert srv._safe_iso_to_epoch("2024-01-02T03:04:05Z") == pytest.approx(expected)
    assert srv._safe_iso_to_epoch("not-a-date") is None
    assert srv._safe_iso_to_epoch("") is None
    assert srv._safe_iso_to_epoch(None) is None
    assert srv._safe_iso_to_epoch(123) is None


def _write_log(tmp_path: Path, lines) -> Path:
    p = tmp_path / "activity.jsonl"
    with p.open("w", encoding="utf-8") as f:
        for line in lines:
            f.write((json.dumps(line) if isinstance(line, (dict, list)) else str(line)) + "\n")
    return p


_ORIGINAL_IS_RUNNING = srv._kya_is_running


@pytest.fixture(autouse=True)
def _appdata(tmp_path, monkeypatch):
    """Point %APPDATA% at a temp dir so a real log on the dev machine stays out."""
    appdata = tmp_path / "AppData" / "Roaming"
    monkeypatch.setenv("APPDATA", str(appdata))
    return appdata


@pytest.fixture(autouse=True)
def _kya_running(monkeypatch):
    """Pretend KYA is running; tests about a stopped app override this."""
    monkeypatch.setattr(srv, "_kya_is_running", lambda: True)


@pytest.fixture()
def patched_log(_appdata):
    def _install(lines):
        log_dir = _appdata / "KeepingYouAwake"
        log_dir.mkdir(parents=True, exist_ok=True)
        return _write_log(log_dir, lines)

    return _install


def test_read_recent_entries_missing_file():
    assert not srv._activity_log_path().exists()
    assert srv._read_recent_entries(10) == []


def test_read_recent_entries_empty_and_newest_first(patched_log):
    patched_log([])
    assert srv._read_recent_entries(10) == []
    patched_log([{"n": 1}, "garbage", "", {"n": 2}, {"n": 3}])
    assert [e["n"] for e in srv._read_recent_entries(50)] == [3, 2, 1]


@pytest.mark.parametrize("limit", [0, -1, -50])
def test_read_recent_entries_nonpositive_limit(patched_log, limit):
    patched_log([{"n": 1}])
    assert srv._read_recent_entries(limit) == []


def test_read_recent_entries_maxlen_bounds(patched_log):
    patched_log([{"n": i} for i in range(20)])
    entries = srv._read_recent_entries(3)
    assert len(entries) == 3
    assert [e["n"] for e in entries] == [19, 18, 17]


def test_current_status_inactive_empty(patched_log):
    patched_log([])
    assert srv._current_status() == {"active": False}


def test_current_status_inactive_closed_entry(patched_log):
    patched_log([{"startedAt": "2024-01-01T00:00:00Z", "endedAt": "2024-01-01T01:00:00Z", "source": "u"}])
    assert srv._current_status() == {"active": False}


def test_current_status_active_future_duration(patched_log):
    started = datetime.now(timezone.utc) - timedelta(seconds=10)
    patched_log([{"startedAt": started.isoformat().replace("+00:00", "Z"), "source": "u", "requestedDuration": 3600}])
    s = srv._current_status()
    assert s["active"] is True
    assert s["source"] == "u"
    assert s["remainingSeconds"] == pytest.approx(3600 - 10, abs=30)
    assert s["fireDate"] is not None


def test_current_status_active_bad_started(patched_log):
    patched_log([{"startedAt": "garbage", "source": "u", "requestedDuration": 600}])
    s = srv._current_status()
    assert s["active"] is True
    assert s["remainingSeconds"] is None
    assert s["fireDate"] is None


# --------------------------------------------------------------------------
# tools/list surface
# --------------------------------------------------------------------------

EXPECTED_TOOL_NAMES = {
    "kya_activate",
    "kya_deactivate",
    "kya_toggle",
    "kya_status",
    "kya_list_recent_sessions",
    "kya_set_default",
}


def _list_tools():
    return _run(srv.list_tools())


def test_list_tools_returns_six_with_required_keys():
    tools = _list_tools()
    assert len(tools) == 6
    names = set()
    for t in tools:
        assert t.name
        assert t.description
        # mcp Tool model exposes inputSchema; it must be a dict-ish schema.
        assert isinstance(t.inputSchema, dict)
        assert t.inputSchema.get("type") == "object"
        # round-trips through JSON (it's an MCP wire payload after all).
        json.dumps(t.model_dump())
        names.add(t.name)
    assert names == EXPECTED_TOOL_NAMES


# --------------------------------------------------------------------------
# call_tool dispatch — _open_url stubbed
# --------------------------------------------------------------------------

@pytest.fixture()
def captured_urls(monkeypatch):
    urls: list[str] = []
    # `_open_url` returns None on success (an error string on failure).
    monkeypatch.setattr(srv, "_open_url", lambda url: urls.append(url))
    return urls


def _call(name, arguments=None):
    return _run(srv.call_tool(name, arguments or {}))


def _text(result):
    # call_tool returns list[TextContent]
    assert isinstance(result, list) and result
    assert result[0].type == "text"
    return result[0].text


def test_call_kya_activate_seconds(captured_urls):
    out = json.loads(_text(_call("kya_activate", {"duration": 900})))
    assert out == {"activated": True, "request": 900}
    assert captured_urls == ["keepingyouawake:///activate?seconds=900"]


def test_call_kya_activate_indefinite(captured_urls):
    out = json.loads(_text(_call("kya_activate", {"duration": "indefinite"})))
    assert out == {"activated": True, "request": "indefinite"}
    # Without an explicit seconds=0 the app falls back to its default
    # duration, which is not indefinite.
    assert captured_urls == ["keepingyouawake:///activate?seconds=0"]


def test_call_kya_activate_until_end_of_day(captured_urls):
    out = json.loads(_text(_call("kya_activate", {"duration": "until_end_of_day"})))
    assert out["activated"] is True
    assert len(captured_urls) == 1
    assert captured_urls[0].startswith("keepingyouawake:///activate?seconds=")


def test_call_kya_deactivate(captured_urls):
    out = json.loads(_text(_call("kya_deactivate")))
    assert out == {"deactivated": True}
    assert captured_urls == ["keepingyouawake:///deactivate"]


def test_call_kya_toggle(captured_urls):
    out = json.loads(_text(_call("kya_toggle")))
    assert out == {"toggled": True}
    assert captured_urls == ["keepingyouawake:///toggle"]


def test_call_kya_status(patched_log):
    patched_log([])
    out = json.loads(_text(_call("kya_status")))
    assert out == {"active": False}


def test_call_kya_status_active(patched_log):
    started = datetime.now(timezone.utc) - timedelta(seconds=10)
    patched_log([
        {
            "startedAt": started.isoformat().replace("+00:00", "Z"),
            "source": "url-scheme",
            "requestedDuration": 3600,
        },
    ])
    out = json.loads(_text(_call("kya_status")))
    assert out["active"] is True
    assert out["source"] == "url-scheme"
    assert out["requestedDuration"] == 3600
    assert out["remainingSeconds"] == pytest.approx(3600 - 10, abs=30)
    assert out["fireDate"] is not None


def test_call_kya_list_recent_sessions(patched_log):
    patched_log([{"n": 1}, {"n": 2}, {"n": 3}])
    out = json.loads(_text(_call("kya_list_recent_sessions", {"limit": 2})))
    assert [e["n"] for e in out] == [3, 2]
    # default limit path also works
    out_default = json.loads(_text(_call("kya_list_recent_sessions")))
    assert [e["n"] for e in out_default] == [3, 2, 1]


def test_call_kya_list_recent_sessions_limit_zero(patched_log):
    patched_log([{"n": 1}])
    out = json.loads(_text(_call("kya_list_recent_sessions", {"limit": 0})))
    assert out == []


def test_call_unknown_tool():
    text = _text(_call("kya_does_not_exist"))
    assert "unknown tool" in text


# --------------------------------------------------------------------------
# ALLOWED_DEFAULT_KEYS allow-list
# --------------------------------------------------------------------------

def test_set_default_allowlist_is_nonempty_set_of_strings():
    assert isinstance(srv.ALLOWED_DEFAULT_KEYS, set)
    assert srv.ALLOWED_DEFAULT_KEYS
    assert all(isinstance(k, str) for k in srv.ALLOWED_DEFAULT_KEYS)


def test_set_default_rejects_key_not_in_allowlist(monkeypatch):
    called = []
    monkeypatch.setattr(srv.subprocess, "run", lambda *a, **k: called.append(a) or None)
    text = _text(_call("kya_set_default", {"key": "info.marcel-dierkes.KeepingYouAwake.SomethingEvil", "value": True}))
    assert "refused" in text
    assert "not in allow-list" in text
    # crucially: nothing was shelled out.
    assert called == []


@pytest.fixture
def settings_dir(tmp_path, monkeypatch):
    """Point %APPDATA% at a temp dir; returns the settings.json path."""
    monkeypatch.setenv("APPDATA", str(tmp_path))
    return tmp_path / "KeepingYouAwake" / "settings.json"


def test_set_default_writes_settings_json_and_keeps_other_keys(settings_dir, monkeypatch):
    monkeypatch.setattr(srv.subprocess, "run", lambda *a, **k: pytest.fail("must not spawn"))
    settings_dir.parent.mkdir(parents=True)
    settings_dir.write_text(json.dumps({"showNotifications": False, "MouseJiggler": False}), encoding="utf-8")

    out = json.loads(_text(_call("kya_set_default", {"key": "info.marcel-dierkes.KeepingYouAwake.MouseJigglerEnabled", "value": True})))

    assert out == {"set": True, "key": "info.marcel-dierkes.KeepingYouAwake.MouseJigglerEnabled",
                   "property": "mouseJiggler", "value": True}
    data = json.loads(settings_dir.read_text(encoding="utf-8"))
    assert data == {"showNotifications": False, "mouseJiggler": True}


def test_set_default_accepts_new_property_names_and_numbers(settings_dir):
    out = json.loads(_text(_call("kya_set_default", {"key": "cpuLoadThreshold", "value": "75"})))
    assert out["set"] is True
    assert json.loads(settings_dir.read_text(encoding="utf-8")) == {"cpuLoadThreshold": 75.0}


@pytest.mark.parametrize("key,value", [
    ("mouseJiggler", 3),
    ("mouseJiggler", "maybe"),
    ("cpuLoadThreshold", True),
    ("cpuLoadThreshold", 150),
    ("cpuLoadThreshold", "x"),
])
def test_set_default_rejects_bad_values(settings_dir, key, value):
    out = json.loads(_text(_call("kya_set_default", {"key": key, "value": value})))
    assert out["set"] is False
    assert out["error"].startswith("expected")
    assert not settings_dir.exists()


def test_set_default_reports_corrupt_settings_file(settings_dir):
    settings_dir.parent.mkdir(parents=True)
    settings_dir.write_text("[1, 2]", encoding="utf-8")
    out = json.loads(_text(_call("kya_set_default", {"key": "mouseJiggler", "value": True})))
    assert out["set"] is False
    assert settings_dir.read_text(encoding="utf-8") == "[1, 2]"


def test_current_status_ignores_stale_open_entry_behind_newer_closed(patched_log):
    # An entry left open by a quit/crash must not be reported once a newer
    # session has been opened and closed.
    patched_log([
        {"startedAt": "2024-01-01T00:00:00Z", "source": "watched-app"},
        {"startedAt": "2024-01-02T00:00:00Z", "endedAt": "2024-01-02T01:00:00Z",
         "source": "user", "requestedDuration": 3600, "endedReason": "expired"},
    ])
    assert srv._current_status() == {"active": False}


def test_current_status_inactive_when_kya_not_running(patched_log, monkeypatch):
    patched_log([{"startedAt": "2024-01-01T00:00:00Z", "source": "user"}])
    monkeypatch.setattr(srv, "_kya_is_running", lambda: False)
    assert srv._current_status() == {"active": False}


# --------------------------------------------------------------------------
# URL-opening failures surface in the tool payload
# --------------------------------------------------------------------------

@pytest.mark.parametrize(
    "tool,args,key",
    [
        ("kya_activate", {"duration": 60}, "activated"),
        ("kya_deactivate", {}, "deactivated"),
        ("kya_toggle", {}, "toggled"),
    ],
)
def test_call_reports_open_url_failure(monkeypatch, tool, args, key):
    monkeypatch.setattr(srv, "_open_url", lambda url: "cannot open: nope")
    out = json.loads(_text(_call(tool, args)))
    assert out[key] is False
    assert out["error"] == "cannot open: nope"


# --------------------------------------------------------------------------
# _open_url — os.startfile (Windows) mocked
# --------------------------------------------------------------------------

def test_open_url_uses_startfile(monkeypatch):
    opened = []
    monkeypatch.setattr(os, "startfile", lambda url: opened.append(url), raising=False)
    assert srv._open_url("keepingyouawake:///toggle") is None
    assert opened == ["keepingyouawake:///toggle"]


def test_open_url_startfile_oserror_returns_error(monkeypatch, capsys):
    def _boom(url):
        raise OSError("No application is associated with the specified file")

    monkeypatch.setattr(os, "startfile", _boom, raising=False)
    err = srv._open_url("keepingyouawake:///toggle")
    assert err and "keepingyouawake:///toggle" in err
    captured = capsys.readouterr()
    assert captured.out == ""  # stdout is the MCP transport
    assert "keepingyouawake:///toggle" in captured.err


def test_open_url_without_startfile_returns_error(monkeypatch, capsys):
    monkeypatch.delattr(os, "startfile", raising=False)
    monkeypatch.setattr(srv.subprocess, "run", lambda *a, **k: pytest.fail("must not spawn"))
    err = srv._open_url("keepingyouawake:///toggle")
    assert err and "Windows" in err
    assert capsys.readouterr().out == ""


# --------------------------------------------------------------------------
# _activity_log_path — %APPDATA% resolution
# --------------------------------------------------------------------------

def test_activity_log_path_uses_appdata(tmp_path, monkeypatch):
    monkeypatch.setenv("APPDATA", str(tmp_path / "Roaming"))
    assert srv._activity_log_path() == tmp_path / "Roaming" / "KeepingYouAwake" / "activity.jsonl"


def test_activity_log_path_falls_back_to_home_roaming(tmp_path, monkeypatch):
    monkeypatch.delenv("APPDATA", raising=False)
    monkeypatch.setattr(srv.Path, "home", classmethod(lambda cls: tmp_path))
    assert srv._activity_log_path() == (
        tmp_path / "AppData" / "Roaming" / "KeepingYouAwake" / "activity.jsonl"
    )


# --------------------------------------------------------------------------
# _kya_is_running — `tasklist` mocked
# --------------------------------------------------------------------------

def _fake_tasklist(monkeypatch, stdout="", exc=None):
    calls = []

    def _run(cmd, *a, **k):
        calls.append((cmd, k))
        if exc is not None:
            raise exc
        return subprocess.CompletedProcess(cmd, 0, stdout=stdout, stderr="")

    monkeypatch.setattr(srv.subprocess, "run", _run)
    return calls


def test_kya_is_running_true_when_tasklist_lists_process(monkeypatch):
    calls = _fake_tasklist(
        monkeypatch, stdout='"KeepingYouAwake.exe","4242","Console","1","52,340 K"\r\n'
    )
    assert _ORIGINAL_IS_RUNNING() is True
    cmd, kwargs = calls[0]
    assert cmd == ["tasklist", "/FI", "IMAGENAME eq KeepingYouAwake.exe", "/NH", "/FO", "CSV"]
    assert kwargs["timeout"] == 5
    assert kwargs["check"] is False


def test_kya_is_running_false_when_no_match(monkeypatch):
    _fake_tasklist(monkeypatch, stdout="INFO: No tasks are running which match the specified criteria.\r\n")
    assert _ORIGINAL_IS_RUNNING() is False


@pytest.mark.parametrize(
    "exc",
    [FileNotFoundError("tasklist"), OSError("boom"), subprocess.TimeoutExpired("tasklist", 5)],
)
def test_kya_is_running_true_when_tasklist_unavailable(monkeypatch, exc):
    _fake_tasklist(monkeypatch, exc=exc)
    assert _ORIGINAL_IS_RUNNING() is True


@pytest.mark.parametrize("value,expected", [("YES", True), ("0", False), ("false", False)])
def test_set_default_accepts_boolean_strings(settings_dir, value, expected):
    out = json.loads(_text(_call("kya_set_default", {"key": "mouseJiggler", "value": value})))
    assert out["set"] is True and out["value"] is expected


def test_activate_url_mapping():
    assert srv._activate_url(90) == "keepingyouawake:///activate?seconds=90"
    assert srv._activate_url(12.7) == "keepingyouawake:///activate?seconds=12"
    assert srv._activate_url("indefinite") == "keepingyouawake:///activate?seconds=0"
    assert srv._activate_url("forever") is None


def test_handler_reports_unknown_duration_string():
    # The SDK's schema validation normally rejects this first; the handler guards anyway.
    assert "unknown duration string" in _text(_call("kya_activate", {"duration": "forever"}))
