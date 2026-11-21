from dataclasses import replace
from pathlib import Path
from uuid import uuid4
import locale
import shlex

from PySide6.QtCore import QPoint, QProcess, QProcessEnvironment, QSize, Qt, QTimer, QUrl
from PySide6.QtGui import QColor, QDesktopServices, QIcon, QPainter, QPixmap
from PySide6.QtWidgets import (
    QApplication, QCheckBox, QComboBox, QDialog, QFileDialog, QFormLayout, QFrame,
    QHBoxLayout, QHeaderView, QLabel, QLineEdit, QListView, QListWidget, QListWidgetItem,
    QMainWindow, QMessageBox, QPushButton, QSpinBox, QStackedWidget, QStyle,
    QTableWidget, QTableWidgetItem, QVBoxLayout, QWidget,
)

from .model import APP_ID, VERSION, Entry, Group, Store, launch
from .ports import parse_ss
from .text import translate
from .xdg import Autostart

PAGE_NAMES = ("Startup apps", "Startup groups", "Desktop startup", "Port monitor")
PAGE_ICONS = ("system-run", "folder", "preferences-system-startup", "network-wired")
PAGE_HINTS = (
    "Choose which programs and scripts run when you sign in.",
    "Run related programs together or change their sign-in settings in one place.",
    "Manage the desktop’s existing XDG startup entries for your account.",
    "Find local TCP listeners and UDP bindings.",
)
ROLE = Qt.ItemDataRole.UserRole


def icon(name: str, fallback=QStyle.StandardPixmap.SP_FileIcon) -> QIcon:
    if name.startswith("/") and Path(name).is_file():
        return QIcon(name)
    return QIcon.fromTheme(name, QApplication.style().standardIcon(fallback))


def program_icon(path: str) -> QIcon:
    stem = Path(path).stem.lower()
    if Path(path).suffix in (".sh", ".py", ".fish", ".zsh"):
        return icon("text-x-script")
    return icon(stem, QStyle.StandardPixmap.SP_ComputerIcon)


