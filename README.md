<div align="center">

<img src="docs/brand/jay-feather-amber-v1.png" alt="Jay" width="96">

# Jay

**干净、轻快的 Minecraft Java 版启动器**

没有遥测，没有远程开关，下载解压就能用。

[![最新版本](https://img.shields.io/github/v/release/warriorleavestar/jay-launcher?label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC&color=d98a1f)](https://github.com/warriorleavestar/jay-launcher/releases/latest)
[![许可证](https://img.shields.io/github/license/warriorleavestar/jay-launcher?label=%E8%AE%B8%E5%8F%AF%E8%AF%81)](LICENSE)
![平台](https://img.shields.io/badge/%E5%B9%B3%E5%8F%B0-Windows%20%7C%20macOS%20%7C%20Linux-blue)

简体中文 | [English](README.en.md)

![Jay 启动页](docs/images/jay-home.png)

</div>

本项目原名 Amber，从 v0.7.0 起改名 **Jay**，程序和图标都换成了 Jay。旧账号和设置可以直接沿用，详见[改名与兼容说明](docs/announcements/2026-11-06-jay-rename.md)。

> **v0.7.0 的联机不兼容旧版本**：联机加密和加入房间的方式重做了，房间要用新的 `JAY2-` 邀请码，直连用 `JAY2D-` / `JAY2R-` 码。旧的 `AMB-` 房间码和直连码不再接受，一起联机的人都要升级到 v0.7.0。
>
> 标了 **（实验）** 的功能已经做完并通过自动测试，但还没有在真实环境里完整验证过，遇到问题欢迎[反馈](https://github.com/warriorleavestar/jay-launcher/issues/new/choose)。

## 下载安装

到 [Releases](https://github.com/warriorleavestar/jay-launcher/releases/latest) 下载与你的电脑对应的包。v0.5.9 及更早的版本使用 `Amber-` 包名前缀和 `Amber.exe`。

| 系统 | 文件 | 运行方式 |
|---|---|---|
| Windows 10+ x64 | `Jay-win-x64-aot.zip`（推荐） | 解压后双击 `Jay.exe` |
| macOS Apple 芯片 | `Jay-osx-arm64.tar.gz` | 解压后在终端运行 `./Jay` |
| macOS Intel（实验） | `Jay-osx-x64.tar.gz` | 解压后在终端运行 `./Jay` |
| Linux x64（实验） | `Jay-linux-x64.tar.gz` | 解压后运行 `./Jay` |
| Linux ARM64（实验） | `Jay-linux-arm64.tar.gz` | 解压后运行 `./Jay`，并使用系统安装的 Java |
| Jay 服务器版（实验） | `JayServer-<系统>.zip` / `.tar.gz` | 见 [server/README.md](server/README.md) |

推荐的 zip 是预先编译成本机代码的版本（Native AOT），启动更快（窗口约 0.3 秒打开）、占内存更少，解压后是 4 个文件，要放在同一个文件夹里。
同一个发布里还有单个文件的 `Jay-win-x64.zip`（里面只有一个 `Jay.exe`），解压即可运行，适合只想要一个文件的情况，启动稍慢一些。两个版本功能和数据完全相同，可以随时换用。

发布包自带 .NET，不用安装。Jay 会按游戏版本选择本机 Java，并在 Mojang 提供对应平台运行时时自动下载；Linux ARM64 需要先安装合适版本的 64 位 Java。

macOS Apple 芯片版已由合作者在真机上完整测过安装、启动和游玩；Intel Mac 和 Linux 版通过了自动构建和测试，但还没有真机验证，所以标为实验。macOS/Linux 版本目前没有代码签名。macOS 第一次运行若被 Gatekeeper 拦截，可在“系统设置 → 隐私与安全性”中确认打开。完整局域网和出口节点模式仍只支持 Windows；普通房间、直连、聊天和仅游戏转发可跨平台使用。

## 功能

### 版本与加载器

- 安装正式版和快照版
- 一键安装 Fabric、Quilt、Forge（包括 1.5.2 及更早的老版本）、NeoForge、OptiFine
- 老 Forge 需要的 FML 库会提前校验好放进实例，旧版本的游戏不会写进你自己的 .minecraft
- 每个版本可以有独立的存档、模组和配置文件夹

### 账号

- 微软账号（设备码登录）、authlib-injector 外置登录、离线账号
- 支持多账号切换和皮肤预览
- 不保存密码；登录令牌在 Windows 上用 DPAPI 加密，在 macOS/Linux 上用仅当前系统用户可读的本机密钥加密

### 模组与整合包

- 在 Modrinth 和 CurseForge 上搜索、安装模组、资源包、光影、数据包和整合包，自动带上必需的依赖
- 导入 mrpack、CurseForge、MultiMC / Prism Launcher 实例、HMCL 和 MCBBS 整合包，也认得 PCL 带启动器的包、压缩的 .minecraft 文件夹和多套一层文件夹的包；导出 mrpack 和 CurseForge 格式
- 管理实例里的模组，以及每个世界的数据包（启用、禁用、删除）
- 模组兼容检查：读每个模组声明的加载器、游戏版本和前置，标出装错的，一键找对应版本
- Fabric 模组上 Forge / NeoForge：可以自动装开源的 Sinytra Connector 和 Forgified Fabric API（不是每个 Fabric 模组都能用）；Quilt 实例直接用 Fabric 模组
- Connector 预先处理：提前把 Fabric 模组转换好，之后启动不用再等
- 模组烘焙（实验，默认关闭）：启动前用本机的通用加载器预处理模组并缓存，目前只支持 1.21.1 的 Fabric、Quilt 和原版；失败时自动按普通方式启动
- CurseForge 上作者关闭了第三方分发的文件不会被偷偷下载，而是给出官网的手动下载入口
- 模组多也读得快：几百个模组的文件夹不到 1 秒，没变的再次打开只要几毫秒

### 游戏管理与崩溃分析

- 每个版本可以单独设置 Java、内存、JVM 参数、窗口大小，以及启动前后要运行的命令
- 管理世界、截图、资源包、光影和服务器列表
- 游戏崩溃时分析原因、列出疑似模组、导出错误报告
- 工具箱：清理游戏垃圾、下载文件、提前下载 Java

### 联机

- 不在同一个局域网也能一起玩：房主把世界“对局域网开放”后创建房间，把邀请码发给朋友；朋友粘贴邀请码后“敲门”，房主在弹出的卡片上同意，朋友才进得来
- 新的联机加密（实验）：邀请码默认 10 分钟内只能用一次，码里不含房间密钥；每个成员有自己的一把钥匙；房主可以踢人，被踢的人马上断开、进不了这个房间，房间随即换一把新钥匙。码在界面上默认打码，复制后 60 秒自动从剪贴板清除，日志和诊断报告里不出现密钥。同一网络内已实测，跨网络（两边不在同一个网络）还没实测
- 基于开源组网工具 [EasyTier](https://github.com/EasyTier/EasyTier)，第一次使用时从它的 GitHub 发布页下载并校验；默认模式不需要管理员权限，也不安装驱动
- 房间聊天：点对点，不经过任何服务器，每个成员用自己的钥匙加密，发送者由房主核对，成员无法冒充别人
- 直连房间（不用联机节点）：双方互发一次邀请码 / 回复码（码里不含房间密钥），依次尝试 IPv6 直连、路由器端口映射（UPnP / NAT-PMP）、IPv4 打洞（含严格 NAT 的端口预测和喷洒）；多人时由能连上的成员中转；都不通且添加了节点时再用节点。“检测我的网络”能提前看出好不好连
- 联机调试模式和诊断报告（自动去掉房间密钥、默认遮住公网 IP），以及一键“全条件测试”：双方同步把每种路径和模式都测一遍，记录连通、用时、延迟、吞吐和失败原因
- 可选的完整局域网模式：每人一个虚拟 IP，游戏的局域网广播也会转发，其他支持局域网联机的游戏和程序也能互通（需要管理员权限）；还可以让房间成员经某个人的电脑上网（出口节点，双方都要手动开启，流量全部走隧道、防止泄漏到隧道外）
- 无痕模式（默认关）：游戏日志不记录聊天内容；完整局域网模式也不再把密钥写到磁盘上
- 联机页有“怎么联机”的引导，贴错码会自动认出来并提示
- 联机节点由你自己添加：[node 文件夹](node/README.md)里有 Linux、Docker、Windows 的一键部署脚本，搭好后把节点分享码发给朋友

### Jay 服务器版（实验）

- 只能用 Jay 进的 Minecraft 服务器，可以放在 VPS 上，也可以是局域网里的电脑。命令行程序 `jay-server` 会下载官方服务端（校验 SHA-1）和合适的 Java
- 服务器管理员给每个人发一次性的 `JAYS1-` 邀请码，用户在联机页的“Jay 服务器”卡片里粘贴后就能一键进服；每台设备有自己的身份，用户名和设备绑定，访问密钥被拿到别的电脑上也用不了
- 不认识的连接一个字节都不回，扫描器看不到这是 MC 服务器
- 目前通过了自动测试，还没有连真实 MC 服务端做过完整测试。用法见 [server/README.md](server/README.md)

### 下载

- 分块并发、断点续传、SHA1 校验
- 官方源和 BMCLAPI 镜像可以手动选择，也可以按时区自动选择，失败时自动换源

## 截图

| 下载游戏 | 实例设置 |
|---|---|
| ![下载游戏](docs/images/download.png) | ![实例设置](docs/images/instance.png) |

![启动器设置](docs/images/settings.png)

## 隐私

- **没有遥测**：不收集使用数据，不生成设备识别码。
- **没有远程控制**：功能不受任何远程开关或云端配置影响。时区只用来选默认下载源。
- **更新由你决定**：默认不检查更新；在设置里填了更新地址才会检查，而且只提示，不会自动下载或替换。
- **联网只为了完成你要做的事**：登录、获取版本列表、下载和搜索。会用到的服务有微软和 Xbox 登录、你添加的皮肤站、Mojang 游戏文件服务器、你选择的下载源、各加载器的下载地址（OptiFine 通过 BMCLAPI，authlib-injector 通过其官方地址或 BMCLAPI），以及 Modrinth 和 CurseForge。
- **联机只连你自己添加的节点**：只有在联机页创建或加入房间时才会连接，连的是你在“联机节点”里添加的节点和房间里的其他玩家；加入 Jay 服务器时只连邀请码里写的那台服务器。Jay 不内置节点、不从网上拉节点列表，房间码在你的电脑上随机生成，不经过 Jay 的服务器（Jay 没有服务器）。联机组件 EasyTier 从 `github.com/EasyTier/EasyTier` 的发布页下载。直连房间会向你设置的 STUN 服务器（默认 `stun.miwifi.com`、`stun.chat.bilibili.com`、`stun.cloudflare.com`，可改可清空）询问你的外部地址，它们只回答地址、不经过游戏数据；也会请你的路由器临时映射一个端口，结束联机时删掉。

代码完全开源，欢迎自行查看。

## 配置

大部分功能不用任何配置。下面两项是可选的：

| 项目 | 在哪里设置 | 环境变量 | 说明 |
|---|---|---|---|
| 微软 Client ID | 「账号」页 | `AMBER_MS_CLIENT_ID` | 留空使用内置的默认值。优先级：界面填写 > 环境变量 > 默认值 |
| CurseForge API Key | 「设置」页 | `AMBER_CF_KEY` | 到 [console.curseforge.com](https://console.curseforge.com) 免费申请。没有 Key 时不能使用 CurseForge，其他功能不受影响 |

设置保存在系统应用数据目录的 `Amber/settings.json`（Windows 为 `%APPDATA%\Amber`，macOS 为 `~/Library/Application Support/Amber`，Linux 通常为 `~/.config/Amber`）。

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```shell
git clone https://github.com/warriorleavestar/jay-launcher.git
cd jay-launcher/src
dotnet build
dotnet test
dotnet run --project Amber.App
```

打包当前平台的单文件版本（把 RID 换成 `win-x64`、`osx-arm64`、`osx-x64`、`linux-x64` 或 `linux-arm64`）：

```shell
dotnet publish Amber.App -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:DebugType=none -o ../publish
```

还有一个命令行版本，例如 `dotnet run --project Amber.Cli -- list-remote`。

项目使用 C#、.NET 10 和 [Avalonia](https://avaloniaui.net/)。

Android 和 iOS 实验版使用独立的平台工程，目前只提供 Jay 服务器登记和本机网关，还不能直接启动 Minecraft。构建方法和能力边界见 [Android 实验版说明](docs/android.md)和 [iOS 实验版说明](docs/ios.md)。

## 常见问题

**Windows 提示“已保护你的电脑”怎么办？**
Jay 的程序没有代码签名，所以 SmartScreen 可能会拦截。确认文件是从本项目的 Releases 下载的，点“更多信息”→“仍要运行”即可。

**exe 为什么这么大？**
.NET 运行时和界面库都打包在里面，所以不用另外安装任何东西。

**可以用我现有的 `.minecraft` 文件夹吗？**
可以，在「设置」页把游戏目录指向它。

**微软账号登录失败怎么办？**
先看提示里的错误信息，并确认这个账号已经拥有 Minecraft。也可以换成你自己的 Azure 应用：注册一个允许个人账号、开启设备码流程、并且有 Minecraft API 访问权限的公共客户端应用，再把 Client ID 填到「账号」页。

**为什么搜不到 CurseForge 的内容？**
CurseForge 的接口需要 API Key，见上面的[配置](#配置)。Modrinth 不需要。

**下载很慢怎么办？**
在「设置」页切换下载源，试试官方源或 BMCLAPI 镜像。

**支持 macOS 和 Linux 吗？**
支持 macOS Intel/Apple 芯片和 Linux x64/ARM64。Apple 芯片版已真机验证，Intel Mac 和 Linux 版目前是实验。完整局域网、出口节点和极端加密仍受 Windows 系统能力限制；其他主要功能可跨平台使用。

**有哪些做不到的？**
在 Fabric / Quilt 上运行 Forge 模组：目前没有可靠的兼容层，Jay 会直接说明。

## 许可证

[MIT](LICENSE)

联机功能使用的 EasyTier 是独立的开源项目（LGPL-3.0），Jay 不内置它，在你第一次使用联机时单独下载。

---

Jay 不是 Minecraft 官方产品，与 Mojang 和 Microsoft 没有关联。
