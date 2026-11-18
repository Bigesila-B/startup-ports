using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace StartupPorts;

internal sealed record ProcessAppearance(string? DisplayName, BitmapSource? Icon);

internal sealed class ProcessIcons
{
    private static readonly ProcessAppearance Empty = new(null, null);
    private readonly Dictionary<string, ProcessAppearance> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int Pid, long Created), (BitmapSource? Icon, long ReadAt)> _windows = [];

    public ProcessAppearance Read(PortRow row)
    {
        var file = ReadFile(row.Path);
        if (file.Icon != null || row.Pid <= 4 || row.Created is not { } created)
            return file;

        var key = (row.Pid, created);
        var now = Environment.TickCount64;
        if (!_windows.TryGetValue(key, out var window) || (window.Icon == null && now - window.ReadAt >= 10000))
            _windows[key] = window = (ReadWindowIcon(row.Pid, created), now);
        return window.Icon == null ? file : file with { Icon = window.Icon };
    }

    public ProcessAppearance ReadFile(string? path)
    {
        if (path is null || !IsLocalFile(path) ||
            !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return Empty;
        if (!_files.TryGetValue(path, out var result))
            _files[path] = result = ReadExecutable(path);
        return result;
    }

    public void Retain(IReadOnlyList<PortRow> rows)
    {
        var files = rows.Select(row => row.Path).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _files.Keys.ToArray())
            if (!files.Contains(path))
                _files.Remove(path);
        var processes = rows.Where(row => row.Created != null).Select(row => (row.Pid, row.Created!.Value)).ToHashSet();
        foreach (var process in _windows.Keys.ToArray())
            if (!processes.Contains(process))
                _windows.Remove(process);
    }

    private static ProcessAppearance ReadExecutable(string path)
    {
        string? displayName = null;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            var name = string.IsNullOrWhiteSpace(version.FileDescription) ? version.ProductName : version.FileDescription;
            if (!string.IsNullOrWhiteSpace(name))
                displayName = name.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception) { }

        return new(displayName, ReadIconResource(path, 0));
    }

    internal static BitmapSource? ReadIconResource(string path, int index)
    {
        if (!IsLocalFile(path) ||
            Path.GetExtension(path).ToLowerInvariant() is not (".exe" or ".dll" or ".ico" or ".icl" or ".cpl"))
            return null;
        ExtractIconEx(path, index, out var handle, 0, 1);
        return ConvertOwnedIcon(handle);
    }

    internal static bool IsLocalFile(string path) =>
        path.Length > 3 && path[1] == ':' && Path.IsPathFullyQualified(path) && GetDriveType(path[..3]) is 2 or 3 or 5 or 6;

    private static BitmapSource? ReadWindowIcon(int pid, long created)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.ToFileTimeUtc() != created)
                return null;
            var window = process.MainWindowHandle;
            if (window == 0 || GetWindowThreadProcessId(window, out var owner) == 0 || owner != (uint)pid)
                return null;

            const uint getIcon = 0x007f;
            const uint abortIfHung = 0x0002;
            const int classLargeIcon = -14;
            if (SendMessageTimeout(window, getIcon, 1, 0, abortIfHung, 50, out var icon) == 0 || icon == 0)
                icon = GetClassLongPtr(window, classLargeIcon);
            return icon != 0 ? ConvertOwnedIcon(CopyIcon(icon)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static BitmapSource? ConvertOwnedIcon(nint handle)
    {
        if (handle == 0)
            return null;
        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException) { return null; }
        finally { DestroyIcon(handle); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string path, int index, out nint large, nint small, uint count);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPath);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern nint CopyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern nint GetClassLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam,
        uint flags, uint timeout, out nint result);
}
