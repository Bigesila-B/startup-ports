using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace StartupPorts;

internal sealed partial class StartupIcons
{
    private readonly Dictionary<string, BitmapSource?> _executables = new(StringComparer.OrdinalIgnoreCase);

    public BitmapSource? FromCommand(string command)
    {
        var expanded = Environment.ExpandEnvironmentVariables(command).Trim();
        if (expanded.Length == 0)
            return null;
        var firstArgument = FirstArgument(expanded);
        if (firstArgument == null)
            return null;
        var icon = FromExecutable(firstArgument);
        if (icon != null || expanded[0] == '"' || Path.GetExtension(firstArgument).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return icon;

        // Older Run entries sometimes omit the quotes around a full executable path.
        var fullPath = UnquotedExecutable().Match(expanded);
        return fullPath.Success ? FromExecutable(fullPath.Value) : null;
    }

    public BitmapSource? FromExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return null;
        var path = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
        if (Path.GetFileName(path) == path && path.Length > 0)
        {
            if (!Path.HasExtension(path))
                path += ".exe";
            path = Path.Combine(Environment.SystemDirectory, path);
        }
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!_executables.TryGetValue(path, out var icon))
            _executables[path] = icon = ProcessIcons.ReadIconResource(path, 0);
        return icon;
    }

    public BitmapSource? FromStartupFile(string path)
    {
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return FromExecutable(path);
        if (!ProcessIcons.IsLocalFile(path))
            return null;

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return null;
            shell = Activator.CreateInstance(shellType);
            shortcut = ((dynamic)shell!).CreateShortcut(path);
            dynamic link = shortcut!;
            string? iconLocation = link.IconLocation;
            string? target = link.TargetPath;
            return FromShortcutIcon(iconLocation) ?? FromExecutable(target);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut))
                Marshal.ReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }
    }

    private static BitmapSource? FromShortcutIcon(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;
        var separator = location.LastIndexOf(',');
        if (separator < 1 || !int.TryParse(location.AsSpan(separator + 1), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var index))
            return null;
        var path = Environment.ExpandEnvironmentVariables(location[..separator].Trim().Trim('"'));
        return ProcessIcons.ReadIconResource(path, index);
    }

    private static string? FirstArgument(string command)
    {
        var arguments = CommandLineToArgv(command, out var count);
        if (arguments == 0)
            return null;
        try { return count > 0 ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments)) : null; }
        finally { LocalFree(arguments); }
    }

    [GeneratedRegex(@"^[A-Za-z]:[\\/][^""\r\n]*?\.exe(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnquotedExecutable();

    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode)]
    private static extern nint CommandLineToArgv(string command, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
