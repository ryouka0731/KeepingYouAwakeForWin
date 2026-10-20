# KeepingYouAwake for Windows

🌐 **English** · [日本語](README.ja.md)

A small Windows tray utility that keeps your PC from going to sleep — for a set time, or for as long as a trigger holds: a watched app is running, you're on a certain Wi-Fi network, a download is in progress, it's within your active hours, and more.

It is a Windows port of [KeepingYouAwake (Amphetamine)](https://github.com/ryouka0731/KeepingYouAwake-Amphetamine), the Amphetamine-style fork of Marcel Dierkes' macOS app [KeepingYouAwake](https://github.com/newmarcel/KeepingYouAwake). It keeps the same feature set, settings model and activity log format, rewritten in C# / .NET for Windows 10 and 11. Following the upstream maintainer's request, it ships under its own name and an orange cup icon.

## Features

| | Feature |
|---|---|
| ☕ | **Click the tray icon** to toggle; right-click for durations (5 min … 5 h, indefinitely, *until end of day*, and your own) |
| ⏱ | Remaining time in the tray tooltip and menu; optional notifications when a session starts or ends |
| 🖥 | Keeps the display on, or optionally lets it turn off while the system stays awake |
| 📶 | **Watched Wi-Fi networks** — stay awake while joined to one of them |
| 🧩 | **Watched applications** — stay awake while e.g. `obs64.exe` or `Teams.exe` runs |
| ⬇️ | **Downloads** — stay awake while `.crdownload` / `.part` / … files exist in watched folders |
| 🗓 | **Active hours** — weekday × time windows, including ones that wrap past midnight |
| 🔌 | **AC power** — stay awake while plugged in |
| 🖵 | **External display** — stay awake while one is connected |
| 🔊 | **External audio** — stay awake while sound plays through Bluetooth / USB / HDMI |
| 📈 | **CPU load** — stay awake while CPU usage stays above a threshold for 30 s |
| 🔋 | Battery rules: stop below a battery level, when Battery Saver turns on, or when fully charged |
| 💽 | Prevent the system disk / other drives from spinning down; optional mouse jiggler |
| 👥 | Pause when you switch to another user, resume when you come back |
| 📜 | Activity log of every session (`%APPDATA%\KeepingYouAwake\activity.jsonl`) |
| 🔗 | `keepingyouawake:` URL scheme, command-line switches, a Python CLI and an MCP server |
| 🌐 | English and Japanese |

Every watched item has a checkbox to pause it without removing it. A trigger only starts a session when none is running, and only ends the session it started — it never ends one you started yourself.

## Installation

Download `KeepingYouAwake-<version>-win-x64.zip` (or `-win-arm64.zip`) from the [Releases page](https://github.com/ryouka0731/KeepingYouAwakeForWin/releases), unzip it anywhere (for example `%LOCALAPPDATA%\Programs\KeepingYouAwake`) and run `KeepingYouAwake.exe`. It is a single self-contained executable; no installer and no .NET runtime are needed. Each zip has a `.sha256` next to it.

The executable is not code-signed, so SmartScreen may warn on first launch: choose **More info → Run anyway**.

Turn on **Settings → General → Start at Login** to start it when you sign in. Tip: drag the cup out of the hidden-icons area onto the taskbar to keep it visible.

### Build from source

```powershell
git clone https://github.com/ryouka0731/KeepingYouAwakeForWin.git
cd KeepingYouAwakeForWin
dotnet run --project src/KeepingYouAwake
# or a single-file build:
dotnet publish src/KeepingYouAwake -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Requires the .NET 10 SDK. See [CONTRIBUTING.md](CONTRIBUTING.md) for the project layout.

## Automation

The app registers the `keepingyouawake:` URL scheme for the current user:

| URL | Action |
|-----|--------|
| `keepingyouawake:///activate` | start with the default duration |
| `keepingyouawake:///activate?seconds=90` (or `minutes=` / `hours=`) | start for that long (`0` = indefinitely) |
| `keepingyouawake:///deactivate` | stop |
| `keepingyouawake:///toggle` | toggle |

```powershell
Start-Process "keepingyouawake:///activate?minutes=30"
# the same through the executable:
KeepingYouAwake.exe --activate 1800   # seconds; omit for the default duration
KeepingYouAwake.exe --deactivate
KeepingYouAwake.exe --toggle
```

- [`tools/cli`](tools/cli/README.md) — `kya activate 30m`, `kya status --json`, …
- [`tools/mcp-server`](tools/mcp-server/README.md) — lets AI assistants start, stop and inspect sessions

Both drive the app through the URL scheme and read the activity log.

## How does it work?

While a session runs, the app holds a Windows **power request** (`PowerCreateRequest` / `PowerSetRequest`, *system required* plus *display required* unless the display may turn off). You can see it with `powercfg /requests`. Windows can still be put to sleep manually, and closing a laptop lid follows your lid settings.

Settings live in `%APPDATA%\KeepingYouAwake\settings.json`, the activity log next to it.

## Differences from the macOS app

- Watched applications are matched by executable name (`obs64.exe`) instead of bundle identifier.
- Windows 11 24H2 and later only reveal the Wi-Fi network name to apps with location access: allow *Let desktop apps access your location* in Settings → Privacy & security → Location if the Wi-Fi trigger doesn't react.
- "Low Power Mode" is Windows' Battery Saver / Energy Saver.
- Drive Alive keeps every non-system local drive spinning (Windows doesn't reliably tell internal from external fixed drives).
- No AppleScript / Shortcuts; use the URL scheme, command-line switches, CLI or MCP server instead.
- Updates: the app checks GitHub Releases and links to the download; it doesn't replace itself.

## License

MIT, same as upstream. The original KeepingYouAwake is © Marcel Dierkes and independently maintained; this port keeps his copyright and adds its own. See [LICENSE](LICENSE).
