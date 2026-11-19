using System.Windows.Media.Imaging;

using static StartupPorts.UiText;

namespace StartupPorts;

internal enum RunTracking { None, Root, Group }

internal sealed record StartupRow(ScheduledEntry Task, RunInfo? Run, PortSnapshot Ports, RunTracking Tracking)
{
    public StartupEntry Entry => Task.Entry;
    public BitmapSource? Icon { get; init; }
    public string Name => Entry.Name;
    public string Enabled => Entry.Enabled ? L("Enabled") : L("Disabled");
    public string Delay => L("SecondsDuration", Entry.DelaySeconds);

    public string Status =>
        Entry.PathIssue() ??
        (Task.State switch
        {
            2 => L("Queued"),
            4 => Tracking != RunTracking.None ? L("Running") : L("Starting"),
            _ when Tracking != RunTracking.None => L("Running"),
            _ when Run is null => L("NeverRun"),
            _ when Run.Error is not null => L("LaunchFailed"),
            _ when !Run.Finished => L("TrackingInterrupted"),
            _ when Run.ExitCode == 0 => L("Exited"),
            _ when Run.ExitCode is { } code => L("ExitCode", code),
            _ => L("Finished"),
        });

    public string Port
    {
        get
        {
            if (Entry.ExpectedPort is not { } port)
                return "—";
            var matches = Ports.Rows.Where(row => row.Port == port && row.Endpoint.Protocol == Entry.ExpectedProtocol).ToArray();
            var own = matches.Any(row => row.ManagedBy.Contains(Entry.Id));
            var other = matches.Any(row => !row.ManagedBy.Contains(Entry.Id) && !row.IdentityUncertain);
            // A missing process group can make an apparent external match ambiguous.
            var uncertain = matches.Any(row => row.IdentityUncertain) ||
                (matches.Length > 0 && Run is { Finished: false } &&
                 Tracking != RunTracking.Group) ||
                Ports.Issues.Any(issue => issue.StartsWith(Entry.ExpectedProtocol.ToString(), StringComparison.OrdinalIgnoreCase));
            var state = own && other && !uncertain ? L("ListeningAndOccupied") :
                own ? (uncertain ? L("ListeningPartlyUnknown") : L("Listening")) :
                uncertain ? L("CannotConfirm") : other ? L("OccupiedByOthers") : L("NotListening");
            return $"{Entry.ExpectedProtocol.ToString().ToUpperInvariant()} {port} · {state}";
        }
    }

    public string Details
    {
        get
        {
            var parts = new List<string> { Entry.Target, L("WorkingDirectoryDetails", Entry.WorkingDirectory) };
            if (Run is { } run)
                parts.Add(L("LastLaunchDetails", run.StartedAt.LocalDateTime));
            if (Run?.Error is { } error)
                parts.Add(error);
            var actual = Ports.Rows.Where(row => row.ManagedBy.Contains(Entry.Id))
                .Select(row => $"{row.Protocol} {row.Port}").Distinct().ToArray();
            if (actual.Length > 0)
                parts.Add(L("ActualListeners", string.Join(L("ListSeparator"), actual)));
            var owners = Ports.Rows.Where(row => row.Port == Entry.ExpectedPort && row.Endpoint.Protocol == Entry.ExpectedProtocol &&
                    !row.ManagedBy.Contains(Entry.Id))
                .Select(row => $"{row.Process} (PID {row.Pid})").Distinct().ToArray();
            if (owners.Length > 0)
                parts.Add(L("OtherPortOwners", string.Join(L("ListSeparator"), owners)));
            return string.Join("  ·  ", parts);
        }
    }
}
