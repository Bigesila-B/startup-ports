using System.ComponentModel;
using System.Net;
using System.Windows.Media.Imaging;

using static StartupPorts.UiText;

namespace StartupPorts;

internal readonly record struct PortServiceKey(Protocol Protocol, int Port, int Pid, long? Created)
{
    public static PortServiceKey From(PortRow row) => new(row.Endpoint.Protocol, row.Port, row.Pid, row.Created);
}

internal sealed class PortService : INotifyPropertyChanged
{
    private PortRow[] _rows;

    private PortService(PortServiceKey key, PortRow[] rows, ProcessAppearance appearance, string? taskName)
    {
        Key = key;
        _rows = rows;
        Appearance = appearance;
        TaskName = taskName;
    }

    public PortServiceKey Key { get; }
    private ProcessAppearance Appearance { get; set; }
    public BitmapSource? Icon => Appearance.Icon;
    public PortRow Primary => _rows[0];
    public string? TaskName { get; private set; }
    public string Name => TaskName ?? Appearance.DisplayName ?? (Primary.Pid == 4 ? L("WindowsSystemProcess") : Primary.Process);
    public string ProcessSummary => Name.Equals(Primary.Process, StringComparison.OrdinalIgnoreCase)
        ? $"PID {Primary.Pid}" : $"{Primary.Process} · PID {Primary.Pid}";
    public int Port => Key.Port;
    public string Protocol => Primary.Protocol;
    public string Address => string.Join("  ·  ", _rows.Select(row => row.Address));
    public string AddressesToCopy => string.Join(Environment.NewLine, _rows.Select(row => row.Endpoint.Display));
    public string Details => $"{string.Join("  ·  ", _rows.Select(row => row.Endpoint.Display))}  ·  {Primary.Path ?? Primary.Process}";
    public string Origin => Primary.Origin ?? (Primary.IsCurrentUser ? L("CurrentUser") : L("UnknownOrigin"));
    public double? Cpu => Primary.Cpu;
    public string CpuDisplay => Primary.CpuDisplay;
    public long? Memory => Primary.Memory;
    public string MemoryDisplay => Primary.MemoryDisplay;
    public Uri? BrowserAddress => _rows.OrderBy(row => IPAddress.IsLoopback(row.Endpoint.Address) ? 0 : 1)
        .Select(row => row.Endpoint.BrowserAddress).FirstOrDefault(uri => uri != null);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyDisplayChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public bool Matches(string query, int protocol, int owner)
    {
        if ((protocol > 0 && (int)Key.Protocol != protocol - 1) ||
            (owner == 1 && !Primary.IsCurrentUser) || (owner == 2 && Primary.IsCurrentUser))
            return false;
        if (query.Length == 0)
            return true;
        if (int.TryParse(query, out var number))
            return Port == number || Key.Pid == number;
        return Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            Primary.Process.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            Origin.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            _rows.Any(row => row.Address.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Endpoint.Display.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    public void Update(PortService next)
    {
        var before = Primary;
        var changed = before.CpuDisplay != next.Primary.CpuDisplay ||
            before.MemoryDisplay != next.Primary.MemoryDisplay ||
            before.Process != next.Primary.Process || before.Path != next.Primary.Path ||
            before.Origin != next.Primary.Origin || before.IsCurrentUser != next.Primary.IsCurrentUser ||
            TaskName != next.TaskName || Appearance != next.Appearance ||
            !_rows.Select(row => row.Endpoint).SequenceEqual(next._rows.Select(row => row.Endpoint));
        _rows = next._rows;
        Appearance = next.Appearance;
        TaskName = next.TaskName;
        if (changed)
            NotifyDisplayChanged();
    }

    public static IReadOnlyList<PortService> Group(PortSnapshot snapshot, IReadOnlyDictionary<Guid, string> names, ProcessIcons icons)
    {
        icons.Retain(snapshot.Rows);
        return snapshot.Rows.GroupBy(PortServiceKey.From).Select(group =>
        {
            var rows = group.OrderBy(row => row.Endpoint.Address.AddressFamily).ThenBy(row => row.Address, StringComparer.Ordinal).ToArray();
            var task = rows[0].ManagedBy.Select(id => names.GetValueOrDefault(id)).FirstOrDefault(name => name != null);
            return new PortService(group.Key, rows, icons.Read(rows[0]), task);
        }).ToArray();
    }
}
