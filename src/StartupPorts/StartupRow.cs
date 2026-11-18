namespace StartupPorts;

internal enum RunTracking { None, Root, Group }

internal sealed record StartupRow(ScheduledEntry Task, RunInfo? Run, PortSnapshot Ports, RunTracking Tracking)
{
    public StartupEntry Entry => Task.Entry;
    public string Name => Entry.Name;
    public string Enabled => Entry.Enabled ? "已启用" : "已停用";
    public string Delay => $"{Entry.DelaySeconds} 秒";

    public string Status =>
        Entry.PathIssue() ??
        (Task.State switch
        {
            2 => "等待启动",
            4 => Tracking != RunTracking.None ? "运行中" : "正在启动",
            _ when Tracking != RunTracking.None => "运行中",
            _ when Run is null => "尚未运行",
            _ when Run.Error is not null => "启动失败",
            _ when !Run.Finished => "跟踪已中断",
            _ when Run.ExitCode == 0 => "已退出",
            _ when Run.ExitCode is { } code => $"退出码 {code}",
            _ => "已结束",
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
            var state = own && other && !uncertain ? "已监听，另有占用" :
                own ? (uncertain ? "已监听，部分未知" : "已监听") :
                uncertain ? "无法确认" : other ? "被其他进程占用" : "未监听";
            return $"{Entry.ExpectedProtocol.ToString().ToUpperInvariant()} {port} · {state}";
        }
    }

    public string Details
    {
        get
        {
            var parts = new List<string> { Entry.Target, $"工作目录 {Entry.WorkingDirectory}" };
            if (Run is { } run)
                parts.Add($"最近启动 {run.StartedAt.LocalDateTime:MM-dd HH:mm:ss}");
            if (Run?.Error is { } error)
                parts.Add(error);
            var actual = Ports.Rows.Where(row => row.ManagedBy.Contains(Entry.Id))
                .Select(row => $"{row.Protocol} {row.Port}").Distinct().ToArray();
            if (actual.Length > 0)
                parts.Add("实际监听 " + string.Join("、", actual));
            var owners = Ports.Rows.Where(row => row.Port == Entry.ExpectedPort && row.Endpoint.Protocol == Entry.ExpectedProtocol &&
                    !row.ManagedBy.Contains(Entry.Id))
                .Select(row => $"{row.Process} (PID {row.Pid})").Distinct().ToArray();
            if (owners.Length > 0)
                parts.Add("同端口的其他进程 " + string.Join("、", owners));
            return string.Join("  ·  ", parts);
        }
    }
}
