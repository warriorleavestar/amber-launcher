# Amber

Amber 是一个原创代码的 Minecraft Java 版启动器，使用 C# / .NET 10 和 Avalonia 编写，目前只测试过 Windows。

## 功能

- **游戏版本和加载器**：原版、Fabric、Quilt、Forge、NeoForge、OptiFine（Forge 1.5.2 及更早版本除外）
- **账号**：微软设备码登录、authlib-injector 外置登录、离线账号，支持多账号和皮肤预览；令牌用 DPAPI 加密，不保存密码
- **内容下载**：从 Modrinth 和 CurseForge 搜索、下载模组、资源包、光影和整合包，自动带上必需依赖
- **整合包**：导入和导出 mrpack、CurseForge 格式
- **实例管理**：每个版本独立设置，管理模组、世界、截图和服务器列表
- **崩溃分析**：分析崩溃原因、列出疑似模组、导出错误报告
- **Java**：扫描本机 Java，缺合适版本时自动从 Mojang 下载运行时
- **下载器**：分块并发、断点续传、SHA1 校验，官方源和 BMCLAPI 自动回退

还没做：联机、数据包下载、MultiMC 等其他格式整合包导入。

尚未实测：真实微软账号和皮肤站账号登录（目前只用模拟响应测试）、CurseForge 官方接口。

## 原则

- 全部是原创代码
- 没有遥测，不生成设备识别码
- 没有远程开关，地区只用来选默认下载源
- 没有强制更新

## 构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```
cd src
dotnet build
dotnet test
dotnet publish Amber.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:DebugType=none -o ..\发布
```

命令行版本：`dotnet run --project Amber.Cli -- list-remote`。

## 配置

| 项目 | 在哪里设置 | 环境变量 | 说明 |
|---|---|---|---|
| 微软 Client ID | 「账号」页 | `AMBER_MS_CLIENT_ID` | 可选，留空使用内置的默认值。优先级：界面填写 > 环境变量 > 默认值 |
| CurseForge API Key | 「设置」页 | `AMBER_CF_KEY` | 到 [console.curseforge.com](https://console.curseforge.com) 申请。没有 Key 时 CurseForge 不可用，其他功能不受影响 |

设置保存在 `%APPDATA%\Amber\settings.json`。

## 声明

Amber 是非官方项目，与 Mojang、Microsoft 没有任何关联，也未获得其认可。Minecraft 是 Mojang Synergies AB 的商标。

## 许可证

[MIT](LICENSE)

---

## English

Amber is an original Minecraft: Java Edition launcher written in C# / .NET 10 with Avalonia. It has only been tested on Windows so far.

**Features:** Fabric, Quilt, Forge, NeoForge and OptiFine; Microsoft, authlib-injector and offline accounts; Modrinth and CurseForge content; modpack import and export; crash analysis; automatic Java download.

**Principles:** original code, no telemetry, no remote switches, no forced updates.

**Build:** install the .NET 10 SDK, then run `dotnet build`, `dotnet test` and `dotnet publish` in `src/`.

**Configuration:** the Microsoft Client ID is optional (Accounts page or `AMBER_MS_CLIENT_ID`; a built-in default is used otherwise). A CurseForge API key is set on the Settings page or with `AMBER_CF_KEY`.

**Not live-tested yet:** real Microsoft and skin-site logins, and the official CurseForge API.

Amber is an unofficial project and is not affiliated with or endorsed by Mojang or Microsoft. Licensed under [MIT](LICENSE).
