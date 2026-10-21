"""API tests: the MCP server over its real stdio JSON-RPC transport.

Launches `kya_mcp_server.py` as a subprocess and talks to it with the official
MCP client, so tool discovery, argument passing and result encoding are tested
end to end — not just the Python handlers. Nothing touches the real app: the
activity log and settings live in a temp %APPDATA%, and only read-only or
file-writing tools are called (no URL is opened).
"""

from __future__ import annotations

import asyncio
import json
import os
import sys
from pathlib import Path

import pytest

mcp = pytest.importorskip("mcp")
from mcp import ClientSession, StdioServerParameters  # noqa: E402
from mcp.client.stdio import stdio_client  # noqa: E402

SERVER = Path(__file__).resolve().parents[1] / "kya_mcp_server.py"


def run_session(appdata: Path, steps):
    """Runs `steps(session)` against a freshly started server and returns its result."""

    async def main():
        params = StdioServerParameters(
            command=sys.executable,
            args=[str(SERVER)],
            env={**os.environ, "APPDATA": str(appdata), "PYTHONUTF8": "1"},
        )
        async with stdio_client(params) as (read, write):
            async with ClientSession(read, write) as session:
                await session.initialize()
                return await steps(session)

    return asyncio.run(asyncio.wait_for(main(), timeout=60))


def payload(result) -> object:
    text = result.content[0].text
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return text


@pytest.fixture
def appdata(tmp_path):
    (tmp_path / "KeepingYouAwake").mkdir()
    return tmp_path


def test_lists_all_tools_with_schemas(appdata):
    async def steps(session):
        return await session.list_tools()

    tools = {t.name: t for t in run_session(appdata, steps).tools}
    assert set(tools) == {
        "kya_activate", "kya_deactivate", "kya_toggle",
        "kya_status", "kya_list_recent_sessions", "kya_set_default",
    }
    assert tools["kya_set_default"].input_schema["required"] == ["key", "value"]
    assert "mouseJiggler" in tools["kya_set_default"].description


def test_status_and_recent_sessions_read_the_activity_log(appdata):
    log = appdata / "KeepingYouAwake" / "activity.jsonl"
    log.write_text(
        json.dumps({"startedAt": "2026-10-21T08:00:00Z", "endedAt": "2026-10-21T08:05:00Z",
                    "source": "user", "requestedDuration": 300, "endedReason": "expired"}) + "\n",
        encoding="utf-8",
    )

    async def steps(session):
        status = await session.call_tool("kya_status", {})
        recent = await session.call_tool("kya_list_recent_sessions", {"limit": 5})
        return payload(status), payload(recent)

    status, recent = run_session(appdata, steps)
    assert status == {"active": False}
    assert [e["endedReason"] for e in recent] == ["expired"]


def test_set_default_round_trips_through_settings_json(appdata):
    async def steps(session):
        ok = await session.call_tool("kya_set_default", {"key": "allowDisplaySleep", "value": True})
        bad = await session.call_tool("kya_set_default", {"key": "allowDisplaySleep", "value": 3})
        refused = await session.call_tool("kya_set_default", {"key": "somethingElse", "value": True})
        return payload(ok), payload(bad), payload(refused)

    ok, bad, refused = run_session(appdata, steps)
    assert ok == {"set": True, "key": "allowDisplaySleep", "property": "allowDisplaySleep", "value": True}
    assert bad["set"] is False
    assert refused.startswith("refused")
    settings = json.loads((appdata / "KeepingYouAwake" / "settings.json").read_text(encoding="utf-8"))
    assert settings == {"allowDisplaySleep": True}


def test_unknown_tool_and_bad_duration_are_reported(appdata):
    async def steps(session):
        unknown = await session.call_tool("kya_nope", {})
        bad = await session.call_tool("kya_activate", {"duration": "forever"})
        return payload(unknown), payload(bad)

    unknown, bad = run_session(appdata, steps)
    assert "unknown tool" in unknown
    # The SDK validates arguments against the tool's input schema before the handler runs.
    assert "forever" in bad and ("validation" in bad.lower() or "unknown duration string" in bad)
