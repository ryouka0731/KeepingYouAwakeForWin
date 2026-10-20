# `kya` — command-line driver

Tiny CLI for **KeepingYouAwake for Windows**. Use it to activate, deactivate, or check status from PowerShell, `cmd.exe`, batch files, Task Scheduler jobs, or anywhere else a one-shot command beats clicking the system tray icon.

## Examples

```powershell
PS> kya activate 30m
PS> kya activate 2h
PS> kya activate                # indefinite
PS> kya activate --until-end-of-day
PS> kya deactivate
PS> kya toggle
PS> kya status
active (source=user) remaining=0:29:54
PS> kya status --json
{"active": true, "source": "user", "startedAt": "...", "requestedDuration": 1800, "fireDate": "...", "remainingSeconds": 1794.7}
```

`kya status` exits **0** when active, **1** when inactive, so you can branch on it in scripts (`if ($LASTEXITCODE -eq 0) { … }` in PowerShell, `if errorlevel 1 …` in batch).

## Install

Recommended: [`uv`](https://docs.astral.sh/uv/):

```powershell
uv tool install "git+https://github.com/ryouka0731/KeepingYouAwakeForWin.git#subdirectory=tools/cli"
```

Or with stock `pip` from a clone:

```powershell
git clone https://github.com/ryouka0731/KeepingYouAwakeForWin.git
cd KeepingYouAwakeForWin\tools\cli
pip install .
```

Either way you get a `kya` executable on PATH. It has no external Python dependencies, only the standard library.

## How it talks to KYA

Same playbook as `kya-mcp-server`:

- Activate / deactivate / toggle go through the app's registered URL scheme (`keepingyouawake:///…`), opened with the Windows shell (`os.startfile`, the same as `start "" "keepingyouawake:///toggle"`).
- Status reads the activity log at `%APPDATA%\KeepingYouAwake\activity.jsonl`. It also checks that `KeepingYouAwake.exe` is running (via `tasklist`). An open log entry left behind by a crash is reported as inactive.

KeepingYouAwake for Windows must be installed. The URL scheme starts it on demand if it isn't running. On non-Windows platforms the activate / deactivate / toggle commands print an error and exit non-zero.

## Duration grammar

| Input | Seconds |
|-------|---------|
| `30s` | 30 |
| `30m` | 1800 |
| `2h` | 7200 |
| `1h30m` | 5400 |
| `90` | 90 (bare integers are seconds) |
| (omitted) | indefinite (no fireDate) |
| `--until-end-of-day` | seconds until next local midnight |

## License

MIT (matches the parent repo).
