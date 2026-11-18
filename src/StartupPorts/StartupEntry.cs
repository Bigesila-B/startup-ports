using System.IO;
using System.Security.Principal;

using static StartupPorts.UiText;

namespace StartupPorts;

internal enum Protocol { Tcp, Udp }

internal sealed record StartupEntry(
    Guid Id, string Name, string Target, string Arguments, string WorkingDirectory,
    int DelaySeconds, int? ExpectedPort, Protocol ExpectedProtocol, bool Enabled)
{
    public string Kind => Path.GetExtension(Target).ToLowerInvariant();

    public string? PathIssue() =>
        !File.Exists(Target) ? L("MissingStartupFile") :
        !Directory.Exists(WorkingDirectory) ? L("MissingWorkingDirectory") : null;

    public StartupEntry Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 100)
            throw new ArgumentException(L("NameValidation"));
        if (!Path.IsPathFullyQualified(Target) || Kind is not (".exe" or ".bat" or ".cmd" or ".ps1"))
            throw new ArgumentException(L("TargetValidation"));
        if (!Path.IsPathFullyQualified(WorkingDirectory))
            throw new ArgumentException(L("WorkingDirectoryValidation"));
        if (DelaySeconds is < 0 or > 600 || ExpectedPort is < 1 or > 65535 ||
            !Enum.IsDefined(ExpectedProtocol))
            throw new ArgumentException(L("DelayPortValidation"));
        if (Arguments.Length > 6000 || Arguments.Any(c => c is '\0' or '\r' or '\n'))
            throw new ArgumentException(L("ArgumentsValidation"));
        if ((Kind is ".cmd" or ".bat") && Target.Length + Arguments.Length > 7800)
            throw new ArgumentException(L("CmdLengthValidation"));
        if (PathIssue() is { } issue)
            throw new ArgumentException(issue);
        return this;
    }
}

internal static class AppIdentity
{
    public const string PreferencesKey = @"HKEY_CURRENT_USER\Software\StartupPorts";
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
