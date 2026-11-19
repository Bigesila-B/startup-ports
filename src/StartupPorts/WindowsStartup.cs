using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

using static StartupPorts.UiText;

namespace StartupPorts;

internal enum WindowsStartupRestriction
{
    OneTime, AllUsers, Unnamed, UnknownApproval, TaskAccount, TaskOtherTriggers, TaskScope, TaskLogonDisabled, PermissionDenied,
}

internal sealed record WindowsStartupToggle(bool Enabled, Action<bool> SetEnabled, string? HintResource = null);
internal sealed record WindowsStartupItem(string Id, string Name, string Source, string Scope, string State, string Command,
    BitmapSource? Icon, WindowsStartupToggle? Toggle = null, WindowsStartupRestriction? Restriction = null, bool IsScheduledTask = false)
{
    public string RestrictionLabel => Restriction is { } reason ? L($"WindowsStartup{reason}Label") : "";
    public string Hint => L(Restriction is { } reason ? $"WindowsStartup{reason}Hint" :
        Toggle?.HintResource ?? "WindowsStartupNextSignIn");
}
internal sealed record WindowsStartupSnapshot(IReadOnlyList<WindowsStartupItem> Items, IReadOnlyList<string> Issues);

internal static class WindowsStartup
{
    private const string CurrentVersion = @"Software\Microsoft\Windows\CurrentVersion\";
    private const string StartupApproved = CurrentVersion + @"Explorer\StartupApproved\";

