using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;

using static StartupPorts.UiText;

namespace StartupPorts;

public partial class UpdateDialog : Window
{
    private readonly GitHubUpdates _updates;
    private readonly Func<Task<UpdateRelease?>> _check;
    private readonly CancellationTokenSource _lifetime = new();
    private UpdateRelease? _release;
    private string? _installer;
    private bool _busy;
    private bool _closed;

    internal UpdateDialog(GitHubUpdates updates, Func<Task<UpdateRelease?>> check, UpdateRelease? available)
    {
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(LanguageCode);
        _updates = updates;
        _check = check;
        _release = available;
        CurrentVersionText.Text = L("CurrentUpdateVersion", GitHubUpdates.CurrentVersion);
        AutoCheck.IsChecked = UpdatePreferences.CheckAtStartup;
        ShowRelease();
        Loaded += async (_, _) =>
        {
            if (_release == null)
                await CheckAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    private async Task CheckAsync()
    {
        await RunAsync("UpdateCheckFailed", async token =>
        {
            UpdateStatus.Text = L("CheckingForUpdates");
            _release = null;
            _installer = null;
            UpdateActions();
            _release = await _check().WaitAsync(token);
            ShowRelease();
        });
    }

    private void ShowRelease()
    {
        UpdateStatus.Text = _release is not { } release ? L("NoNewUpdate") :
            L(release.Package == null ? "UpdateDownloadUnavailable" : "NewUpdateFound", release.Version);
        UpdateActions();
    }

    private void UpdateActions()
    {
        NotesButton.Visibility = _release != null ? Visibility.Visible : Visibility.Collapsed;
        DownloadButton.Visibility = _release?.Package != null && _installer == null ? Visibility.Visible : Visibility.Collapsed;
        InstallerButton.Visibility = _installer != null ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.IsEnabled = NotesButton.IsEnabled = DownloadButton.IsEnabled = InstallerButton.IsEnabled = !_busy;
    }

    private async void CheckClicked(object sender, RoutedEventArgs e) => await CheckAsync();

    private async void DownloadClicked(object sender, RoutedEventArgs e)
    {
        if (_release?.Package is not { } package)
            return;
        await RunAsync("UpdateDownloadFailed", async token =>
        {
            UpdateStatus.Text = L("DownloadingUpdate");
            UpdateProgress.Value = 0;
            UpdateProgress.IsIndeterminate = true;
            UpdateProgress.Visibility = Visibility.Visible;
            var progress = new Progress<int>(percent =>
            {
                if (_closed)
                    return;
                UpdateProgress.IsIndeterminate = false;
                UpdateProgress.Value = percent;
            });
            _installer = await _updates.DownloadAsync(package, progress, token);
            UpdateStatus.Text = L("UpdateReady");
        });
    }

    private async void InstallClicked(object sender, RoutedEventArgs e)
    {
        if (_release?.Package is not { } package || _installer is not { } path)
            return;
        await RunAsync("UpdateOpenFailed", async token =>
        {
            UpdateStatus.Text = L("VerifyingUpdate");
            await using var verified = await GitHubUpdates.VerifyInstallerAsync(path, package, token);
            token.ThrowIfCancellationRequested();
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path)!,
            })?.Dispose();
            Close();
        });
    }

    private async Task RunAsync(string errorMessage, Func<CancellationToken, Task> work)
    {
        if (_busy || _closed)
            return;
        _busy = true;
        UpdateActions();
        try { await work(_lifetime.Token); }
        catch (Exception ex) when (IsUpdateError(ex) || ex is Win32Exception)
        {
            if (!_closed)
            {
                if (errorMessage == "UpdateOpenFailed" && ex is (IOException or InvalidDataException))
                    _installer = null;
                UpdateStatus.Text = L(errorMessage, ex is OperationCanceledException ? L("UpdateTimeout") : ex.Message);
            }
        }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                UpdateProgress.Visibility = Visibility.Collapsed;
                UpdateActions();
            }
        }
    }

    private void ViewNotesClicked(object sender, RoutedEventArgs e)
    {
        if (_release is not { } release)
            return;
        try { Process.Start(new ProcessStartInfo(release.Page.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception ex) { UpdateStatus.Text = L("UpdateOpenNotesFailed", ex.Message); }
    }

    private void SaveStartupCheck(object sender, RoutedEventArgs e)
    {
        try { UpdatePreferences.CheckAtStartup = AutoCheck.IsChecked == true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            AutoCheck.IsChecked = UpdatePreferences.CheckAtStartup;
            UpdateStatus.Text = L("UpdateSettingFailed", ex.Message);
        }
    }

    internal static bool IsUpdateError(Exception ex) =>
        ex is HttpRequestException or JsonException or InvalidDataException or IOException or
            UnauthorizedAccessException or OperationCanceledException or CryptographicException;
}
