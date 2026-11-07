# Jay 功能与平台适配说明

写给要把 Jay 适配到 macOS、Linux（以及以后别的平台）的合作者。文档按 main `89565b5`（2026-11-07）整理，覆盖 v0.7.0 正式版，以及之后合进 main、还没发布的功能（远程控制、Jay 服务器第二到四期、被踢提示修复）。

**目标：Mac 和 Linux 版尽量做到和 Windows 版功能完全一致**，包括现在只有 Windows 有的完整局域网、全部流量出口、远程控制和 Native AOT 包。做不到完全一样的地方（受系统限制），要在界面上说清楚，并给出最接近的替代。

每个功能都写了三件事：它做什么、怎么实现、在各平台上的现状。第 5 节是逐项补齐的路线和验收标准。

## 0. 怎么读

状态标记：

| 标记 | 意思 |
|---|---|
| ✅ | 三个平台用的是同一份代码，已经能用 |
| 🟡 | 代码跨平台，但没在真机上验证过，或者某个细节在 Mac/Linux 上降级 |
| 🪟 | 只有 Windows 实现，Mac/Linux 上不可用或者直接报错 |
| 【实验】 | 和发布说明一致：做完并通过自动测试，没在真实环境完整验证 |
| 【未发布】 | 在 main 上，v0.7.0 里没有 |

真机验证情况：Windows 10/11 x64 是主力平台，所有功能都在上面测过。Apple 芯片 Mac 由你测过安装、启动、游玩和保存退出。Intel Mac、Linux x64、Linux ARM64 只有 CI 构建，没有真机记录。Mac 和 Linux 上的联机目前也没有实测记录。

## 1. 项目总览

### 1.1 技术栈

- C#，.NET 10，Avalonia 12.1.4，MVVM 用 CommunityToolkit.Mvvm。
- 加密：.NET 自带的 ChaCha20-Poly1305、AES-GCM、SHA-256、HKDF、ML-KEM；X25519 用 BouncyCastle 的 RFC 7748 实现（`BouncyCastle.Cryptography` 2.7.0），因为 .NET 没有跨平台的 X25519。
- Windows 凭据加密用 `System.Security.Cryptography.ProtectedData`（DPAPI）。
- 界面、注释和文档都用简体中文，界面文字另有繁中、英文、日文（`src/Amber.Core/Localization/*.json`，嵌进程序集）。

### 1.2 代码结构

| 项目 | 内容 |
|---|---|
| `src/Amber.Core` | 所有非界面逻辑。规则：不能依赖 Avalonia；平台相关代码要放在 `OperatingSystem.IsWindows()` 判断后面，或者放进只在 Windows 用的类型 |
| `src/Amber.App` | Avalonia 桌面程序（程序集名 `Jay`）。`ViewModels/` 每页一个，`Views/` 是 XAML，`Services/LauncherService.cs` 把 Core 的服务接起来，`Views/SelfTest*.cs` 是端到端界面自检 |
| `src/Amber.Cli` | 开发用命令行，复用 Core（测量、脚本化测试） |
| `src/Amber.Server` | Jay 服务器版 `jay-server`（命令行，网关 + MC 服务端管理 + 节点） |
| `src/Amber.Core.Tests` | xUnit，约 1000 个测试 |
| `node/` | EasyTier 联机节点的一键部署脚本（Linux、Docker、Windows） |
| `server/` | `jay-server` 的说明和 systemd 单元 |
| `tools/`、`scripts/` | 自检脚本（PowerShell）、macOS 打包脚本 |

### 1.3 数据放在哪

| 内容 | 位置 |
|---|---|
| 启动器数据目录 | `SpecialFolder.ApplicationData/Amber`：Windows `%APPDATA%\Amber`，macOS `~/Library/Application Support/Amber`，Linux `~/.config/Amber`。环境变量 `AMBER_DATA_DIR` 可以改（测试必须用它） |
| 设置 | `<数据目录>/settings.json` |
| 账号 | `<数据目录>/accounts.json`，非 Windows 另有 `accounts.json.key` |
| 联机身份 | `<数据目录>/identity.key`，非 Windows 另有 `identity.wrap` |
| 信任的成员 | `<数据目录>/trusted.json` |
| 加入过的 Jay 服务器 | `<数据目录>/jay-servers.json` |
| EasyTier | `<数据目录>/easytier/2.6.4/<平台>/`，运行目录 `easytier/run`、`run-lan`、`lobby`、`lobby-join` |
| 默认游戏目录 | Windows `%APPDATA%\.minecraft`，macOS `~/Library/Application Support/minecraft`，Linux `~/.minecraft`（`Game/GameDirectory.cs`） |

注意：数据目录名还是 `Amber`（改名 Jay 时为了兼容没动），环境变量也还是 `AMBER_*`。

## 2. 平台适配总表

| 功能 | Windows | macOS | Linux | 平台相关代码 |
|---|---|---|---|---|
| 版本、加载器安装 | ✅ | ✅ | ✅ | `Game/RuleEvaluator.cs`（os 名和架构） |
| Java 扫描 | ✅ | ✅ | 🟡 | `Java/JavaFinder.cs` |
| Java 自动下载（Mojang 运行时） | ✅ | ✅ | 🟡 x64 有；ARM64 Mojang 不提供 | `Java/JavaRuntimeInstaller.cs` |
| 启动游戏 | ✅ | ✅ | 🟡 | `Launch/GameLauncher.cs` |
| 非 ASCII 路径（中文路径）兜底 | ✅ 目录联接 | 不需要 | 不需要 | `Launch/AsciiPaths.cs`、`Launch/WindowsJunction.cs` |
| 1.6 以前老版本的家目录重定向 | ✅ | 🪟 没做，会写进真实 `minecraft` 目录 | 🪟 同左 | `Launch/AsciiPaths.cs` `LegacyHome` |
| 正常关闭游戏（让游戏存档） | ✅ `CloseMainWindow` | ✅ `NSRunningApplication.terminate` | 🪟 `CloseMainWindow` 在 Linux 上无效 | `Launch/GameLauncher.cs`、`Launch/MacGameProcess.cs` |
| 可用内存检测（自动内存） | ✅ `GlobalMemoryStatusEx` | 🟡 用 GC 信息估算 | 🟡 同左 | `Launch/InstanceSettings.cs` |
| 启动前后命令、导出启动脚本 | ✅ cmd / .bat | ✅ /bin/sh / .sh | ✅ | `ViewModels/HomeViewModel.cs`、`Launch/Toolbox.cs` |
| 账号令牌加密 | ✅ DPAPI | 🟡 本机密钥文件（0600） | 🟡 同左 | `Auth/Accounts.cs` |
| 模组、整合包、资源 | ✅ | ✅ | ✅ | 大小写敏感比较 |
| 回收站（可撤销删除） | ✅ 隐藏目录 | ✅ 目录不隐藏 | ✅ | `Content/TrashService.cs` |
| 下载器 | ✅ | ✅ 打开了 Network.framework TLS | ✅ | `src/Directory.Build.targets` |
| 联机：EasyTier 下载和运行 | ✅ | 🟡 | 🟡 | `Multiplayer/EasyTier.cs` |
| 联机：EasyTier 进程随 Jay 退出 | ✅ 作业对象 | 🪟 Jay 崩溃会留下孤儿进程 | 🪟 同左 | `Multiplayer/ProcessJob.cs` |
| 联机：邀请、敲门、成员钥匙、踢人【实验】 | ✅ | 🟡 | 🟡 | 纯托管代码 |
| 联机：封装模式（MC 专用、高加密） | ✅ | 🟡 | 🟡 | `Multiplayer/Transport/` |
| 联机：极端加密（ML-KEM 混合） | ✅ Win11 新版 | 🪟 .NET 在 macOS 上没有 ML-KEM | 🟡 需要 OpenSSL 3.5+ | `Multiplayer/Transport/Noise.cs` `HybridSupported` |
| 联机：直连房间（IPv6、UPnP/NAT-PMP、打洞） | ✅ | 🟡 | 🟡 | `Multiplayer/DirectLink.cs`、`PortMapping.cs` |
| 联机：房间聊天 | ✅ | 🟡 | 🟡 | `Multiplayer/RoomChat.cs` |
| 联机：完整局域网模式（虚拟网卡） | ✅ 要管理员 | 🪟 | 🪟 | `Multiplayer/EasyTierElevated.cs` |
| 联机：出口节点（全部流量经房主） | ✅ 要管理员 | 🪟 | 🪟 | `Exit*Script.cs`（PowerShell + WFP） |
| 联机：“仅游戏”出口（SOCKS5） | ✅ | 🟡 少一条路由拦截、不查 SRV | 🟡 同左 | `Transport/ExitPolicy.cs`、`Transport/JoinTarget.cs` |
| 联机身份（X25519 私钥）加密 | ✅ DPAPI | 🟡 AES-GCM + 本机密钥文件 | 🟡 同左 | `Multiplayer/MemberIdentity.cs` |
| 敏感文字复制后 60 秒清剪贴板 | ✅ | ✅ | 🟡 Wayland 下要测 | `App/Services/SensitiveClipboard.cs` |
| 远程控制【实验】【未发布】 | ✅ | 🪟 界面提示“只支持 Windows” | 🪟 同左 | `RemoteControl/Windows/WindowsRemote.cs` |
| Jay 服务器（客户端加入）【实验】 | ✅ DPAPI 存访问密钥 | 🟡 访问密钥明文存（0600） | 🟡 同左 | `Multiplayer/Gate/JayServers.cs` |
| `jay-server` 服务端【实验】 | ✅ | ✅ | ✅（systemd） | `src/Amber.Server` |
| 模组烘焙【实验，默认关】 | ✅ | 🟡 | 🟡 | `Bake/BakeService.cs` |
| 发布包：单文件 + ReadyToRun | ✅ | ✅ | ✅ | `.github/workflows/build.yml` |
| 发布包：Native AOT | ✅ | 🪟 CI 没出 | 🪟 CI 没出 | 同上 |
| 代码签名 | 无 | 只有 ad-hoc（`scripts/publish-macos.sh`） | 无 | |

