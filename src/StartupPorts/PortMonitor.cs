using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using static StartupPorts.NativeProcess;

using static StartupPorts.UiText;

namespace StartupPorts;

internal sealed record PortRow(BoundEndpoint Endpoint, string Process, string? Path, long? Created,
    double? Cpu, long? Memory, string? Origin, bool CanEnd, bool CanAdd,
    IReadOnlyList<Guid> ManagedBy, bool IdentityUncertain)
{
    public bool IsCurrentUser { get; init; }
    public string Protocol => Endpoint.Protocol.ToString().ToUpperInvariant();
    public string Address => Endpoint.Address.ToString();
    public int Port => Endpoint.Port;
    public int Pid => Endpoint.ProcessId;
    public string CpuDisplay => Cpu is { } cpu ? $"{cpu:0.0}%" : "—";
    public string MemoryDisplay => Memory is { } memory ? $"{memory / 1048576d:0.0} MB" : "—";
    public string OriginDisplay => Origin ?? "—";
}

internal sealed record PortSnapshot(IReadOnlyList<PortRow> Rows, IReadOnlyList<string> Issues);

internal sealed class PortMonitor
{
    private readonly Dictionary<(int Pid, long Created), (long CpuTime, long Stamp)> _samples = [];
    private static readonly (Protocol Protocol, AddressFamily Family)[] Tables =
    [
        (Protocol.Tcp, AddressFamily.InterNetwork), (Protocol.Tcp, AddressFamily.InterNetworkV6),
        (Protocol.Udp, AddressFamily.InterNetwork), (Protocol.Udp, AddressFamily.InterNetworkV6),
    ];

