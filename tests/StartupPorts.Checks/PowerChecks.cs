using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StartupPorts;

internal static class PowerChecks
{
    public static int MarkDueAction(string name, string marker, string id)
    {
        if (!Guid.TryParseExact(id, "N", out var timerId) ||
            new PowerTaskStore(name, Environment.ProcessPath!, _ => "").GetDueAction(timerId) is not { } action)
            return 3;
        File.WriteAllText(marker, action.ToString());
        return 0;
    }

    public static async Task CheckSchedulerAsync(string launcher)
    {
        var name = "StartupPorts-Test-Power-" + Guid.NewGuid().ToString("N");
        var directory = Directory.CreateTempSubdirectory("StartupPorts Power ");
        var marker = Path.Combine(directory.FullName, "fired.txt");
        var store = new PowerTaskStore(name, Environment.ProcessPath!, task => $"--mark-due-power {name} \"{marker}\" {task.Id:N}");
        try
        {
            Require(store.Read() == null, "The timer fixture already exists.");
            ExpectRejected(() => store.Schedule(PowerAction.Shutdown, DateTimeOffset.UtcNow.AddSeconds(-5)));
            store.Schedule(PowerAction.Hibernate, DateTimeOffset.UtcNow.AddMinutes(2));
            var pending = store.Read();
            Require(pending is { State: PowerState.Waiting, Timer.Action: PowerAction.Hibernate },
                $"The pending timer did not survive a fresh Task Scheduler read. Current status: {pending}.");
            Require(store.GetDueAction(pending!.Timer.Id) == null, "The timer admitted an early manual invocation.");
            ExpectRejected(() => store.Cancel(Guid.NewGuid()));
            ExpectRejected(() => store.Schedule(PowerAction.Sleep, DateTimeOffset.UtcNow.AddMinutes(3)));
            Require(store.Read()?.Timer.Id == pending.Timer.Id, "A stale cancellation or competing schedule replaced the timer.");
            store.Cancel(pending.Timer.Id);
            Require(store.Read() == null && !File.Exists(marker), "Cancellation left a pending task or executed its action.");

            store.Schedule(PowerAction.Restart, DateTimeOffset.UtcNow.AddSeconds(7));
            var limit = Stopwatch.StartNew();
            while (limit.Elapsed < TimeSpan.FromSeconds(30) && store.Read()?.State is PowerState.Waiting or PowerState.Running)
                await Task.Delay(250);
            var completed = store.Read();
            Require(completed is { State: PowerState.Completed, Result: 0 } && File.Exists(marker) &&
                File.ReadAllText(marker) == nameof(PowerAction.Restart),
                $"The real timer did not dispatch its harmless marker. Current status: {completed}.");
            ExpectRejected(() => store.Cancel(completed!.Timer.Id));
            store.Schedule(PowerAction.Sleep, DateTimeOffset.UtcNow.AddMinutes(2));
            Require(store.Read() is { State: PowerState.Waiting, Timer.Action: PowerAction.Sleep }, "A completed timer blocked a replacement.");
            store.DeleteOnUninstall();
            Require(store.Read() == null, "Uninstall cleanup left a pending power timer.");

            using var direct = Process.Start(new ProcessStartInfo(launcher, $"--run-power {Guid.NewGuid():N}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            await direct.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Require(direct.ExitCode == 2, "The app accepted a power invocation with no matching timer.");
            var options = WindowsPower.Available();
            Console.WriteLine($"Verified Windows timed action, cancellation, ownership, replacement, and cleanup. Sleep available: {options.Sleep}. Hibernate available: {options.Hibernate}.");
        }
        finally
        {
            store.DeleteOnUninstall();
            File.Delete(marker);
            directory.Delete();
        }
    }

    public static void CheckUi(MainWindow window)
    {
        var nav = (RadioButton)window.FindName("PowerNav");
        var panel = (PowerPage)window.FindName("PowerPanel");
        var title = (TextBlock)window.FindName("PageTitle");
        var actions = (ComboBox)panel.FindName("Actions");
        var schedule = (Button)panel.FindName("ScheduleButton");
        var cancel = (Button)panel.FindName("CancelButton");
        var formHint = (TextBlock)panel.FindName("FormHint");
        var details = (TextBlock)panel.FindName("TimerDetails");
        nav.IsChecked = true;
        Require(panel.Visibility == Visibility.Visible && actions.Items.Count == 4, "The tools menu did not open the power page.");

        foreach (var language in new[] { "en-US", "zh-CN" })
        {
            window.ApplyLanguage(language);
            Require(title.Text == UiText.L("TimedShutdown"), "The power page title did not switch languages.");
            panel.SetStatus(null, new PowerOptions(true, false));
            actions.SelectedIndex = (int)PowerAction.Hibernate;
            Require(!schedule.IsEnabled && formHint.Text == UiText.L("PowerUnsupported"), "Unsupported hibernation was offered.");
            actions.SelectedIndex = (int)PowerAction.Restart;
            Require(schedule.IsEnabled && cancel.Visibility == Visibility.Collapsed, "An available restart could not be scheduled.");
            var timeBox = (TextBox)panel.FindName("DueTime");
            var originalTime = timeBox.Text;
            timeBox.Text = "27:61";
            Require(!schedule.IsEnabled && formHint.Text == UiText.L("PowerTimeFormat"), "An invalid time could be scheduled.");
            timeBox.Text = originalTime;
            panel.SetStatus(new PowerStatus(new PowerTimer(Guid.NewGuid(), PowerAction.Restart, DateTimeOffset.Now.AddMinutes(28)),
                PowerState.Waiting, 0), new PowerOptions(true, false));
            panel.Render();
            Require(!schedule.IsEnabled && cancel.IsEnabled && details.Text.Contains(UiText.L("PowerCountdown", "")),
                "A pending timer lacked its countdown or cancellation button.");
        }

        if (Environment.GetEnvironmentVariable("STARTUPPORTS_WINDOWS_POWER_PREVIEW") is { Length: > 0 } output)
        {
            var content = (Grid)window.Content;
            content.Background = window.Background;
            content.Measure(new Size(1160, 680));
            content.Arrange(new Rect(0, 0, 1160, 680));
            content.UpdateLayout();
            var image = new RenderTargetBitmap(1160, 680, 96, 96, PixelFormats.Pbgra32);
            image.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(output);
            encoder.Save(file);
        }
        ((RadioButton)window.FindName("StartupNav")).IsChecked = true;
        Console.WriteLine("Verified the bilingual timed shutdown page, four action choices, invalid times, and pending timer controls.");
    }

    private static void ExpectRejected(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("The power timer accepted an invalid operation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
