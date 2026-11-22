from contextlib import contextmanager
from dataclasses import asdict, dataclass, field
from pathlib import Path
import fcntl
import json
import os
import subprocess
import sys
import tempfile
import time

APP_ID = "io.github.BigesilaB.StartupPorts"
VERSION = "0.1.2"
DISPATCHER = f"{APP_ID}.desktop"


def config_home() -> Path:
    configured = Path(os.environ.get("XDG_CONFIG_HOME", ""))
    return configured if configured.is_absolute() else Path.home() / ".config"


def source_command(script: Path) -> tuple[str, ...]:
    return (sys.executable,) if getattr(sys, "frozen", False) else (sys.executable, str(script.resolve()))


def atomic_write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, delete=False) as out:
        temporary = Path(out.name)
        try:
            out.write(text)
            out.close()
            os.replace(temporary, path)
        finally:
            temporary.unlink(missing_ok=True)


def desktop_exec(args: tuple[str, ...]) -> str:
    # Desktop Entry escaping is separate from the Exec field's quoting rules.
    def quote(arg: str) -> str:
        if any(char in arg for char in "\x00\n\r"):
            raise ValueError("Invalid desktop entry path")
        arg = arg.replace("%", "%%")
        quoted = '"' + "".join("\\" + char if char in '\\"`$' else char for char in arg) + '"'
        return quoted.replace("\\", "\\\\")

    return " ".join(quote(arg) for arg in args)


@dataclass(frozen=True)
class Entry:
    id: str
    name: str
    program: str
    args: list[str]
    directory: str
    delay: int
    enabled: bool

    def validate(self) -> None:
        if not self.name.strip() or len(self.name) > 100:
            raise ValueError("invalid_name")
        program = Path(self.program)
        if not program.is_absolute() or not program.is_file() or not os.access(program, os.X_OK):
            raise ValueError("invalid_program")
        if not Path(self.directory).is_absolute() or not Path(self.directory).is_dir():
            raise ValueError("invalid_directory")
        if not 0 <= self.delay <= 600 or any("\0" in arg for arg in self.args):
            raise ValueError("invalid_arguments")


@dataclass(frozen=True)
class Group:
    id: str
    name: str
    members: list[str]


@dataclass
class Settings:
    language: str = ""
    entries: list[Entry] = field(default_factory=list)
    groups: list[Group] = field(default_factory=list)


class Store:
    def __init__(self, command: tuple[str, ...], home: Path | None = None):
        self.command = command
        self.home = config_home() if home is None else home
        self.directory = self.home / "startupports"
        self.path = self.directory / "settings.json"
        self.dispatcher = self.home / "autostart" / DISPATCHER

    def read(self) -> Settings:
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return Settings()
        try:
            if data["version"] != 1:
                raise ValueError("unsupported_settings")
            return Settings(data.get("language", ""), [Entry(**row) for row in data["entries"]],
                            [Group(**row) for row in data["groups"]])
        except (KeyError, TypeError) as exc:
            raise ValueError("unsupported_settings") from exc

    @contextmanager
    def locked_settings(self):
        self.directory.mkdir(parents=True, exist_ok=True)
        with (self.directory / "settings.lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            yield self.read()

    def update(self, change) -> Settings:
        with self.locked_settings() as settings:
            change(settings)
            if any(entry.enabled for entry in settings.entries):
                self.register_dispatcher()
            data = {"version": 1, **asdict(settings)}
            atomic_write(self.path, json.dumps(data, ensure_ascii=False, indent=2) + "\n")
            return settings

    def register_dispatcher(self) -> None:
        desktop = ("[Desktop Entry]\nType=Application\nName=StartupPorts login launcher\n"
                   "Name[zh_CN]=StartupPorts 登录启动器\nX-StartupPorts-Managed=true\n"
                   f"Exec={desktop_exec((*self.command, '--autostart'))}\nTerminal=false\n")
        try:
            current = self.dispatcher.read_text(encoding="utf-8")
        except FileNotFoundError:
            current = None
        if current is not None and "X-StartupPorts-Managed=true" not in current.splitlines():
            raise ValueError("dispatcher_conflict")
        if current != desktop:
            atomic_write(self.dispatcher, desktop)


def launch(entry: Entry) -> subprocess.Popen:
    entry.validate()
    return subprocess.Popen([entry.program, *entry.args], cwd=entry.directory,
                            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                            start_new_session=True)


def login_launch(store: Store, entry_id: str = "") -> int:
    if not entry_id:
        for entry in store.read().entries:
            if entry.enabled:
                subprocess.Popen([*store.command, "--run-entry", entry.id], start_new_session=True)
        return 0

    entry = next((row for row in store.read().entries if row.id == entry_id and row.enabled), None)
    if entry is not None:
        time.sleep(entry.delay)
        # A user can disable or remove the item while its login delay is counting down.
        current = next((row for row in store.read().entries if row.id == entry_id and row.enabled), None)
        if current is not None:
            launch(current)
    return 0
