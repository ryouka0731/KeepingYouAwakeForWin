# Changelog

All notable changes to KeepingYouAwake for Windows are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
