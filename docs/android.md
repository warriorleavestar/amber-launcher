# Jay Android 实验版

Android 版是一个独立的 Avalonia 移动端工程，不会把桌面端的窗口、进程启动和 EasyTier 代码强行带到手机上。

## 现在能做什么

- 在应用私有目录生成并加密保存设备身份。
- 解析 Jay 服务器的一次性邀请码，登记手机并加密保存访问密钥。
- 给已保存的 Jay 服务器开一个 `127.0.0.1:<端口>` 网关。其他 Android Minecraft Java 启动器如果能访问同一手机的回环端口，可以把这个地址当服务器地址手动连接。
- 跟随系统语言，提供简中、繁中、英文和日文。

## 现在不能做什么

- 不能在 Android 上直接下载、安装或启动 Minecraft Java。桌面版依赖外部 JVM 进程、LWJGL 桌面原生库和窗口系统；Android 需要单独的 JVM/LWJGL 适配层，不是换个目标框架就能运行。
- 不能创建普通 Jay 房间。EasyTier 2.6.4 没有在本项目中集成 Android 可执行文件。
- 网关还没有 Android 前台服务。切到后台后，系统可能暂停或结束进程。
- 当前 CI 产物使用 Android 调试证书签名，只用于测试，不能直接上架 Google Play。

## 本地构建

需要 .NET 10 Android workload、Android SDK 和 JDK 21。可按 Microsoft 提供的 `InstallAndroidDependencies` 目标安装与当前项目匹配的依赖：

```sh
dotnet workload install android
dotnet build src/Amber.Android/Amber.Android.csproj \
  -t:InstallAndroidDependencies -f net10.0-android \
  -p:AndroidSdkDirectory=/absolute/path/android-sdk \
  -p:JavaSdkDirectory=/absolute/path/jdk \
  -p:AcceptAndroidSdkLicenses=true

dotnet publish src/Amber.Android/Amber.Android.csproj -c Release \
  -p:AndroidSdkDirectory=/absolute/path/android-sdk \
  -p:JavaSdkDirectory=/absolute/path/jdk \
  -p:AndroidPackageFormats=apk
```

产物在 `src/Amber.Android/bin/Release/net10.0-android/publish/`。对外发布前要按 [Avalonia Android 发布文档](https://docs.avaloniaui.net/docs/deployment/android) 配置独立密钥库，密码只能放在 GitHub Secrets 中。

## 下一步

1. 用 Android 前台服务承载本机网关，增加常驻通知和明确的停止按钮。
2. 在真机上验证回环端口能否被目标 Android Java 启动器访问。
3. 决定是与现有开源 Android Java 运行时集成，还是只保留为 Jay 服务器伴侣应用。
4. 给正式包配置专用签名密钥，再考虑发布 APK/AAB。
