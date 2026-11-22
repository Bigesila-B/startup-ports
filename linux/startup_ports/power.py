from contextlib import contextmanager
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
import os
import shutil
import subprocess
import time


ACTIONS = {"shutdown": "PowerOff", "restart": "Reboot", "sleep": "Suspend", "hibernate": "Hibernate"}
LABELS = {"shutdown": "Shut down", "restart": "Restart", "sleep": "Sleep", "hibernate": "Hibernate"}
MANAGER = ("org.freedesktop.login1", "/org/freedesktop/login1", "org.freedesktop.login1.Manager")
UNIT = "startupports-scheduled-power"
DESCRIPTION = "StartupPorts power timer"


class PowerError(RuntimeError):
    pass


@dataclass(frozen=True)
class PowerTask:
    action: str
    timestamp: int
    state: str


class PowerScheduler:
    def __init__(self, unit: str = UNIT):
        self.unit = unit
        self.timer = f"{unit}.timer"
        self.service = f"{unit}.service"

    def _run(self, *args: str, check: bool = True) -> subprocess.CompletedProcess[str]:
        try:
            result = subprocess.run(args, capture_output=True, text=True, errors="replace", timeout=5,
                                    env={**os.environ, "LC_ALL": "C"})
        except (OSError, subprocess.TimeoutExpired) as exc:
            raise PowerError("power_requires_systemd") from exc
        if check and result.returncode != 0:
            raise PowerError((result.stderr or result.stdout).strip() or "power_command_failed")
        return result

    @contextmanager
    def _lock(self):
        import fcntl

        runtime = Path(os.environ.get("XDG_RUNTIME_DIR", ""))
        if not runtime.is_absolute() or not runtime.is_dir():
            raise PowerError("power_requires_systemd")
        with (runtime / "startupports-power.lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            yield

    def _show(self, unit: str, *properties: str) -> dict[str, str]:
        result = self._run("systemctl", "--user", "show", "--property=" + ",".join(properties), unit, check=False)
        values = dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)
        if values.get("LoadState") == "not-found":
            return {}
        if result.returncode != 0 or not values:
            raise PowerError("power_requires_systemd")
        return values

    def status(self) -> PowerTask | None:
        timer = self._show(self.timer, "LoadState", "SubState", "Description")
        if not timer or timer.get("SubState") in ("dead", "failed"):
            return None
        prefix, _, fields = timer.get("Description", "").partition(":")
        action, _, timestamp = fields.partition(":")
        if prefix != DESCRIPTION or action not in ACTIONS or not timestamp.isdecimal():
            raise PowerError("power_unit_in_use")
        if timer.get("SubState") == "waiting":
            return PowerTask(action, int(timestamp), "scheduled")
        service = self._show(self.service, "LoadState", "ActiveState", "SubState", "Result")
        if service.get("ActiveState") == "failed" or service.get("Result") not in (None, "success"):
            state = "failed"
        elif service.get("SubState") == "exited":
            state = "finished"
        else:
            state = "running" if timer.get("SubState") == "running" else "finished"
        return PowerTask(action, int(timestamp), state)

    def _busctl(self, method: str, *args: str) -> tuple[str, ...]:
        executable = shutil.which("busctl")
        if not executable:
            raise PowerError("power_requires_systemd")
        return executable, "--system", "--timeout=3s", "call", *MANAGER, method, *args

    def permission(self, action: str) -> str:
        # Check from the same user-service context that will execute the timer.
        command = self._busctl("Can" + ACTIONS[action])
        result = self._run("systemd-run", "--user", "--quiet", "--wait", "--pipe", "--collect", "--",
                           *command, check=False)
        if result.returncode != 0:
            raise PowerError("power_requires_systemd")
        return result.stdout.strip().removeprefix('s "').removesuffix('"')

    def _power_command(self, action: str) -> tuple[str, ...]:
        return self._busctl(ACTIONS[action], "b", "false")

    def schedule(self, action: str, timestamp: int) -> PowerTask:
        if action not in ACTIONS:
            raise PowerError("power_unknown_action")
        if timestamp <= time.time() + 2:
            raise PowerError("power_future_required")
        with self._lock():
            task = self.status()
            if task and task.state in ("scheduled", "running"):
                raise PowerError("power_task_active")
            permission = self.permission(action)
            if permission != "yes":
                raise PowerError("power_needs_permission" if permission in ("no", "challenge") else "power_not_supported")
            if task:
                self._run("systemctl", "--user", "stop", self.timer, self.service)
            when = datetime.fromtimestamp(timestamp, timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
            if timestamp <= time.time() + 1:
                raise PowerError("power_future_required")
            self._run("systemd-run", "--user", "--quiet", f"--unit={self.unit}", f"--on-calendar={when}",
                      f"--timer-property=Description={DESCRIPTION}:{action}:{timestamp}",
                      "--timer-property=AccuracySec=1s", "--timer-property=Persistent=no",
                      "--timer-property=RemainAfterElapse=yes", "--service-type=oneshot",
                      "--property=RemainAfterExit=yes", "--", *self._power_command(action))
            return PowerTask(action, timestamp, "scheduled")

    def cancel(self, expected: PowerTask) -> None:
        with self._lock():
            task = self.status()
            if task != expected or task.state != "scheduled":
                raise PowerError("power_cancel_stale")
            self._run("systemctl", "--user", "stop", self.timer)
            service = self._show(self.service, "LoadState", "ActiveState")
            if service.get("ActiveState") in ("activating", "active", "failed"):
                raise PowerError("power_cancel_too_late")
