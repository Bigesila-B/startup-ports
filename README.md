# StartupPorts

**简体中文** · [English](README.en.md)

在 Windows 上设置登录自启，同时查看本机的端口占用、监听进程和启动来源。适合把桌面程序和本地开发服务放进同一张自启列表。

## 安装与使用

从 [GitHub Releases](https://github.com/Bigesila-B/startup-ports/releases) 下载 Windows 11 x64 安装包 `StartupPorts-*-win-x64-setup.exe`。安装仅针对当前 Windows 账户，已包括 .NET 运行时。桌面界面为简体中文。

1. 在「自启管理」点击「添加自启项」，选择 `.exe`、`.bat`、`.cmd` 或 `.ps1`。按需填写参数、工作目录和登录后延迟。
2. 如果程序会监听端口，填写预期端口和 TCP 或 UDP 协议。保存后可点击「立即运行」，在任务行查看预期端口是否由该项监听。选择任务行可查看实际端口或占用进程。
3. 在「端口监控」搜索端口、进程或 PID。选中一行可以复制地址、对支持的 TCP 地址打开浏览器，或者预填一条自启配置。若端点由脚本启动，可把预填的可执行文件改为原来的脚本。

自启由 Windows 任务计划程序执行。主窗口关闭后，启用的条目仍在当前账户下一次登录时运行。停用一项只关闭登录触发器，「立即运行」仍可用于试跑。PowerShell 脚本遵循当前账户的执行策略，批处理参数遵循 CMD 语法。

端口查看覆盖 IPv4、IPv6、TCP 监听与 UDP 绑定。进程详情取决于当前账户的读取权限。启动来源来自可验证的父进程链或本工具记录的进程归属。结束进程时，应用会要求确认并复核当前占用者。

安装路径为 `%LOCALAPPDATA%\Programs\StartupPorts`。升级保留自启任务，活动程序继续运行，部分实时归属会在下次受管启动后恢复。卸载会移除本工具登记的自启任务，已启动的目标程序继续运行。

## 从源码构建

需要 Windows 11 x64 和 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。生成安装包另需 [Inno Setup](https://jrsoftware.org/inno-setup/downloads)。

```powershell
dotnet build StartupPorts.slnx
dotnet run --project src/StartupPorts
dotnet run --project tests/StartupPorts.Checks
./scripts/build-installer.ps1
```

已登录的交互式 Windows 会话可增加 `--scheduler` 检查。它登记临时任务，试跑四种启动目标，检查子进程端口归属和结束操作，随后清理测试任务。

```powershell
dotnet run --project tests/StartupPorts.Checks -- --scheduler './src/StartupPorts/bin/Debug/net10.0-windows/StartupPorts.exe'
```

## 来源与许可证

端口列表、新增端口提示和启动来源的设计参考了 [local-ops](https://github.com/laogou717/local-ops)。StartupPorts 使用 Windows 原生接口实现这些功能。项目采用 [MIT 许可证](LICENSE)。