## 3. 功能和实现

### 3.1 版本、加载器安装 ✅

- 原版：读 Mojang 版本清单，下载客户端 jar、库、资源索引和资源（`Install/VanillaInstaller.cs`）。
- 加载器：Fabric、Quilt（`FabricLikeInstaller.cs`，直接拿官方 meta 生成版本 JSON），Forge 新版和 NeoForge（`ForgeInstaller.cs`，跑官方 installer 的 processors），1.5.2 及更早的老 Forge（`LegacyForge.cs`，提前校验 FML 库放进实例），OptiFine（`OptiFineInstaller.cs`，只经 BMCLAPI）。
- 库的平台筛选按 Mojang 版本 JSON 的 `rules`：`Game/RuleEvaluator.cs` 把当前系统映射成 `windows` / `osx` / `linux`，架构映射成 `x86` / `x86_64` / `arm64`。新平台只要在这里加映射。
- 版本隔离：每个实例可以有独立的存档、模组、配置目录（`Install/InstanceInstallDirectory.cs`）。

### 3.2 Java 发现和自动下载

`Java/JavaFinder.cs` 扫描这些地方：

- `JAVA_HOME`、`PATH` 里的 `java` / `java.exe`；
- Windows：`Program Files` 下常见厂商目录（Java、Adoptium、Microsoft、Zulu、BellSoft、Corretto、Semeru 等）和官方启动器的运行时目录；
- 非 Windows：`/usr/lib/jvm`、`/Library/Java/JavaVirtualMachines`；macOS 另加 `~/Library/Java/JavaVirtualMachines`，并在 Mojang 运行时下额外找 `jre.bundle/Contents/Home`；
- 所有平台：`~/.jdks`、`<游戏目录>/runtime`。

版本优先读 `release` 文件，读不到才起进程跑 `java -version`。

Linux 可以补的：SDKMAN（`~/.sdkman/candidates/java`）、`/usr/lib64/jvm`、`/opt` 下的 JDK、Flatpak/Snap 装的 JDK。macOS 可以补 `/usr/libexec/java_home -V` 的结果和 Homebrew 的 `/opt/homebrew/opt/openjdk*`。

自动下载在 `Java/JavaRuntimeInstaller.cs`：用 Mojang 的运行时清单，平台键是 `windows-x64` / `windows-arm64` / `windows-x86` / `mac-os` / `mac-os-arm64` / `linux` / `linux-i386` / `linux-arm64`。Mojang 目前不发 Linux ARM64 的运行时，所以 Linux ARM64 要用户自己装 Java。下载完给 `executable` 标记的文件加执行权限。清单里 `type` 为 `link` 的条目（符号链接）目前没处理，只处理了 `directory` 和 `file`，如果 Mac 上遇到运行时缺库可以先查这里。

### 3.3 启动游戏

主流程在 `Launch/GameLauncher.cs`：拼 JVM 和游戏参数（`${classpath_separator}` 用 `Path.PathSeparator`，`-Djava.library.path` 指向 natives 目录），把 natives（`.dll` / `.so` / `.dylib`）平铺解压到一个目录，准备旧版资源，然后起 Java。

平台相关的几处：

- **中文路径兜底（只 Windows 需要）**：Windows 上 Java 按 ANSI 代码页读路径，系统语言是英文时中文路径会变成问号。`AsciiPaths.cs` 检测到当前代码页表示不了路径，就用 `WindowsJunction.cs` 建一个纯英文名的目录联接（不需要管理员），用联接路径启动。Mac/Linux 是 UTF-8，直接跳过。
- **老版本家目录 🪟**：1.6 以前的 MC 和老 FML 不认 `--gameDir`，固定把 `%APPDATA%\.minecraft` 当家目录。Windows 上 `AsciiPaths.LegacyHome` 建一个指向实例的联接，只给这个游戏进程把 `APPDATA` 指过去，这样不会写进用户真正的 `.minecraft`。Mac/Linux 上这个函数直接返回 null，老版本会写进 `~/Library/Application Support/minecraft` 或 `~/.minecraft`。适配做法：建一个临时目录，里面放指向实例的符号链接（Linux 叫 `.minecraft`，macOS 是 `Library/Application Support/minecraft`），启动时设 `-Duser.home=<临时目录>`（老版本按 `user.home` 找家目录）。
- **正常关闭**：`GameLauncher.RequestExit`。Windows 用 `Process.CloseMainWindow()`，macOS 用 `MacGameProcess.cs`（经 objc 运行时调 `NSRunningApplication terminate`，和 Dock 的“退出”一样）。Linux 上 `CloseMainWindow` 什么都不做，返回 false。可选方案：X11 下发 `WM_DELETE_WINDOW`（或者调 `xdotool`/`wmctrl`），Wayland 下没有通用办法，可以退而发 SIGTERM（JVM 会跑关闭钩子，但不保证游戏存档）。
- **内存**：`InstanceSettings.FreePhysicalMb` 在 Windows 用 `GlobalMemoryStatusEx`，其他平台用 `GC.GetGCMemoryInfo()` 估算（容器里会被 cgroup 限制影响）。Linux 可以读 `/proc/meminfo` 的 `MemAvailable`，macOS 可以用 `host_statistics64` 或 `sysctl vm.page_free_count`。
- **启动前后命令**：Windows 走 `cmd.exe /s /c "…"`，其他平台走 `/bin/sh -c`（`HomeViewModel.cs`）。
- **导出启动脚本**：Windows 生成 `.bat`，其他平台生成带 `#!/bin/sh` 的 `.sh` 并加执行权限（`Toolbox.cs`）。令牌默认换成占位符。
- **无痕模式**：给 log4j2 配置的文件 appender 加 RegexFilter，聊天行不写进 `latest.log`（`ChatLogFilter.cs`、`NoTraceDirs.cs`）。跨平台。
- **游戏语言**：新实例第一次启动按系统语言预设（`GameLanguage.cs`），跨平台。
- **崩溃保护**：`App/DevCrashGuard.cs` 只在无人值守的开发运行里，Windows 上关掉系统错误对话框。

