# Changelog

All notable changes to KeepingYouAwake for Windows are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- The UI is available in 22 languages (da, de, el, en, es, fi, fr, hi, id, it, ja, ko, nl, pl, pt, ru, sk, tr, uk, vi, zh-Hans, zh-Hant), with plural forms and localized weekday names; the language follows the Windows display language.

## [1.1.0] - 2026-10-21

### Added
- `--quit` / `keepingyouawake:///quit` to close the app from scripts; `KEEPINGYOUAWAKE_DATA_DIR` to relocate settings and the activity log.
- Component, platform, API (MCP over stdio, GitHub releases) and end-to-end tests; coverage and complexity gates in CI; [code metrics report](docs/code-metrics.md).
- Property-based and model-based tests (FsCheck) and weekly mutation testing (Stryker.NET, Core score 90.9%).

### Fixed
- Settings: a failed Start at Login change showed its error twice.

### Changed
- `kya-mcp-server` targets MCP Python SDK 2.x (`mcp>=2.3,<3`).
- Test and CI dependencies updated (xunit runner 4, coverlet 10, Test SDK 18; checkout / setup-python v7, CodeQL 4.39, Scorecard 2.5, SignPath action v3).
- Win32 result decoding and the tray menu / tooltip moved into the platform-independent core; complex functions split up (max cyclomatic complexity 21 → 10).
- Update-check User-Agent reports the version as `x.y.z`.

## [1.0.0] - 2026-10-21

First release: a Windows port of [KeepingYouAwake (Amphetamine)](https://github.com/ryouka0731/KeepingYouAwake-Amphetamine) v1.7.0-amphetamine.11, rewritten in C# / .NET 10 (WinForms) for Windows 10 and 11.

### Added
- Tray app: click to toggle, right-click menu with durations (including *until end of day*), default duration, remaining time, Settings and Activity Log.
- Sleep prevention through Windows power requests (visible in `powercfg /requests`), with an option to let the display turn off.
- Triggers, each starting a session only when none runs and ending only its own: watched Wi-Fi networks, watched applications (by executable name), downloads in progress, active hours, AC power, external display, external audio output, CPU load.
- Battery rules: below a battery level, Battery Saver, full charge.
- Prevent disk sleep, Drive Alive for other drives, mouse jiggler, pause on user switch, quit when the duration is over.
- Per-item on/off checkboxes for every watched item.
- Activity log in the same JSONL format as the macOS app (`%APPDATA%\KeepingYouAwake\activity.jsonl`).
- `keepingyouawake:` URL scheme, `--activate` / `--deactivate` / `--toggle` switches, single-instance forwarding.
- Start at Login, update check against GitHub Releases, English and Japanese UI.
- `kya` CLI and `kya-mcp-server` ported to Windows.
- CI on Windows (build, unit tests, publish + launch smoke test), tag-driven releases for x64 and ARM64, CodeQL for C# and Python.

### Removed
- The macOS app, Xcode project, Swift packages, Sparkle appcast and macOS release tooling. They remain in [KeepingYouAwake-Amphetamine](https://github.com/ryouka0731/KeepingYouAwake-Amphetamine).
