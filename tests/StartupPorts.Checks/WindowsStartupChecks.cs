using System.Windows.Controls;
using System.Windows;
using System.Globalization;
using Microsoft.Win32;
using StartupPorts;

internal static class WindowsStartupChecks
{
    public static void CheckControls(MainWindow window, WindowsStartupItem item)
    {
        var grid = (DataGrid)window.FindName("WindowsStartupGrid");
        var button = (Button)window.FindName("ToggleWindowsStartupButton");
        var systemButton = (Button)window.FindName("OpenWindowsStartupButton");
        var hint = (TextBlock)window.FindName("WindowsStartupHint");
        var active = item with { Toggle = new(true, _ => throw new InvalidOperationException()) };
        var inactive = item with { Toggle = new(false, _ => throw new InvalidOperationException()) };
        var task = item with { IsScheduledTask = true, Restriction = WindowsStartupRestriction.TaskOtherTriggers };
        grid.ItemsSource = new[] { active, inactive, task };
        grid.SelectedItem = active;
        Require(button.IsEnabled && (string)button.Content == UiText.L("WindowsStartupDisable"),
            "An enabled entry did not offer to disable sign-in startup.");
        grid.SelectedItem = inactive;
        Require(button.IsEnabled && (string)button.Content == UiText.L("WindowsStartupEnable"),
            "A disabled entry did not offer to restore sign-in startup.");
        grid.SelectedItem = task;
        Require(!button.IsEnabled && (string)systemButton.Content == UiText.L("OpenTaskScheduler") &&
            hint.Text == UiText.L("WindowsStartupTaskOtherTriggersHint"), "A restricted task did not explain its other triggers.");
        var template = ((DataGridTemplateColumn)grid.Columns[1]).CellTemplate;
        var cell = new ContentPresenter { Content = task, ContentTemplate = template };
        cell.Measure(new Size(120, 53));
        cell.Arrange(new Rect(0, 0, 120, 53));
        cell.UpdateLayout();
        var reason = (TextBlock)template.FindName("RestrictionText", cell);
        Require(reason.Visibility == Visibility.Visible && reason.Text == UiText.L("WindowsStartupTaskOtherTriggersLabel"),
            "The restricted row did not show its reason.");
        var denied = task with { Restriction = WindowsStartupRestriction.PermissionDenied };
        grid.ItemsSource = new[] { denied };
        grid.SelectedItem = denied;
        Require(!button.IsEnabled && (string)systemButton.Content == UiText.L("OpenTaskSchedulerAsAdmin"),
            "A write-protected task did not offer the administrator console.");
        foreach (var restriction in Enum.GetValues<WindowsStartupRestriction>())
        {
            var restricted = item with { Restriction = restriction };
            Require(restricted.RestrictionLabel.Length > 0 && restricted.Hint.Length > 0,
                $"The restriction {restriction} is missing its row label or details.");
        }
        grid.ItemsSource = null;
    }

    public static void Run()
    {
        CheckRunEntry();
        CheckApprovalFormat();
        CheckTaskAccess();
        CheckScheduledEntry(anyUser: false);
        CheckScheduledEntry(anyUser: true);
        CheckScheduledEntry(anyUser: true, withOtherTrigger: true);
        Console.WriteLine("Verified Windows Run entries, startup approval encoding, and login-only task controls for both trigger scopes.");
    }

    private static void CheckTaskAccess()
    {
        var sid = AppIdentity.Sid;
        Require(TaskWriteAccess.IsDenied($"O:{sid}G:SYD:(A;;FR;;;{sid})"), "A read-only task was treated as writable.");
        Require(!TaskWriteAccess.IsDenied($"O:{sid}G:SYD:(A;;FRFW;;;{sid})"), "A writable task was treated as read-only.");
    }

