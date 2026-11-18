using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using StartupPorts;

if (args is ["--listen", var port, var ready])
{
    await Listen(int.Parse(port), ready);
    return;
}
if (args is ["--mark", var marker, var markerValue])
{
    File.WriteAllText(marker, markerValue + "|" + Environment.CurrentDirectory);
    return;
}

CheckNativePorts();
CheckWindowsStartup();
CheckProcessCell();
if (args is ["--scheduler", var launcher])
    await CheckScheduler(Path.GetFullPath(launcher));
if (args is ["--installer", var installer])
    await CheckInstaller(Path.GetFullPath(installer));
Console.WriteLine("StartupPorts checks passed.");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void CheckNativePorts()
{
    using var tcp4 = new TcpListener(IPAddress.Loopback, 0);
    using var tcp6 = new TcpListener(IPAddress.IPv6Loopback, 0);
    using var udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var udp6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
    tcp4.Start();
    tcp6.Start();
    var endpoints = new[]
    {
        (Protocol.Tcp, (IPEndPoint)tcp4.LocalEndpoint),
        (Protocol.Tcp, (IPEndPoint)tcp6.LocalEndpoint),
        (Protocol.Udp, (IPEndPoint)udp4.Client.LocalEndPoint!),
        (Protocol.Udp, (IPEndPoint)udp6.Client.LocalEndPoint!)
    };

    var monitor = new PortMonitor();
    var snapshot = monitor.Capture(new Dictionary<Guid, RunInfo>());
    Require(snapshot.Issues.Count == 0, string.Join("; ", snapshot.Issues));
    foreach (var (protocol, endpoint) in endpoints)
    {
        var expected = new BoundEndpoint(protocol, endpoint.Address, endpoint.Port, Environment.ProcessId);
        var row = snapshot.Rows.Single(row => row.Endpoint == expected);
        Require(row.Created is not null && row.Memory > 0, "Process metadata missing.");
        Require(!row.CanEnd, "The app must protect its own process.");
        Require((row.Endpoint.BrowserAddress != null) == (protocol == Protocol.Tcp), "Invalid browser action.");
    }
    Thread.Sleep(100);
    var updated = monitor.Capture(new Dictionary<Guid, RunInfo>());
    Require(updated.Rows.Any(row => row.Pid == Environment.ProcessId && row.Cpu >= 0), "CPU sample missing.");

    var discovery = new PortDiscovery();
    var existing = snapshot.Rows.First(row => row.Pid == Environment.ProcessId);
    var replacement = existing with { Created = existing.Created + 1 };
    var alternateAddress = existing.Endpoint.Address.Equals(IPAddress.IPv6Loopback) ? IPAddress.Loopback : IPAddress.IPv6Loopback;
    var alternate = existing with { Endpoint = existing.Endpoint with { Address = alternateAddress } };
    var services = PortService.Group(new([existing, alternate], []), new Dictionary<Guid, string>(), new ProcessIcons());
    var service = services.Single();
    Require(service.Address.Contains(IPAddress.Loopback.ToString()) && service.Address.Contains(IPAddress.IPv6Loopback.ToString()),
        "Grouping lost one of the listen addresses.");
    Require(service.Matches(service.Port.ToString(), 0, 1) && !service.Matches("61000" + service.Port, 0, 0),
        "Port search or current-user filter is incorrect.");
    Require(discovery.Update(new([existing], [])).Count == 0, "Initial discovery must be silent.");
    Require(discovery.Update(new([existing, alternate], [])).Count == 0, "Adding a second address announced a duplicate port.");
    Require(discovery.Update(new([replacement], [])).Single() == replacement, "Reused PID was not rediscovered.");
    discovery.Dismiss();
    Require(discovery.Update(new([replacement], [])).Count == 0, "Dismissed discovery reappeared.");
    Require(discovery.Update(new([], ["Snapshot failed"])).Count == 0, "Partial table should reset discovery.");
    Require(discovery.Update(new([replacement], [])).Count == 0, "Recovery must be silent.");

    using var client = new TcpClient();
    client.Connect((IPEndPoint)tcp4.LocalEndpoint);
    using var accepted = tcp4.AcceptTcpClient();
    var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    var updatedRows = NativePorts.Read(Protocol.Tcp, AddressFamily.InterNetwork);
    Require(!updatedRows.Any(row => row.Port == clientPort), "TCP connection incorrectly reported as a listener.");
}

