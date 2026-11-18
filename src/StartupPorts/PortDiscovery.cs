namespace StartupPorts;

internal sealed class PortDiscovery
{
    private Dictionary<(Protocol Protocol, int Port, int Pid), long?>? _baseline;
    private readonly HashSet<(Protocol Protocol, int Port, int Pid)> _discovered = [];

    public void Reset()
    {
        _baseline = null;
        Dismiss();
    }

    public void Dismiss() => _discovered.Clear();

    public IReadOnlyList<PortRow> Update(PortSnapshot snapshot)
    {
        if (snapshot.Issues.Count > 0)
        {
            Reset();
            return [];
        }

        var rows = snapshot.Rows.GroupBy(row => (row.Endpoint.Protocol, row.Port, row.Pid))
            .ToDictionary(group => group.Key, group => group.First());
        var now = rows.ToDictionary(pair => pair.Key, pair => pair.Value.Created);
        if (_baseline is { } before)
        {
            _discovered.RemoveWhere(endpoint => !now.ContainsKey(endpoint));
            foreach (var (endpoint, created) in now)
                if (!before.TryGetValue(endpoint, out var prior) ||
                    (prior.HasValue && created.HasValue && prior.Value != created.Value))
                    _discovered.Add(endpoint);
        }
        _baseline = now;
        return rows.Where(pair => _discovered.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
    }
}
