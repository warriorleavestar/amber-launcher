# Amber

原创代码的 Minecraft 启动器，C# / .NET 10。没有云控、没有强制更新、没有遥测。

## 目录

| 路径 | 内容 |
|---|---|
| `src/` | 解决方案 `Amber.slnx`：`Amber.Core` 核心库、`Amber.App` 图形界面、`Amber.Cli` 命令行、`Amber.Core.Tests` 测试 |
| `发布/Amber.exe` | 打包好的单文件程序，双击即可运行（不需要装 .NET） |
| `测试游戏目录/` | 开发测试用的 .minecraft，可随时删除 |

## 当前进度（2026-11-02，v0.5.1）

| 模块 | 内容 |
|---|---|
| 游戏版本 | 原版安装与补全；Fabric、Quilt、Forge（含 1.7.10、1.12.2 旧格式）、NeoForge、OptiFine；可自定义版本名 |
| Java | 本机扫描，缺合适版本时从 Mojang 下载运行时，按版本自动选择，也可以手动指定 |
| 账号 | 微软设备码登录（要你自己的 Client ID）、authlib-injector 外置登录、离线、多账号、头像和全身皮肤预览；令牌用 DPAPI 加密，密码不保存 |
| 内容 | Modrinth、CurseForge（要你自己的 API Key）搜索和下载模组、资源包、光影、整合包，自动带必需依赖 |
| 实例 | 每个版本独立的设置（隔离、Java、内存、JVM 和游戏参数、窗口、启动前后命令、启动后进服）、模组管理（启用、禁用、更新、删除）、世界（进入、备份、导入、删除）、截图、资源包、光影、服务器列表（状态查询、一键进入）、mrpack 和 CurseForge 整合包导出、导出启动脚本 |
| 诊断 | 崩溃原因分析（规则自己写的）、疑似模组、导出错误报告 |
| 工具箱 | 清理游戏垃圾、下载任意文件、下载 Java、检查更新（只提示；默认没有配置地址，不联网） |
| 下载器 | 大文件分块并发、断点续传、官方和 BMCLAPI 自动回退、SHA1 校验、卡住就换源、校验结果缓存；镜像连接数上限（BMCLAPI 32）；被限流（429、带 Retry-After 的 503）时先换源，只剩一个源就等它恢复，最多等 30 秒 |

没有遥测、设备识别码、远程开关和强制更新。地区只用来挑默认下载源。删除模组、世界和版本都进回收站。

需要你自己准备（缺了对应功能不可用，其他不受影响）：
- 微软登录：Azure 注册公共客户端应用，允许个人账号并开启设备码流程，向 Mojang 申请 Minecraft API 权限。填到「账号」页，或设环境变量 `AMBER_MS_CLIENT_ID`。
- CurseForge：在 console.curseforge.com 申请 API Key，填到「设置」页，或设环境变量 `AMBER_CF_KEY`。

还没做：联机、数据包下载、MultiMC 等其他格式整合包导入、Forge 1.5.2 及更早版本（官方没有安装器）。

## 实测（2026-11-02，本机）

| 项目 | 结果 |
|---|---|
| 各加载器真实启动到主界面 | Fabric 1.21.1 / 26.3，Quilt 1.21.1，NeoForge 1.21.1 / 26.3，Forge 1.20.1 / 1.12.2 / 1.7.10，OptiFine 1.20.1 |
| 真实整合包 | Fabulously Optimized（Modrinth）、Cozy Zen（CurseForge，247 模组 + 33 资源包，58 秒装完）都能启动 |
| 启动器启动到窗口打开 | 约 0.6 秒（连续启动，磁盘缓存已热；ReadyToRun，未开启时约 1.4 秒；代价是 exe 从 97 MB 涨到 140 MB） |
| 从零安装 26.3（5223 个文件） | 官方源 17 秒，BMCLAPI 121 秒；重跑 0.25 秒；中途打断后重跑 10 秒完成，不留残留 |
| 下载器内存（本机回环，5000 个小文件 + 64 MB 大文件） | 内存分配从 1927 MB 降到约 50 MB，完整 GC 从约 28 次降到 0-1 次；总耗时基本没变（主要花在建文件上） |
| 单元测试 / 界面自检 | 见 `src/Amber.Core.Tests`；界面自检用环境变量 `AMBER_SELFTEST=<报告目录>` 运行 |

没有实测的部分：真实微软账号和皮肤站账号登录（只测了模拟响应）、CurseForge 官方接口（没有 Key，用第三方兼容接口测的）、手动点击文件选择器、真实镜像返回 429（限流逻辑只用本机模拟服务器测过）。

## 使用（图形界面）

双击 `发布\Amber.exe`。重新打包：

```
cd src
dotnet publish Amber.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:DebugType=none -o ..\发布
```

## 使用（命令行）

```
cd src
dotnet run --project Amber.Cli -- list-remote            # 可安装的正式版
dotnet run --project Amber.Cli -- install 1.21.4         # 安装到 %APPDATA%\.minecraft
dotnet run --project Amber.Cli -- launch 1.21.4 玩家名 --mem 4096 --isolate
# 可选：--dir <游戏目录>  --source official|mirror  launch 时加 --dry-run 只打印参数
dotnet run --project Amber.Cli -- install-loader fabric 1.21.1 --id 我的版本
dotnet run --project Amber.Cli -- search modrinth mod sodium --mc 1.21.1 --loader fabric
dotnet run --project Amber.Cli -- add modrinth AANobbMI 我的版本 --isolate
dotnet run --project Amber.Cli -- import 某整合包.mrpack 新版本名
dotnet run --project Amber.Cli -- export 我的版本 输出.mrpack --isolate
# 还有 loaders、java-install、mods、check-updates、update-mods、worlds、ping、junk、analyze；launch 可加 --smoke 秒数 做启动测试
dotnet test                                              # 运行单元测试
```