    private static readonly Dictionary<string, string> KnownOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["codex.exe"] = "Codex", ["codex-standalone.exe"] = "Codex",
        ["claude.exe"] = "Claude", ["Code.exe"] = "VS Code", ["Code - Insiders.exe"] = "VS Code Insiders",
        ["Cursor.exe"] = "Cursor", ["WindowsTerminal.exe"] = "Windows Terminal",
        ["devenv.exe"] = "Visual Studio", ["idea64.exe"] = "IntelliJ IDEA",
        ["pycharm64.exe"] = "PyCharm",
    };

    public PortSnapshot Capture(IReadOnlyDictionary<Guid, RunInfo> runs)
    {
        var started = DateTime.UtcNow.ToFileTimeUtc();
        var issues = new List<string>();
        var endpoints = new HashSet<BoundEndpoint>();
        foreach (var (protocol, family) in Tables)
        {
            try { endpoints.UnionWith(NativePorts.Read(protocol, family)); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                issues.Add($"{protocol.ToString().ToUpperInvariant()} {(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6")}：{ex.Message}");
            }
        }

        var processEntries = ProcessEntries();
        var jobs = runs.Where(run => run.Value.JobName != null && !run.Value.Finished)
            .ToDictionary(run => run.Key, run => OpenJobObject(4, false, run.Value.JobName!));
        try
        {
            var details = endpoints.Select(endpoint => endpoint.ProcessId).Distinct().ToDictionary(
                pid => pid, pid => Describe(pid, started, processEntries, runs, jobs));
            var rows = endpoints.Select(endpoint => details[endpoint.ProcessId] with { Endpoint = endpoint })
                .OrderBy(row => row.Port).ThenBy(row => row.Protocol)
                .ThenBy(row => row.Address, StringComparer.Ordinal).ThenBy(row => row.Pid).ToArray();
            foreach (var key in _samples.Keys.ToArray())
                if (!details.TryGetValue(key.Pid, out var value) || value.Created != key.Created)
                    _samples.Remove(key);
            return new(rows, issues);
        }
        finally
        {
            foreach (var job in jobs.Values)
                job.Dispose();
        }
    }

    public static void End(PortRow row)
    {
        if (!row.CanEnd || row.Created is not { } expectedCreation)
            throw new InvalidOperationException(L("CannotVerifyProcess"));
        using var process = OpenProcess(QueryAccess | TerminateAccess | SynchronizeAccess, false, row.Pid);
        if (process.IsInvalid)
            throw Error();
        if (!GetProcessTimes(process, out var actualCreation, out _, out _, out _) || actualCreation != expectedCreation ||
            !SafeToEnd(process, row.Pid, ImagePath(process)) ||
            WaitForSingleObject(process, 0) != WaitTimeout ||
            !NativePorts.Read(row.Endpoint.Protocol, row.Endpoint.Address.AddressFamily).Contains(row.Endpoint))
            throw new InvalidOperationException(L("ProcessChanged"));
        if (!TerminateProcess(process, 1))
            throw Error();
        if (WaitForSingleObject(process, 2000) != WaitSignaled)
            throw new InvalidOperationException(L("ProcessEnding"));
    }

    private PortRow Describe(int pid, long started, Dictionary<int, ProcessEntry> entries,
        IReadOnlyDictionary<Guid, RunInfo> runs, Dictionary<Guid, SafeJobHandle> jobs)
    {
        using var process = OpenProcess(QueryAccess | SynchronizeAccess, false, pid);
        entries.TryGetValue(pid, out var entry);
        if (process.IsInvalid || !GetProcessTimes(process, out var created, out _, out var kernel, out var user) ||
            created >= started || WaitForSingleObject(process, 0) != WaitTimeout)
            return new(null!, entry.Executable ?? L("ProcessUnavailable"), null, null, null, null, null, false, false, [], true);

        var path = ImagePath(process);
        var name = Path.GetFileName(path) ?? entry.Executable ?? L("ProcessUnavailable");
        var managed = new List<Guid>();
        foreach (var (id, run) in runs)
        {
            if (run.Finished)
                continue;
            if ((run.ProcessId == pid && run.ProcessCreated == created) ||
                (jobs.TryGetValue(id, out var job) && !job.IsInvalid &&
                 IsProcessInJob(process, job, out var assigned) && assigned))
                managed.Add(id);
        }

        var stamp = Stopwatch.GetTimestamp();
        var cpuTime = kernel + user;
        double? cpu = _samples.TryGetValue((pid, created), out var previous) && stamp > previous.Stamp
            ? Math.Clamp((cpuTime - previous.CpuTime) / (double)Stopwatch.GetElapsedTime(previous.Stamp, stamp).Ticks
                * 100 / Environment.ProcessorCount, 0, 100)
            : null;
        _samples[(pid, created)] = (cpuTime, stamp);
        long? memory = K32GetProcessMemoryInfo(process, out var counters, (uint)Marshal.SizeOf<ProcessMemoryCounters>())
            ? (long)counters.WorkingSet : null;
        var own = IsOwned(process);
        var endable = own && !Self(pid, path) && IsProcessCritical(process, out var critical) && !critical;
        var origin = managed.Count > 0 ? "StartupPorts" : Origin(entries, pid, created);
        return new PortRow(null!, name, path, created, cpu, memory, origin, endable,
            own && !Self(pid, path) && Path.GetExtension(path)?.Equals(".exe", StringComparison.OrdinalIgnoreCase) == true,
            managed, false) { IsCurrentUser = own };
    }

    private static string? Origin(Dictionary<int, ProcessEntry> entries, int pid, long latest)
    {
        for (var index = 0; index < 12 && entries.TryGetValue(pid, out var entry); index++)
        {
            pid = (int)entry.ParentProcessId;
            if (pid <= 0)
                break;
            using var parent = OpenProcess(QueryAccess, false, pid);
            if (parent.IsInvalid || !GetProcessTimes(parent, out var created, out _, out _, out _) || created >= latest)
                break;
            latest = created;
            var path = ImagePath(parent);
            if (path == null)
                continue;
            var executable = Path.GetFileName(path);
            if (executable.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                return L("WindowsExplorer");
            if (KnownOrigins.TryGetValue(executable, out var origin))
                return origin;
        }
        return null;
    }

    private static Dictionary<int, ProcessEntry> ProcessEntries()
    {
        var result = new Dictionary<int, ProcessEntry>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid)
            return result;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
        if (Process32First(snapshot, ref entry))
            do { result[(int)entry.ProcessId] = entry; } while (Process32Next(snapshot, ref entry));
        return result;
    }

    private static bool IsOwned(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 8, out var token)) // TOKEN_QUERY
            return false;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value == AppIdentity.Sid;
    }

    private static bool SafeToEnd(SafeProcessHandle process, int pid, string? path) =>
        !Self(pid, path) && IsProcessCritical(process, out var critical) && !critical && IsOwned(process);

    private static bool Self(int pid, string? path) => pid <= 4 || pid == Environment.ProcessId || path == null ||
        path.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
}
