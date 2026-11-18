using System.IO;
using System.Security.Principal;

namespace StartupPorts;

internal enum Protocol { Tcp, Udp }

internal sealed record StartupEntry(
    Guid Id, string Name, string Target, string Arguments, string WorkingDirectory,
    int DelaySeconds, int? ExpectedPort, Protocol ExpectedProtocol, bool Enabled)
{
    public string Kind => Path.GetExtension(Target).ToLowerInvariant();

    public string? PathIssue() =>
        !File.Exists(Target) ? "找不到启动文件" :
        !Directory.Exists(WorkingDirectory) ? "找不到工作目录" : null;

    public StartupEntry Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 100)
            throw new ArgumentException("请输入 1–100 字的名称。");
        if (!Path.IsPathFullyQualified(Target) || Kind is not (".exe" or ".bat" or ".cmd" or ".ps1"))
            throw new ArgumentException("请选择 EXE、BAT、CMD 或 PowerShell 脚本的完整路径。");
        if (!Path.IsPathFullyQualified(WorkingDirectory))
            throw new ArgumentException("请输入工作目录的完整路径。");
        if (DelaySeconds is < 0 or > 600 || ExpectedPort is < 1 or > 65535 ||
            !Enum.IsDefined(ExpectedProtocol))
            throw new ArgumentException("延迟为 0–600 秒，端口为 1–65535。");
        if (Arguments.Length > 6000 || Arguments.Any(c => c is '\0' or '\r' or '\n'))
            throw new ArgumentException("参数需填写在一行内，最多 6000 个字符。");
        if ((Kind is ".cmd" or ".bat") && Target.Length + Arguments.Length > 7800)
            throw new ArgumentException("批处理路径与参数超过 CMD 的长度限制。");
        if (PathIssue() is { } issue)
            throw new ArgumentException(issue + "。");
        return this;
    }
}

internal static class AppIdentity
{
    public static string Sid { get; } = GetSid();
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StartupPorts", "Runtime");
    public static string QuiesceEvent => $@"Global\StartupPorts.Quiesce.{Sid}";

    private static string GetSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }
}
