using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace StartupPorts;

internal sealed record ScheduledEntry(StartupEntry Entry, int State);
internal sealed record TaskList(IReadOnlyList<ScheduledEntry> Entries, IReadOnlyList<string> Issues);

internal sealed class TaskStore
{
    private const string Source = "StartupPorts";
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int TaskNotFound = unchecked((int)0x8004130F);
    private static string Prefix => $"StartupPorts-{AppIdentity.Sid}-";
    private readonly string _launcher;
    private readonly dynamic _scheduler;
    private dynamic Folder { get; }

    public TaskStore(string? launcher = null)
    {
        _launcher = launcher ?? Environment.ProcessPath!;
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        _scheduler = Activator.CreateInstance(type)!;
        _scheduler.Connect();
        Folder = _scheduler.GetFolder(@"\");
    }

    public TaskList List()
    {
        var result = new List<ScheduledEntry>();
        var issues = new List<string>();
        foreach (var (id, task) in OwnedTasks())
        {
            try { result.Add(Read(id, task)); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or COMException)
            {
                issues.Add($"无法读取任务 {id:N}：{ex.Message}");
            }
        }
        return new(result.OrderBy(row => row.Entry.Name, StringComparer.CurrentCulture).ToArray(), issues);
    }

    public ScheduledEntry Get(Guid id) => Read(id, RequireOwned(id));

    public void Save(StartupEntry entry, bool create)
    {
        entry.Validate();
        if (!create)
            RequireOwned(entry.Id);

        dynamic definition = _scheduler.NewTask(0);
        definition.RegistrationInfo.Source = Source;
        definition.RegistrationInfo.Description = $"StartupPorts · {entry.Name}";
        definition.Data = JsonSerializer.Serialize(new StoredEntry(1, entry));
        definition.Principal.UserId = AppIdentity.Sid;
        definition.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
        definition.Principal.RunLevel = 0; // TASK_RUNLEVEL_LUA
        definition.Settings.Enabled = true;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW

        dynamic trigger = definition.Triggers.Create(9); // TASK_TRIGGER_LOGON
        trigger.UserId = AppIdentity.Sid;
        trigger.Enabled = entry.Enabled;
        if (entry.DelaySeconds > 0)
            trigger.Delay = $"PT{entry.DelaySeconds}S";

        dynamic action = definition.Actions.Create(0); // TASK_ACTION_EXEC
        action.Path = _launcher;
        action.Arguments = $"--run {entry.Id:N}";
        action.WorkingDirectory = Path.GetDirectoryName(_launcher);
        Folder.RegisterTaskDefinition(Prefix + entry.Id.ToString("N"), definition, create ? 2 : 4,
            AppIdentity.Sid, null, 3, null);
    }

    public void SetEnabled(Guid id, bool enabled)
    {
        dynamic task = RequireOwned(id);
        var current = Read(id, (object)task);
        dynamic definition = task.Definition;
        definition.Triggers[1].Enabled = enabled;
        definition.Settings.Enabled = true;
        definition.Data = JsonSerializer.Serialize(new StoredEntry(1, current.Entry with { Enabled = enabled }));
        Folder.RegisterTaskDefinition(Prefix + id.ToString("N"), definition, 4, AppIdentity.Sid, null, 3, null);
    }

    public void Run(Guid id)
    {
        dynamic task = RequireOwned(id);
        ScheduledEntry entry = Read(id, task);
        entry.Entry.Validate();
        if (entry.State == 4)
            throw new InvalidOperationException("这一项已在运行。");
        task.Run(null);
    }

    public void Delete(Guid id)
    {
        RequireOwned(id);
        Folder.DeleteTask(Prefix + id.ToString("N"), 0);
        RunState.TryDelete(id);
    }

    public void DeleteAll()
    {
        foreach (var (id, _) in OwnedTasks().ToArray())
            Delete(id);
    }

    private IEnumerable<(Guid Id, object Task)> OwnedTasks()
    {
        dynamic tasks = Folder.GetTasks(1);
        for (var index = 1; index <= (int)tasks.Count; index++)
        {
            dynamic task = tasks[index];
            string name = task.Name;
            if (name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(name[Prefix.Length..], "N", out var id) && IsOwned((object)task))
                yield return (id, (object)task);
        }
    }

    private object RequireOwned(Guid id)
    {
        try
        {
            dynamic task = Folder.GetTask(Prefix + id.ToString("N"));
            if (IsOwned((object)task))
                return task;
        }
        catch (COMException ex) when (ex.HResult is FileNotFound or TaskNotFound) { }
        throw new InvalidOperationException("找不到由本工具登记的自启任务。");
    }

    private static bool IsOwned(object candidate)
    {
        dynamic task = candidate;
        dynamic definition = task.Definition;
        if ((string)definition.RegistrationInfo.Source != Source)
            return false;
        string principal = definition.Principal.UserId;
        if (string.Equals(principal, AppIdentity.Sid, StringComparison.OrdinalIgnoreCase))
            return true;
        try { return new NTAccount(principal).Translate(typeof(SecurityIdentifier)).Value == AppIdentity.Sid; }
        catch (IdentityNotMappedException) { return false; }
    }

    private static ScheduledEntry Read(Guid id, object candidate)
    {
        dynamic task = candidate;
        dynamic definition = task.Definition;
        var stored = JsonSerializer.Deserialize<StoredEntry>((string)definition.Data);
        if (stored is not { Version: 1 } || stored.Entry is not { } entry || entry.Id != id ||
            entry.Name is null || entry.Target is null || entry.WorkingDirectory is null || entry.Arguments is null)
            throw new InvalidDataException("任务配置格式无效。");
        if ((int)definition.Triggers.Count != 1 || (int)definition.Triggers[1].Type != 9)
            throw new InvalidDataException("任务的登录触发器已被修改。");
        bool enabled = task.Enabled && definition.Triggers[1].Enabled;
        return new(entry with { Enabled = enabled }, (int)task.State);
    }

    private sealed record StoredEntry(int Version, StartupEntry Entry);
}
