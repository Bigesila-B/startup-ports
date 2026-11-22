"""Exercise the real user timer with a file write in place of the power action."""
from pathlib import Path
from unittest.mock import patch
from uuid import uuid4
import os
import subprocess
import sys
import tempfile
import time

from PySide6.QtCore import QDateTime, QSize
from PySide6.QtGui import QPixmap
from PySide6.QtTest import QTest
from PySide6.QtWidgets import QApplication, QLabel, QMessageBox

from startup_ports.model import Store
from startup_ports.power import ACTIONS, PowerError, PowerScheduler
from startup_ports.ui import POWER_PAGE, STYLE, Window


class MarkerScheduler(PowerScheduler):
    def __init__(self, marker: Path):
        super().__init__(f"startupports-power-test-{uuid4().hex}")
        self.marker = marker

    def permission(self, action: str) -> str:
        return "yes" if action != "hibernate" else "na"

    def _power_command(self, action: str) -> tuple[str, ...]:
        return (sys.executable, "-c", "import sys; from pathlib import Path; Path(sys.argv[1]).write_text(sys.argv[2])",
                str(self.marker), action)


def check(root: Path, scheduler: MarkerScheduler):
    production = PowerScheduler()
    for action, method in ACTIONS.items():
        assert production._power_command(action)[-3:] == (method, "b", "false")

    app = QApplication([])
    app.setQuitOnLastWindowClosed(False)
    app.setStyle("Fusion")
    app.setStyleSheet(STYLE)
    store = Store((sys.executable,), home=root / "config")
    store.update(lambda settings: setattr(settings, "language", "zh_CN"))
    window = Window(store, power=scheduler)
    window.show()
    window.navigation[POWER_PAGE].click()
    assert window.heading.text() == window.navigation[POWER_PAGE].text() == "定时关机"
    logo = window.findChild(QLabel, "app_logo")
    assert logo and not logo.pixmap().isNull()
    icons = [button.icon().pixmap(QSize(18, 18)).toImage() for button in window.navigation]
    assert all(not image.isNull() for image in icons)
    assert all(image != other for index, image in enumerate(icons) for other in icons[index + 1:])
    page = window.power_page

    page.action.setCurrentIndex(page.action.findData("hibernate"))
    assert not page.schedule_button.isEnabled()
    assert "不支持" in page.notice.text()
    page.action.setCurrentIndex(page.action.findData("restart"))
    page.when.setDateTime(QDateTime.fromSecsSinceEpoch(int(time.time()) + 120))
    with patch.object(QMessageBox, "question", return_value=QMessageBox.StandardButton.Yes):
        page.schedule_button.click()
    pending = scheduler.status()
    assert pending and pending.action == "restart" and pending.state == "scheduled"
    assert page.cancel_button.isEnabled() and not page.schedule_button.isEnabled()
    assert "剩余时间" in page.task_details.text()

    if output := os.environ.get("STARTUPPORTS_POWER_SCREENSHOT"):
        QTest.qWait(150)
        image = QPixmap(window.size())
        window.render(image)
        assert image.save(output)
    window.close()
    assert scheduler.status() == pending, "Closing the window removed the system timer"
    window = Window(store, power=scheduler)
    window.show()
    window.navigation[POWER_PAGE].click()
    page = window.power_page
    page.cancel_button.click()
    assert scheduler.status() is None and not scheduler.marker.exists()

    try:
        scheduler.schedule("sleep", int(time.time()) - 30)
    except PowerError as exc:
        assert str(exc) == "power_future_required"
    else:
        raise AssertionError("The timer accepted a past date")

    due = scheduler.schedule("sleep", int(time.time()) + 5)
    assert due.state == "scheduled"
    for _ in range(80):
        task = scheduler.status()
        if scheduler.marker.is_file() and task and task.state == "finished":
            break
        QTest.qWait(100)
    assert scheduler.marker.read_text() == "sleep", "The real timer did not run its harmless command"
    assert scheduler.status().state == "finished"
    scheduler.schedule("shutdown", int(time.time()) + 120)
    assert scheduler.status().action == "shutdown", "Could not replace a completed timer"
    scheduler.cancel(scheduler.status())
    window.close()
    app.processEvents()
    print("PASS: four non-interactive power mappings, unsupported action, scheduling, countdown, close, cancellation, elapsed timer, and replacement.")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="startupports-power-test-") as temporary:
        root = Path(temporary)
        scheduler = MarkerScheduler(root / "marker.txt")
        try:
            check(root, scheduler)
        finally:
            subprocess.run(["systemctl", "--user", "stop", scheduler.timer, scheduler.service], capture_output=True, timeout=5)
