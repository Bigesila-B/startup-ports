using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Media.Imaging;

using static StartupPorts.UiText;

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
        _scheduler = ConnectScheduler();
        Folder = _scheduler.GetFolder(@"\");
    }

    internal static object ConnectScheduler()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic scheduler = Activator.CreateInstance(type)!;
        scheduler.Connect();
        return (object)scheduler;
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
                issues.Add(L("TaskReadError", id, ex.Message));
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
            throw new InvalidOperationException(L("TaskAlreadyRunning"));
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

    public void ReadOtherLogonTasks(List<WindowsStartupItem> items, StartupIcons icons)
    {
        var folders = new Stack<object>();
        folders.Push((object)Folder);
        while (folders.TryPop(out var candidate))
        {
            dynamic folder = candidate;
            try
            {
                dynamic children = folder.GetFolders(0);
                for (var index = 1; index <= (int)children.Count; index++)
                    folders.Push((object)children[index]);
                dynamic tasks = folder.GetTasks(1);
                for (var index = 1; index <= (int)tasks.Count; index++)
                    AddLogonTask((object)tasks[index], items, icons);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException ||
                ex.HResult is FileNotFound or TaskNotFound) { }
        }
    }

    private static void AddLogonTask(object candidate, List<WindowsStartupItem> items, StartupIcons icons)
    {
        dynamic task = candidate;
        try
        {
            string name = task.Name;
            if (name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && IsOwned(candidate))
                return;
            dynamic definition = task.Definition;
            var (matches, allUsers, triggerEnabled, restriction) = InspectOtherLogonTask((object)definition);
            if (!matches)
                return;
            string path = task.Path;
            var icon = ReadActionIcon((object)definition.Actions, icons);
            var enabled = (bool)task.Enabled;
            WindowsStartupToggle? toggle = null;
            if (restriction == null && IsTaskWriteDenied(candidate))
                restriction = WindowsStartupRestriction.PermissionDenied;
            if (restriction == null)
            {
                string xml = definition.XmlText;
                toggle = new(enabled, value => new TaskStore().SetOtherLogonEnabled(path, xml, value),
                    allUsers ? "WindowsStartupInteractiveTaskHint" : null);
            }
            items.Add(new(path, name, L("ScheduledLogon"), allUsers ? L("AllUsers") : L("CurrentUser"),
                enabled && triggerEnabled ? L("Enabled") : L("Disabled"), path, icon, toggle, restriction, IsScheduledTask: true));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException ||
            ex.HResult is FileNotFound or TaskNotFound) { }
    }

    internal static (bool Matches, bool AllUsers, bool TriggerEnabled, WindowsStartupRestriction? Restriction)
        InspectOtherLogonTask(object taskDefinition)
    {
        dynamic definition = taskDefinition;
        var matches = false;
        var allUsers = false;
        var triggerEnabled = false;
        var otherTrigger = false;
        var otherUser = false;
        dynamic triggers = definition.Triggers;
        for (var index = 1; index <= (int)triggers.Count; index++)
        {
            dynamic trigger = triggers[index];
            if ((int)trigger.Type != 9)
            {
                otherTrigger = true;
                continue;
            }
            string? user = trigger.UserId;
            var anyUser = string.IsNullOrWhiteSpace(user);
            if (!anyUser && !IsCurrentUser(user!))
            {
                otherUser = true;
                continue;
            }
            matches = true;
            allUsers |= anyUser;
            triggerEnabled |= (bool)trigger.Enabled;
        }
        if (!matches)
            return (false, false, false, null);

        dynamic principal = definition.Principal;
        var restriction = !IsCurrentUser((string)principal.UserId) ? WindowsStartupRestriction.TaskAccount :
            otherTrigger ? WindowsStartupRestriction.TaskOtherTriggers :
            // An all-user trigger is scoped to this account by a non-elevated interactive principal.
            otherUser || (allUsers && ((int)principal.LogonType != 3 || (int)principal.RunLevel != 0))
                ? WindowsStartupRestriction.TaskScope :
            !triggerEnabled ? WindowsStartupRestriction.TaskLogonDisabled : (WindowsStartupRestriction?)null;
        return (matches, allUsers, triggerEnabled, restriction);
    }

    private static bool IsTaskWriteDenied(object candidate)
    {
        dynamic task = candidate;
        try
        {
            // AccessCheck requires the owner and group along with the DACL.
            return TaskWriteAccess.IsDenied((string)task.GetSecurityDescriptor(7));
        }
        catch (Exception ex) when (ex is ArgumentException or COMException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false; // The Task Scheduler service still checks access when the user changes the task.
        }
    }

    private void SetOtherLogonEnabled(string path, string expectedDefinition, bool enabled)
    {
        var separator = path.LastIndexOf('\\');
        dynamic folder = _scheduler.GetFolder(separator == 0 ? @"\" : path[..separator]);
        dynamic task = folder.GetTask(path[(separator + 1)..]);
        dynamic definition = task.Definition;
        var (matches, _, _, restriction) = InspectOtherLogonTask((object)definition);
        if (!matches || restriction != null || (string)definition.XmlText != expectedDefinition)
            throw new InvalidOperationException(L("WindowsStartupItemChanged"));
        task.Enabled = enabled;
    }

    private static BitmapSource? ReadActionIcon(object collection, StartupIcons icons)
    {
        dynamic actions = collection;
        try
        {
            for (var index = 1; index <= (int)actions.Count; index++)
            {
                dynamic action = actions[index];
                if ((int)action.Type == 0 && icons.FromExecutable((string?)action.Path) is { } icon)
                    return icon;
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException) { }
        return null;
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
        catch (Exception ex) when (ex.HResult is FileNotFound or TaskNotFound) { }
        throw new InvalidOperationException(L("TaskNotFound"));
    }

    private static bool IsOwned(object candidate)
    {
        dynamic task = candidate;
        dynamic definition = task.Definition;
        if ((string)definition.RegistrationInfo.Source != Source)
            return false;
        return IsCurrentUser((string)definition.Principal.UserId);
    }

    internal static bool IsCurrentUser(string user)
    {
        if (string.Equals(user, AppIdentity.Sid, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(user))
            return false;
        try { return new NTAccount(user).Translate(typeof(SecurityIdentifier)).Value == AppIdentity.Sid; }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException) { return false; }
    }

    private static ScheduledEntry Read(Guid id, object candidate)
    {
        dynamic task = candidate;
        dynamic definition = task.Definition;
        if (definition.Data is not string data || data.Length == 0)
            throw new InvalidDataException(L("TaskConfigMissing"));
        var stored = JsonSerializer.Deserialize<StoredEntry>(data);
        if (stored is not { Version: 1 } || stored.Entry is not { } entry || entry.Id != id ||
            entry.Name is null || entry.Target is null || entry.WorkingDirectory is null || entry.Arguments is null)
            throw new InvalidDataException(L("TaskConfigInvalid"));
        if ((int)definition.Triggers.Count != 1 || (int)definition.Triggers[1].Type != 9)
            throw new InvalidDataException(L("TaskTriggerChanged"));
        bool enabled = task.Enabled && definition.Triggers[1].Enabled;
        return new(entry with { Enabled = enabled }, (int)task.State);
    }

    private sealed record StoredEntry(int Version, StartupEntry Entry);
}
