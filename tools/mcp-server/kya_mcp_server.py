#!/usr/bin/env python3
"""KYA MCP server.

Exposes KeepingYouAwake for Windows primitives as MCP tools so
Claude Code (or any other MCP client speaking stdio) can:

  - activate / deactivate / toggle the keep-awake timer
  - read the current state from the activity-log JSONL
  - list recent sessions

Implementation strategy: the server hands `keepingyouawake:///...` URLs
to the Windows shell (`os.startfile`) to drive the running KYA tray app
via its registered URL-scheme entry points. State queries read the JSONL
log the app writes to `%APPDATA%\\KeepingYouAwake\\activity.jsonl`. No IPC
wiring needed; the server runs on the same PC as the app and fails
gracefully if KYA isn't installed.

Install:
    uv tool install git+https://github.com/ryouka0731/KeepingYouAwakeForWin.git#subdirectory=tools/mcp-server
        # or `pip install` from a checkout

Wire into Claude Code (~/.claude/mcp_settings.json):
    {
      "mcpServers": {
        "kya": { "command": "kya-mcp-server" }
      }
    }
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

try:
    from mcp.server import Server, ServerRequestContext
    from mcp.server.stdio import stdio_server
    import mcp.types as mcp_types
except ImportError as e:
    print(
        "kya-mcp-server requires the 'mcp' Python package.\n"
        "Install it with one of:\n"
        "    uv pip install 'mcp>=2.3,<3'\n"
        "    pip install 'mcp>=2.3,<3'\n"
        f"\nUnderlying import error: {e}",
        file=sys.stderr,
    )
    raise SystemExit(2)


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
            # Don't flash a console window when spawned by a windowless MCP host.
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.SubprocessError):
        return True  # can't tell — don't hide a real session
    return f'"{APP_EXECUTABLE}"'.lower() in (result.stdout or "").lower()


def _open_url(url: str) -> str | None:
    """Drive the KYA app via its registered URL scheme.

    Returns None on success, or a human-readable error string. Diagnostics go
    to stderr only — stdout is the MCP stdio transport.
    """
    if not hasattr(os, "startfile"):
        err = (
            f"cannot open {url}: the keepingyouawake:// URL scheme is only "
            "available on Windows (os.startfile is missing on this platform)"
        )
    else:
        try:
            os.startfile(url)
            return None
        except OSError as exc:
            err = f"cannot open {url}: {exc}. Is KeepingYouAwake for Windows installed?"
    print(f"kya-mcp-server: {err}", file=sys.stderr)
    return err


# Setting keys `kya_set_default` accepts, mapped to their `settings.json`
# property and value type. The macOS-era `info.marcel-dierkes…` names are kept
# as aliases so existing prompts keep working. The app watches settings.json
# and applies changes immediately.
_SETTING_PROPERTIES: dict[str, tuple[str, type]] = {
    "ActivateOnACPowerEnabled": ("activateOnACPower", bool),
    "ActivateOnExternalDisplayConnectedEnabled": ("activateOnExternalDisplay", bool),
    "ActivateOnExternalAudioOutputEnabled": ("activateOnExternalAudioOutput", bool),
    "ActivateOnCPULoadEnabled": ("activateOnCpuLoad", bool),
    "CPULoadActivationThreshold": ("cpuLoadThreshold", float),
    "AllowDisplaySleep": ("allowDisplaySleep", bool),
    "DeactivateOnFullChargeEnabled": ("deactivateOnFullCharge", bool),
    "DownloadInProgressActivationEnabled": ("downloadInProgressActivationEnabled", bool),
    "DriveAliveEnabled": ("driveAlive", bool),
    "MenuBarCountdownDisabled": ("countdownHidden", bool),
    "MouseJigglerEnabled": ("mouseJiggler", bool),
    "PreventDiskSleepEnabled": ("preventDiskSleep", bool),
    "ScheduleEnabled": ("scheduleEnabled", bool),
}
_LEGACY_PREFIX = "info.marcel-dierkes.KeepingYouAwake."
SETTING_KEYS: dict[str, tuple[str, type]] = {}
for _legacy, (_prop, _kind) in _SETTING_PROPERTIES.items():
    SETTING_KEYS[_LEGACY_PREFIX + _legacy] = (_prop, _kind)
    SETTING_KEYS[_prop] = (_prop, _kind)
ALLOWED_DEFAULT_KEYS = set(SETTING_KEYS)


def _settings_path() -> Path:
    """The app's settings file: `%APPDATA%\\KeepingYouAwake\\settings.json`."""
    return _activity_log_path().with_name("settings.json")


