# `kya-mcp-server`

[Model Context Protocol](https://modelcontextprotocol.io) server for **KeepingYouAwake for Windows**. MCP-aware clients (Claude Code, Claude Desktop, Cursor, Zed, …) can use it to drive KYA over stdio from inside the chat.

## Tools

| Tool | What it does |
|------|--------------|
| `kya_activate(duration)` | Start a keep-awake session. `duration` is an integer (seconds), `"indefinite"`, or `"until_end_of_day"`. |
| `kya_deactivate()` | Stop the current session, if any. |
| `kya_toggle()` | Toggle. Equivalent to clicking the system tray icon. |
| `kya_status()` | Returns `{active, source, fireDate, remainingSeconds}` from the activity log. |
| `kya_list_recent_sessions(limit)` | Up to N recent activate/deactivate entries (`limit` default 20). |
| `kya_set_default(key, value)` | Changes an allow-listed setting in `%APPDATA%\KeepingYouAwake\settings.json` (e.g. `mouseJiggler`, `cpuLoadThreshold`, `allowDisplaySleep`; the macOS-era `info.marcel-dierkes.KeepingYouAwake.*` names also work). The running app applies it immediately. Other keys and wrongly typed values are rejected. |

## Install

The server is a single Python file with one external dependency (`mcp`). The recommended install is via [`uv`](https://docs.astral.sh/uv/):

```powershell
uv tool install "git+https://github.com/ryouka0731/KeepingYouAwakeForWin.git#subdirectory=tools/mcp-server"
```

Or with stock `pip` from a clone:

```powershell
git clone https://github.com/ryouka0731/KeepingYouAwakeForWin.git
cd KeepingYouAwakeForWin\tools\mcp-server
pip install .
```

Either way you get a `kya-mcp-server` executable on PATH.

## Wire into Claude Code

Run `claude mcp add kya -- kya-mcp-server`, or add this to `%USERPROFILE%\.claude\mcp_settings.json` (create the file if needed):

```jsonc
{
  "mcpServers": {
    "kya": {
      "command": "kya-mcp-server"
    }
  }
}
```

Restart Claude Code. The 6 tools should appear under the new `kya` server.

## How it talks to KYA

The server doesn't reach into KYA's internals. It drives the **installed KeepingYouAwake for Windows app** through:

- `keepingyouawake:///activate?seconds=N` (and `/deactivate`, `/toggle`), the URL scheme the app registers. The server opens these with the Windows shell (`os.startfile`), the same as `start "" "keepingyouawake:///toggle"`.
- The activity log JSONL at `%APPDATA%\KeepingYouAwake\activity.jsonl` for state queries, together with a `tasklist` check that `KeepingYouAwake.exe` is running. An open log entry left behind by a crash is reported as inactive.

This means:

- The server runs on the same PC as KYA.
- It needs no administrator rights or extra Windows permissions.
- On non-Windows platforms the activate / deactivate / toggle tools return an error payload (`"activated": false`, `"error": …`) instead of opening anything.

## Clients

Any stdio MCP client should work, for example Claude Code 2.x or MCP Inspector (`npx @modelcontextprotocol/inspector kya-mcp-server`). Report any glitches in the [issues tracker](https://github.com/ryouka0731/KeepingYouAwakeForWin/issues).

## License

MIT (matches the parent repo).
