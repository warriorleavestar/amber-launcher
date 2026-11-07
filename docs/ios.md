# Jay iOS 实验版

iOS 版与 Android 版共用 `Amber.Mobile` 界面和 Jay Server 客户端逻辑，平台入口放在独立的 `Amber.iOS` 工程中。它支持 iOS/iPadOS 15 或更高版本，是 companion，不是能直接运行 Minecraft Java 的完整启动器。

## 现在能做什么

- 在应用私有目录生成并加密保存设备身份。
- 解析 Jay Server 的一次性邀请码，登记 iPhone 或 iPad，并保存访问密钥。
- 为已保存的 Jay Server 建立 `127.0.0.1:<端口>` 本机网关，供同一设备上的兼容客户端访问。
- 跟随系统深浅色和语言，支持简中、繁中、英文和日文。

## 现在不能做什么

- 不能在 iOS 上下载、安装或启动 Minecraft Java。iOS 不允许像桌面系统那样任意启动外部 JVM 进程，也没有为本项目准备的 iOS LWJGL 运行层。
- 不能创建普通 Jay 房间；当前没有集成 iOS 版 EasyTier。
- iOS 会暂停后台应用，本机网关只应在 Jay 保持前台时使用。
- GitHub Actions 只构建无需开发者证书的 Apple silicon 模拟器 `.app`。它不能安装到实体 iPhone，也不是 App Store 包。

## 模拟器构建

需要 macOS、兼容的 Xcode、.NET 10 和 iOS workload：

```sh
dotnet workload install ios
dotnet build src/Amber.iOS/Amber.iOS.csproj -c Release \
  -r iossimulator-arm64 -p:CodesignKey=-
```

模拟器产物位于 `src/Amber.iOS/bin/Release/net10.0-ios/iossimulator-arm64/`。

## 真机与 IPA

真机包必须由项目维护者自己的 Apple Developer 身份签名。准备好 Distribution certificate、provisioning profile 和 App Store Connect 记录后，可按 [Avalonia iOS 发布文档](https://docs.avaloniaui.net/docs/deployment/ios) 发布：

```sh
dotnet publish src/Amber.iOS/Amber.iOS.csproj -c Release \
  -r ios-arm64 -p:ArchiveOnBuild=true \
  -p:CodesignKey="Apple Distribution: ..." \
  -p:CodesignProvision="Jay Distribution"
```

证书、私钥和 profile 不应提交到仓库；如果以后在 CI 签名，应只通过 GitHub Secrets 临时导入。

## 下一步

1. 在 iOS Simulator 中启动并检查界面、安全区、登记流程和本机端口。
2. 在实体 iPhone/iPad 上确认另一应用能否访问 Jay 的回环端口。
3. 根据真机测试结果决定是否实现符合 Apple 后台执行规则的连接方式。
4. 准备 1024×1024、无透明通道的 App Store 图标和正式签名配置。