### 3.4 账号和凭据存储

- 微软账号：设备码登录（`Auth/MicrosoftAuth.cs`）。公开的 Azure Client ID 是 `9015e9e2-b197-4e1a-9659-61c96cf38ffa`，用户可以在账号页或 `AMBER_MS_CLIENT_ID` 换成自己的。打开验证网址用 `Process.Start(UseShellExecute = true)`，在 macOS 走 `open`，在 Linux 走 `xdg-open`。
- authlib-injector 外置登录（`ExternalAuth.cs`）和离线账号（`OfflineNameRules.cs`）。
- 令牌存储（`Auth/Accounts.cs`）：
  - Windows：DPAPI（当前用户范围）。
  - 其他平台：`accounts.json.key` 里放一把随机 32 字节密钥（创建时就是 0600，不留先建后改权限的窗口），令牌用 AES-256-GCM 加密，带 `v2:` 前缀。v0.5.x 在非 Windows 上只是 Base64，读到旧格式还能读，下次保存自动迁移。
  - 这只防“别的系统用户读文件”，防不了同一用户下的程序。**建议适配**：macOS 用钥匙串（Security.framework 的 `SecItemAdd` / `SecItemCopyMatching`），Linux 用 Secret Service（libsecret，经 D-Bus），拿不到时退回现在的密钥文件。存进钥匙串的可以只是那把 32 字节密钥，不用改数据格式。
- CurseForge API Key 存在 `settings.json`（或 `AMBER_CF_KEY`），是明文；仓库里不放任何 Key。

### 3.5 模组、整合包、资源 ✅

- Modrinth（`ModrinthProvider.cs`）和 CurseForge（`CurseForgeProvider.cs`，要 API Key）搜索和安装，自动带必需依赖；作者关了第三方分发的 CurseForge 文件不下载，给官网入口。
- 整合包导入导出（`ModpackService*.cs`）：mrpack、CurseForge、MultiMC/Prism、HMCL、MCBBS、PCL 带启动器的包、压缩的 `.minecraft`、多套一层的包；读 PCL/HMCL 的版本隔离设置（`OtherLauncherSettings.cs`）。
- 模组管理和兼容检查（`ModManager.cs`、`ModCompat.cs`），每个世界的数据包管理（`Game/DatapackManager.cs`）。
- Forge/NeoForge 上跑 Fabric 模组：自动装 Sinytra Connector 和 Forgified Fabric API（`ConnectorInstaller.cs`），并提前预热（`ConnectorWarmup.cs`）。
- 可撤销删除（`TrashService.cs`）：删除的东西先挪到回收目录，Windows 上把回收目录设成隐藏，其他平台没有隐藏（可以把目录名改成以 `.` 开头）。
- 平台注意点：路径比较在 Windows 上不区分大小写，其他平台区分（`ModpackService.cs`、`LauncherService.cs`、`OnboardingViewModel.cs` 都按这个写了）。macOS 默认的 APFS 其实也不区分大小写，目前按“区分”处理，最坏情况是同名不同大小写的文件被当成两个。

### 3.6 下载器 ✅

`Net/Downloader.cs`：分块并发、断点续传、SHA-1 校验、每个主机的连接数上限、429/503 换源，官方源和 BMCLAPI 镜像（`DownloadSource.cs`），可按时区自动选。macOS 的旧 TLS 后端不支持 TLS 1.3，部分 CDN 会断开，所以 `src/Directory.Build.targets` 给 osx RID 打开了 `System.Net.Security.UseNetworkFramework`。

### 3.7 崩溃分析、工具箱、更新检查 ✅

- `Diagnostics/CrashAnalyzer.cs`：分析崩溃日志，列出疑似模组，导出报告。
- `Launch/Toolbox.cs`：清理日志、崩溃报告、natives 和下载残留；提前下载 Java；更新检查只在用户填了更新地址时联网，只提示不下载。

### 3.8 界面 ✅

- Avalonia 自绘，三个平台同一套 XAML。主题在 `Themes/Tokens.axaml`。窗口大小按屏幕自适应（`Layout/WindowSizing.cs`）。
- 打开文件夹、网页一律 `Process.Start(UseShellExecute = true)`。
- Windows 专属的只有 `app.manifest`（PerMonitorV2 DPI）和 `.ico` 图标，csproj 里已经按平台条件化。
- 开发开关：`AMBER_SNAPSHOT_DIR`（每页渲染成 PNG）、`AMBER_STARTUP_LOG`（启动耗时）、`AMBER_PSEUDO_LOC=1`（伪本地化）、`AMBER_SELFTEST=<报告目录>`（端到端自检，`AMBER_SELFTEST_SCOPE` 可以只跑 `multiplayer-guide` / `content` / `multiplayer`）。自检的启动脚本 `tools/run-selftest.ps1` 是 PowerShell，Mac/Linux 上直接设环境变量运行 `Jay` 就行，或者补一个 `.sh`。

### 3.9 联机

联机是平台差异最多的部分。整体结构：

```
EasyTier（组网，P2P 或经节点中继）
  └─ 房间网络 10.233.144.0/24，房主 10.233.144.1
       ├─ 游戏端口（MC 对局域网开放的端口）
       ├─ 聊天服务        RoomChat.Port
       ├─ 封装通道        TunnelSettings.Port（封装模式下游戏、聊天、远程控制都走这里）
       └─ 远程控制服务    47662
大厅网络 10.234.0.0/24（只在有可用邀请时开，用来敲门）
```

#### EasyTier 组件 🟡

- `Multiplayer/EasyTier.cs`：固定版本 2.6.4，第一次用联机时从 EasyTier 的 GitHub 发布页下载对应平台的 zip，校验 SHA-256 和大小后解压到数据目录。七个平台都有哈希：windows x86_64 / arm64 / i686，macos x86_64 / aarch64，linux x86_64 / aarch64。Windows 另外要 `WinDivert64.sys`（完整局域网模式转发广播用）。非 Windows 解压后加执行权限。
- 默认用 `--no-tun`（不建虚拟网卡），所以不需要管理员也不装驱动。加入方用 `--port-forward` 把本机端口转到房主的虚拟地址；房主只放行本次提供服务的 TCP 端口（`--tcp-whitelist`），UDP 全拦。
- 房间密钥经环境变量 `ET_NETWORK_SECRET` 传，不放命令行。
- 进程管理：Windows 上 easytier-core 放进作业对象（`ProcessJob.cs`），Jay 崩溃或被结束时系统会一起结束它。**Mac/Linux 没有对应实现**，Jay 崩溃会留下 easytier-core。适配做法：Linux 用 `prctl(PR_SET_PDEATHSIG)`（要在子进程里调，可以经一个小的包装程序，或者 easytier 自己支持的话用它的参数），或者让 Jay 启动时清理上次留下的进程（记录 PID 和启动时间）；macOS 可以用 kqueue 的 `NOTE_EXIT` 在包装进程里监视父进程。
- macOS 上从网络下载的程序没有隔离属性（.NET 的 HttpClient 不加 `com.apple.quarantine`），未签名的 easytier-core 可以直接运行。如果以后改用别的下载方式要注意这一点。

