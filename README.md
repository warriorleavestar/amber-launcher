# Amber

原创代码的 Minecraft 启动器，C# / .NET 10。没有云控、没有强制更新、没有遥测。

## 目录

| 路径 | 内容 |
|---|---|
| `src/` | 解决方案 `Amber.slnx`：`Amber.Core` 核心库、`Amber.Cli` 命令行、`Amber.Core.Tests` 测试 |
| `测试游戏目录/` | 开发测试用的 .minecraft，可随时删除 |

## 当前进度（2026-10-28）

M0 核心可用 ✅：
- 版本清单、原版安装与补全（客户端、库、natives、资源、log4j 配置），SHA1 校验，失败自动换源
- 下载源：官方 / BMCLAPI，按优先级回退（中国时区默认镜像优先，只影响默认值）
- inheritsFrom 合并（为 Fabric / Forge 等加载器版本做好准备）
- 本机 Java 扫描与按版本自动选择
- 离线账号（UUID 与服务器离线模式一致）
- 启动参数拼装（新旧两种格式）、natives 解压、旧版资源平铺、版本隔离

实测：MC 26.3 从零安装 5223 个文件 / 586 MB 用时 18 秒（BMCLAPI），离线启动成功。

## 使用（命令行）

```
cd src
dotnet run --project Amber.Cli -- list-remote            # 可安装的正式版
dotnet run --project Amber.Cli -- install 1.21.4         # 安装到 %APPDATA%\.minecraft
dotnet run --project Amber.Cli -- launch 1.21.4 玩家名 --mem 4096 --isolate
# 可选：--dir <游戏目录>  --source official|mirror  launch 时加 --dry-run 只打印参数
dotnet test                                              # 运行单元测试
```

## 下一步

M1 Avalonia 图形界面 → M2 Fabric/Quilt/NeoForge/Forge → M3 微软登录（需要你自己在 Azure 注册应用拿 Client ID）→ M4 Modrinth/CurseForge 与整合包。详见设计方案。
