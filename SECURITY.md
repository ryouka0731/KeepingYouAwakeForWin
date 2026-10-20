# Security Policy

## Supported versions

Only the latest release receives security fixes. The app checks GitHub Releases for updates (Settings → Updates), so staying current is the fix.

## Reporting a vulnerability

Please **don't** open a public issue for a security problem.

Report it privately through GitHub instead: [**Report a vulnerability**](https://github.com/ryouka0731/KeepingYouAwakeForWin/security/advisories/new) (Security tab → *Report a vulnerability*). Include:

- the affected component and version: the app version from Settings → About, or for the `kya` CLI / `kya-mcp-server`, the installed package version (`pip show` / `uv tool list`) or the source revision
- what an attacker can do, and the steps or a proof of concept to reproduce it
- any suggested fix, if you have one

You should get an acknowledgement within a week. Once a fix is released, the advisory is published and credits you unless you'd rather stay anonymous.

## Scope

This repository covers the Windows app, its `keepingyouawake:` URL scheme and single-instance pipe, the `kya` CLI and `kya-mcp-server`. Problems in the macOS apps belong to [`ryouka0731/KeepingYouAwake-Amphetamine`](https://github.com/ryouka0731/KeepingYouAwake-Amphetamine) or upstream [`newmarcel/KeepingYouAwake`](https://github.com/newmarcel/KeepingYouAwake).