    public static WindowsStartupSnapshot Read()
    {
        var items = new List<WindowsStartupItem>();
        var issues = new List<string>();
        var icons = new StartupIcons();
        TryRead(L("CurrentUserRegistry"), () => ReadRegistry(RegistryHive.CurrentUser, RegistryView.Registry64, L("CurrentUser"), items, icons), issues);
        TryRead(L("SystemRegistry"), () => ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, L("AllUsers"), items, icons), issues);
        TryRead(L("SystemRegistry32"), () => ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, L("AllUsers"), items, icons), issues);
        TryRead(L("CurrentUserStartupFolder"), () => ReadFolder(Environment.SpecialFolder.Startup, RegistryHive.CurrentUser, L("CurrentUser"), items, icons), issues);
        TryRead(L("CommonStartupFolder"), () => ReadFolder(Environment.SpecialFolder.CommonStartup, RegistryHive.LocalMachine, L("AllUsers"), items, icons), issues);
        TryRead(L("ScheduledLogonTasks"), () => new TaskStore().ReadOtherLogonTasks(items, icons), issues);
        return new(items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Source, StringComparer.CurrentCulture).ToArray(), issues);
    }

    private static void ReadRegistry(RegistryHive hive, RegistryView view, string scope, List<WindowsStartupItem> items, StartupIcons icons)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var approvalsRoot = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        var is32 = view == RegistryView.Registry32;
        using var approvals = approvalsRoot.OpenSubKey(StartupApproved + (is32 ? "Run32" : "Run"));
        foreach (var kind in new[] { "Run", "RunOnce" })
        {
            using var key = root.OpenSubKey(CurrentVersion + kind);
            if (key == null)
                continue;
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command ||
                    string.IsNullOrWhiteSpace(command))
                    continue;
                var oneTime = kind == "RunOnce";
                var approval = approvals?.GetValue(name);
                var enabled = EditableApproval(approval);
                var restriction = oneTime ? WindowsStartupRestriction.OneTime :
                    name.Length == 0 ? WindowsStartupRestriction.Unnamed : ApprovalRestriction(hive, enabled);
                var toggle = restriction == null && enabled is { } canStart
                    ? new WindowsStartupToggle(canStart, value => SetRunEnabled(view, name, command, value)) : null;
                var state = oneTime ? L("RunOnce") : DisplayApproval(approval, toggle);
                items.Add(new($"{hive}/{view}/{kind}/{name}", name.Length > 0 ? name : L("DefaultStartupItem"),
                    is32 ? L("RegistrySource32", kind) : L("RegistrySource", kind), scope, state, command, icons.FromCommand(command),
                    toggle, restriction));
            }
        }
    }

    private static void ReadFolder(Environment.SpecialFolder location, RegistryHive hive, string scope, List<WindowsStartupItem> items, StartupIcons icons)
    {
        var folder = Environment.GetFolderPath(location);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var approvals = root.OpenSubKey(StartupApproved + "StartupFolder");
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                continue;
            var approval = approvals?.GetValue(fileName);
            var enabled = EditableApproval(approval);
            var restriction = ApprovalRestriction(hive, enabled);
            var toggle = restriction == null && enabled is { } canStart
                ? new WindowsStartupToggle(canStart, value => SetFolderEnabled(folder, path, value)) : null;
            items.Add(new($"{location}/{path}", Path.GetFileNameWithoutExtension(path), L("StartupFolder"), scope,
                DisplayApproval(approval, toggle), path, icons.FromStartupFile(path), toggle, restriction));
        }
    }

    private static WindowsStartupRestriction? ApprovalRestriction(RegistryHive hive, bool? enabled) =>
        hive == RegistryHive.LocalMachine ? WindowsStartupRestriction.AllUsers :
        enabled == null ? WindowsStartupRestriction.UnknownApproval : null;

    private static string DisplayApproval(object? approval, WindowsStartupToggle? toggle) =>
        toggle is { Enabled: true } ? L("Enabled") : ApprovalState(approval);

    internal static string ApprovalState(object? value) => value == null ? L("Registered") : ApprovalCode(value) switch
    {
        2 or 6 => L("Enabled"),
        3 or 7 => L("Disabled"),
        _ => L("UnknownState"),
    };

    private static uint? ApprovalCode(object? value) => value is byte[] { Length: >= 4 } data
        ? BinaryPrimitives.ReadUInt32LittleEndian(data) : null;

    private static bool? EditableApproval(object? value) => value switch
    {
        null => true,
        byte[] { Length: 12 } => ApprovalCode(value) switch
        {
            2 or 6 => true,
            3 or 7 => false,
            _ => null,
        },
        _ => null,
    };

    private static void SetRunEnabled(RegistryView view, string name, string command, bool enabled)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
        using var source = root.OpenSubKey(CurrentVersion + "Run");
        if (!string.Equals(source?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                command, StringComparison.Ordinal))
            throw new InvalidOperationException(L("WindowsStartupItemChanged"));
        var bucket = view == RegistryView.Registry32 ? "Run32" : "Run";
        SetCurrentUserApproval(bucket, name, enabled);
    }

    private static void SetFolderEnabled(string folder, string path, bool enabled)
    {
        if (!string.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Startup), folder, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
            throw new InvalidOperationException(L("WindowsStartupItemChanged"));
        SetCurrentUserApproval("StartupFolder", Path.GetFileName(path), enabled);
    }

    private static void SetCurrentUserApproval(string bucket, string name, bool enabled)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.CreateSubKey(StartupApproved + bucket);
        WriteApproval(key, name, enabled);
    }

    internal static void WriteApproval(RegistryKey key, string name, bool enabled)
    {
        var previous = key.GetValue(name);
        var current = EditableApproval(previous) ?? throw new InvalidOperationException(L("WindowsStartupItemChanged"));
        if (current == enabled)
            return;

        var code = ApprovalCode(previous) ?? 2u;
        var data = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data, enabled ? code & ~1u : code | 1u);
        if (!enabled)
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4), DateTime.UtcNow.ToFileTimeUtc());
        key.SetValue(name, data, RegistryValueKind.Binary);
    }

    private static void TryRead(string source, Action read, List<string> issues)
    {
        try { read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or COMException)
        {
            issues.Add($"{source}：{ex.Message}");
        }
    }
}