static void CheckWindowsStartup()
{
    Require(WindowsStartup.ApprovalState(BitConverter.GetBytes(2u)) == "已启用" &&
        WindowsStartup.ApprovalState(BitConverter.GetBytes(7u)) == "已停用" &&
        WindowsStartup.ApprovalState(BitConverter.GetBytes(255u)) == "状态未知" &&
        WindowsStartup.ApprovalState(null) == "已登记", "Startup approval states were interpreted incorrectly.");
    var snapshot = WindowsStartup.Read();
    Require(snapshot.Items.DistinctBy(item => item.Id).Count() == snapshot.Items.Count,
        "Windows startup list contained duplicate entries.");
    var icons = new StartupIcons();
    Require(icons.FromCommand("  \"%SystemRoot%\\explorer.exe\" --sample") is { IsFrozen: true },
        "Quoted Run commands or environment paths lost their icons.");
    Require(icons.FromCommand("some-unknown-host \"%SystemRoot%\\explorer.exe\"") == null,
        "An executable argument was incorrectly treated as the startup target.");

    var fixture = Directory.CreateTempSubdirectory("StartupPorts icons ");
    var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
    var executable = Path.Combine(fixture.FullName, "Test Explorer.exe");
    var shortcutPath = Path.Combine(fixture.FullName, "Test Explorer.lnk");
    try
    {
        File.Copy(explorer, executable);
        Require(icons.FromCommand(executable + " --sample") is { IsFrozen: true },
            "A legacy unquoted Run path lost its icon.");
        Require(icons.FromExecutable(executable) is { IsFrozen: true },
            "A scheduled action or direct startup file lost its icon.");

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        try
        {
            shortcut.TargetPath = executable;
            shortcut.Save();
            Require(icons.FromStartupFile(shortcutPath) is { IsFrozen: true },
                "A shortcut did not display its target icon.");

            shortcut.TargetPath = Path.Combine(fixture.FullName, "Missing.exe");
            shortcut.IconLocation = $"{explorer},0";
            shortcut.Save();
            Require(icons.FromStartupFile(shortcutPath) is { IsFrozen: true },
                "A shortcut did not display its explicit icon.");
        }
        finally
        {
            Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell);
        }
    }
    finally
    {
        File.Delete(shortcutPath);
        File.Delete(executable);
        fixture.Delete();
    }
    Console.WriteLine($"Read {snapshot.Items.Count} Windows startup items, {snapshot.Items.Count(item => item.Icon != null)} with program icons.");
}

