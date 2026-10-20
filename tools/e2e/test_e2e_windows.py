"""End-to-end tests: the published KeepingYouAwake.exe on a real Windows session.

Drives the running app the way users and scripts do — the `kya` CLI (URL
scheme through the shell), command-line switches forwarded to the running
instance, and outside edits of settings.json — and checks the observable
results: the activity log, `powercfg /requests`, and process lifetime.

Runs only on Windows with ``KYA_E2E_EXE`` pointing at the executable (CI sets
it after `dotnet publish`); skipped everywhere else. Each test gets its own
data directory through ``KEEPINGYOUAWAKE_DATA_DIR`` / ``APPDATA``.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from pathlib import Path
from typing import Callable

import pytest

pytestmark = pytest.mark.skipif(
    sys.platform != "win32" or not os.environ.get("KYA_E2E_EXE"),
    reason="end-to-end tests need Windows and KYA_E2E_EXE",
)

import kya  # noqa: E402  (tools/conftest.py puts tools/cli on sys.path)

TIMEOUT = 20.0


def wait_for(predicate: Callable[[], bool], what: str, timeout: float = TIMEOUT) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.25)
    pytest.fail(f"timed out after {timeout:.0f}s waiting for {what}")


class App:
    """A running KeepingYouAwake instance with an isolated data directory."""

    def __init__(self, exe: Path, appdata: Path):
        self.exe = exe
        self.data = appdata / "KeepingYouAwake"
        self.data.mkdir(parents=True, exist_ok=True)
        self.env = {**os.environ, "APPDATA": str(appdata), "KEEPINGYOUAWAKE_DATA_DIR": str(self.data)}
        self.process: subprocess.Popen | None = None

    @property
    def log(self) -> Path:
        return self.data / "activity.jsonl"

    @property
    def settings(self) -> Path:
        return self.data / "settings.json"

    def start(self, *args: str) -> None:
        self.process = subprocess.Popen([str(self.exe), *args], env=self.env)
        # Ready once its single-instance pipe exists. Launching a second
        # instance before that would race it for the instance mutex.
        wait_for(self._listening, "the app to start")

    def _listening(self) -> bool:
        assert self.process is not None
        if self.process.poll() is not None:
            pytest.fail(f"the app exited during startup with code {self.process.returncode}")
        return any(name.startswith("KeepingYouAwake-") for name in os.listdir("\\\\.\\pipe\\"))

    def forward(self, *args: str) -> int:
        """Runs a second instance, which hands its arguments to the running one."""
        return subprocess.run([str(self.exe), *args], env=self.env, timeout=TIMEOUT).returncode

    def entries(self) -> list[dict]:
        if not self.log.exists():
            return []
        return [json.loads(line) for line in self.log.read_text(encoding="utf-8").splitlines() if line.strip()]

    def last(self) -> dict | None:
        entries = self.entries()
        return entries[-1] if entries else None

    def write_settings(self, **values: object) -> None:
        current = json.loads(self.settings.read_text(encoding="utf-8")) if self.settings.exists() else {}
        current.update(values)
        tmp = self.settings.with_suffix(".e2e.tmp")
        tmp.write_text(json.dumps(current), encoding="utf-8")
        os.replace(tmp, self.settings)

    def quit(self) -> None:
        if self.process and self.process.poll() is None:
            self.forward("--quit")
            try:
                self.process.wait(timeout=TIMEOUT)
            except subprocess.TimeoutExpired:
                self.process.kill()
                raise


@pytest.fixture
def app(tmp_path, monkeypatch):
    exe = Path(os.environ["KYA_E2E_EXE"]).resolve()
    instance = App(exe, tmp_path / "AppData")
    monkeypatch.setenv("APPDATA", str(tmp_path / "AppData"))  # what `kya` reads
    instance.start()
    yield instance
    instance.quit()


def active(app: App, source: str | None = None) -> Callable[[], bool]:
    def check() -> bool:
        last = app.last()
        return bool(last) and "endedAt" not in last and (source is None or last.get("source") == source)
    return check


def inactive(app: App) -> Callable[[], bool]:
    return lambda: (last := app.last()) is None or "endedAt" in last


def test_cli_activate_status_and_deactivate(app, capsys):
    assert kya.main(["activate", "2m"]) == 0
    wait_for(active(app), "a session")
    assert app.last()["requestedDuration"] == 120
    assert app.last()["source"] == "user"

    capsys.readouterr()
    assert kya.main(["status", "--json"]) == 0
    status = json.loads(capsys.readouterr().out)
    assert status["active"] is True
    assert 0 < status["remainingSeconds"] <= 120

    assert kya.main(["deactivate"]) == 0
    wait_for(inactive(app), "the session to end")
    assert app.last()["endedReason"] == "user-cancelled"
    assert kya.main(["status"]) == 1


def test_power_request_is_visible_to_windows(app):
    app.forward("--activate", "60")
    wait_for(active(app), "a session")
    result = subprocess.run(["powercfg", "/requests"], capture_output=True, text=True, timeout=TIMEOUT)
    if result.returncode != 0:
        pytest.skip(f"powercfg /requests needs elevation here: {result.stderr.strip()}")
    assert "KeepingYouAwake is preventing sleep." in result.stdout

    app.forward("--deactivate")
    wait_for(inactive(app), "the session to end")
    after = subprocess.run(["powercfg", "/requests"], capture_output=True, text=True, timeout=TIMEOUT).stdout
    assert "KeepingYouAwake is preventing sleep." not in after


def test_toggle_uses_the_default_duration(app):
    app.write_settings(defaultDurationSeconds=300, durations=[300, 600])
    time.sleep(1.5)  # settings reload is debounced
    app.forward("--toggle")
    wait_for(active(app), "a session")
    assert app.last()["requestedDuration"] == 300
    app.forward("--toggle")
    wait_for(inactive(app), "the session to end")


def test_session_expires_on_time(app):
    app.forward("keepingyouawake:///activate?seconds=3")
    wait_for(active(app), "a session")
    wait_for(inactive(app), "the session to expire", timeout=15)
    assert app.last()["endedReason"] == "expired"


def test_watched_application_trigger(app):
    app.write_settings(watchedApplications=["ping.exe"])
    time.sleep(1.5)
    ping = subprocess.Popen(["ping", "-n", "60", "127.0.0.1"], stdout=subprocess.DEVNULL)
    try:
        wait_for(active(app, "watched-app"), "the watched-app trigger")
    finally:
        ping.kill()
        ping.wait()
    wait_for(inactive(app), "the watched-app session to end")
    assert app.last()["endedReason"] == "trigger-cancelled"


def test_download_trigger(app, tmp_path):
    downloads = tmp_path / "Downloads"
    downloads.mkdir()
    app.write_settings(downloadInProgressActivationEnabled=True, downloadDirectories=[str(downloads)])
    time.sleep(1.5)
    partial = downloads / "big.iso.crdownload"
    partial.write_bytes(b"x")
    wait_for(active(app, "download"), "the download trigger")
    partial.unlink()
    wait_for(inactive(app), "the download session to end")
    assert app.last()["endedReason"] == "trigger-cancelled"


def test_trigger_never_ends_a_user_session(app, tmp_path):
    downloads = tmp_path / "Downloads"
    downloads.mkdir()
    app.forward("--activate", "600")
    wait_for(active(app, "user"), "a user session")
    app.write_settings(downloadInProgressActivationEnabled=True, downloadDirectories=[str(downloads)])
    partial = downloads / "a.part"
    partial.write_bytes(b"x")
    time.sleep(12)  # one download scan
    partial.unlink()
    time.sleep(12)
    assert active(app, "user")()


def test_quit_closes_the_open_log_entry(app):
    app.forward("--activate")
    wait_for(active(app), "a session")
    app.quit()
    assert app.process.returncode == 0
    assert app.last()["endedReason"] == "app-terminated"


def test_restart_repairs_a_killed_session(app):
    app.forward("--activate")
    wait_for(active(app), "a session")
    app.process.kill()
    app.process.wait(timeout=TIMEOUT)
    assert "endedAt" not in app.last()

    app.start()
    wait_for(inactive(app), "the dangling entry to be repaired")
    assert app.last()["endedReason"] == "app-terminated"


def test_second_launch_hands_over_and_exits(app):
    started = time.monotonic()
    assert app.forward("--deactivate") == 0
    assert time.monotonic() - started < TIMEOUT
    assert app.process.poll() is None  # the first instance keeps running


def test_url_scheme_is_registered_for_the_executable(app):
    import winreg

    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Classes\keepingyouawake\shell\open\command") as key:
        command, _ = winreg.QueryValueEx(key, "")
    assert command == f'"{app.exe}" "%1"'
