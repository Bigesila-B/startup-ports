using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace StartupPorts;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim _refreshGate = new(1);
    private readonly PortMonitor _monitor = new();
    private readonly EventWaitHandle _maintenance = LaunchRunner.OpenQuiesceEvent();
    private Dictionary<BoundEndpoint, long?>? _baseline;
    private IReadOnlyList<PortRow> _ports = [];
    private bool _working;
    private bool _closed;
    private int _discoveryGeneration;
    private readonly HashSet<BoundEndpoint> _discovered = [];

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"StartupPorts  {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        StartupNav.IsChecked = true;
        Loaded += async (_, _) =>
        {
            _timer.Start();
            await RefreshAsync(wait: true);
        };
        _timer.Tick += async (_, _) =>
        {
            if (_maintenance.WaitOne(0))
                Close();
            else if (!_working && WindowState != WindowState.Minimized)
                await RefreshAsync(wait: false);
        };
        StateChanged += async (_, _) =>
        {
            ResetDiscovery();
            if (WindowState != WindowState.Minimized && IsLoaded)
                await RefreshAsync(wait: true);
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _closed = true;
            _maintenance.Dispose();
        };
    }

    private async Task RefreshAsync(bool wait)
    {
        if (_closed || WindowState == WindowState.Minimized)
            return;
        if (wait)
            await _refreshGate.WaitAsync();
        else if (!_refreshGate.Wait(0))
            return;
        try
        {
            if (_closed)
                return;
            var generation = _discoveryGeneration;
            var state = await Task.Run(Capture);
            if (_closed || WindowState == WindowState.Minimized)
                return;
            if (state.Tasks != null)
            {
                var id = (StartupGrid.SelectedItem as StartupRow)?.Entry.Id;
                Replace(StartupGrid, state.Tasks, state.Tasks.FirstOrDefault(task => task.Entry.Id == id));
                StartupEmpty.Visibility = state.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                TaskCount.Text = $"{state.Tasks.Count} 项 · {state.Tasks.Count(task => task.Entry.Enabled)} 项已启用";
            }
            else
                TaskCount.Text = "任务暂不可用";

            _ports = state.Ports.Rows;
            FilterPorts();
            if (PortsNav.IsChecked == true && generation == _discoveryGeneration)
                UpdateDiscovery(state.Ports);
            StatusText.Text = state.Issues.Count == 0
                ? $"更新于 {DateTime.Now:HH:mm:ss}  ·  每 2 秒刷新"
                : string.Join("  ·  ", state.Issues);
            UpdateControls();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (!_closed)
            {
                ResetDiscovery();
                StatusText.Text = $"读取本机状态失败：{ex.Message}";
            }
        }
        finally { _refreshGate.Release(); }
    }

    private (IReadOnlyList<StartupRow>? Tasks, PortSnapshot Ports, IReadOnlyList<string> Issues) Capture()
    {
        TaskList? tasks = null;
        var issues = new List<string>();
        try
        {
            tasks = new TaskStore().List();
            issues.AddRange(tasks.Issues);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            issues.Add($"自启任务暂不可用：{ex.Message}");
        }

        var runs = new Dictionary<Guid, RunInfo>();
        foreach (var task in tasks?.Entries ?? [])
            if (RunState.Read(task.Entry.Id) is { } run)
                runs[task.Entry.Id] = run;
        var ports = _monitor.Capture(runs);
        issues.AddRange(ports.Issues);
        var rows = tasks?.Entries.Select(task =>
        {
            runs.TryGetValue(task.Entry.Id, out var run);
            return new StartupRow(task, run, ports, ReadTracking(run));
        }).ToArray();
        return (rows, ports, issues);
    }

    private static RunTracking ReadTracking(RunInfo? run)
    {
        if (run is not { Finished: false, ProcessId: { } pid, ProcessCreated: { } expected })
            return RunTracking.None;
        if (run.JobName is { } name)
        {
            using var job = NativeProcess.OpenJobObject(4, false, name);
            if (!job.IsInvalid)
                return RunTracking.Group;
        }
        using var process = NativeProcess.OpenProcess(NativeProcess.QueryAccess | NativeProcess.SynchronizeAccess, false, pid);
        return !process.IsInvalid && NativeProcess.GetProcessTimes(process, out var created, out _, out _, out _) &&
            created == expected && NativeProcess.WaitForSingleObject(process, 0) == NativeProcess.WaitTimeout
            ? RunTracking.Root : RunTracking.None;
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (StartupPanel == null)
            return;
        var showPorts = ReferenceEquals(sender, PortsNav);
        StartupPanel.Visibility = showPorts ? Visibility.Collapsed : Visibility.Visible;
        PortsPanel.Visibility = showPorts ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Visibility = showPorts ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = showPorts ? "端口监控" : "自启管理";
        PageDescription.Text = showPorts
            ? "检查本机监听端口、绑定地址及其占用进程。"
            : "选择当前 Windows 账户登录后要运行的程序和脚本。";
        ResetDiscovery();
        UpdateControls();
    }

    private void ResetDiscovery()
    {
        _discoveryGeneration++;
        _baseline = null;
        ClearNotice();
    }

    private void ClearNotice()
    {
        _discovered.Clear();
        Notice.Visibility = Visibility.Collapsed;
    }

    private void UpdateDiscovery(PortSnapshot snapshot)
    {
        if (snapshot.Issues.Count > 0)
        {
            ResetDiscovery();
            return;
        }
        var now = snapshot.Rows.ToDictionary(row => row.Endpoint, row => row.Created);
        if (_baseline is { } before)
        {
            _discovered.RemoveWhere(endpoint => !now.ContainsKey(endpoint));
            foreach (var (endpoint, created) in now)
                if (!before.TryGetValue(endpoint, out var prior) ||
                    (prior.HasValue && created.HasValue && prior.Value != created.Value))
                    _discovered.Add(endpoint);
            Notice.Visibility = _discovered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (_discovered.Count > 0)
            {
                var names = snapshot.Rows.Where(row => _discovered.Contains(row.Endpoint))
                    .Select(row => $"{row.Protocol} {row.Port}（{row.Process}）").Distinct().Take(5);
                NoticeText.Text = $"发现 {_discovered.Count} 个新端点  ·  {string.Join("、", names)}";
            }
        }
        _baseline = now;
    }

    private void FilterPorts()
    {
        if (PortGrid == null)
            return;
        var query = SearchBox.Text.Trim();
        var filter = ProtocolFilter.SelectedIndex;
        var selected = PortGrid.SelectedItem as PortRow;
        var filtered = _ports.Where(row => (filter <= 0 || (int)row.Endpoint.Protocol == filter - 1) &&
            (query.Length == 0 || row.Process.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             row.Port.ToString().Contains(query, StringComparison.Ordinal) || row.Pid.ToString().Contains(query, StringComparison.Ordinal) ||
             row.Address.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        Replace(PortGrid, filtered, selected is null ? null :
            filtered.FirstOrDefault(row => row.Endpoint == selected.Endpoint && row.Created == selected.Created));
        PortCount.Text = query.Length == 0 && filter <= 0
            ? $"{_ports.Count} 个端点" : $"{filtered.Length} / {_ports.Count} 个端点";
        PortsEmpty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void Replace<T>(DataGrid grid, IEnumerable<T> rows, T? selected) where T : class
    {
        var sorting = grid.Items.SortDescriptions.ToArray();
        grid.ItemsSource = rows;
        using (grid.Items.DeferRefresh())
            foreach (var sort in sorting)
                if (!grid.Items.SortDescriptions.Contains(sort))
                    grid.Items.SortDescriptions.Add(sort);
        grid.SelectedItem = selected;
    }

    private void UpdateControls()
    {
        if (TaskDetails == null || PortDetails == null)
            return;
        var startup = StartupGrid.SelectedItem as StartupRow;
        var port = PortGrid.SelectedItem as PortRow;
        EditButton.IsEnabled = DeleteButton.IsEnabled = EnableButton.IsEnabled = !_working && startup != null;
        RunButton.IsEnabled = !_working && startup != null && startup.Tracking == RunTracking.None && startup.Task.State != 4 &&
            startup.Entry.PathIssue() == null;
        EnableButton.Content = startup == null ? "启用 / 停用" : startup.Entry.Enabled ? "停用" : "启用";
        AddButton.IsEnabled = RefreshButton.IsEnabled = !_working;
        CopyButton.IsEnabled = port != null;
        OpenButton.IsEnabled = port?.Endpoint.BrowserAddress != null;
        FromPortButton.IsEnabled = !_working && port?.CanAdd == true;
        EndButton.IsEnabled = !_working && port?.CanEnd == true;
        TaskDetails.Text = startup?.Details ?? "选择一项查看启动文件和实际监听端口。";
        PortDetails.Text = port is null ? "选择端点查看进程路径。" :
            $"{port.Endpoint.Display}  ·  {port.Path ?? port.Process}";
    }

    private async Task Change(Action change)
    {
        if (_working)
            return;
        _working = true;
        UpdateControls();
        try
        {
            await Task.Run(change);
            await RefreshAsync(wait: true);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (!_closed)
                ShowError(ex);
        }
        finally
        {
            _working = false;
            if (!_closed)
                UpdateControls();
        }
    }

    private async Task Edit(StartupRow? row = null, PortRow? port = null)
    {
        if (_working)
            return;
        var dialog = new EntryDialog(row?.Entry, port) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Entry is { } entry)
            await Change(() => new TaskStore().Save(entry, create: row == null));
    }

    private async void AddClicked(object sender, RoutedEventArgs e) => await Edit();
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync(wait: true);
    private void DismissNoticeClicked(object sender, RoutedEventArgs e) => ClearNotice();
    private void SearchChanged(object sender, RoutedEventArgs e) => FilterPorts();
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();

    private async void EditClicked(object sender, RoutedEventArgs e)
    {
        if (StartupGrid.SelectedItem is StartupRow row)
            await Edit(row);
    }

    private void EditDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(StartupGrid, source) is DataGridRow)
            EditClicked(sender, e);
    }

    private async void RunClicked(object sender, RoutedEventArgs e)
    {
        if (StartupGrid.SelectedItem is StartupRow row)
            await Change(() => new TaskStore().Run(row.Entry.Id));
    }

    private async void EnableClicked(object sender, RoutedEventArgs e)
    {
        if (StartupGrid.SelectedItem is StartupRow row)
            await Change(() => new TaskStore().SetEnabled(row.Entry.Id, !row.Entry.Enabled));
    }

    private async void DeleteClicked(object sender, RoutedEventArgs e)
    {
        if (StartupGrid.SelectedItem is StartupRow row && Confirm($"删除「{row.Name}」的登录自启设置？"))
            await Change(() => new TaskStore().Delete(row.Entry.Id));
    }

    private async void FromPortClicked(object sender, RoutedEventArgs e)
    {
        if (PortGrid.SelectedItem is PortRow { CanAdd: true } row)
            await Edit(port: row);
    }

    private async void EndClicked(object sender, RoutedEventArgs e)
    {
        if (PortGrid.SelectedItem is PortRow { CanEnd: true } row &&
            Confirm($"结束 {row.Process}（PID {row.Pid}）？该进程内未保存的数据可能丢失。"))
            await Change(() => PortMonitor.End(row));
    }

    private void CopyClicked(object sender, RoutedEventArgs e)
    {
        if (PortGrid.SelectedItem is not PortRow row)
            return;
        try { Clipboard.SetText(row.Endpoint.Display); }
        catch (ExternalException ex) { ShowError(ex); }
    }

    private void OpenClicked(object sender, RoutedEventArgs e)
    {
        if (PortGrid.SelectedItem is not PortRow row || row.Endpoint.BrowserAddress is not { } uri)
            return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception ex) { ShowError(ex); }
    }

    private bool Confirm(string message) => MessageBox.Show(this, message, "StartupPorts", MessageBoxButton.YesNo,
        MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void ShowError(Exception ex) =>
        MessageBox.Show(this, ex.Message, "StartupPorts", MessageBoxButton.OK, MessageBoxImage.Error);

    private static bool IsExpected(Exception ex) => ex is ArgumentException or InvalidOperationException or
        UnauthorizedAccessException or System.IO.IOException or Win32Exception or COMException or
        System.Text.Json.JsonException or System.Security.SecurityException;
}
