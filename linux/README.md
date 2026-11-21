# StartupPorts for Linux

**简体中文** · [English](README.en.md)

Linux 版支持当前账户的桌面登录自启、自启分组、已有桌面启动项管理、本机端口监控及中英文切换。

## 启动

需要 Linux 桌面、Python 3.10 或更新版本，以及 Python 的 `venv` 模块。端口监控使用 iproute2 提供的 `ss` 命令。在 Debian 或 Ubuntu 上，缺少这些组件时可安装 `python3-venv` 和 `iproute2`。

在解压后的 `linux` 目录或源码仓库的 `linux` 目录中运行：

```sh
sh setup.sh
sh run.sh
```

首次设置会下载 Qt for Python，依赖保存在当前目录的 `.venv` 中。启用自启后，桌面登录会从此目录运行启动器，请保留解压目录的位置。

## 使用

- **启动程序**：添加具有执行权限的程序或脚本。可设置参数、工作目录和延迟。参数按命令行的引号规则输入，运行时直接传给程序。
- **自启分组**：选择已有自启项组成分组，可查看成员图标、立即运行整组或批量更改登录自启。组内的更改与「启动程序」共用状态。
- **桌面启动项**：管理 XDG 桌面启动项。停用系统默认项时，会写入当前账户的覆盖文件。受限项目会逐项显示原因。
- **端口监控**：显示 TCP 监听和 UDP 绑定，支持搜索、复制地址并打开本机 TCP 地址。进程信息取决于 Linux 给当前账户的访问权限。

自启在登录支持 XDG Autostart 的桌面时触发。系统服务和登录前任务由 Linux 自己管理。配置保存在 `$XDG_CONFIG_HOME/startupports`（默认 `~/.config/startupports`）。如果删除便携目录，可以移除 `~/.config/autostart/io.github.BigesilaB.StartupPorts.desktop` 中的登录启动器。自启分组和程序设置保存在独立的配置目录。