def folder_icon(members: list[Entry]) -> QIcon:
    image = QPixmap(116, 68)
    image.fill(Qt.GlobalColor.transparent)
    painter = QPainter(image)
    painter.setRenderHint(QPainter.RenderHint.Antialiasing)
    painter.setPen(Qt.PenStyle.NoPen)
    painter.setBrush(QColor("#cfddf8"))
    painter.drawRoundedRect(9, 5, 40, 26, 7, 7)
    painter.setBrush(QColor("#e5edff"))
    painter.drawRoundedRect(9, 13, 98, 51, 8, 8)
    for index, entry in enumerate(members[:4]):
        painter.drawPixmap(QPoint(36 + 24 * (index % 2), 16 + 23 * (index // 2)), program_icon(entry.program).pixmap(20, 20))
    painter.end()
    return QIcon(image)


class Window(QMainWindow):
    def __init__(self, store: Store):
        super().__init__()
        self.store = store
        self.settings = store.read()
        self.desktop = Autostart(store.home)
        self.language = self.settings.language or ("zh_CN" if (locale.getlocale()[0] or "").startswith("zh") else "en")
        self.page = 0
        self.ports = []
        self.launched = []
        self.port_process = QProcess(self)
        environment = QProcessEnvironment.systemEnvironment()
        environment.insert("LC_ALL", "C")
        self.port_process.setProcessEnvironment(environment)
        self.port_process.finished.connect(self.read_ports)
        self.port_process.errorOccurred.connect(self.port_error)
        self.port_timeout = QTimer(self, singleShot=True, interval=3500)
        self.port_timeout.timeout.connect(self.port_process.kill)
        self.timer = QTimer(self, interval=2000)
        self.timer.timeout.connect(self.tick)
        self.setWindowTitle("StartupPorts · Linux")
        self.resize(1160, 690)
        self.setMinimumSize(880, 550)
        self.build()
        self.timer.start()

    def t(self, text: str, *args) -> str:
        return translate(self.language, text, *args)

    def label(self, key: str, name: str = "") -> QLabel:
        label = QLabel(self.t(key))
        if name:
            label.setObjectName(name)
        return label

    def button(self, text, action, name="", primary=False) -> QPushButton:
        button = QPushButton(self.t(text))
        button.setObjectName(name)
        button.setProperty("primary", primary)
        button.clicked.connect(lambda: action())
        return button

    def build(self):
        root = QWidget(objectName="root")
        self.setCentralWidget(root)
        frame = QHBoxLayout(root)
        frame.setContentsMargins(0, 0, 0, 0)
        frame.setSpacing(0)
        sidebar = QFrame(objectName="sidebar")
        sidebar.setFixedWidth(194)
        side = QVBoxLayout(sidebar)
        side.setContentsMargins(16, 24, 16, 17)
        title = self.label("StartupPorts", "wordmark")
        side.addWidget(title)
        side.addSpacing(15)
        self.navigation = []
        for index, name in enumerate(PAGE_NAMES):
            button = self.button(name, lambda checked=False, page=index: self.navigate(page), f"page_{index}")
            button.setProperty("navigation", True)
            button.setCheckable(True)
            button.setIcon(icon(PAGE_ICONS[index]))
            self.navigation.append(button)
            side.addWidget(button)
        side.addStretch()
        side.addWidget(self.button("Check releases", self.open_releases))
        side.addWidget(self.label("Language", "muted"))
        languages = QComboBox(objectName="language")
        languages.addItems(["简体中文", "English"])
        languages.setCurrentIndex(0 if self.language == "zh_CN" else 1)
        languages.currentIndexChanged.connect(lambda index: QTimer.singleShot(0, lambda: self.change_language(index)))
        side.addWidget(languages)
        side.addWidget(self.label(f"Linux  {VERSION}", "muted"))
        frame.addWidget(sidebar)

        main = QVBoxLayout()
        main.setContentsMargins(26, 22, 26, 16)
        header = QHBoxLayout()
        titles = QVBoxLayout()
        self.heading = self.label("", "heading")
        self.subtitle = self.label("", "muted")
        self.subtitle.setWordWrap(True)
        titles.addWidget(self.heading)
        titles.addWidget(self.subtitle)
        header.addLayout(titles, 1)
        header.addWidget(self.button("Refresh", self.refresh, "refresh"))
        main.addLayout(header)
        main.addSpacing(9)
        self.pages = QStackedWidget(objectName="pages")
        self.pages.addWidget(self.entries_page())
        self.pages.addWidget(self.groups_page())
        self.pages.addWidget(self.desktop_page())
        self.pages.addWidget(self.ports_page())
        main.addWidget(self.pages, 1)
        self.status = self.label("", "muted")
        self.status.setWordWrap(True)
        main.addWidget(self.status)
        frame.addLayout(main, 1)
        self.navigate(self.page)

    def navigate(self, page: int):
        self.page = page
        self.pages.setCurrentIndex(page)
        self.heading.setText(self.t(PAGE_NAMES[page]))
        self.subtitle.setText(self.t(PAGE_HINTS[page]))
        for index, button in enumerate(self.navigation):
            button.setChecked(index == page)
        self.status.clear()
        self.refresh()

    def change_language(self, index: int):
        language = "zh_CN" if index == 0 else "en"
        if self.language != language and self.change(lambda settings: setattr(settings, "language", language), refresh=False):
            self.language = language
            self.build()

    def open_releases(self):
        QDesktopServices.openUrl(QUrl("https://github.com/Bigesila-B/startup-ports/releases"))

    def panel(self):
        panel = QFrame(objectName="card")
        body = QVBoxLayout(panel)
        body.setContentsMargins(14, 12, 14, 12)
        body.setSpacing(9)
        return panel, body

    def table(self, headers, name):
        table = QTableWidget(0, len(headers), objectName=name)
        table.setHorizontalHeaderLabels([self.t(header) for header in headers])
        table.verticalHeader().hide()
        table.verticalHeader().setDefaultSectionSize(38)
        table.setSelectionBehavior(QTableWidget.SelectionBehavior.SelectRows)
        table.setSelectionMode(QTableWidget.SelectionMode.SingleSelection)
        table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
        table.setAlternatingRowColors(True)
        table.setShowGrid(False)
        table.setIconSize(QSize(19, 19))
        table.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeMode.ResizeToContents)
        table.horizontalHeader().setStretchLastSection(True)
        return table

    @staticmethod
    def selected(table):
        row = table.currentRow()
        item = table.item(row, 0) if row >= 0 and table.selectedItems() else None
        return item.data(ROLE) if item else None

    def fill(self, table, rows):
        selected = self.selected(table)
        blocked = table.blockSignals(True)
        table.clearContents()
        table.setRowCount(len(rows))
        selected_row = -1
        for row, (data, row_icon, columns) in enumerate(rows):
            for index, value in enumerate(columns):
                item = QTableWidgetItem(str(value))
                item.setToolTip(str(value))
                if index == 0:
                    item.setData(ROLE, data)
                    if row_icon is not None:
                        item.setIcon(row_icon)
                table.setItem(row, index, item)
            if data == selected:
                selected_row = row
        table.clearSelection()
        if selected_row >= 0:
            table.selectRow(selected_row)
        table.blockSignals(blocked)

    def entries_page(self):
        panel, body = self.panel()
        bar = QHBoxLayout()
        bar.addWidget(self.button("Add item", self.edit_entry, "add_entry", primary=True))
        bar.addStretch()
        self.entry_run = self.button("Run now", lambda: self.launch_entries([self.current_entry()]), "run_entry")
        self.entry_toggle = self.button("Enable at sign-in", self.toggle_entry, "toggle_entry")
        self.entry_edit = self.button("Edit", lambda: self.edit_entry(self.current_entry()), "edit_entry")
        self.entry_delete = self.button("Delete", self.delete_entry, "delete_entry")
        for button in (self.entry_run, self.entry_toggle, self.entry_edit, self.entry_delete):
            bar.addWidget(button)
        body.addLayout(bar)
        self.entries = self.table(("Name", "Sign-in", "Delay", "Program"), "entries")
        self.entries.itemSelectionChanged.connect(self.entry_controls)
        self.entries.itemDoubleClicked.connect(lambda _: self.edit_entry(self.current_entry()))
        body.addWidget(self.entries, 1)
        self.entry_hint = self.label("Select an item to change its settings.", "muted")
        body.addWidget(self.entry_hint)
        return panel

    def current_entry(self):
        selected = self.selected(self.entries)
        return next((entry for entry in self.settings.entries if entry.id == selected), None)

    def entry_controls(self):
        entry = self.current_entry()
        for button in (self.entry_run, self.entry_toggle, self.entry_edit, self.entry_delete):
            button.setEnabled(entry is not None)
        self.entry_toggle.setText(self.t("Disable at sign-in" if entry and entry.enabled else "Enable at sign-in"))
        self.entry_hint.setText(self.t("Select an item to change its settings." if self.settings.entries else "No startup apps yet."))

    def groups_page(self):
        page = QWidget()
        layout = QHBoxLayout(page)
        layout.setContentsMargins(0, 0, 0, 0)
        left, list_body = self.panel()
        add = QHBoxLayout()
        add.addWidget(self.button("New group", self.edit_group, "add_group", primary=True))
        add.addStretch()
        list_body.addLayout(add)
        self.groups = QListWidget(objectName="groups")
        self.groups.setViewMode(QListView.ViewMode.IconMode)
        self.groups.setMovement(QListView.Movement.Static)
        self.groups.setResizeMode(QListView.ResizeMode.Adjust)
        self.groups.setGridSize(QSize(142, 101))
        self.groups.setIconSize(QSize(116, 68))
        self.groups.setUniformItemSizes(True)
        self.groups.currentItemChanged.connect(self.group_controls)
        list_body.addWidget(self.groups, 1)
        self.groups_empty = self.label("No groups yet.", "muted")
        self.groups_empty.setWordWrap(True)
        list_body.addWidget(self.groups_empty)
        layout.addWidget(left, 4)

        right, details = self.panel()
        self.group_name = self.label("", "section")
        details.addWidget(self.group_name)
        self.group_summary = self.label("", "muted")
        details.addWidget(self.group_summary)
        self.group_run = self.button("Run group now", lambda: self.launch_entries(self.group_members()), "run_group", primary=True)
        details.addWidget(self.group_run)
        toggle_bar = QHBoxLayout()
        self.group_on = self.button("Enable all", lambda: self.toggle_members({entry.id for entry in self.group_members()}, True), "enable_group")
        self.group_off = self.button("Disable all", lambda: self.toggle_members({entry.id for entry in self.group_members()}, False), "disable_group")
        toggle_bar.addWidget(self.group_on)
        toggle_bar.addWidget(self.group_off)
        details.addLayout(toggle_bar)
        description = self.label("Changes here apply to the same entries in Startup apps.", "muted")
        description.setWordWrap(True)
        details.addWidget(description)
        self.members = self.table(("Members", "Sign-in"), "members")
        details.addWidget(self.members, 1)
        group_bar = QHBoxLayout()
        self.group_edit = self.button("Edit group", lambda: self.edit_group(self.current_group()), "edit_group")
        self.group_delete = self.button("Delete group", self.delete_group, "delete_group")
        group_bar.addWidget(self.group_edit)
        group_bar.addStretch()
        group_bar.addWidget(self.group_delete)
        details.addLayout(group_bar)
        layout.addWidget(right, 3)
        return page

    def current_group(self):
        selected = self.groups.currentItem()
        return next((group for group in self.settings.groups if selected and selected.data(ROLE) == group.id), None)

    def group_members(self, group=None):
        group = group or self.current_group()
        ids = set(group.members) if group else set()
        return [entry for entry in self.settings.entries if entry.id in ids]

    def group_controls(self):
        group = self.current_group()
        members = self.group_members(group)
        self.group_name.setText(group.name if group else self.t("Startup groups"))
        self.group_summary.setText(self.t("{0} in group · {1} enabled", len(members), sum(entry.enabled for entry in members))
                                   if group else self.t("No members selected."))
        self.group_run.setEnabled(bool(members))
        self.group_on.setEnabled(any(not entry.enabled for entry in members))
        self.group_off.setEnabled(any(entry.enabled for entry in members))
        self.group_edit.setEnabled(group is not None)
        self.group_delete.setEnabled(group is not None)
        self.fill(self.members, [(entry.id, program_icon(entry.program), (entry.name, self.state(entry.enabled))) for entry in members])

    def desktop_page(self):
        panel, body = self.panel()
        bar = QHBoxLayout()
        self.desktop_count = self.label("", "muted")
        bar.addWidget(self.desktop_count)
        bar.addStretch()
        self.desktop_toggle = self.button("Disable at sign-in", self.toggle_desktop, "toggle_desktop", primary=True)
        bar.addWidget(self.desktop_toggle)
        body.addLayout(bar)
        self.desktop_entries = self.table(("Name", "Status", "Source", "Reason", "Command"), "desktop_entries")
        self.desktop_entries.itemSelectionChanged.connect(self.desktop_controls)
        body.addWidget(self.desktop_entries, 1)
        hint = self.label("System defaults are changed for your account through a local override.", "muted")
        hint.setWordWrap(True)
        body.addWidget(hint)
        return panel

    def desktop_controls(self):
        entry = self.selected(self.desktop_entries)
        self.desktop_toggle.setEnabled(entry is not None and not entry.reason)
        self.desktop_toggle.setText(self.t("Disable at sign-in" if entry and entry.enabled else "Enable at sign-in"))

    def ports_page(self):
        panel, body = self.panel()
        bar = QHBoxLayout()
        self.search = QLineEdit(objectName="port_search")
        self.search.setPlaceholderText(self.t("Search address, port, process, or PID"))
        self.search.textChanged.connect(self.filter_ports)
        bar.addWidget(self.search, 1)
        self.copy = self.button("Copy address", self.copy_port, "copy_address")
        self.open = self.button("Open in browser", self.open_port, "open_port")
        bar.addWidget(self.copy)
        bar.addWidget(self.open)
        body.addLayout(bar)
        self.port_table = self.table(("Process", "PID", "Protocol", "Local address"), "ports")
        self.port_table.itemSelectionChanged.connect(self.port_controls)
        body.addWidget(self.port_table, 1)
        return panel

    def port_controls(self):
        entry = self.selected(self.port_table)
        self.copy.setEnabled(entry is not None)
        self.open.setEnabled(entry is not None and bool(entry.browser_address))

    def copy_port(self):
        if entry := self.selected(self.port_table):
            QApplication.clipboard().setText(entry.address)

    def open_port(self):
        if (entry := self.selected(self.port_table)) and entry.browser_address:
            QDesktopServices.openUrl(QUrl(entry.browser_address))

    def state(self, enabled):
        return self.t("Enabled" if enabled else "Disabled")

    def refresh(self):
        try:
            if self.page == 0:
                self.settings = self.store.read()
                self.fill(self.entries, [(entry.id, program_icon(entry.program), (entry.name, self.state(entry.enabled),
                           self.t("{0} s", entry.delay), entry.program)) for entry in self.settings.entries])
                self.entry_controls()
            elif self.page == 1:
                self.settings = self.store.read()
                previous = self.current_group()
                blocked = self.groups.blockSignals(True)
                self.groups.clear()
                for group in self.settings.groups:
                    members = self.group_members(group)
                    item = QListWidgetItem(folder_icon(members), group.name)
                    item.setData(ROLE, group.id)
                    item.setToolTip("\n".join([group.name, *(entry.name for entry in members)]))
                    self.groups.addItem(item)
                    if previous and previous.id == group.id:
                        self.groups.setCurrentItem(item)
                if self.groups.currentRow() < 0 and self.groups.count():
                    self.groups.setCurrentRow(0)
                self.groups.blockSignals(blocked)
                self.groups_empty.setVisible(not self.settings.groups)
                self.group_controls()
            elif self.page == 2:
                rows = self.desktop.list(self.language)
                self.fill(self.desktop_entries, [(item, icon(item.icon), (item.name, self.state(item.enabled),
                          self.t("Current user" if item.local and not item.override else "System default"),
                          self.t(item.reason) if item.reason else "", item.command)) for item in rows])
                self.desktop_count.setText(self.t("{0} items · {1} editable", len(rows), sum(not item.reason for item in rows))
                                           if rows else self.t("No desktop startup entries found."))
                self.desktop_controls()
            else:
                self.filter_ports()
                self.scan_ports()
        except (OSError, ValueError) as exc:
            self.error(exc)

    def tick(self):
        self.launched = [child for child in self.launched if child.poll() is None]
        if self.page == 3:
            self.scan_ports()

    def closeEvent(self, event):
        self.timer.stop()
        self.port_timeout.stop()
        if self.port_process.state() != QProcess.ProcessState.NotRunning:
            self.port_process.kill()
            self.port_process.waitForFinished()
        super().closeEvent(event)

    def scan_ports(self):
        if self.port_process.state() == QProcess.ProcessState.NotRunning:
            self.port_process.start("ss", ["-H", "-lntup"])
            self.port_timeout.start()

    def read_ports(self, code, status):
        self.port_timeout.stop()
        output = bytes(self.port_process.readAllStandardOutput()).decode("utf-8", errors="replace")
        error = bytes(self.port_process.readAllStandardError()).decode("utf-8", errors="replace").strip()
        if self.page != 3:
            return
        if code or status != QProcess.ExitStatus.NormalExit:
            self.status.setText(self.t("Could not read local ports.") + (f" {error}" if error else ""))
            return
        self.ports = parse_ss(output)
        self.filter_ports()

    def port_error(self, error):
        if error == QProcess.ProcessError.FailedToStart and self.page == 3:
            self.port_timeout.stop()
            self.status.setText(self.t("Install iproute2 to make the ss command available."))

    def filter_ports(self):
        query = self.search.text().strip().casefold()
        rows = [entry for entry in self.ports if query in f"{entry.protocol} {entry.address} {entry.process} {entry.pid or ''}".casefold()]
        self.fill(self.port_table, [(entry, program_icon(entry.process) if entry.process else icon("network-server"),
                   (entry.process or self.t("Process hidden by Linux"), entry.pid or "—", entry.protocol, entry.address)) for entry in rows])
        self.port_controls()
        self.status.setText("" if rows else self.t("No matching ports."))

    def error(self, exc):
        QMessageBox.warning(self, "StartupPorts", self.t("Could not complete the operation.") + "\n\n" + self.t(str(exc)))

    def change(self, mutation, refresh=True):
        try:
            self.settings = self.store.update(mutation)
        except (OSError, ValueError) as exc:
            self.error(exc)
            return False
        if refresh:
            self.refresh()
        return True

    def confirm(self, message):
        return QMessageBox.question(self, "StartupPorts", self.t(message),
                                    QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                                    QMessageBox.StandardButton.No) == QMessageBox.StandardButton.Yes

    def launch_entries(self, entries):
        errors = []
        count = 0
        for entry in entries:
            if entry is not None:
                try:
                    self.launched.append(launch(entry))
                    count += 1
                except (OSError, ValueError) as exc:
                    errors.append(f"{entry.name} · {self.t(str(exc))}")
        self.status.setText(self.t("Started {0} item(s).", count))
        if errors:
            QMessageBox.warning(self, "StartupPorts", "\n".join(errors))

    def toggle_members(self, ids: set[str], enabled: bool):
        if ids:
            def update(settings):
                settings.entries = [replace(entry, enabled=enabled) if entry.id in ids else entry for entry in settings.entries]
            self.change(update)

    def toggle_entry(self):
        if entry := self.current_entry():
            self.toggle_members({entry.id}, not entry.enabled)

    def toggle_desktop(self):
        if (entry := self.selected(self.desktop_entries)) and not entry.reason:
            try:
                self.desktop.set_enabled(entry, not entry.enabled)
            except (OSError, ValueError) as exc:
                self.error(exc)
            self.refresh()

    def delete_entry(self):
        if (entry := self.current_entry()) and self.confirm("Remove this startup item?"):
            def remove(settings):
                settings.entries = [row for row in settings.entries if row.id != entry.id]
                settings.groups = [replace(group, members=[key for key in group.members if key != entry.id]) for group in settings.groups]
            self.change(remove)

    def delete_group(self):
        if (group := self.current_group()) and self.confirm("Remove this group? Its programs remain in Startup apps."):
            self.change(lambda settings: setattr(settings, "groups", [row for row in settings.groups if row.id != group.id]))

    def dialog(self, title, name):
        dialog = QDialog(self, objectName=name)
        dialog.setWindowTitle(self.t(title))
        dialog.setMinimumWidth(530)
        layout = QVBoxLayout(dialog)
        layout.setContentsMargins(20, 17, 20, 17)
        layout.addWidget(self.label(title, "section"))
        return dialog, layout

    def dialog_actions(self, dialog, layout, save):
        errors = self.label("", "error")
        errors.setWordWrap(True)
        layout.addWidget(errors)
        actions = QHBoxLayout()
        actions.addStretch()
        actions.addWidget(self.button("Cancel", dialog.reject))
        commit = self.button("Save", lambda: save(errors), "save", primary=True)
        commit.setDefault(True)
        actions.addWidget(commit)
        layout.addLayout(actions)

    def path_field(self, textbox, picker):
        widget = QWidget()
        line = QHBoxLayout(widget)
        line.setContentsMargins(0, 0, 0, 0)
        line.addWidget(textbox, 1)
        line.addWidget(self.button("Browse", picker))
        return widget

    def edit_entry(self, entry=None):
        dialog, layout = self.dialog("Edit startup item" if entry else "Add startup item", "entry_editor")
        form = QFormLayout()
        name = QLineEdit(entry.name if entry else "", objectName="name")
        name.setMaxLength(100)
        program = QLineEdit(entry.program if entry else "", objectName="program")
        directory = QLineEdit(entry.directory if entry else "", objectName="directory")
        directory.setPlaceholderText(self.t("Defaults to the program’s directory"))
        arguments = QLineEdit(shlex.join(entry.args) if entry else "", objectName="arguments")
        arguments.setPlaceholderText(self.t("Optional, for example --profile work"))
        delay = QSpinBox(objectName="delay", maximum=600, value=entry.delay if entry else 0)
        enabled = QCheckBox(self.t("Start when I sign in"), objectName="enabled", checked=bool(entry and entry.enabled))

        def choose_program():
            filename, _ = QFileDialog.getOpenFileName(dialog, self.t("Program or executable script"), program.text() or str(Path.home()))
            if filename:
                program.setText(filename)
                if not name.text().strip():
                    name.setText(Path(filename).stem)

        def choose_directory():
            chosen = QFileDialog.getExistingDirectory(dialog, self.t("Working directory"), directory.text() or str(Path.home()))
            if chosen:
                directory.setText(chosen)

        form.addRow(self.t("Name"), name)
        form.addRow(self.t("Program or executable script"), self.path_field(program, choose_program))
        form.addRow(self.t("Arguments"), arguments)
        form.addRow(self.t("Working directory"), self.path_field(directory, choose_directory))
        form.addRow(self.t("Seconds after sign-in"), delay)
        form.addRow(enabled)
        layout.addLayout(form)

        def save(errors):
            try:
                argv = shlex.split(arguments.text())
            except ValueError:
                errors.setText(self.t("invalid_arguments"))
                return
            try:
                target = Path(program.text().strip()).expanduser()
                cwd = Path(directory.text().strip()).expanduser() if directory.text().strip() else target.parent
                updated = Entry(entry.id if entry else uuid4().hex, name.text().strip(), str(target), argv, str(cwd), delay.value(), enabled.isChecked())
                updated.validate()
            except (OSError, ValueError, RuntimeError) as exc:
                errors.setText(self.t(str(exc)))
                return

            def update(settings):
                if entry and not any(row.id == entry.id for row in settings.entries):
                    raise ValueError("entry_missing")
                settings.entries = [row for row in settings.entries if row.id != updated.id] + [updated]
                settings.entries.sort(key=lambda row: (row.name.casefold(), row.id))

            if self.change(update):
                dialog.accept()

        self.dialog_actions(dialog, layout, save)
        name.setFocus()
        dialog.exec()
        dialog.deleteLater()

    def edit_group(self, group=None):
        dialog, layout = self.dialog("Edit group" if group else "New group", "group_editor")
        form = QFormLayout()
        name = QLineEdit(group.name if group else "", objectName="group_name")
        name.setMaxLength(100)
        form.addRow(self.t("Group name"), name)
        layout.addLayout(form)
        layout.addWidget(self.label("Select startup apps to include.", "muted"))
        choices = QListWidget(objectName="group_choices")
        choices.setMinimumHeight(160)
        selected = set(group.members) if group else set()
        for entry in self.settings.entries:
            choice = QListWidgetItem(program_icon(entry.program), entry.name, choices)
            choice.setToolTip(entry.program)
            choice.setData(ROLE, entry.id)
            choice.setFlags(choice.flags() | Qt.ItemFlag.ItemIsUserCheckable)
            choice.setCheckState(Qt.CheckState.Checked if entry.id in selected else Qt.CheckState.Unchecked)
        layout.addWidget(choices, 1)

        def save(errors):
            text = name.text().strip()
            if not text:
                errors.setText(self.t("invalid_name"))
                return
            member_ids = [choices.item(index).data(ROLE) for index in range(choices.count())
                          if choices.item(index).checkState() == Qt.CheckState.Checked]
            key = group.id if group else uuid4().hex

            def update(settings):
                if group and not any(row.id == group.id for row in settings.groups):
                    raise ValueError("group_missing")
                if any(row.name.casefold() == text.casefold() and row.id != key for row in settings.groups):
                    raise ValueError("group_exists")
                available = {entry.id for entry in settings.entries}
                updated = Group(key, text, [member for member in member_ids if member in available])
                settings.groups = [row for row in settings.groups if row.id != key] + [updated]
                settings.groups.sort(key=lambda row: (row.name.casefold(), row.id))

            if self.change(update):
                for index in range(self.groups.count()):
                    if self.groups.item(index).data(ROLE) == key:
                        self.groups.setCurrentRow(index)
                        break
                dialog.accept()

        self.dialog_actions(dialog, layout, save)
        dialog.exec()
        dialog.deleteLater()


STYLE = """
QWidget { color: #16263c; font-size: 13px; }
QWidget#root, QDialog { background: #f1f5fb; }
QFrame#sidebar { background: #ffffff; border-right: 1px solid #dfe7f2; }
QFrame#card { background: white; border: 1px solid #dce5f2; border-radius: 12px; }
QLabel#wordmark { color: #18365f; font-size: 22px; font-weight: 700; padding: 2px 4px; }
QLabel#heading { font-size: 30px; font-weight: 700; color: #162841; }
QLabel#section { font-size: 21px; font-weight: 650; }
QLabel#muted { color: #526783; }
QLabel#error { color: #9e2d44; }
QPushButton, QComboBox, QLineEdit, QSpinBox { min-height: 22px; padding: 5px 9px; background: white;
    border: 1px solid #cfdaea; border-radius: 7px; }
QPushButton { padding: 6px 11px; }
QPushButton:hover { background: #edf4ff; border-color: #9dafcc; }
QPushButton:disabled { color: #718198; background: #f4f7fb; border-color: #e0e7f1; }
QPushButton[primary="true"] { color: white; background: #2756d3; border-color: #2756d3; font-weight: 600; }
QPushButton[primary="true"]:hover { background: #2149b4; }
QPushButton[primary="true"]:disabled { color: #647b9f; background: #e9eff8; border-color: #dce6f6; }
QPushButton[navigation="true"] { text-align: left; padding: 9px; background: transparent; border: 1px solid transparent; }
QPushButton[navigation="true"]:hover { background: #f3f7ff; }
QPushButton[navigation="true"]:checked { color: #204ebd; background: #e5edff; border-color: #d9e5ff; font-weight: 650; }
QTableWidget, QListWidget { background: #ffffff; alternate-background-color: #f6f9fe;
    border: 1px solid #e0e8f3; border-radius: 6px; selection-background-color: #e0ebff; selection-color: #183b7b; }
QListWidget#groups { background: #f8fafe; }
QListWidget#groups::item { padding: 4px; border: 1px solid transparent; border-radius: 8px; }
QListWidget#groups::item:selected { border-color: #7597eb; background: #e4eeff; }
QHeaderView::section { background: #edf3fb; color: #304968; padding: 7px; border: 0; font-weight: 600; }
QLineEdit:focus, QComboBox:focus, QSpinBox:focus, QPushButton:focus { border-color: #7298f1; }
"""


def run(store: Store) -> int:
    app = QApplication([])
    app.setApplicationName("StartupPorts")
    app.setDesktopFileName(APP_ID)
    app.setStyle("Fusion")
    app.setStyleSheet(STYLE)
    try:
        window = Window(store)
    except (OSError, ValueError) as exc:
        QMessageBox.critical(None, "StartupPorts", str(exc))
        return 1
    window.show()
    return app.exec()