#### 邀请、敲门和成员钥匙【实验】🟡

纯托管代码，理论上三平台一样，只是没在 Mac/Linux 上实测过。

- `Invite.cs`：`JAY2-` 邀请码，里面是大厅密钥 L 和一次性令牌 K，没有房间密钥。默认 10 分钟、用一次。
- `Lobby.cs`、`Knock.cs`：房主有可用邀请时开一个“大厅”（第二个 EasyTier 实例 + 敲门服务）。加入方用 Noise XXpsk0 敲门，房主在卡片上批准后，经加密通道发下房间凭据和这个成员自己的 memberId + psk。
- `Members.cs`、`Transport/MemberChannel.cs`：每个成员一把钥匙，游戏通道、聊天、远程控制都用它握手；聊天发送者由房主核对。
- 踢人：作废这个人的钥匙和他用过的邀请，房间换新网络名和密钥（epoch + 1），留下的人自动跟过去（`RoomChat.cs` 里的控制帧）。
- `MemberIdentity.cs`：每台设备一把 X25519 长期身份密钥。Windows 用 DPAPI 加密（文件头 `AMBID1`）；其他平台用 AES-GCM，密钥在 `identity.wrap`（0600，文件头 `AMBID2`）。和账号令牌一样，建议改成钥匙串 / libsecret 存 wrap 密钥。
- `TrustedMembers.cs`：记住信任过的成员公钥。

#### 封装模式（`Transport/`）

房主选，加入方跟随：

| 模式 | 内容 | 平台 |
|---|---|---|
| 通用 | 不另外封装，只靠 EasyTier 的加密 | ✅ |
| MC 专用 | 关 Nagle、合并写入，加轻量认证加密 | 🟡 |
| 高加密 | Noise（X25519 + ChaCha20-Poly1305），定期换钥，长度对齐 64 字节 | 🟡 |
| 极端加密 | 再加 ML-KEM-768 混合，长度填充到固定档位，空闲时发掩护流量 | 看 `MLKem.IsSupported` |

极端加密能不能用取决于 .NET 的 `MLKem.IsSupported`：Windows 要较新的 Windows 11（CNG 带 ML-KEM），Linux 要 OpenSSL 3.5 以上，macOS 上 .NET 目前没有实现。不支持时界面里不显示这个选项，收到极端加密的邀请会提示。如果要在 Mac 上支持，可以改用 BouncyCastle 的 ML-KEM（项目已经依赖 BouncyCastle），但要保证和 .NET 实现的线上格式完全一致，并补互通测试。

#### 直连房间 🟡

`DirectLink.cs`、`DirectSession.cs`：`JAY2D-`（邀请）/ `JAY2R-`（回复）码，依次试 IPv6 直连、路由器端口映射（`PortMapping.cs`，UPnP IGD 和 NAT-PMP）、同局域网、IPv4 打洞（`Stun.cs` 判断 NAT 类型，严格 NAT 时做端口预测和“生日”喷洒），多人时由能连上的成员中转，都不行再用节点。

平台注意点：
- Windows 上对 UDP 套接字设了 `SIO_UDP_CONNRESET = 0`，避免收到 ICMP 端口不可达时整个套接字报错。其他平台不需要。
- NAT-PMP 要知道默认网关，用的是 `NetworkInterface.GetIPProperties().GatewayAddresses`。这个接口在 macOS 上的可靠性要实测，如果是空的，NAT-PMP 会直接跳过（UPnP 用组播发现，不受影响）。
- 云测试环境没有 IPv6 时有 23 个直连/测速测试会失败，这是环境问题。

#### 房间聊天、局域网广播 🟡

- `RoomChat.cs`：点对点，不经服务器，AES-GCM，发送者由房主核对。
- `LanGames.cs`：房主侧监听本机的局域网广播（224.0.2.60:4445）找到开放的游戏；加入方在本机回环上广播，让游戏的“多人游戏”列表出现这个房间。组播收发在 macOS 上的回环行为和 Windows 不完全一样，需要实测。

#### 完整局域网模式 🪟

`EasyTierElevated.cs`：EasyTier 建虚拟网卡（Windows 用 wintun），每人一个虚拟 IP，局域网广播也转发（`--enable-udp-broadcast-relay`，EasyTier 这个功能本身只支持 Windows）。建网卡要管理员，所以经 UAC（`powershell.exe` + `Verb = "runas"`）启动一个守护脚本，参数和密钥经命名管道传，不落盘；Jay 退出时管道断开，守护脚本结束 easytier-core 后退出。

Mac/Linux 上直接抛 `PlatformNotSupportedException`（“完整局域网模式目前只支持 Windows”）。**注意界面上这个勾选框在 Mac/Linux 上也会显示**（`MultiplayerPage.axaml` 里没按平台隐藏），勾了以后创建房间才报错，建议先在界面上禁用并说明。

适配思路：
- macOS：EasyTier 在 macOS 上用 utun，要 root。可以用 `osascript -e 'do shell script … with administrator privileges'` 拉起一个守护进程，或者做一个 `SMAppService` 特权助手（要签名）。
- Linux：用 `pkexec` 拉起守护进程，或者给 easytier-core 设 `cap_net_admin`（`setcap`，要 root 一次）。
- 守护进程和 Jay 之间沿用 Windows 的思路：用 Unix 域套接字（权限 0600）代替命名管道，连上后核对对方 PID，密钥只经套接字传，Jay 断开时守护进程收尾退出。
- 广播转发在 Mac/Linux 上 EasyTier 不支持，需要的话要自己在虚拟网卡上转。

#### 出口节点

有两种，别弄混：

1. **全部流量经房主（完整局域网 + 出口）🪟**：依赖完整局域网模式。使用方加 `--exit-nodes` 和两条 `/1` 路由接管系统流量；提权守护在改路由前核对网卡、网关，用 Windows 筛选平台（WFP）加临时防泄漏规则（`ExitTrafficPolicyScript.cs`），按网卡 GUID 改和恢复 DNS（`ExitDnsScript.cs`）、路由（`ExitRoutingScript.cs`）。全部是 PowerShell + Win32。Mac 上对应的是 `route` + `pf`（pfctl 锚点）+ `scutil`/`networksetup` 改 DNS；Linux 上是 `ip route`/策略路由 + nftables + `resolvectl`。工作量大，建议放到最后。
2. **“仅游戏”出口（SOCKS5）🟡**：不需要虚拟网卡和管理员。出口成员跑一个最小 SOCKS5（`Transport/Socks5.cs`），只转发到公网地址；`Transport/ExitPolicy.cs` 拒绝私有地址、本机所有网卡地址和网段、路由器公网 IP，主机名在出口侧解析后逐个判断（防 DNS 重绑定）。平台差异有两处：
   - 出口侧：Windows 上额外读路由表（`GetIpForwardTable2`）拦掉所有非默认路由的网段（比如 VPN 推下来的内网段）。Mac/Linux 没有这一步，**别人借你上网时可能连到经静态路由或 VPN 可达的内网**。适配：Linux 读 `/proc/net/route` 和 `/proc/net/ipv6_route`（或 netlink），macOS 用 `sysctl` 的 `NET_RT_DUMP` 或解析 `netstat -rn`。
   - 加入侧：`JoinTarget.cs` 在服务器地址没写端口时查 `_minecraft._tcp` 的 SRV 记录，目前只用 Windows 的 `DnsQuery_W`，其他平台返回 null（直接按 25565 连）。适配：自己发一个 DNS SRV 查询（UDP 53，读系统 DNS 服务器），或者 Linux/macOS 上调 libresolv 的 `res_query`。

