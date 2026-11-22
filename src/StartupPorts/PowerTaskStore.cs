using System.Globalization;
using System.IO;
using System.Text.Json;
using static StartupPorts.UiText;

namespace StartupPorts;

internal enum PowerAction { Shutdown, Restart, Sleep, Hibernate }
internal enum PowerState { Waiting, Running, Completed, Failed, Missed, Disabled }
internal sealed record PowerTimer(Guid Id, PowerAction Action, DateTimeOffset Due);
internal sealed record PowerStatus(PowerTimer Timer, PowerState State, int Result);

internal sealed class PowerTaskStore
{
    private const string Source = "StartupPorts.Power";
    private const int TaskNotFound = unchecked((int)0x8004130F);
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int HasNotRun = 0x00041303; // SCHED_S_TASK_HAS_NOT_RUN
    private static readonly TimeSpan DispatchWindow = TimeSpan.FromMinutes(2);
    private readonly string _name;
    private readonly string _launcher;
    private readonly Func<PowerTimer, string> _arguments;

    public PowerTaskStore() : this($"StartupPorts-Power-{AppIdentity.Sid}", Environment.ProcessPath!,
        timer => $"--run-power {timer.Id:N}") { }

    internal PowerTaskStore(string name, string launcher, Func<PowerTimer, string> arguments)
    {
        _name = name;
        _launcher = launcher;
        _arguments = arguments;
    }

    public PowerStatus? Read()
    {
        dynamic service = TaskStore.ConnectScheduler();
        return Find((object)service.GetFolder(@"\"));
    }

    public void Schedule(PowerAction action, DateTimeOffset due)
    {
        if (!Enum.IsDefined(action) || due <= DateTimeOffset.UtcNow.AddSeconds(2))
            throw new InvalidOperationException(L("PowerFutureTime"));

        WithLock(() =>
        {
            dynamic service = TaskStore.ConnectScheduler();
            dynamic folder = service.GetFolder(@"\");
            var previous = Find((object)folder);
            if (previous?.State is PowerState.Waiting or PowerState.Running)
                throw new InvalidOperationException(L("PowerAlreadyScheduled"));

            var timer = new PowerTimer(Guid.NewGuid(), action, DateTimeOffset.FromUnixTimeSeconds(due.ToUnixTimeSeconds()));
            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Source = Source;
            definition.RegistrationInfo.Description = "StartupPorts · Timed shutdown";
            definition.Data = JsonSerializer.Serialize(timer);
            definition.Principal.UserId = AppIdentity.Sid;
            definition.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
            definition.Principal.RunLevel = 0;
            definition.Settings.AllowDemandStart = false;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.WakeToRun = false;
            definition.Settings.StartWhenAvailable = false;
            definition.Settings.ExecutionTimeLimit = "PT5M";
            definition.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW

            dynamic trigger = definition.Triggers.Create(1); // TASK_TRIGGER_TIME
            trigger.StartBoundary = FormatBoundary(timer.Due);
            trigger.EndBoundary = FormatBoundary(timer.Due.Add(DispatchWindow));
            dynamic taskAction = definition.Actions.Create(0); // TASK_ACTION_EXEC
            taskAction.Path = _launcher;
            taskAction.Arguments = _arguments(timer);
            taskAction.WorkingDirectory = Path.GetDirectoryName(_launcher);

            if (timer.Due <= DateTimeOffset.UtcNow.AddSeconds(1))
                throw new InvalidOperationException(L("PowerFutureTime"));
            if (previous != null)
                folder.DeleteTask(_name, 0);
            folder.RegisterTaskDefinition(_name, definition, 2, AppIdentity.Sid, null, 3, null); // TASK_CREATE
            return true;
        });
    }

    public void Cancel(Guid id) => WithLock(() =>
    {
        dynamic service = TaskStore.ConnectScheduler();
        dynamic folder = service.GetFolder(@"\");
        var task = Find((object)folder);
        if (task is not { State: PowerState.Waiting } status || status.Timer.Id != id)
            throw new InvalidOperationException(L("PowerCannotCancel"));
        folder.DeleteTask(_name, 0);
        return true;
    });

    public void DeleteOnUninstall() => WithLock(() =>
    {
        dynamic service = TaskStore.ConnectScheduler();
        dynamic folder = service.GetFolder(@"\");
        if (Find((object)folder) != null)
            folder.DeleteTask(_name, 0);
        return true;
    });

    public PowerAction? GetDueAction(Guid id) => WithLock<PowerAction?>(() =>
    {
        using var maintenance = LaunchRunner.OpenQuiesceEvent();
        if (maintenance.WaitOne(0))
            return null;
        dynamic service = TaskStore.ConnectScheduler();
        var task = Find((object)service.GetFolder(@"\"));
        var now = DateTimeOffset.UtcNow;
        return task is { State: PowerState.Running, Timer: { } timer } &&
            timer.Id == id && now >= timer.Due && now < timer.Due.Add(DispatchWindow) ? timer.Action : null;
    });

    private PowerStatus? Find(object taskFolder)
    {
        dynamic folder = taskFolder;
        dynamic task;
        try { task = folder.GetTask(_name); }
        catch (Exception ex) when (ex.HResult is TaskNotFound or FileNotFound) { return null; }

        dynamic definition = task.Definition;
        if ((string)definition.RegistrationInfo.Source != Source ||
            !TaskStore.IsCurrentUser((string)definition.Principal.UserId) ||
            (int)definition.Triggers.Count != 1 || (int)definition.Triggers[1].Type != 1 ||
            definition.Data is not string data || JsonSerializer.Deserialize<PowerTimer>(data) is not { } timer ||
            timer.Id == Guid.Empty || !Enum.IsDefined(timer.Action))
            throw new InvalidDataException(L("PowerTaskInvalid"));

        var result = (int)task.LastTaskResult;
        var state = (int)task.State is 2 or 4 ? PowerState.Running :
            !(bool)task.Enabled ? PowerState.Disabled :
            result != HasNotRun ? result == 0 ? PowerState.Completed : PowerState.Failed :
            DateTimeOffset.UtcNow >= timer.Due.Add(DispatchWindow) ? PowerState.Missed : PowerState.Waiting;
        return new PowerStatus(timer, state, result);
    }

    private T WithLock<T>(Func<T> work)
    {
        using var gate = new Mutex(false, $@"Global\{_name}");
        try
        {
            if (!gate.WaitOne(TimeSpan.FromSeconds(4)))
                throw new InvalidOperationException(L("PowerTaskBusy"));
        }
        catch (AbandonedMutexException) { }
        try { return work(); }
        finally { gate.ReleaseMutex(); }
    }

    private static string FormatBoundary(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

}
