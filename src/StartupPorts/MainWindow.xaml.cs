using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;

using static StartupPorts.UiText;

namespace StartupPorts;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim _refreshGate = new(1);
    private readonly PortMonitor _monitor = new();
    private readonly PortDiscovery _discovery = new();
    private readonly ProcessIcons _icons = new();
    private readonly EventWaitHandle _maintenance = LaunchRunner.OpenQuiesceEvent();
    private readonly ObservableCollection<PortService> _ports = [];
    private readonly Dictionary<DataGridColumn, string> _columnHeaders;
    private readonly GitHubUpdates _updates = new();
    private readonly CancellationTokenSource _updateLifetime = new();
    private Task<UpdateRelease?>? _updateCheck;
    private UpdateRelease? _availableUpdate;
    private ICollectionView? _portView;
    private bool _readingWindowsStartup;
    private bool _working;
    private bool _closed;
    private int _discoveryGeneration;

    public MainWindow()
    {
        InitializeComponent();
        _columnHeaders = new[] { StartupGrid, WindowsStartupGrid, PortGrid }
            .SelectMany(grid => grid.Columns).ToDictionary(column => column, column => (string)column.Header);
        UpdateColumnHeaders();
        Language = XmlLanguage.GetLanguage(LanguageCode);
        LanguageSelector.SelectedValue = LanguageCode;
        LanguageSelector.SelectionChanged += LanguageChanged;
        VersionText.Text = $"StartupPorts  {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        PortGrid.ItemsSource = _ports;
        _portView = CollectionViewSource.GetDefaultView(_ports);
        _portView.Filter = item => item is PortService service && MatchesPortFilter(service);
        _portView.SortDescriptions.Add(new SortDescription(nameof(PortService.Port), ListSortDirection.Ascending));
        if (_portView is ICollectionViewLiveShaping { CanChangeLiveSorting: true } liveView)
            liveView.IsLiveSorting = true;
        StartupNav.IsChecked = true;
        Loaded += async (_, _) =>
        {
            _timer.Start();
            if (UpdatePreferences.CheckAtStartup)
                _ = CheckForUpdateOnStartupAsync();
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
            _updateLifetime.Cancel();
            _updateLifetime.Dispose();
            _maintenance.Dispose();
        };
    }

    private async Task CheckForUpdateOnStartupAsync()
    {
        try { await CheckForUpdatesAsync(); }
        catch (Exception ex) when (UpdateDialog.IsUpdateError(ex)) { }
    }

    private async Task<UpdateRelease?> CheckForUpdatesAsync()
    {
        if (_closed)
            return null;
        if (_updateCheck is null || _updateCheck.IsCompleted)
            _updateCheck = _updates.FindLatestAsync(GitHubUpdates.CurrentVersion, _updateLifetime.Token);

        var latest = await _updateCheck;
        if (!_closed)
        {
            _availableUpdate = latest;
            UpdateButtonLabel();
        }
        return latest;
    }

    private void UpdateButtonLabel() => UpdatesButton.Content = _availableUpdate == null
        ? L("CheckForUpdates") : L("UpdateAvailableButton", _availableUpdate.Version);

    private void OpenUpdatesClicked(object sender, RoutedEventArgs e) =>
        new UpdateDialog(_updates, CheckForUpdatesAsync, _availableUpdate) { Owner = this }.ShowDialog();

    private async Task RefreshAsync(bool wait)
    {
        if (_closed || WindowState == WindowState.Minimized || WindowsStartupNav.IsChecked == true)
            return;
        if (wait)
            await _refreshGate.WaitAsync();
        else if (!_refreshGate.Wait(0))
            return;
        try
        {
            if (_closed || WindowsStartupNav.IsChecked == true)
                return;
            var generation = _discoveryGeneration;
            var language = LanguageCode;
            var state = await Task.Run(Capture);
            if (_closed || WindowState == WindowState.Minimized || WindowsStartupNav.IsChecked == true || language != LanguageCode)
                return;
            if (state.Tasks != null)
            {
                var id = (StartupGrid.SelectedItem as StartupRow)?.Entry.Id;
                Replace(StartupGrid, state.Tasks, state.Tasks.FirstOrDefault(task => task.Entry.Id == id));
                StartupEmpty.Visibility = state.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                TaskCount.Text = L("TaskCount", state.Tasks.Count, state.Tasks.Count(task => task.Entry.Enabled));
            }
            else
                TaskCount.Text = L("TasksUnavailable");

            UpdatePorts(state.Services);
            if (PortsNav.IsChecked == true && generation == _discoveryGeneration)
                UpdateDiscovery(state.Ports);
            StatusText.Text = state.Issues.Count == 0
                ? L("RefreshedEveryTwoSeconds", DateTime.Now)
                : string.Join("  ·  ", state.Issues);
            UpdateControls();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (!_closed)
            {
                ResetDiscovery();
                StatusText.Text = L("LocalReadError", ex.Message);
            }
        }
        finally { _refreshGate.Release(); }
    }

    private (IReadOnlyList<StartupRow>? Tasks, PortSnapshot Ports, IReadOnlyList<PortService> Services, IReadOnlyList<string> Issues) Capture()
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
            issues.Add(L("TasksReadError", ex.Message));
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
        var names = tasks?.Entries.ToDictionary(task => task.Entry.Id, task => task.Entry.Name) ?? [];
        return (rows, ports, PortService.Group(ports, names, _icons), issues);
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

    private async void Navigate(object sender, RoutedEventArgs e)
    {
        if (StartupPanel == null)
            return;
        var showPorts = ReferenceEquals(sender, PortsNav);
        var showWindows = ReferenceEquals(sender, WindowsStartupNav);
        StartupPanel.Visibility = showPorts || showWindows ? Visibility.Collapsed : Visibility.Visible;
        WindowsStartupPanel.Visibility = showWindows ? Visibility.Visible : Visibility.Collapsed;
        PortsPanel.Visibility = showPorts ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Visibility = showPorts || showWindows ? Visibility.Collapsed : Visibility.Visible;
        UpdatePageLabels();
        ResetDiscovery();
        UpdateControls();
        if (showWindows)
            await ReadWindowsStartupAsync();
        else if (IsLoaded)
            await RefreshAsync(wait: true);
    }

    private void UpdatePageLabels()
    {
        var showPorts = PortsNav.IsChecked == true;
        var showWindows = WindowsStartupNav.IsChecked == true;
        PageTitle.Text = L(showPorts ? "PortMonitor" : showWindows ? "WindowsStartupItems" : "StartupManagement");
        PageDescription.Text = L(showPorts ? "PortMonitorDescription" : showWindows
            ? "WindowsStartupDescription" : "StartupPageDescription");
    }

    private async void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_closed || LanguageSelector.SelectedValue is not string code || code == LanguageCode)
            return;

        ApplyLanguage(code);
        try { SavePreference(); }
        catch (Exception ex) when (IsExpected(ex))
        {
            ShowError(new InvalidOperationException(L("LanguageSaveError", ex.Message)));
        }
        if (WindowsStartupNav.IsChecked == true)
            await ReadWindowsStartupAsync();
        else
            await RefreshAsync(wait: true);
    }

    internal void ApplyLanguage(string code)
    {
        SetLanguage(code);
        ApplyResources(Application.Current);
        Language = XmlLanguage.GetLanguage(code);
        LanguageSelector.SelectedValue = code;
        UpdateColumnHeaders();
        UpdateButtonLabel();
        UpdatePageLabels();
        ResetDiscovery();
        foreach (var row in _ports)
            row.NotifyDisplayChanged();
        _portView?.Refresh();
        ShowPortCount();
        UpdateControls();
    }

    private void UpdateColumnHeaders()
    {
        foreach (var (column, resource) in _columnHeaders)
            column.Header = L(resource);
    }

    private async Task ReadWindowsStartupAsync()
    {
        if (_readingWindowsStartup || _closed)
            return;
        _readingWindowsStartup = true;
        StatusText.Text = L("ReadingWindowsStartup");
        UpdateControls();
        try
        {
            var snapshot = await Task.Run(WindowsStartup.Read);
            if (_closed || WindowsStartupNav.IsChecked != true)
                return;
            var id = (WindowsStartupGrid.SelectedItem as WindowsStartupItem)?.Id;
            Replace(WindowsStartupGrid, snapshot.Items, snapshot.Items.FirstOrDefault(item => item.Id == id));
            WindowsStartupCount.Text = L("SystemCount", snapshot.Items.Count);
            WindowsStartupEmpty.Visibility = snapshot.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = snapshot.Issues.Count == 0
                ? L("RefreshedAt", DateTime.Now)
                : string.Join("  ·  ", snapshot.Issues);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (!_closed && WindowsStartupNav.IsChecked == true)
                StatusText.Text = L("WindowsStartupReadError", ex.Message);
        }
        finally
        {
            _readingWindowsStartup = false;
            if (!_closed)
                UpdateControls();
        }
    }

    private void ResetDiscovery()
    {
        _discoveryGeneration++;
        _discovery.Reset();
        Notice.Visibility = Visibility.Collapsed;
    }

    private void ClearNotice()
    {
        _discovery.Dismiss();
        Notice.Visibility = Visibility.Collapsed;
    }

    private void UpdateDiscovery(PortSnapshot snapshot)
    {
        var discovered = _discovery.Update(snapshot);
        Notice.Visibility = discovered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (discovered.Count == 0)
            return;
        var names = discovered.Select(row => $"{row.Protocol} {row.Port}（{row.Process}）").Distinct().Take(5);
        NoticeText.Text = L("DiscoveryCount", discovered.Count, string.Join(L("ListSeparator"), names));
    }

    private bool MatchesPortFilter(PortService service) =>
        service.Matches(SearchBox.Text.Trim(), ProtocolFilter.SelectedIndex, OwnerFilter.SelectedIndex);

    private void UpdatePorts(IReadOnlyList<PortService> next)
    {
        var nextKeys = next.Select(service => service.Key).ToHashSet();
        for (var index = _ports.Count - 1; index >= 0; index--)
            if (!nextKeys.Contains(_ports[index].Key))
                _ports.RemoveAt(index);
        var existing = _ports.ToDictionary(service => service.Key);
        var filterChanged = false;
        foreach (var service in next)
        {
            if (existing.TryGetValue(service.Key, out var row))
            {
                var wasVisible = MatchesPortFilter(row);
                row.Update(service);
                filterChanged |= wasVisible != MatchesPortFilter(row);
            }
            else
                _ports.Add(service);
        }
        if (filterChanged)
            _portView!.Refresh();
        ShowPortCount();
    }

    private void ShowPortCount()
    {
        var visible = PortGrid.Items.Count;
        PortCount.Text = visible == _ports.Count
            ? L("PortCount", visible) : L("FilteredPortCount", visible, _ports.Count);
        PortsEmpty.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        if (TaskDetails == null || PortDetails == null || WindowsStartupDetails == null)
            return;
        var startup = StartupGrid.SelectedItem as StartupRow;
        var service = PortGrid.SelectedItem as PortService;
        var port = service?.Primary;
        var windowsItem = WindowsStartupGrid.SelectedItem as WindowsStartupItem;
        EditButton.IsEnabled = DeleteButton.IsEnabled = EnableButton.IsEnabled = !_working && startup != null;
        RunButton.IsEnabled = !_working && startup != null && startup.Tracking == RunTracking.None && startup.Task.State != 4 &&
            startup.Entry.PathIssue() == null;
        EnableButton.Content = startup == null ? L("EnableDisable") : startup.Entry.Enabled ? L("Disable") : L("Enable");
        AddButton.IsEnabled = !_working;
        RefreshButton.IsEnabled = WindowsStartupNav.IsChecked == true ? !_readingWindowsStartup : !_working;
        LanguageSelector.IsEnabled = !_working && !_readingWindowsStartup;
        UpdatesButton.IsEnabled = !_working;
        CopyButton.IsEnabled = port != null;
        OpenButton.IsEnabled = service?.BrowserAddress != null;
        FromPortButton.IsEnabled = !_working && port?.CanAdd == true;
        EndButton.IsEnabled = !_working && port?.CanEnd == true;
        TaskDetails.Text = startup?.Details ?? L("ChooseStartupTask");
        PortDetails.Text = service?.Details ?? L("ChoosePort");
        CopyStartupButton.IsEnabled = windowsItem != null;
        WindowsStartupDetails.Text = windowsItem?.Command ?? L("ChooseWindowsStartupItem");
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
    private async void RefreshClicked(object sender, RoutedEventArgs e)
    {
        if (WindowsStartupNav.IsChecked == true)
            await ReadWindowsStartupAsync();
        else
            await RefreshAsync(wait: true);
    }
    private void DismissNoticeClicked(object sender, RoutedEventArgs e) => ClearNotice();
    private void SearchChanged(object sender, RoutedEventArgs e)
    {
        if (_portView == null)
            return;
        _portView.Refresh();
        ShowPortCount();
        UpdateControls();
    }
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
        if (StartupGrid.SelectedItem is StartupRow row && Confirm(L("DeletePrompt", row.Name)))
            await Change(() => new TaskStore().Delete(row.Entry.Id));
    }

    private async void FromPortClicked(object sender, RoutedEventArgs e)
    {
        if (PortGrid.SelectedItem is PortService { Primary: { CanAdd: true } row })
            await Edit(port: row);
    }

    private async void EndClicked(object sender, RoutedEventArgs e)
    {
        if (PortGrid.SelectedItem is PortService { Primary: { CanEnd: true } row } &&
            Confirm(L("EndProcessPrompt", row.Process, row.Pid)))
            await Change(() => PortMonitor.End(row));
    }

    private void CopyClicked(object sender, RoutedEventArgs e)
    {
        if (PortGrid.SelectedItem is PortService service)
            CopyText(service.AddressesToCopy);
    }

    private void CopyStartupClicked(object sender, RoutedEventArgs e)
    {
        if (WindowsStartupGrid.SelectedItem is WindowsStartupItem item)
            CopyText(item.Command);
    }

    private void CopyText(string value)
    {
        try { Clipboard.SetText(value); }
        catch (ExternalException ex) { ShowError(ex); }
    }

    private void OpenClicked(object sender, RoutedEventArgs e)
    {
        if (PortGrid.SelectedItem is PortService { BrowserAddress: { } uri })
            OpenAddress(uri.AbsoluteUri);
    }

    private void OpenWindowsStartupSettings(object sender, RoutedEventArgs e) => OpenAddress("ms-settings:startupapps");

    private void OpenAddress(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose(); }
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