#### 诊断和全条件测试 🟡

- `Diagnostics.cs`：诊断报告，自动去掉密钥、默认遮公网 IP。
- `TestMatrix.cs`、`GuestTestConsent.cs`：双方同步把每条路径和模式测一遍。涉及完整局域网和出口的步骤在 Mac/Linux 上会跳过或失败，适配完整局域网前，矩阵里应该把这些步骤按平台排除。

#### 敏感文字和剪贴板 ✅

邀请码、直连码在界面上默认打码；复制后 60 秒，如果剪贴板里还是这段文字就清掉（`App/Services/SensitiveClipboard.cs`，只用 Avalonia 的剪贴板接口，三平台一样）。日志和诊断里不写任何密钥或码。

可以改进的平台细节：
- Windows 的剪贴板历史（Win+V）和云同步会留一份，可以在设置剪贴板时加 `ExcludeClipboardContentFromMonitorProcessing` 格式（要绕过 Avalonia 直接调 Win32）。
- macOS 可以写 `org.nspasteboard.ConcealedType` 标记，大部分剪贴板管理器会跳过。
- Linux Wayland 下 Avalonia 的剪贴板读写要实测，读不到时 60 秒清除会失效。

### 3.10 远程控制【实验】【未发布】🪟

在邀请房间里，房主和成员可以互相看屏幕、控制鼠标键盘。每次都要被控方点同意（30 秒不点算拒绝，没有“一直信任”选项）；被控方可以只让看不让控；被控方屏幕四周有红框，顶部有小条，按 Ctrl+Shift+F12 立即停止；踢人、离开房间时自动断开。画面只在内存里，不落盘、不进日志。

设计文档：`/mnt/project-files/amber/remote-desktop-design.md`（项目文件夹）。

#### 结构

| 文件 | 内容 | 平台 |
|---|---|---|
| `RemoteControl/RemoteAbstractions.cs` | 接口 `IScreenSource`（截屏）、`IInputSink`（注入）、`ITileCodec`（编码）、同意策略；`RemotePlatform` 按系统选实现 | 跨平台，**你要改的入口** |
| `RemoteControl/RemoteProtocol.cs` | 帧格式：Hello、Accept、Deny、Tiles、Cursor、MouseInput、KeyInput、Grant/RevokeControl、Stop、Ping/Pong、FrameAck | 跨平台 |
| `RemoteControl/RemoteSession.cs` | `RemoteSharer`（被控方：截屏、分块对比、编码、按往返时间调节奏和质量）和 `RemoteViewer`（控制方） | 跨平台 |
| `RemoteControl/ScreenFrame.cs` | BGRA 帧、64×64 分块对比、缩放（最大 2560×1600）、坐标换算、无损编码 | 跨平台 |
| `RemoteControl/RemoteHub.cs` | 房主侧服务，端口 47662，连接都由成员发起，经 `MemberChannel`（purpose `remote-v1`）认证 | 跨平台 |
| `App/Services/SkiaTileCodec.cs` | JPEG 编码（用 Avalonia 自带的 Skia） | 跨平台 |
| `App/Views/RemoteWindows.cs` | 同意弹窗、查看窗口、共享小条、键盘映射 | 跨平台，红框和热键只在 Windows 上挂 |
| `RemoteControl/Windows/WindowsRemote.cs` | GDI BitBlt 截整个虚拟桌面、SendInput 注入、全局热键、红框原生窗口、窗口样式 | 🪟 |

#### 节奏

被控方每 100 毫秒截一次（往返时间超过 150 毫秒放慢到 200 毫秒、超过 400 毫秒放慢到 400 毫秒），只发变化的 64×64 块，JPEG 质量 70 / 55 / 40 随网络变化；控制方收到一批后回 FrameAck，被控方据此控制发送节奏。

#### 要你实现的接口

```csharp
public interface IScreenSource : IDisposable
{
    ScreenBounds Bounds { get; }          // 整个虚拟桌面在桌面坐标里的位置（多显示器时左上角可能是负数）
    bool TryCapture(ScreenFrame frame);   // 截一张 BGRA 到 frame；截不到（锁屏等）返回 false
    (int X, int Y)? CursorPosition();
}

public interface IInputSink : IDisposable
{
    void Mouse(RemoteMouseAction action, RemoteMouseButton button, int desktopX, int desktopY, int delta);
    void Key(ushort scanCode, bool up, bool extended);
}
```

然后在 `RemotePlatform` 里按系统返回你的实现，把 `Supported` 改成包含你的平台。界面会自动放开（`MultiplayerViewModel.Remote.cs` 只看 `RemotePlatform.Supported`）。

#### 坐标和按键约定（线上格式，两端必须一致）

- 鼠标坐标是**桌面像素坐标**，和 `Bounds` 同一套。控制方把查看窗口里的位置换算成被控方帧坐标，再换算成桌面坐标（`RemoteGeometry`）。
- 键盘发的是 **PC/AT 第 1 套扫描码**（就是 Windows 的扫描码），加一个 extended 标志（E0 前缀的键，比如方向键、右 Ctrl、Insert/Delete 那一组）。控制方从 Avalonia 的 `PhysicalKey` 映射过来（`RemoteKeyMap.ScanCode`），这样不受两边键盘布局影响。
- 截屏格式是 BGRA，每行 `Width * 4` 字节。

#### macOS 怎么做

- 截屏：ScreenCaptureKit（macOS 12.3+，`SCStream` 或 `SCScreenshotManager`），老系统退回 `CGDisplayCreateImage`（14 以后废弃）。多显示器要拼成一张虚拟桌面，或者第一版只共享主屏，把 `Bounds` 设成主屏。
- 权限：截屏要“屏幕录制”权限，注入要“辅助功能”权限，都要用户在系统设置里手动打开，而且是按 App 授权的。**没签名、不是 .app 的可执行文件拿权限很麻烦**，建议这个功能只在 `scripts/publish-macos.sh` 打出来的 `Jay.app` 里启用，并在界面上引导用户去开权限（`CGPreflightScreenCaptureAccess` / `AXIsProcessTrusted` 先检查）。
- 注入：`CGEventCreateMouseEvent` / `CGEventCreateKeyboardEvent` + `CGEventPost(kCGHIDEventTap, …)`。
- 坐标：CoreGraphics 的事件坐标是**点**不是像素，Retina 屏上要除以缩放系数；截屏拿到的是像素。`Bounds` 用像素，注入时换算成点。
- 按键：扫描码要换成 macOS 的虚拟键码（`kVK_ANSI_A = 0x00` 那一套），要写一张对照表。
- 红框和热键：红框可以用 Avalonia 的透明置顶窗口（设成鼠标穿透要调 `NSWindow.ignoresMouseEvents`）；全局热键可以用 `CGEventTap` 或 Carbon 的 `RegisterEventHotKey`。没有热键时界面上会显示“热键不可用”，小条上的停止按钮仍然有效。

#### Linux 怎么做

