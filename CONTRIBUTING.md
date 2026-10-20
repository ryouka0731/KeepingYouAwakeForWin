# Contributing

Thanks for helping out! Issues and pull requests are welcome.

## Building

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download). The app itself only runs on Windows 10 / 11, but the solution builds on any OS (`EnableWindowsTargeting`), and the `KeepingYouAwake.Core` library and its tests run anywhere.

```powershell
dotnet build KeepingYouAwake.slnx
dotnet test tests/KeepingYouAwake.Core.Tests     # unit + API tests, any OS
dotnet test tests/KeepingYouAwake.Tests          # component + platform tests, Windows only
dotnet run --project src/KeepingYouAwake         # Windows only
```

End-to-end tests drive the published executable (Windows only):

```powershell
dotnet publish src/KeepingYouAwake -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
$env:KYA_E2E_EXE = (Resolve-Path publish/KeepingYouAwake.exe).Path
pytest tools/e2e -v
```

Coverage and complexity thresholds are enforced in CI by [`scripts/code_metrics.py`](scripts/code_metrics.py); see [docs/code-metrics.md](docs/code-metrics.md) for the current numbers and how to measure them.

Python tools (`tools/cli`, `tools/mcp-server`): `python -m pip install --require-hashes -r .github/requirements/requirements-python-tests.txt` and `pytest`.

## Project layout

| Path | What it is |
|------|------------|
| `src/KeepingYouAwake.Core` | Platform-independent logic: sessions, triggers, settings, activity log, URL commands, strings. Talks to the OS only through the interfaces in `Platform/`. |
| `src/KeepingYouAwake` | The WinForms tray app: Win32 implementations of those interfaces (`Windows/`) and the UI (`UI/`). |
| `tests/KeepingYouAwake.Core.Tests` | xUnit tests for Core, using fakes for every OS service and a manually advanced clock. |
| `tests/KeepingYouAwake.Tests` | WinForms component tests and tests against the real Win32 APIs (Windows only). |
| `tools/e2e` | End-to-end tests of the published executable. |
| `tools/` | `kya` CLI and MCP server (Python). |

## Guidelines

- Keep OS calls out of Core. A new trigger gets an interface in `Core/Platform`, its logic and tests in Core, and the Win32 implementation in `src/KeepingYouAwake/Windows`.
- The invariant every trigger must keep: a trigger starts a session only when none is running, and only ends the session it started (`AppController.TerminateIfOwnedBy`).
- New user-facing text goes into `Strings.cs` with both English and Japanese; `StringsTests` fails on missing translations.
- Keep pure logic in Core so it is unit-tested everywhere; the Windows project should only call the OS and draw.
- Functions stay at CCN ≤ 10, ≤ 50 lines and ≤ 5 parameters (checked in CI).
- Warnings are errors. Run `dotnet test` before opening a pull request.

## Translations

Add a language by adding a column to the table in `src/KeepingYouAwake.Core/Strings.cs` and the culture check in `Strings.Get`.
