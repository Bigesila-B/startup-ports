"""Run against an isolated Linux configuration and local test processes."""
from pathlib import Path
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time

from PySide6.QtCore import Qt, QTimer
from PySide6.QtTest import QTest
from PySide6.QtWidgets import QApplication, QComboBox, QDialog, QLineEdit, QListWidget, QPushButton

from startup_ports.model import Store
from startup_ports.ui import STYLE, Window
from startup_ports.xdg import Autostart


def wait_for(check, label):
    for _ in range(100):
        if check():
            return
        QTest.qWait(25)
    raise AssertionError(label)


def fill_dialog(open_button, window, enter):
    errors = []

    def fill():
        dialog = next((item for item in window.findChildren(QDialog) if item.isVisible()), None)
        try:
            assert dialog is not None
            enter(dialog)
            dialog.findChild(QPushButton, "save").click()
        except Exception as exc:
            errors.append(exc)
        finally:
            if dialog and dialog.isVisible():
                errors.append(AssertionError("Dialog failed to save"))
                dialog.reject()

    QTimer.singleShot(30, fill)
    open_button.click()
    if errors:
        raise errors[0]


def check(root: Path):
    local = root / "user config"
    system = root / "system config"
    desktop = system / "autostart" / "example.desktop"
    desktop.parent.mkdir(parents=True)
    desktop.write_text("[Desktop Entry]\nType=Application\nName=Example Service\nExec=/usr/bin/true\n", encoding="utf-8")
    original = desktop.read_bytes()
    os.environ["XDG_CONFIG_HOME"] = str(local)
    os.environ["XDG_CONFIG_DIRS"] = str(system)
    os.environ["XDG_CURRENT_DESKTOP"] = "XFCE"

    script = root / "write marker.sh"
    script.write_text('#!/bin/sh\nprintf %s "$2" > "$1"\n', encoding="utf-8")
    script.chmod(0o700)
    marker = root / "did run.txt"
    args = f"'{marker}' 'with spaces; no shell expansion'"

    entrypoint = root / "app % test" / "startupports.py"
    entrypoint.parent.mkdir()
    entrypoint.write_text(Path(__file__).with_name("startupports.py").read_text(encoding="utf-8"), encoding="utf-8")
    shutil.copytree(Path(__file__).with_name("startup_ports"), entrypoint.parent / "startup_ports")
    store = Store((sys.executable, str(entrypoint)))
    store.update(lambda settings: setattr(settings, "language", "en"))

    app = QApplication([])
    app.setStyle("Fusion")
    app.setStyleSheet(STYLE)
    window = Window(store)
    window.show()
    app.processEvents()
    assert window.isVisible() and window.heading.text() == "Startup apps"

    def button(name):
        return window.findChild(QPushButton, name)

    def enter_entry(dialog):
        dialog.findChild(QLineEdit, "name").setText("Marker script")
        dialog.findChild(QLineEdit, "program").setText(str(script))
        dialog.findChild(QLineEdit, "arguments").setText(args)

    fill_dialog(button("add_entry"), window, enter_entry)
    assert window.entries.rowCount() == 1 and not store.read().entries[0].enabled
    window.entries.selectRow(0)
    button("run_entry").click()
    wait_for(marker.exists, "Run now did not launch the script")
    assert marker.read_text() == "with spaces; no shell expansion"
    marker.unlink()

    button("page_1").click()

    def enter_group(dialog):
        dialog.findChild(QLineEdit, "group_name").setText("Development")
        dialog.findChild(QListWidget, "group_choices").item(0).setCheckState(Qt.CheckState.Checked)

    fill_dialog(button("add_group"), window, enter_group)
    assert window.groups.count() == 1 and not window.groups.item(0).icon().isNull()
    button("enable_group").click()
    assert store.read().entries[0].enabled and store.dispatcher.exists()
    if shutil.which("gio"):
        subprocess.run(["gio", "launch", str(store.dispatcher)], check=True, timeout=5)
    else:
        subprocess.run([*store.command, "--autostart"], check=True, timeout=5)
    wait_for(marker.exists, "Desktop login dispatcher did not launch the enabled entry")
    marker.unlink()
    button("disable_group").click()
    assert not store.read().entries[0].enabled
    subprocess.run([*store.command, "--autostart"], check=True, timeout=5)
    time.sleep(0.15)
    assert not marker.exists(), "Disabled entry ran via the dispatcher"
    button("run_group").click()
    wait_for(marker.exists, "Run group did not run the script independently of sign-in settings")

    button("page_2").click()
    assert window.desktop_entries.rowCount() == 1
    window.desktop_entries.selectRow(0)
    button("toggle_desktop").click()
    assert not Autostart().list()[0].enabled and desktop.read_bytes() == original
    window.desktop_entries.selectRow(0)
    button("toggle_desktop").click()
    assert Autostart().list()[0].enabled and desktop.read_bytes() == original

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as tcp, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
        tcp.bind(("127.0.0.1", 0))
        tcp.listen()
        udp.bind(("127.0.0.1", 0))
        button("page_3").click()
        matches = lambda protocol, sock: any(port.protocol == protocol and port.number == sock.getsockname()[1]
                                             and port.pid == os.getpid() for port in window.ports)
        wait_for(lambda: matches("TCP", tcp) and matches("UDP", udp), "Real TCP/UDP listeners and PIDs missing")
        window.search.setText(str(tcp.getsockname()[1]))
        assert window.port_table.rowCount() >= 1
        window.port_table.selectRow(0)
        button("copy_address").click()
        assert str(tcp.getsockname()[1]) in app.clipboard().text()

    window.findChild(QComboBox, "language").setCurrentIndex(0)
    wait_for(lambda: window.heading.text() == "端口监控", "Chinese language did not switch")
    assert store.read().language == "zh_CN"
    button("page_1").click()
    assert window.heading.text() == "自启分组" and window.groups.count() == 1
    if output := os.environ.get("STARTUPPORTS_SCREENSHOT"):
        QTest.qWaitForWindowExposed(window)
        app.processEvents()
        assert window.grab().save(output)
    window.close()
    app.processEvents()
    print("PASS: Qt window, entry creation, literal arguments, launch now, group icon and controls, XDG dispatcher, reversible overrides, real TCP/UDP with PID, clipboard, and Chinese switch.")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="startupports-test-") as temporary:
        check(Path(temporary))