def _coerce_setting(value: Any, kind: type) -> Any:
    """Validate `value` for a setting of type `kind`; raise ValueError if it doesn't fit."""
    if kind is bool:
        if isinstance(value, bool):
            return value
        if isinstance(value, str) and value.lower() in ("true", "false", "yes", "no", "1", "0"):
            return value.lower() in ("true", "yes", "1")
        raise ValueError("expected a boolean")
    if isinstance(value, bool):
        raise ValueError("expected a number")
    try:
        number = float(value)
    except (TypeError, ValueError):
        raise ValueError("expected a number") from None
    if not 1 <= number <= 99:
        raise ValueError("expected a number between 1 and 99")
    return number


def _write_setting(prop: str, value: Any) -> None:
    """Set one property in settings.json, keeping every other key. Atomic replace."""
    path = _settings_path()
    data: dict[str, Any] = {}
    if path.exists():
        loaded = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(loaded, dict):
            raise ValueError("settings.json is not a JSON object")
        data = loaded
    # The app reads property names case-insensitively; drop other spellings.
    for existing in [k for k in data if k.lower() == prop.lower() and k != prop]:
        del data[existing]
    data[prop] = value
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".mcp.tmp")
    tmp.write_text(json.dumps(data, indent=2), encoding="utf-8")
    os.replace(tmp, path)


def _read_recent_entries(limit: int) -> list[dict[str, Any]]:
    """Return up to `limit` most recent entries, newest-first.

    Uses a bounded `deque` so memory + post-iteration slice cost stay
    O(limit) rather than O(file size). On a long-lived install the
    JSONL log is capped at 1000 lines anyway, but `limit` here can be
    much smaller, and there's no reason to materialise more than that.
    """
    from collections import deque
    log_path = _activity_log_path()
    if not log_path.exists():
        return []
    if int(limit) <= 0:
        return []
    tail: deque[dict[str, Any]] = deque(maxlen=int(limit))
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


