using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
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
    Require(discovery.Update(new([existing], [])).Count == 0, "Initial discovery must be silent.");
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
        await Eventually(() => store.List().Entries.All(task => task.Entry.Id != id) && !File.Exists(launcher),
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
    while (timeout.Elapsed < TimeSpan.FromSeconds(25) && !File.Exists(ready + ".stop"))
        await Task.Delay(100);
}

static string CopyFixture(string destination)
{
    foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    return Path.Combine(destination, Path.GetFileName(Environment.ProcessPath!));
}