- 要区分 X11 和 Wayland（看 `XDG_SESSION_TYPE` / `WAYLAND_DISPLAY`）。
- X11：截屏用 XShm（`XShmGetImage`）或 `XGetImage`；注入用 XTest（`XTestFakeMotionEvent`、`XTestFakeButtonEvent`、`XTestFakeKeyEvent`）。X11 的 keycode = evdev 码 + 8，而**evdev 码和第 1 套扫描码在非 extended 键上是一样的**（`KEY_ESC=1`、`KEY_1=2`、`KEY_Q=16`…），所以只有 extended 键要查表。
- Wayland：截屏只能走 xdg-desktop-portal 的 ScreenCast + PipeWire，注入走 portal 的 RemoteDesktop（GNOME、KDE 支持）；每次会弹系统授权框，正好和“每次都要同意”一致。也可以用 `/dev/uinput` 注入，但要额外权限，不推荐。
- 第一版可以只做 X11，Wayland 上 `Supported` 返回 false 并说明。

#### 要注意的

- 截屏和注入都放在后台线程，`TryCapture` 会被频繁调用，避免每次分配大缓冲（`ScreenFrame.Resize` 只在尺寸变时重新分配）。
- 红框窗口自己最好不要被截进去（Windows 上目前没排除，问题不大）。
- 锁屏、登录界面截不到时返回 false，会话会继续等，不要抛异常。
- 新平台实现后，跑 `SelfTest.Remote.cs` 那一组自检，再找两台不同系统的电脑实测互控。

### 3.11 Jay 服务器版【实验】

一个只能用 Jay 进的 MC 服务器。三部分：

- **服务端 `jay-server`**（`src/Amber.Server/Program.cs`）：`init`、`setup`（下载官方服务端 jar 并核对 SHA-1、准备 Java）、`run`、`invite`、`revoke`、节点管理等命令。MC 服务端固定只听 127.0.0.1（`Gate/McServerHost.cs`）。网关（`Gate/GateServer.cs`）在公网端口（默认 47670）上做 Noise 握手、核对设备名单和用户名（`Gate/McLogin.cs` 读 Handshake 和 Login Start），认不出的连接一个字节都不回。数据在 `Gate/GateStore.cs`，只存设备公钥和用户名，Unix 上文件权限 0600。Windows 上用 Ctrl+C，Unix 上另外处理 SIGTERM（systemd 停服务时正常关闭 MC）。三平台都能跑。
- **客户端**（`Gate/JayServers.cs`，联机页的“Jay 服务器”卡片）：粘贴 `JAYS1-` 邀请码（`Gate/GateInvite.cs`）入服，拿到这台设备的访问密钥，之后一键进服。访问密钥在 Windows 上 DPAPI 加密（`dpapi:` 前缀），**在 Mac/Linux 上是明文 Base64（`plain:` 前缀），只靠文件权限 0600**。这是现在凭据存储里最弱的一处，建议和账号令牌一起改成钥匙串 / libsecret，至少先改成和 `Accounts.cs` 一样的 AES-GCM + 本机密钥。
- **节点**（`Gate/GateRelay.cs`）：把 TCP 原样转给网关，不持有密钥。

main 上 v0.7.0 之后新增【未发布】：
- 隐私+：客户端经两个不同节点各开一条腿，用 2-of-2 异或拆分（`Gate/SplitStream.cs`），只拿到一条腿什么也还原不出来。
- 隐私++：节点和网关保持心跳通道（`JAYR1-` 节点码，`Gate/GateRelayCode.cs`），能用的节点不够两个时熔断。
- 入口端口轮换（`Gate/EntryRotation.cs`）：端口按 HMAC(种子, 时间窗) 计算，扫描器扫不到固定端口。

这些都是纯托管代码和普通 TCP，没有平台差异，只有上面说的访问密钥存储。

### 3.12 模组烘焙【实验，默认关闭】🟡

`Bake/BakeService.cs`：启动前调用本机的通用加载器原型（Java 程序，`amber.ul.Bake`）预处理模组并缓存到 `<数据目录>/bake-cache`，只支持 1.21.1 的 Fabric、Quilt 和原版。要另外准备加载器目录（`build/loader`、`build/loader-cp.txt`、`mc/mappings-amber`），普通用户不用开。代码本身跨平台；Windows 上原型遇到过“系统代码页是 1252 时 Java 打不开中文路径”的问题，Mac/Linux 是 UTF-8，应该没有。

### 3.13 联机节点部署脚本

`node/install.sh`（Linux，systemd）、`node/docker-compose.yml`、`node/install-windows.ps1`。没有 macOS 脚本，需要的话可以用 launchd plist 包一层 easytier-core。

## 4. 构建和发布

| 产物 | 怎么出 | 平台 |
|---|---|---|
| 单文件 + ReadyToRun | `dotnet publish Amber.App -c Release -r <RID> --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:DebugType=none` | CI 五个 RID 都出：win-x64、osx-x64、osx-arm64、linux-x64、linux-arm64，各在原生 runner 上 |
| Native AOT | `dotnet publish Amber.App -c Release -r win-x64 -p:PublishAot=true -p:DebugType=none` | **只出 win-x64**（4 个文件，启动约 0.3 秒） |
| macOS `.app` | `scripts/publish-macos.sh [osx-arm64|osx-x64]`：生成图标、Info.plist（`io.github.amber.launcher`），ad-hoc 签名 | 只在 Mac 本机跑，CI 没用 |
| `jay-server` | `dotnet publish Amber.Server …`（单文件，不开 ReadyToRun） | CI 五个 RID 都出 |

AOT 相关：JSON 读写全部走源生成（`Core/AmberJson.cs` 的 `AmberJsonContext`），新加的序列化类型要加进去，否则 AOT 版本会出错（普通版本会退回反射，看不出来）。Avalonia 12 支持 macOS 和 Linux 的 AOT，要在对应系统上装好原生工具链（macOS 要 Xcode 命令行工具，Linux 要 clang 和 zlib 开发包）。想给 Mac/Linux 出 AOT 版，在 `build.yml` 的 matrix 里照 win-x64 加一步就行，但要先在真机上跑一遍自检，特别是 `MacGameProcess.cs` 这种用 `DllImport` 的地方。

CI 的单元测试只在 `windows-latest` 上跑。有一些测试本身按 `OperatingSystem.IsWindows()` 分支（`TrashServiceTests`、`AuthTests`、`ExitDnsScriptTests` 等）。建议在 `validate` job 里加 macOS 和 Linux，这样 Mac/Linux 分支的代码也有测试兜底。

代码签名：三个平台都没有正式签名。macOS 上远程控制、完整局域网这类要系统权限的功能，正式签名 + 公证会省很多事（权限按签名身份记，ad-hoc 签名每次重新打包都会变）。

## 5. 补齐到和 Windows 一致：逐项路线

目标是 Mac、Linux 上每个功能都和 Windows 一样能用。下面按“先易后难”排成四批，每一项写了要改哪里、怎么做、做完怎么算数（验收）。一批做完再发一个版本，新做的功能先标【实验】，在真机上验证过再去掉。

系统确实做不到的地方，按这个原则处理：界面上不藏起来，而是显示“这台电脑上不可用”并说明原因和替代办法（比如 Wayland 上远程控制要走系统授权框）。

### 第一批：小改动，影响安全和数据（建议先做）