static void CheckProcessCell()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/StartupPorts;component/Theme.xaml"),
            });
            UiText.ApplyResources(app);
            var window = new MainWindow();
            try
            {
                var grid = (DataGrid)window.FindName("PortGrid");
                var template = ((DataGridTemplateColumn)grid.Columns[0]).CellTemplate;
                var startupGrid = (DataGrid)window.FindName("WindowsStartupGrid");
                var startupTemplate = ((DataGridTemplateColumn)startupGrid.Columns[0]).CellTemplate;
                var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                var sample = new PortRow(new(Protocol.Tcp, IPAddress.Loopback, 3000, 1000), "explorer.exe",
                    explorer, null, null, null, null, false, false, [], false);
                var icons = new ProcessIcons();
                var names = new Dictionary<Guid, string>();
                var known = PortService.Group(new([sample], []), names, icons).Single();
                var unknown = PortService.Group(new([sample with { Process = "sample.exe", Path = null }], []), names, icons).Single();
                Require(known.Icon is { IsFrozen: true, PixelWidth: >= 32, PixelHeight: >= 32 } &&
                    known.Name != "explorer.exe" && known.ProcessSummary.Contains("explorer.exe"),
                    "Explorer's large application icon or friendly name could not be read.");
                Require(unknown.Icon == null && unknown.Name == "sample.exe", "The executable name fallback was lost.");

                var startup = new WindowsStartupItem("shortcut", "Explorer", "启动文件夹", "当前用户", "已启用", explorer, known.Icon);
                var rows = new (DataTemplate Template, object Item, BitmapSource? Icon)[]
                {
                    (template, known, known.Icon),
                    (template, unknown, null),
                    (startupTemplate, startup, startup.Icon),
                    (startupTemplate, startup with { Icon = null }, null),
                };
                foreach (var row in rows)
                {
                    var cell = new ContentPresenter { Content = row.Item, ContentTemplate = row.Template };
                    cell.ApplyTemplate();
                    cell.Measure(new Size(230, 58));
                    cell.Arrange(new Rect(0, 0, 230, 58));
                    cell.UpdateLayout();
                    var badge = (ContentPresenter)row.Template.FindName("ProgramBadge", cell);
                    var image = (Image)badge.ContentTemplate.FindName("ProcessImage", badge);
                    var fallback = (FrameworkElement)badge.ContentTemplate.FindName("ProcessFallback", badge);
                    if (row.Icon != null)
                        Require(image.Visibility == Visibility.Visible && image.Source == row.Icon &&
                            image.ActualWidth == 32 && fallback.Visibility == Visibility.Collapsed,
                            "The port table hid or shrank the application icon.");
                    else
                        Require(image.Visibility == Visibility.Collapsed && fallback.Visibility == Visibility.Visible &&
                            fallback.ActualWidth > 0, "The port table hid the generic process icon.");

                    var rendered = new RenderTargetBitmap(230, 58, 96, 96, PixelFormats.Pbgra32);
                    rendered.Render(cell);
                    var pixels = new byte[230 * 58 * 4];
                    rendered.CopyPixels(pixels, 230 * 4, 0);
                    var iconPixels = Enumerable.Range(8, 38).Sum(y => Enumerable.Range(3, 32).Count(x =>
                    {
                        var index = (y * 230 + x) * 4;
                        return pixels[index + 3] > 0 && Math.Min(pixels[index], Math.Min(pixels[index + 1], pixels[index + 2])) < 180;
                    }));
                    Require(iconPixels > 20, "The rendered process cell has no visible icon inside its badge.");
                }
                Console.WriteLine("Rendered Windows startup and port rows show application icons and the generic fallback.");

                Require(UiText.ResolveLanguage("en-US", CultureInfo.GetCultureInfo("zh-CN")) == "en-US" &&
                    UiText.ResolveLanguage("zh-CN", CultureInfo.GetCultureInfo("en-US")) == "zh-CN" &&
                    UiText.ResolveLanguage(null, CultureInfo.GetCultureInfo("zh-CN")) == "zh-CN",
                    "The selected language did not override the Windows default.");

                var refresh = (Button)window.FindName("RefreshButton");
                var language = (ComboBox)window.FindName("LanguageSelector");
                var header = startupGrid.Columns[0];
                foreach (var (code, refreshText, name, entryTitle, enabled) in new[]
                {
                    ("en-US", "Refresh", "Name", "Add startup item", "Enabled"),
                    ("zh-CN", "刷新", "名称", "添加自启项", "已启用"),
                })
                {
                    window.ApplyLanguage(code);
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    Require((string)refresh.Content == refreshText, $"Refresh text did not update for {code}.");
                    Require((string)language.SelectedValue == code, $"The language selector did not select {code}.");
                    Require((string)header.Header == name, $"The startup column header did not update for {code}.");
                    var editor = new EntryDialog(null, null);
                    try { Require(editor.Title == entryTitle, "The startup editor did not use the selected language."); }
                    finally { editor.Close(); }
                    Require(WindowsStartup.ApprovalState(BitConverter.GetBytes(2u)) == enabled,
                        "Startup status did not use the selected language.");
                }
                Console.WriteLine("Verified live English and Simplified Chinese switching in the main window and startup editor.");
            }
            finally { window.Close(); }
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure != null)
        ExceptionDispatchInfo.Capture(failure).Throw();
}

