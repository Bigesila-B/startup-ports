using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static StartupPorts.NativeProcess;

namespace StartupPorts;

internal static class LaunchRunner
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    public static int Run(Guid id)
    {
        using var quiesce = OpenQuiesceEvent();
        using var gate = new Mutex(false, $@"Global\StartupPorts.Run.{AppIdentity.Sid}.{id:N}");
        if (!TryEnter(gate))
            return 0;
        try
        {
            if (quiesce.WaitOne(0))
                return 0;
            return Launch(id, quiesce);
        }
        finally { gate.ReleaseMutex(); }
    }

    public static EventWaitHandle OpenQuiesceEvent() =>
        new(false, EventResetMode.ManualReset, AppIdentity.QuiesceEvent);

    public static int PrepareMaintenance(bool uninstall)
    {
        using var quiesce = OpenQuiesceEvent();
        quiesce.Set();
        try
        {
            if (uninstall)
            {
                new PowerTaskStore().DeleteOnUninstall();
                new TaskStore().DeleteAll();
            }
            using var current = Process.GetCurrentProcess();
            var processPath = Environment.ProcessPath;
            var limit = Stopwatch.StartNew();
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId)
                        continue;
                    using var handle = OpenProcess(QueryAccess | SynchronizeAccess, false, process.Id);
                    if (handle.IsInvalid || !string.Equals(ImagePath(handle), processPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (WaitForSingleObject(handle, (uint)Math.Max(0, 10000 - limit.ElapsedMilliseconds)) != WaitSignaled)
                        return 1;
                }
            }
            return 0;
        }
        finally { quiesce.Reset(); }
    }

    private static bool TryEnter(Mutex gate)
    {
        try { return gate.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    private static int Launch(Guid id, EventWaitHandle quiesce)
    {
        var info = new RunInfo(Guid.NewGuid(), DateTimeOffset.UtcNow, null, null, null, false, null, null);
        try
        {
            var entry = new TaskStore().Get(id).Entry.Validate();
            RunState.Write(id, info);
            var jobName = $@"Local\StartupPorts.{AppIdentity.Sid}.{info.RunId:N}";
            using var job = CreateJobObject(0, jobName);
            var execution = StartSuspended(entry);
            using var process = new SafeProcessHandle(execution.ProcessHandle, ownsHandle: true);
            using var thread = new SafeWaitHandle(execution.ThreadHandle, ownsHandle: true);
            var tracked = !job.IsInvalid && AssignProcessToJobObject(job, process);
            var created = GetProcessTimes(process, out var creation, out _, out _, out _) ? (long?)creation : null;
            if (ResumeThread(thread) == uint.MaxValue)
            {
                var error = Error();
                TerminateProcess(process, 1);
                throw error;
            }

            info = info with
            {
                ProcessId = execution.ProcessId,
                ProcessCreated = created,
                JobName = tracked ? jobName : null,
            };
            RunState.Write(id, info);
            var rootResult = WaitForSingleObject(process, 0);
            while (rootResult == WaitTimeout || (rootResult == WaitSignaled && tracked && HasMembers(job)))
            {
                if (quiesce.WaitOne(400))
                    return 0;
                rootResult = WaitForSingleObject(process, 0);
            }
            if (rootResult != WaitSignaled)
                throw Error();

            int? exitCode = GetExitCodeProcess(process, out var code) ? code : null;
            if (RunState.Read(id)?.RunId == info.RunId)
                RunState.Write(id, info with { Finished = true, ExitCode = exitCode });
            return exitCode ?? 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or Win32Exception or COMException or System.Text.Json.JsonException)
        {
            try { RunState.Write(id, info with { Finished = true, Error = ex.Message }); }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
            return 1;
        }
    }

    private static bool HasMembers(SafeJobHandle job) =>
        QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<JobAccounting>(), 0) &&
        accounting.ActiveProcesses > 0;

    private static ProcessInformation StartSuspended(StartupEntry entry)
    {
        var application = entry.Target;
        var command = $"\"{application}\" {entry.Arguments}";
        var flags = CreateSuspended;
        nint environment = 0;
        try
        {
            switch (entry.Kind)
            {
                case ".cmd":
                case ".bat":
                    application = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                    command = $"\"{application}\" /d /v:off /s /c \"\"%STARTUP_PORTS_SCRIPT%\" {entry.Arguments}\"";
                    environment = ScriptEnvironment(entry.Target);
                    flags |= CreateNewConsole | CreateUnicodeEnvironment;
                    break;
                case ".ps1":
                    application = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                    command = $"\"{application}\" -NoLogo -NoProfile -File \"{entry.Target}\" {entry.Arguments}";
                    flags |= CreateNewConsole;
                    break;
            }
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcess(application, new StringBuilder(command), 0, 0, false, flags,
                    environment, entry.WorkingDirectory, ref startup, out var process))
                throw Error();
            return process;
        }
        finally
        {
            if (environment != 0)
                Marshal.FreeHGlobal(environment);
        }
    }

    private static nint ScriptEnvironment(string target)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
            variables[(string)variable.Key] = (string)variable.Value!;
        variables["STARTUP_PORTS_SCRIPT"] = target;
        return Marshal.StringToHGlobalUni(string.Join('\0',
            variables.Select(variable => $"{variable.Key}={variable.Value}")) + "\0");
    }
}
