# StartupPorts

[简体中文](README.md) · **English**

Manage which apps and scripts start when you sign in to Windows. See which processes own local ports and what launched them. Keep desktop apps and local development services in one startup list.

## Install and use

Download the Windows 11 x64 installer, `StartupPorts-*-win-x64-setup.exe`, from [GitHub Releases](https://github.com/Bigesila-B/startup-ports/releases). It installs for the current Windows account and includes the .NET runtime. The desktop interface is in Simplified Chinese.

1. In Startup Management (自启管理), select Add Startup Item (添加自启项) and choose an `.exe`, `.bat`, `.cmd`, or `.ps1` file. Set arguments, a working directory, and a sign-in delay as needed.
2. If the app listens on a port, enter the expected port and choose TCP or UDP. Save the entry and select Run Now (立即运行) to check whether the startup item owns that port. Select an item to see which ports it actually uses or which process owns the expected port.
3. In Port Monitor (端口监控), search by port, process name, or PID. Select a row to copy the address, open a supported TCP address in your browser, or prefill a startup item. If a script launched the process, you can use that script in place of the executable in the prefilled form.

Windows Task Scheduler runs enabled items the next time the current account signs in, even if the main window is closed. Disabling an item turns off its sign-in trigger. You can still test it with Run Now. PowerShell scripts follow the account's execution policy, and batch arguments follow CMD syntax.

The monitor covers IPv4 and IPv6 TCP listeners and UDP bindings. Available process details depend on the current account's permissions. StartupPorts identifies launch sources from verifiable parent processes or from its own launch records. Before ending a process, the app asks for confirmation and checks that it still owns the port.

The app installs to `%LOCALAPPDATA%\Programs\StartupPorts`. Updates keep your startup tasks and leave running apps open. Some port ownership details become available again the next time StartupPorts launches the item. Uninstalling removes tasks registered by StartupPorts and leaves the apps it launched running.

## Build from source

You need Windows 11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). To build the installer, also install [Inno Setup](https://jrsoftware.org/inno-setup/downloads).

```powershell
dotnet build StartupPorts.slnx
dotnet run --project src/StartupPorts
dotnet run --project tests/StartupPorts.Checks
./scripts/build-installer.ps1
```

In an interactive Windows session, you can also run the `--scheduler` checks. They register temporary tasks, try all four launch target types, verify child-process port ownership and process termination, and remove the test tasks.

```powershell
dotnet run --project tests/StartupPorts.Checks -- --scheduler './src/StartupPorts/bin/Debug/net10.0-windows/StartupPorts.exe'
```

## Credits and license

The port lookup, new-port notifications, and process launch-source features were inspired by [local-ops-windows](https://github.com/Bigesila-B/local-ops-windows). StartupPorts implements these features using native Windows APIs. The project is released under the [MIT License](LICENSE).