def _safe_iso_to_epoch(s: Any) -> float | None:
    """Tolerant ISO-8601 → epoch seconds. Returns None on any malformation."""
    if not isinstance(s, str):
        return None
    try:
        return datetime.fromisoformat(s.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return None


def _current_status() -> dict[str, Any]:
    """Best-effort: "active" when the most recent log entry has no `endedAt`.

    Tolerant to malformed `startedAt` — if the timestamp can't be
    parsed we still return `active=True` but omit `fireDate` /
    `remainingSeconds` rather than crashing the MCP call.
    """
    # Only one session is open at a time, so the newest entry decides. An
    # older entry without endedAt was left behind by a quit or crash, and
    # so is the newest one while KYA isn't running.
    if not _kya_is_running():
        return {"active": False}
    entries = _read_recent_entries(1)
    for entry in entries:
        if "endedAt" not in entry or entry.get("endedAt") is None:
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


# ---------------------------------------------------------------------------
# MCP server wiring
# ---------------------------------------------------------------------------

# Tool definitions advertised by list_tools(); call_tool() dispatches through _TOOLS.
TOOL_DEFINITIONS: list[mcp_types.Tool] = [
    mcp_types.Tool(
        name="kya_activate",
        description=(
            "Start a keep-awake session. Pass an integer number of seconds, "
            "or the strings 'indefinite' or 'until_end_of_day'."
        ),
        inputSchema={
            "type": "object",
            "properties": {
                "duration": {
                    "oneOf": [
                        {"type": "integer", "minimum": 1},
                        {"type": "string", "enum": ["indefinite", "until_end_of_day"]},
                    ]
                }
            },
            "required": ["duration"],
        },
    ),
    mcp_types.Tool(
        name="kya_deactivate",
        description="Stop the current keep-awake session, if any.",
        inputSchema={"type": "object", "properties": {}},
    ),
    mcp_types.Tool(
        name="kya_toggle",
        description="Toggle KYA. If active → deactivate; if inactive → activate (default duration).",
        inputSchema={"type": "object", "properties": {}},
    ),
    mcp_types.Tool(
        name="kya_status",
        description="Return the current KYA state (active, source, fireDate, remainingSeconds).",
        inputSchema={"type": "object", "properties": {}},
    ),
    mcp_types.Tool(
        name="kya_list_recent_sessions",
        description="Return up to N recent activate/deactivate entries from the activity log.",
        inputSchema={
            "type": "object",
            "properties": {
                "limit": {"type": "integer", "minimum": 1, "maximum": 200, "default": 20}
            },
        },
    ),
    mcp_types.Tool(
        name="kya_set_default",
        description=(
            "Change one of KYA's known settings (written to "
            "%APPDATA%\\KeepingYouAwake\\settings.json; the running app applies "
            "it immediately). The key must be in the allow-list — arbitrary "
            "keys are rejected. Keys: " + ", ".join(sorted(p for p, _ in _SETTING_PROPERTIES.values())) + "."
        ),
        inputSchema={
            "type": "object",
            "properties": {
                "key": {"type": "string"},
                "value": {
                    "oneOf": [
                        {"type": "boolean"},
                        {"type": "number"},
                        {"type": "string"},
                    ]
                },
            },
            "required": ["key", "value"],
        },
    ),
]


async def list_tools() -> list[mcp_types.Tool]:
    return TOOL_DEFINITIONS


def _text(payload: Any) -> list[mcp_types.TextContent]:
    text = payload if isinstance(payload, str) else json.dumps(payload)
    return [mcp_types.TextContent(type="text", text=text)]


def _activate_url(duration: Any) -> str | None:
    """The activate URL for a duration (seconds, "indefinite" or "until_end_of_day"); None if unknown."""
    if duration == "indefinite":
        # Explicit seconds=0: without a duration the app uses its default
        # duration, which is not indefinite.
        return "keepingyouawake:///activate?seconds=0"
    if duration == "until_end_of_day":
        now = datetime.now()
        tomorrow = now.replace(hour=0, minute=0, second=0, microsecond=0) + timedelta(days=1)
        return f"keepingyouawake:///activate?seconds={int((tomorrow - now).total_seconds())}"
    if isinstance(duration, str):
        return None
    return f"keepingyouawake:///activate?seconds={int(duration)}"


def _tool_activate(arguments: dict[str, Any]) -> Any:
    duration = arguments["duration"]
    url = _activate_url(duration)
    if url is None:
        return f"unknown duration string: {duration!r}"
    err = _open_url(url)
    if err:
        return {"activated": False, "request": duration, "error": err}
    return {"activated": True, "request": duration}


def _simple_url_tool(url: str, result_key: str) -> Any:
    err = _open_url(url)
    return {result_key: False, "error": err} if err else {result_key: True}


def _tool_set_default(arguments: dict[str, Any]) -> Any:
    key = arguments["key"]
    value = arguments["value"]
    if key not in ALLOWED_DEFAULT_KEYS:
        return f"refused: key {key!r} not in allow-list"
    prop, kind = SETTING_KEYS[key]
    try:
        coerced = _coerce_setting(value, kind)
        _write_setting(prop, coerced)
    except (ValueError, OSError) as exc:
        return {"set": False, "key": key, "value": value, "error": str(exc)}
    return {"set": True, "key": key, "property": prop, "value": coerced}


_TOOLS: dict[str, Any] = {
    "kya_activate": _tool_activate,
    "kya_deactivate": lambda _args: _simple_url_tool("keepingyouawake:///deactivate", "deactivated"),
    "kya_toggle": lambda _args: _simple_url_tool("keepingyouawake:///toggle", "toggled"),
    "kya_status": lambda _args: _current_status(),
    "kya_list_recent_sessions": lambda args: _read_recent_entries(int(args.get("limit", 20))),
    "kya_set_default": _tool_set_default,
}


async def call_tool(name: str, arguments: dict[str, Any]) -> list[mcp_types.TextContent]:
    handler = _TOOLS.get(name)
    if handler is None:
        return _text(f"unknown tool: {name}")
    return _text(handler(arguments))


# MCP SDK 2.x low-level handlers: (ctx, params) -> full result types.
async def _on_list_tools(
    ctx: ServerRequestContext, params: mcp_types.PaginatedRequestParams | None
) -> mcp_types.ListToolsResult:
    return mcp_types.ListToolsResult(tools=await list_tools())


async def _on_call_tool(
    ctx: ServerRequestContext, params: mcp_types.CallToolRequestParams
) -> mcp_types.CallToolResult:
    return mcp_types.CallToolResult(content=await call_tool(params.name, params.arguments or {}))


server = Server("kya", on_list_tools=_on_list_tools, on_call_tool=_on_call_tool)


def main() -> None:
    parser = argparse.ArgumentParser(description="KYA MCP server (stdio)")
    parser.parse_args()
    import asyncio

    async def _run() -> None:
        async with stdio_server() as (read, write):
            await server.run(read, write, server.create_initialization_options())

    asyncio.run(_run())


if __name__ == "__main__":
    main()