    private static void CheckApprovalFormat()
    {
        var path = $@"Software\StartupPorts Test {Guid.NewGuid():N}";
        const string name = "Example.lnk";
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using var key = root.CreateSubKey(path);
            var approval = new byte[12];
            approval[0] = 6;
            key.SetValue(name, approval, RegistryValueKind.Binary);
            WindowsStartup.WriteApproval(key, name, enabled: false);
            approval = (byte[])key.GetValue(name)!;
            Require(approval.Length == 12 && BitConverter.ToUInt32(approval) == 7 && BitConverter.ToInt64(approval, 4) > 0,
                "Disabling an approval lost its flag bits or timestamp.");
            WindowsStartup.WriteApproval(key, name, enabled: true);
            approval = (byte[])key.GetValue(name)!;
            Require(BitConverter.ToUInt32(approval) == 6 && BitConverter.ToInt64(approval, 4) == 0,
                "Restoring an approval did not clear its timestamp or preserve the flag bits.");
        }
        finally { root.DeleteSubKey(path, throwOnMissingSubKey: false); }
    }

    private static void CheckRunEntry()
    {
        const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string approvalPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string command = "\"%SystemRoot%\\System32\\cmd.exe\" /d /c exit 0";
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var run = root.CreateSubKey(runPath);
        var name = "StartupPorts Test " + Guid.NewGuid().ToString("N");
        try
        {
            run.SetValue(name, command, RegistryValueKind.ExpandString);
            CheckToggle($"CurrentUser/Registry64/Run/{name}");
            Require((string?)run.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) == command &&
                run.GetValueKind(name) == RegistryValueKind.ExpandString, "The Run command or its value type was modified.");
        }
        finally
        {
            run.DeleteValue(name, throwOnMissingValue: false);
            using var approval = root.OpenSubKey(approvalPath, writable: true);
            approval?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    private static void CheckScheduledEntry(bool anyUser, bool withOtherTrigger = false)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        service.Connect();
        dynamic folder = service.GetFolder(@"\");
        dynamic definition = service.NewTask(0);
        definition.Principal.UserId = AppIdentity.Sid;
        definition.Principal.LogonType = 3;
        dynamic trigger = definition.Triggers.Create(9);
        if (!anyUser)
            trigger.UserId = AppIdentity.Sid;
        trigger.Enabled = true;
        if (withOtherTrigger)
        {
            dynamic time = definition.Triggers.Create(1);
            time.StartBoundary = DateTime.Today.AddDays(2).ToString("s", CultureInfo.InvariantCulture);
        }
        var inspected = TaskStore.InspectOtherLogonTask((object)definition);
        var expectedReason = withOtherTrigger ? WindowsStartupRestriction.TaskOtherTriggers : (WindowsStartupRestriction?)null;
        Require(inspected.Matches && inspected.AllUsers == anyUser && inspected.Restriction == expectedReason,
            "A logon-only task or a task with another trigger was classified incorrectly.");
        if (anyUser)
            return; // Registering an all-user trigger requires elevation, even for a current-user principal.

        dynamic action = definition.Actions.Create(0);
        action.Path = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        action.Arguments = "/d /c exit 0";
        var name = "StartupPorts Test " + Guid.NewGuid().ToString("N");
        folder.RegisterTaskDefinition(name, definition, 2, AppIdentity.Sid, null, 3, null);
        try { CheckToggle("\\" + name); }
        finally { folder.DeleteTask(name, 0); }
    }

    private static void CheckToggle(string id)
    {
        WindowsStartupToggle ReadToggle() => WindowsStartup.Read().Items.Single(item => item.Id == id).Toggle
            ?? throw new InvalidOperationException($"No toggle for the startup fixture {id}.");

        var toggle = ReadToggle();
        Require(toggle.Enabled, "The Windows fixture was not initially enabled.");
        toggle.SetEnabled(false);
        toggle = ReadToggle();
        Require(!toggle.Enabled, "Windows startup state did not survive disabling and reloading.");
        toggle.SetEnabled(true);
        Require(ReadToggle().Enabled, "Windows startup state did not survive restoring and reloading.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
