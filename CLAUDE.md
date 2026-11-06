# Amber launcher: notes for coding agents

Amber is an original cross-platform Minecraft Java launcher for Windows, macOS and Linux, built with .NET 10 and Avalonia 12 (MVVM via CommunityToolkit.Mvvm).
The UI text, comments and docs are written in Simplified Chinese. Keep new ones in the same language and tone.

## Layout

- `src/Amber.Core`: everything that isn't UI. That covers game launch (`Launch/`), version and loader install (`Install/`, `Game/`), mods and resources (`Content/`), accounts (`Auth/`), Java detection, the downloader with mirrors (`Net/`) and EasyTier-based multiplayer (`Multiplayer/`).
- `src/Amber.App`: the Avalonia desktop app. `ViewModels/` holds one view model per page and `Views/` the XAML. `Services/LauncherService.cs` wires the Core services together. `Views/SelfTest.cs` is the end-to-end GUI self-check.
- `src/Amber.Cli`: a developer CLI over the same Core. It is used for measurements and for scripted tests (`mods`, `launch`, `prewarm`, `add`, …).
- `src/Amber.Core.Tests`: xUnit tests.
- `tools/`: helper scripts (GUI self-check runner, multiplayer test).

## Build and test

```bash
cd src
dotnet build Amber.slnx -c Release
dotnet test Amber.Core.Tests -c Release
```

Release build, a single self-contained exe:

```bash
dotnet publish Amber.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:DebugType=none -o ../publish
```

Use `osx-arm64`, `osx-x64`, `linux-x64` or `linux-arm64` for the other release targets. The CI workflow publishes every supported RID on its native runner.

GUI self-check: it drives the real window through install, launch, mods, multiplayer and import, then writes `report.txt` to the report directory you pass.

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-selftest.ps1 -Report <dir>
```

只验证资源页首次加载和来源切换时，先设置 `AMBER_SELFTEST_SCOPE=content`；同样输出报告和截图，跳过游戏安装与启动。
只验证联机（邀请、敲门、房间、直连）时设置 `AMBER_SELFTEST_SCOPE=multiplayer`。

Other developer switches are environment variables:

| Variable | Effect |
|---|---|
| `AMBER_SNAPSHOT_DIR` | Render every page to PNG |
| `AMBER_STARTUP_LOG` | Write startup timing |
| `AMBER_ET_VERBOSE` | Verbose EasyTier output |

## Rules

- Write original code only. Don't copy or port code from other launchers (HMCL, PCL and the like) or from decompiled binaries. Public protocol and format documentation is fine.
- No telemetry, analytics or remote kill switches. Network calls are limited to:
  - user-initiated downloads
  - Microsoft login
  - the Modrinth and CurseForge APIs
  - the update check
  - multiplayer
- Tests and experiments must use a separate test game directory. Never write to a user's real `%APPDATA%\.minecraft`. Old Forge (≤1.5.2) writes to the APPDATA home, which is why `GameLauncher` redirects it.
- Use established cryptographic primitives and libraries. Don't invent new ones.
- Keep personal data out of committed files: no emails, usernames, tokens, or machine-specific absolute paths. Machine-specific notes go in `CLAUDE.local.md`, which is gitignored.
- Keep `Amber.Core` free of UI dependencies. Platform-specific code goes behind `OperatingSystem.IsWindows()` or into a Windows-only type.
- New behaviour gets unit tests, and a self-check step when it is UI-visible.