static async Task CheckScheduler(string launcher)
{
    var store = new TaskStore(launcher);
    var directory = Directory.CreateTempSubdirectory("StartupPorts checks ");
    var root = Directory.CreateDirectory(Path.Combine(directory.FullName, "测试 空格 & %"));
    var fixtureDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "fixture"));
    var fixtureExecutable = CopyFixture(fixtureDirectory.FullName);
    var ready = Path.Combine(root.FullName, "ready.txt");
    var ids = new List<Guid>();
    using var shell = new TcpListener(IPAddress.Loopback, 0);
    shell.Start();
    var port = ((IPEndPoint)shell.LocalEndpoint).Port;

    StartupEntry Entry(string target, string arguments, int? expectedPort = null)
    {
        var id = Guid.NewGuid();
        ids.Add(id);
        return new(id, "测试 项 " + Path.GetExtension(target), target, arguments, root.FullName, 5,
            expectedPort, Protocol.Tcp, false);
    }

    try
    {
        foreach (var suffix in new[] { ".exe", ".bat", ".cmd", ".ps1" })
        {
            var path = Path.Combine(root.FullName, "启动" + suffix);
            var marker = Path.Combine(root.FullName, suffix[1..] + ".txt");
            var arguments = $"\"{marker}\" \"test value\"";
            if (suffix == ".exe")
            {
                path = Environment.ProcessPath!;
                arguments = "--mark " + arguments;
            }
            else if (suffix == ".ps1")
                File.WriteAllText(path, "param($marker, $value)\n[IO.File]::WriteAllText($marker, ($value + '|' + (Get-Location).Path))\n");
            else
            {
                // CMD reads batch contents using the console code page. Pass Unicode paths as arguments.
                File.WriteAllText(path, "@echo off\r\n\"%~3\" --mark \"%~1\" \"%~2\"\r\n");
                arguments += $" \"{Environment.ProcessPath}\"";
            }

            var entry = Entry(path, arguments);
            store.Save(entry, create: true);
            Require(store.Get(entry.Id).Entry == entry, "Task data did not round-trip.");
            Require(store.List().Entries.Any(item => item.Entry == entry), "Task did not appear in list.");
            store.SetEnabled(entry.Id, true);
            Require(store.Get(entry.Id).Entry.Enabled, "Could not enable task.");
            dynamic definition = TaskDefinition(entry.Id);
            string triggerUser = definition.Triggers[1].UserId;
            var triggerSid = triggerUser.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? triggerUser : new NTAccount(triggerUser).Translate(typeof(SecurityIdentifier)).Value;
            string duration = definition.Triggers[1].Delay;
            int level = definition.Principal.RunLevel;
            int logonType = definition.Principal.LogonType;
            Require(XmlConvert.ToTimeSpan(duration) == TimeSpan.FromSeconds(5) &&
                triggerSid == AppIdentity.Sid && level == 0 && logonType == 3,
                $"Wrong Windows task trigger or principal (delay {duration}, level {level}, logon type {logonType}).");
            store.SetEnabled(entry.Id, false);
            Require(!store.Get(entry.Id).Entry.Enabled, "Could not disable task.");
            store.Save(entry with { Name = "Edited " + suffix }, create: false);
            Require(store.Get(entry.Id).Entry.Name == "Edited " + suffix, "Cannot edit task.");

            if (suffix == ".ps1")
            {
                // Scope the fixture policy to this child environment. The launcher inherits it unchanged.
                var launch = new ProcessStartInfo(launcher, $"--run {entry.Id:N}");
                launch.Environment["PSExecutionPolicyPreference"] = "RemoteSigned";
                using var process = Process.Start(launch)!;
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            else
                store.Run(entry.Id);
            await Eventually(() => RunState.Read(entry.Id) is { Finished: true }, "Launch did not finish.");
            var run = RunState.Read(entry.Id)!;
            Require(run.ExitCode == 0, $"Launch {suffix} failed: {run.Error}, exit {run.ExitCode}");
            Require(File.ReadAllText(marker) == "test value|" + root.FullName, $"{suffix} arguments or working directory changed.");
        }

        var wrapper = Path.Combine(root.FullName, "子进程.cmd");
        // Start the listener and exit the script. The child must remain associated with its startup item.
        File.WriteAllText(wrapper, $"@echo off\r\nstart \"\" /b \"%~2\" --listen {port} \"%~1\"\r\n");
        var listener = Entry(wrapper, $"\"{ready}\" \"{fixtureExecutable}\"", port);
        store.Save(listener, create: true);

        var monitor = new PortMonitor();
        var unrelated = monitor.Capture(new Dictionary<Guid, RunInfo>());
        var prior = new StartupRow(store.Get(listener.Id), null, unrelated, RunTracking.None);
        Require(prior.Port.Contains("被其他进程占用"), "Unrelated listener was not reported as external.");
        shell.Stop();
        store.Run(listener.Id);
        await Eventually(() => File.Exists(ready) && RunState.Read(listener.Id)?.JobName != null,
            "Child process or its job did not appear.");

        var child = int.Parse(File.ReadAllText(ready));
        var trackedRun = RunState.Read(listener.Id)!;
        var jobs = new Dictionary<Guid, RunInfo> { [listener.Id] = trackedRun };
        var observed = monitor.Capture(jobs);
        var owned = observed.Rows.Where(row => row.Pid == child && row.Port == port).ToArray();
        Require(owned.Length == 4 && owned.All(row => row.ManagedBy.Contains(listener.Id)),
            "Child listeners were not associated with the startup item.");
        Require(owned.All(row => row.Origin == "StartupPorts" && row.CanEnd && row.CanAdd),
            "Port actions or origin were incorrect.");
        try
        {
            PortMonitor.End(owned[0] with { Created = owned[0].Created - 1 });
            throw new Exception("Reused PID guard failed.");
        }
        catch (InvalidOperationException) { }

        store.Delete(listener.Id);
        Require(NativePorts.Read(Protocol.Tcp, AddressFamily.InterNetwork).Any(row => row.ProcessId == child && row.Port == port),
            "Deleting a task stopped its running target.");
        PortMonitor.End(owned[0]);
        try
        {
            PortMonitor.End(owned[0]);
            throw new Exception("Exited process guard failed.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        Console.WriteLine("Windows task registration, each target type, job tracking, and process termination passed.");
    }
    finally
    {
        File.WriteAllText(ready + ".stop", "");
        if (File.Exists(ready) && int.TryParse(File.ReadAllText(ready), out var fixturePid))
        {
            try
            {
                using var fixture = Process.GetProcessById(fixturePid);
                await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is ArgumentException or TimeoutException) { }
        }
        foreach (var id in ids)
        {
            try { store.Delete(id); } catch (InvalidOperationException) { }
            RunState.TryDelete(id);
        }
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                directory.Delete(recursive: true);
                break;
            }
            catch (IOException) when (attempt < 14) { await Task.Delay(200); }
        }
    }
}

