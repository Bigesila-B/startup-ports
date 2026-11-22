using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using static StartupPorts.UiText;

namespace StartupPorts;

public partial class PowerPage : UserControl
{
    private readonly PowerTaskStore _store = new();
    private readonly SemaphoreSlim _gate = new(1);
    private PowerStatus? _status;
    private PowerOptions _options;
    private bool _busy;
    private bool _ready;
    private bool _invalidDate;
    private string? _readError;
    private string? _actionError;

    public PowerPage()
    {
        InitializeComponent();
        SetDelay(30);
        Render();
    }

    internal async Task RefreshAsync()
    {
        if (!_gate.Wait(0))
            return;
        try { await ReadStatusAsync(); }
        finally { _gate.Release(); }
        Render();
    }

    private async Task ReadStatusAsync()
    {
        try
        {
            var (status, options) = await Task.Run(() => (_store.Read(), WindowsPower.Available()));
            SetStatus(status, options);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _ready = false;
            _readError = ex.Message;
        }
    }

    internal void SetStatus(PowerStatus? status, PowerOptions options)
    {
        _status = status;
        _options = options;
        _ready = true;
        _readError = null;
    }

    internal void Render()
    {
        if (ScheduleButton == null || CancelButton == null || DueTime == null)
            return;
        var action = (PowerAction)Actions.SelectedIndex;
        var active = _status?.State is PowerState.Waiting or PowerState.Running;
        var time = ReadDue();
        ScheduleButton.IsEnabled = _ready && !_busy && !active && _options.Supports(action) && time is { } due &&
            due > DateTimeOffset.UtcNow.AddSeconds(2);
        CancelButton.IsEnabled = _ready && !_busy && _status?.State == PowerState.Waiting;
        CancelButton.Visibility = _status?.State == PowerState.Waiting ? Visibility.Visible : Visibility.Collapsed;
        FormHint.Text = _actionError ?? _readError ?? (!_ready ? L("PowerLoading") : !_options.Supports(action) ? L("PowerUnsupported") :
            time == null ? L("PowerTimeFormat") : time <= DateTimeOffset.UtcNow ? L("PowerFutureTime") : L("PowerSaveWork"));
        TimerTitle.Text = _status == null ? L("PowerNoTimer") : WindowsPower.ActionLabel(_status.Timer.Action);
        if (_status is not { } status)
        {
            TimerDetails.Text = "";
            return;
        }
        var detail = status.State switch
        {
            PowerState.Waiting => L("PowerCountdown", Countdown(status.Timer.Due)),
            PowerState.Running => L("PowerRunning"),
            PowerState.Completed => L("PowerCompleted"),
            PowerState.Failed => L("PowerFailed", $"0x{status.Result:X8}"),
            PowerState.Missed => L("PowerMissed"),
            _ => L("PowerDisabled"),
        };
        TimerDetails.Text = $"{FormatDue(status.Timer.Due)}\n{detail}";
    }

    private async void ScheduleClicked(object sender, RoutedEventArgs e)
    {
        var action = (PowerAction)Actions.SelectedIndex;
        if (!ScheduleButton.IsEnabled || ReadDue() is not { } due)
            return;
        var question = L("PowerConfirm", WindowsPower.ActionLabel(action), FormatDue(due));
        if (MessageBox.Show(Window.GetWindow(this), question, L("TimedShutdown"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        await ChangeAsync(() => _store.Schedule(action, due));
    }

    private async void CancelClicked(object sender, RoutedEventArgs e)
    {
        if (CancelButton.IsEnabled && _status is { State: PowerState.Waiting } status)
            await ChangeAsync(() => _store.Cancel(status.Timer.Id));
    }

    private async Task ChangeAsync(Action change)
    {
        if (_busy)
            return;
        _busy = true;
        _actionError = null;
        Render();
        string? changeError = null;
        await _gate.WaitAsync();
        try
        {
            try { await Task.Run(change); }
            catch (Exception ex) when (IsExpected(ex)) { changeError = ex.Message; }
            await ReadStatusAsync();
            _actionError = changeError;
        }
        finally
        {
            _gate.Release();
            _busy = false;
            Render();
        }
    }

    private void PresetClicked(object sender, RoutedEventArgs e) =>
        SetDelay(int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture));

    private void SetDelay(int minutes)
    {
        var time = DateTime.Now.AddMinutes(minutes);
        if (time.Second != 0 || time.Millisecond != 0)
            time = time.AddMinutes(1);
        DueDate.SelectedDate = time.Date;
        _invalidDate = false;
        DueTime.Text = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        InputChanged(this, new RoutedEventArgs());
    }

    private void DateChanged(object? sender, SelectionChangedEventArgs e)
    {
        _invalidDate = false;
        InputChanged(sender, e);
    }

    private void DateInvalid(object? sender, DatePickerDateValidationErrorEventArgs e)
    {
        e.ThrowException = false;
        _invalidDate = true;
        Render();
    }

    private void InputChanged(object? sender, RoutedEventArgs e)
    {
        if (_ready)
            _actionError = null;
        Render();
    }

    private DateTimeOffset? ReadDue()
    {
        if (_invalidDate || DueDate.SelectedDate is not { } day ||
            !TimeOnly.TryParseExact(DueTime.Text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            return null;
        var local = day.Date + time.ToTimeSpan();
        var zone = TimeZoneInfo.Local;
        return zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local) ? null : new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static string FormatDue(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Countdown(DateTimeOffset value)
    {
        var remaining = value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return "00:00:00";
        return $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private static bool IsExpected(Exception ex) => ex is COMException or IOException or InvalidDataException or
        SecurityException or JsonException or UnauthorizedAccessException or InvalidOperationException or Win32Exception;
}
