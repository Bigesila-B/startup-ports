from configparser import ConfigParser, Error as ConfigError
from dataclasses import dataclass
from pathlib import Path
import locale
import os
import shutil

from .model import DISPATCHER, atomic_write, config_home

OVERRIDE = "X-StartupPorts-Override"


@dataclass(frozen=True)
class DesktopEntry:
    path: Path
    text: str
    name: str
    icon: str
    command: str
    enabled: bool
    local: bool
    override: bool
    reason: str = ""


def parse(text: str):
    parser = ConfigParser(interpolation=None, delimiters=("=",), empty_lines_in_values=False)
    parser.optionxform = str
    parser.read_string(text)
    return parser["Desktop Entry"]


def replace_key(text: str, key: str, value: str) -> str:
    lines = text.splitlines(keepends=True)
    start = next(index for index, line in enumerate(lines) if line.strip() == "[Desktop Entry]") + 1
    for index in range(start, len(lines)):
        line = lines[index].lstrip()
        if line.startswith("["):
            break
        if line.partition("=")[0].strip() == key and "=" in line:
            lines[index] = f"{key}={value}\n"
            return "".join(lines)
    lines.insert(start, f"{key}={value}\n")
    return "".join(lines)


class Autostart:
    def __init__(self, home: Path | None = None, system: list[Path] | None = None):
        self.user = (config_home() if home is None else home) / "autostart"
        if system is None:
            system = [Path(part) for part in (os.environ.get("XDG_CONFIG_DIRS") or "/etc/xdg").split(":")]
        self.system = [path / "autostart" for path in system if path.is_absolute()]

    def list(self, language: str = "") -> list[DesktopEntry]:
        desktop = set(os.environ.get("XDG_CURRENT_DESKTOP", "").split(":")) - {""}
        language = language or (locale.getlocale()[0] or "")
        locale_keys = (f"Name[{language}]", f"Name[{language.split('_')[0]}]", "Name")
        seen = {DISPATCHER}
        items = []
        for folder in [self.user, *self.system]:
            for path in sorted(folder.glob("*.desktop")):
                if path.name in seen:
                    continue
                seen.add(path.name)
                try:
                    text = path.read_text(encoding="utf-8")
                    section = parse(text)
                    hidden = section.getboolean("Hidden", False)
                    allowed = section.getboolean("X-GNOME-Autostart-enabled", True)
                    override = section.getboolean(OVERRIDE, False) and folder == self.user
                except (OSError, ValueError, ConfigError, KeyError, UnicodeError):
                    items.append(DesktopEntry(path, "", path.stem, "", "", False, folder == self.user,
                                              False, "desktop_invalid"))
                    continue
                name = next((section[key] for key in locale_keys if section.get(key)), path.stem)
                command = section.get("Exec", "")
                only = set(section.get("OnlyShowIn", "").split(";")) - {""}
                excluded = set(section.get("NotShowIn", "").split(";")) - {""}
                executable = section.get("TryExec", "")
                reason = ""
                if path.is_symlink() and folder == self.user:
                    reason = "desktop_symlink"
                elif not command and not override:
                    reason = "desktop_no_command"
                elif only and not only.intersection(desktop) or excluded.intersection(desktop):
                    reason = "desktop_filter"
                elif executable and not shutil.which(executable):
                    reason = "desktop_missing"
                elif folder != self.user and (hidden or not allowed):
                    reason = "desktop_system_disabled"
                items.append(DesktopEntry(path, text, name, section.get("Icon", ""), command,
                                          not hidden and allowed and not reason, folder == self.user, override, reason))
        return sorted(items, key=lambda item: (item.name.casefold(), item.path.name))

    def set_enabled(self, entry: DesktopEntry, enabled: bool) -> None:
        current = next((item for item in self.list() if item.path.name == entry.path.name), None)
        if current is None or current.path != entry.path or current.text != entry.text or current.reason:
            raise ValueError("desktop_changed")
        if enabled == current.enabled:
            return
        if enabled and current.override:
            current.path.unlink()
        elif current.local:
            text = replace_key(current.text, "Hidden", str(not enabled).lower())
            if enabled and "X-GNOME-Autostart-enabled" in parse(current.text):
                text = replace_key(text, "X-GNOME-Autostart-enabled", "true")
            atomic_write(current.path, text)
        else:
            text = replace_key(current.text, "Hidden", "true")
            atomic_write(self.user / current.path.name, replace_key(text, OVERRIDE, "true"))
