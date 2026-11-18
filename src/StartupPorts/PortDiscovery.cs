namespace StartupPorts;

internal sealed class PortDiscovery
{
    private Dictionary<BoundEndpoint, long?>? _baseline;
    private readonly HashSet<BoundEndpoint> _discovered = [];

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

        var now = snapshot.Rows.ToDictionary(row => row.Endpoint, row => row.Created);
        if (_baseline is { } before)
        {
            _discovered.RemoveWhere(endpoint => !now.ContainsKey(endpoint));
            foreach (var (endpoint, created) in now)
                if (!before.TryGetValue(endpoint, out var prior) ||
                    (prior.HasValue && created.HasValue && prior.Value != created.Value))
                    _discovered.Add(endpoint);
        }
        _baseline = now;
        return snapshot.Rows.Where(row => _discovered.Contains(row.Endpoint)).ToArray();
    }
}
