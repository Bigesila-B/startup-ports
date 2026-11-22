import time

from PySide6.QtCore import QDateTime
from PySide6.QtWidgets import (QComboBox, QDateTimeEdit, QFormLayout, QFrame, QHBoxLayout, QLabel,
                               QMessageBox, QPushButton, QVBoxLayout, QWidget)

from .power import ACTIONS, LABELS, PowerError, PowerScheduler


class PowerPage(QWidget):
    def __init__(self, translate, scheduler: PowerScheduler):
        super().__init__()
        self.t = translate
        self.scheduler = scheduler
        self.task = None
        self.manager_available = False
        self.allowed = False

        layout = QHBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(12)
        form_panel, form_body = self.panel()
        form_body.addWidget(self.label("Set a timer", "section"))
        form = QFormLayout()
        self.action = QComboBox(objectName="power_action")
        for action in ACTIONS:
            self.action.addItem(self.t(LABELS[action]), action)
        self.when = QDateTimeEdit(objectName="power_when", calendarPopup=True)
        self.when.setDisplayFormat("yyyy-MM-dd  HH:mm")
        self.set_delay(30)
        form.addRow(self.t("Power action"), self.action)
        form.addRow(self.t("Local date and time"), self.when)
        form_body.addLayout(form)

        presets = QHBoxLayout()
        for minutes in (15, 30, 60):
            presets.addWidget(self.button(self.t("In {0} min", minutes), lambda delay=minutes: self.set_delay(delay)))
        presets.addStretch()
        form_body.addLayout(presets)
        self.schedule_button = self.button(self.t("Schedule"), self.schedule, "schedule_power", primary=True)
        form_body.addWidget(self.schedule_button)
        self.notice = self.label("", "muted")
        form_body.addWidget(self.notice)
        form_body.addStretch()
        layout.addWidget(form_panel, 3)

        task_panel, task_body = self.panel()
        task_body.addWidget(self.label("Current timer", "section"))
        self.task_title = self.label("No timer scheduled.", "section")
        task_body.addWidget(self.task_title)
        self.task_details = self.label("", "muted")
        task_body.addWidget(self.task_details)
        self.cancel_button = self.button(self.t("Cancel timer"), self.cancel, "cancel_power")
        task_body.addWidget(self.cancel_button)
        task_body.addStretch()
        task_body.addWidget(self.label("The timer keeps running when you close StartupPorts.", "muted"))
        task_body.addWidget(self.label("The timer does not wake a sleeping computer. A missed time may run when it wakes.", "muted"))
        layout.addWidget(task_panel, 2)

        self.action.currentIndexChanged.connect(self.refresh_permission)
        self.when.dateTimeChanged.connect(self.update_controls)
        self.update_controls()

    @staticmethod
    def panel():
        frame = QFrame(objectName="card")
        body = QVBoxLayout(frame)
        body.setContentsMargins(18, 16, 18, 16)
        body.setSpacing(12)
        return frame, body

    def label(self, key, name=""):
        label = QLabel(self.t(key), objectName=name, wordWrap=True)
        return label

    def button(self, title, handler, name="", primary=False):
        button = QPushButton(title, objectName=name)
        button.setProperty("primary", primary)
        button.clicked.connect(lambda: handler())
        return button

    def set_delay(self, minutes):
        now = int(time.time())
        self.when.setDateTime(QDateTime.fromSecsSinceEpoch(((now + 59) // 60 + minutes) * 60))

    @staticmethod
    def format_time(timestamp):
        return QDateTime.fromSecsSinceEpoch(timestamp).toString("yyyy-MM-dd  HH:mm:ss")

    def refresh(self, *, check_permission=True):
        try:
            self.task = self.scheduler.status()
            self.manager_available = True
        except (OSError, PowerError) as exc:
            self.manager_available = False
            self.allowed = False
            self.task = None
            self.notice.setText(self.t(str(exc)))
        if self.manager_available and check_permission:
            self.refresh_permission()
        self.update_controls()

    def refresh_permission(self):
        if not self.manager_available:
            return
        try:
            permission = self.scheduler.permission(self.action.currentData())
            self.allowed = permission == "yes"
            if self.allowed:
                notice = "Save open work before the scheduled time."
            elif permission in ("no", "challenge"):
                notice = "power_needs_permission"
            else:
                notice = "power_not_supported"
            self.notice.setText(self.t(notice))
        except (OSError, PowerError) as exc:
            self.allowed = False
            self.notice.setText(self.t(str(exc)))
        self.update_controls()

    def update_controls(self):
        task = self.task
        pending = task is not None and task.state == "scheduled"
        active = pending or (task is not None and task.state == "running")
        self.schedule_button.setEnabled(self.manager_available and self.allowed and not active and
                                        self.when.dateTime().toSecsSinceEpoch() > time.time() + 2)
        self.cancel_button.setEnabled(pending)
        self.task_title.setText(self.t(LABELS[task.action]) if task else self.t("No timer scheduled."))
        if not task:
            self.task_details.clear()
        elif pending:
            remaining = max(0, task.timestamp - int(time.time()))
            hours, rest = divmod(remaining, 3600)
            minutes, seconds = divmod(rest, 60)
            self.task_details.setText(self.t("{0}\nTime left: {1:02}:{2:02}:{3:02}", self.format_time(task.timestamp), hours, minutes, seconds))
        else:
            state = {"running": "Sending the power request…", "finished": "The system accepted the request.",
                     "failed": "The system did not carry out the request."}[task.state]
            self.task_details.setText(f"{self.format_time(task.timestamp)}\n{self.t(state)}")

    def tick(self):
        if self.manager_available and self.task:
            self.refresh(check_permission=False)
        else:
            self.update_controls()

    def schedule(self):
        action = self.action.currentData()
        when = self.when.dateTime().toSecsSinceEpoch()
        message = self.t("Schedule {0} for {1}? Save any open work before then.",
                         self.t(LABELS[action]), self.format_time(when))
        if QMessageBox.question(self, self.t("Timed shutdown"), message, QMessageBox.StandardButton.Yes |
                                QMessageBox.StandardButton.No, QMessageBox.StandardButton.No) != QMessageBox.StandardButton.Yes:
            return
        try:
            self.task = self.scheduler.schedule(action, when)
        except (OSError, PowerError) as exc:
            self.refresh()
            self.notice.setText(self.t(str(exc)))
        self.update_controls()

    def cancel(self):
        if not self.task:
            return
        try:
            self.scheduler.cancel(self.task)
        except (OSError, PowerError) as exc:
            self.refresh()
            self.notice.setText(self.t(str(exc)))
            return
        self.refresh()
        self.notice.setText(self.t("Timer canceled."))