static object TaskDefinition(Guid id)
{
    dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
    scheduler.Connect();
    return scheduler.GetFolder(@"\").GetTask($"StartupPorts-{AppIdentity.Sid}-{id:N}").Definition;
}

static async Task CheckInstaller(string installer)
{
    var installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "StartupPorts");
    var launcher = Path.Combine(installDirectory, "StartupPorts.exe");
    var uninstaller = Path.Combine(installDirectory, "unins000.exe");
    var store = new TaskStore(launcher);
    Require(!Directory.Exists(installDirectory) && store.List().Entries.Count == 0,
        "Installer checks require an account with no existing StartupPorts install or tasks.");

    var temp = Directory.CreateTempSubdirectory("StartupPorts installer check ");
    var ready = Path.Combine(temp.FullName, "ready.txt");
    var id = Guid.NewGuid();
    Process? fixture = null;
    var setupFlags = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
    try
    {
        await ExecuteInstaller(installer, setupFlags);
        Require(File.Exists(launcher) && File.Exists(uninstaller), "Setup did not install to the per-user location.");
        var target = CopyFixture(temp.FullName);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var entry = new StartupEntry(id, "Installer test fixture", target, $"--listen {port} \"{ready}\"",
            temp.FullName, 6, port, Protocol.Tcp, false);
        store.Save(entry, create: true);
        store.Run(id);
        await Eventually(() => File.Exists(ready) && int.TryParse(File.ReadAllText(ready), out _), "Installed launcher failed.");
        fixture = Process.GetProcessById(int.Parse(File.ReadAllText(ready)));

        await ExecuteInstaller(installer, setupFlags);
        Require(store.Get(id).Entry == entry && !fixture.HasExited,
            "Updating changed the saved task or stopped its target.");
        await ExecuteInstaller(uninstaller, setupFlags);
        await Eventually(() => store.List().Entries.All(task => task.Entry.Id != id) &&
            !File.Exists(launcher) && !File.Exists(uninstaller),
            "Uninstall left a registered task or application binary.");
        Require(!fixture.HasExited, "Uninstall stopped the target.");
        Console.WriteLine("Per-user install, upgrade, and uninstall passed with a running target.");
    }
    finally
    {
        File.WriteAllText(ready + ".stop", "");
        if (fixture != null)
        {
            using (fixture)
                await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        if (File.Exists(uninstaller))
            await ExecuteInstaller(uninstaller, setupFlags);
        temp.Delete(recursive: true);
    }
}

static async Task ExecuteInstaller(string executable, string arguments)
{
    using var child = Process.Start(new ProcessStartInfo(executable, arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
    })!;
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
    Require(child.ExitCode == 0, $"Setup exited with code {child.ExitCode}.");
}

static async Task Eventually(Func<bool> predicate, string message)
{
    var timeout = Stopwatch.StartNew();
    while (timeout.Elapsed < TimeSpan.FromSeconds(15))
    {
        if (predicate())
            return;
        await Task.Delay(150);
    }
    throw new InvalidOperationException(message);
}

static async Task Listen(int port, string ready)
{
    using var tcp4 = new TcpListener(IPAddress.Loopback, port);
    using var tcp6 = new TcpListener(IPAddress.IPv6Loopback, port);
    using var udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
    using var udp6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, port));
    tcp4.Start();
    tcp6.Start();
    File.WriteAllText(ready, Environment.ProcessId.ToString());
    var timeout = Stopwatch.StartNew();
    while (timeout.Elapsed < TimeSpan.FromMinutes(2) && !File.Exists(ready + ".stop"))
        await Task.Delay(100);
}

static string CopyFixture(string destination)
{
    foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    return Path.Combine(destination, Path.GetFileName(Environment.ProcessPath!));
}
