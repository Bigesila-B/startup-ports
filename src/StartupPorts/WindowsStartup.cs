using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

using static StartupPorts.UiText;

namespace StartupPorts;

internal sealed record WindowsStartupItem(string Id, string Name, string Source, string Scope, string State, string Command, BitmapSource? Icon);
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
                var state = kind == "RunOnce" ? L("RunOnce") : ApprovalState(approvals?.GetValue(name));
                items.Add(new($"{hive}/{view}/{kind}/{name}", name.Length > 0 ? name : L("DefaultStartupItem"),
                    is32 ? L("RegistrySource32", kind) : L("RegistrySource", kind), scope, state, command, icons.FromCommand(command)));
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
            items.Add(new($"{location}/{path}", Path.GetFileNameWithoutExtension(path), L("StartupFolder"), scope,
                ApprovalState(approvals?.GetValue(fileName)), path, icons.FromStartupFile(path)));
        }
    }

    internal static string ApprovalState(object? value) =>
        value is not byte[] { Length: >= 4 } data ? L("Registered") : BitConverter.ToUInt32(data) switch
        {
            2 or 6 => L("Enabled"),
            3 or 7 => L("Disabled"),
            _ => L("UnknownState"),
        };

    private static void TryRead(string source, Action read, List<string> issues)
    {
        try { read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or COMException)
        {
            issues.Add($"{source}：{ex.Message}");
        }
    }
}