| # | 功能 | 改哪里 | 怎么做 | 验收 |
|---|---|---|---|---|
| 1 | Jay 服务器访问密钥加密 | `Gate/JayServers.cs` `Protect`/`Unprotect` | 先改成和 `Auth/Accounts.cs` 一样的 AES-GCM + 本机密钥（新前缀，比如 `aesgcm:`），旧的 `plain:` 能读，下次保存自动迁移；第二批再接钥匙串 | Mac/Linux 上 `jay-servers.json` 里看不到明文密钥；旧文件升级后还能进服；补单元测试 |
| 2 | 老版本（1.6 以前）家目录 | `Launch/AsciiPaths.cs` `LegacyHome`、`GameLauncher.cs` 第 310 行附近 | 在数据目录下建 `legacyhome/<哈希>/`，里面放指向实例的符号链接（Linux：`.minecraft`；macOS：`Library/Application Support/minecraft`），启动时加 `-Duser.home=<该目录>`；符号链接用 `Directory.CreateSymbolicLink` | 在测试目录里启动 1.5.2 + 老 Forge，用户真实的 minecraft 目录没有新文件 |
| 3 | 正常关闭游戏（Linux） | `GameLauncher.RequestExit` | X11：找到游戏进程的窗口（`_NET_WM_PID`）发 `WM_DELETE_WINDOW`；Wayland 或找不到窗口时发 SIGTERM，并在界面上提示“可能来不及存档” | X11 下点“关闭游戏”，世界正常保存（看 `latest.log` 的 Saving 行） |
| 4 | EasyTier 不残留 | `Multiplayer/EasyTier.cs` `Launch` | 每次启动把 PID + 进程启动时间写进 `easytier/run/*.pid`；Jay 启动和开房间前清理“PID 还在、启动时间对得上”的旧进程。Linux 额外可以用 `prctl(PR_SET_PDEATHSIG, SIGTERM)`（需要一个小包装进程，因为 .NET 的 `Process.Start` 不能在 fork 后插代码） | `kill -9` 掉 Jay 后重新打开，没有 easytier-core 残留 |
| 5 | 不支持的选项先说明 | `Views/MultiplayerPage.axaml`、`MultiplayerViewModel.cs`、`TestMatrix.cs` | 在 Core 里加 `PlatformFeatures`（完整局域网、全部流量出口、远程控制、极端加密各一个布尔），界面按它禁用并显示原因；全条件测试排除不支持的步骤。之后每补齐一项就把对应开关改成 true | Mac/Linux 上勾不了完整局域网，旁边有说明；全条件测试不再出现必然失败的步骤 |
| 6 | “仅游戏”出口拦截非默认路由 | `Transport/ExitPolicy.cs` `NonDefaultRoutes` | Linux 读 `/proc/net/route` 和 `/proc/net/ipv6_route`；macOS 用 `sysctl` `NET_RT_DUMP`（或者先解析 `netstat -rn -f inet` / `inet6`）。太宽的路由（IPv4 短于 /8、IPv6 短于 /16）照 Windows 的规则不拦 | 有 VPN 内网路由时，经出口连那个网段被拒绝；补单元测试（解析部分用样例文本） |
| 7 | 进服时查 SRV | `Transport/JoinTarget.cs` | 自己实现一个最小 DNS 客户端：读系统 DNS（Linux `/etc/resolv.conf`，macOS `scutil --dns` 或 `/etc/resolv.conf`），UDP 53 发 SRV 查询，解析应答；或者 P/Invoke libresolv 的 `res_query` | 输入不带端口的服务器域名，经出口时按 SRV 的主机和端口连 |
| 8 | 回收目录隐藏 | `Content/TrashService.cs` `HideOnWindows` | Mac/Linux 上回收目录名用 `.` 开头；macOS 另外可以设 `UF_HIDDEN`（`chflags hidden`） | Finder / 文件管理器默认看不到回收目录 |
| 9 | 可用内存 | `Launch/InstanceSettings.cs` `FreePhysicalMb` | Linux 读 `/proc/meminfo` 的 `MemAvailable`；macOS 用 `host_statistics64`（free + inactive + purgeable 页 × 页大小） | 自动内存给出的数值和系统监视器看到的可用内存一致 |

### 第二批：凭据、Java、打包

| # | 功能 | 改哪里 | 怎么做 | 验收 |
|---|---|---|---|---|
| 10 | 凭据进系统钥匙串 | `Auth/Accounts.cs`、`Multiplayer/MemberIdentity.cs`、`Gate/JayServers.cs` | 抽一个 `ISecretStore`（存取一把 32 字节包裹密钥）：Windows 继续 DPAPI；macOS 用 Security.framework（`SecItemAdd` / `SecItemCopyMatching`，P/Invoke）；Linux 用 Secret Service（D-Bus，有 libsecret 时 P/Invoke `secret_password_store_sync`），都拿不到时退回现在的 0600 密钥文件并在设置页提示。三处数据格式不变，只换包裹密钥的来源 | Mac/Linux 上数据目录里不再有明文密钥文件（有钥匙串时）；删掉钥匙串条目后提示重新登录而不是崩溃；旧数据自动迁移 |
| 11 | Java 扫描补全 | `Java/JavaFinder.cs` | macOS：`/usr/libexec/java_home -V` 的结果、Homebrew（`/opt/homebrew/opt/openjdk*`、`/usr/local/opt/openjdk*`）；Linux：`/usr/lib64/jvm`、`/opt`、`~/.sdkman/candidates/java`、Flatpak/Snap 的 JDK | 常见安装方式装的 Java 都能被找到 |
| 12 | Mojang 运行时的符号链接 | `Java/JavaRuntimeInstaller.cs` | 处理清单里 `type: link` 的条目：校验 `target` 在运行时目录内，用 `File.CreateSymbolicLink` 建 | Mac 上新下载的运行时和官方启动器下载的文件结构一致 |
| 13 | Linux ARM64 自动下载 Java | `Java/JavaRuntimeInstaller.cs` | Mojang 没有 Linux ARM64 运行时。可以加一个备用来源：Adoptium API（`api.adoptium.net`，有 SHA-256）。这是新的联网地址，属于“用户发起的下载”，要写进 README 隐私一节；默认先问用户 | Linux ARM64 上能一键装好对应版本的 Java |
| 14 | Mac/Linux 的 Native AOT 包 | `.github/workflows/build.yml` | 照 win-x64 的 AOT 步骤给 osx-arm64、osx-x64、linux-x64、linux-arm64 各加一步；先在真机上跑全套自检（特别是 `MacGameProcess.cs` 的 `DllImport`、Skia、剪贴板） | 发布页多出 `Jay-<RID>-aot` 包，功能和单文件版一致 |
| 15 | macOS `.app` 进 CI | `scripts/publish-macos.sh`、`build.yml` | 在 macOS runner 上跑这个脚本，发布 `Jay-osx-*.zip`（.app）。正式签名和公证要 Apple 开发者账号（年费），由 Yuma 决定；没有时保持 ad-hoc 签名 | 下载后双击 `Jay.app` 能打开 |
| 16 | Linux 桌面集成（可选） | 新增 | 提供 `.desktop` 文件和图标，或者出 AppImage | 能从应用菜单启动 |
| 17 | 测试跑在三个平台 | `build.yml` 的 `validate` | 加 macOS、Linux runner 跑 `dotnet test`；没有 IPv6 的 runner 上那些直连测试要按环境跳过（检测环境，不是按平台一刀切） | 三个平台单元测试都绿 |
| 18 | 自检脚本 | `tools/` | 加 `run-selftest.sh`（设 `AMBER_SELFTEST` 运行 `Jay`） | Mac/Linux 上一条命令跑完界面自检 |

### 第三批：远程控制（和 Windows 一样：全屏、全控、双向、每次都要同意）

实现细节见 3.10。拆成这几步，每步都能单独验收：

| # | 内容 | 验收 |
|---|---|---|
| 19 | macOS 截屏 `IScreenSource`（ScreenCaptureKit，多显示器拼成虚拟桌面） | 从 Windows 看 Mac 的画面，多显示器位置正确 |
| 20 | macOS 注入 `IInputSink`（CGEvent，像素换算成点，扫描码换虚拟键码） | 从 Windows 控制 Mac：鼠标位置准确（Retina 屏也对），中英文输入法下按键正确 |
| 21 | macOS 权限引导（屏幕录制、辅助功能），只在 `Jay.app` 里启用 | 没权限时界面说明怎么开，开了以后不用重启 Jay 也能用（或明确提示要重启） |
| 22 | macOS 红框（置顶、鼠标穿透的透明窗口）和全局停止热键（`CGEventTap` 或 `RegisterEventHotKey`） | 被控时四周有红框；按 Ctrl+Shift+F12（Mac 上可以另定，比如 ⌃⇧F12）立即停止 |
| 23 | Linux X11 截屏（XShm）+ 注入（XTest）+ 红框 + 热键（`XGrabKey`） | 同上，在 X11 桌面上 |
| 24 | Linux Wayland：xdg-desktop-portal 的 ScreenCast（PipeWire 取帧）+ RemoteDesktop（注入）；红框由系统自己的共享提示代替 | GNOME 和 KDE 上能被看和被控，系统授权框只在开始时出现一次 |
| 25 | 三平台互控矩阵 | Windows↔Mac、Windows↔Linux、Mac↔Linux，两个方向各测一次，邀请房间和直连房间都测 |

做完后把 `RemotePlatform.Supported` 按平台放开，`SelfTest.Remote.cs` 在三个平台都跑一遍。

### 第四批：完整局域网和全部流量出口（工作量最大）

| # | 内容 | 怎么做 | 验收 |
|---|---|---|---|
| 26 | 特权守护进程 | 照 Windows 的 `ElevatedGuard` 设计：macOS 用 `osascript … with administrator privileges` 拉起守护（以后有签名可以换 `SMAppService` 特权助手），Linux 用 `pkexec`。守护和 Jay 之间用 Unix 域套接字（目录 0700、套接字 0600），连上后用 `SO_PEERCRED`（Linux）/ `LOCAL_PEERPID`（macOS）核对对方 PID；参数和密钥只经套接字传，不落盘；Jay 断开时守护结束 easytier-core 并恢复所有改动后退出 | 输一次密码就能开完整局域网；Jay 被强制结束后，虚拟网卡、路由、DNS 都恢复原样 |
| 27 | 虚拟网卡 | EasyTier 在 macOS 上用 utun、在 Linux 上用 tun，去掉 `--no-tun` 即可，网卡名参数按平台调整（`EasyTierArgs.DeviceName` 现在是 Windows 风格的名字） | 每人拿到 10.233.144.x，能互相 ping 通、MC 用虚拟 IP 直连 |
| 28 | 局域网广播转发 | EasyTier 的 `--enable-udp-broadcast-relay` 只支持 Windows。Mac/Linux 上由守护进程自己做：在物理网卡上收本机发出的 UDP 广播和组播（MC 的 224.0.2.60:4445 必须支持，其他按端口白名单），经虚拟网卡转发给房间成员，对方守护再在本机回环上重放；用 BPF（macOS）/ `AF_PACKET`（Linux）或者普通套接字绑定组播都可以 | 其他电脑的 MC 多人游戏列表里自动出现房主开放的局域网世界；别的靠广播找房间的游戏也能看到 |
| 29 | 全部流量出口：路由 | macOS：`route add -net 0.0.0.0/1` 和 `128.0.0.0/1` 指向 utun，并给节点和 EasyTier 对端地址加经物理网关的主机路由；Linux：`ip route` + 策略路由（`ip rule`），同样给承载地址加例外 | 开出口后 `curl ifconfig.me` 显示出口成员的公网 IP；关掉后恢复 |
| 30 | 全部流量出口：防泄漏 | macOS：pf 锚点（`pfctl -a jay -f -`），只放行 utun 和承载地址，其余出站拒绝，结束时清掉锚点；Linux：nftables 单独的表，规则同上。对应 Windows 的 WFP 规则（`ExitTrafficPolicyScript.cs`） | 拔掉出口成员或隧道断开时，本机不会退回直接上网（没有流量漏到物理网卡） |
| 31 | 全部流量出口：DNS | macOS：`scutil` 设置只对本次会话生效的 DNS 字典，结束时删掉；Linux：`resolvectl dns <网卡>` + `resolvectl domain <网卡> ~.`（systemd-resolved），没有 resolved 时改 `/etc/resolv.conf` 前先备份 | DNS 查询走隧道；结束后 DNS 设置和开始前完全一样 |
| 32 | 全条件测试 | `TestMatrix.cs`、`GuestTestConsent.cs` 按 `PlatformFeatures` 放开完整局域网和出口步骤 | 三平台混合测一遍全条件测试，全部步骤有结果 |

### 其他零散项

| # | 内容 | 怎么做 |
|---|---|---|
| 33 | 极端加密在 macOS 上可用 | `Transport/Noise.cs` 的 ML-KEM 部分在 `MLKem.IsSupported` 为 false 时改用 BouncyCastle 的 ML-KEM-768（项目已经引用 BouncyCastle）。必须和 .NET 的实现线上格式一致：补一组固定向量测试，并让 Windows（.NET 实现）和 Mac（BouncyCastle）实际握手一次 |
| 34 | 敏感文字不进剪贴板历史 | macOS 写剪贴板时加 `org.nspasteboard.ConcealedType`；Windows 加 `ExcludeClipboardContentFromMonitorProcessing`；Linux 上 KDE 的 Klipper 认 `x-kde-passwordManagerHint`。要绕过 Avalonia 直接调系统接口 |
| 35 | 联机节点 macOS 脚本 | `node/` 加一个 launchd plist 的安装脚本 |
| 36 | Linux Wayland 剪贴板 | 实测 60 秒清除在 Wayland 下是否生效，不生效时查 Avalonia 的 Wayland 后端或者改用 `wl-copy`/`wl-paste` |

### 做完的标准

- 第 2 节总表里所有 🪟 和 🟡 都变成 ✅，或者在界面上有明确说明的系统限制。
- 三个平台单元测试全绿，界面自检全过，联机（含远程控制）做过跨平台、跨网络的真机测试。
- README 和发布说明里的“只支持 Windows”字样同步删掉。

## 6. 开发和测试规矩

- 只写原创代码，不从 HMCL、PCL 等启动器或反编译的程序里抄。公开的协议和格式文档可以参考。
- 没有遥测、统计、远程开关。联网只限于：用户发起的下载、微软登录、Modrinth / CurseForge、更新检查、联机。
- **测试一律用单独的数据目录和游戏目录**：设 `AMBER_DATA_DIR` 指向测试配置，在里面指定测试游戏目录。绝不写用户真实的 `.minecraft`（或 Mac 的 `~/Library/Application Support/minecraft`）。
- 加密只用成熟的原语和库，不自己发明。
- 提交里不能有个人信息：邮箱、用户名、令牌、本机绝对路径。CurseForge Key、任何密钥都不进仓库。
- `Amber.Core` 不依赖界面；平台代码放在 `OperatingSystem.IsXxx()` 后面或单独的平台类型里（可以照 `RemoteControl/Windows/` 建 `RemoteControl/MacOS/`、`RemoteControl/Linux/`）。
- 新行为要有单元测试；界面上看得到的，加一步自检。
- 构建和测试：

```bash
cd src
dotnet build Amber.slnx -c Release
dotnet test Amber.Core.Tests -c Release
```

- 没有 IPv6 的环境里直连/测速测试会失败。计时相关的测试一律“等到条件成立（有上限）”，不要用固定延时后断言。
