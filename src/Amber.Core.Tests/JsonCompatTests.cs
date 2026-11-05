using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Amber.App.Services;
using Amber.Core.Auth;
using Amber.Core.Game;
using Amber.Core.Launch;
using Amber.Core.Multiplayer;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>
/// 改用编译时生成的 JSON 代码后，读写的格式必须和 v0.5.5（运行时反射）完全一样：老文件照样能读，新写的文件老版本也能读。
/// 每个用例都分两路：只用反射（等于 v0.5.5），只用生成的代码（等于 Native AOT 版，没有反射可退）。
/// 两路读同一份 JSON，再各自写回去，结果必须一字不差。
/// </summary>
public class JsonCompatTests
{
    private static JsonSerializerOptions Reflection(JsonSerializerOptions options) => new(options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private static JsonSerializerOptions GeneratedOnly(JsonSerializerOptions options, IJsonTypeInfoResolver generated) => new(options) { TypeInfoResolver = generated };

    private static JsonSerializerOptions Field(Type type, string name) =>
        (JsonSerializerOptions)type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    /// <summary>两路读同一份 JSON、各自写回，返回写出来的内容（两路必须相同）。</summary>
    private static string AssertSame<T>(string json, JsonSerializerOptions options, IJsonTypeInfoResolver generated)
    {
        var reflection = Reflection(options);
        var aot = GeneratedOnly(options, generated);
        var a = JsonSerializer.Deserialize<T>(json, reflection);
        var b = JsonSerializer.Deserialize<T>(json, aot);
        var written = JsonSerializer.Serialize(a, reflection);
        Assert.Equal(written, JsonSerializer.Serialize(b, aot));
        // 新版本写出来的，老版本（反射）也能原样读回。
        Assert.Equal(written, JsonSerializer.Serialize(JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(b, aot), reflection), reflection));
        return written;
    }

    private static IJsonTypeInfoResolver Core => AmberJsonContext.Default;

    [Fact]
    public void InstanceSettings_SameFormat()
    {
        var options = Field(typeof(InstanceSettings), "Options");
        // 未知字段、null、中文和反斜杠（UnsafeRelaxedJsonEscaping）、大小写不同的属性名（区分大小写，忽略）。
        var written = AssertSame<InstanceSettings>("""
            {"Isolate":true,"JavaPath":"D:\\游戏\\java\\bin\\javaw.exe","AutoMemory":null,"MaxMemoryMb":6144,"JvmArgs":"-XX:+UseG1GC \"-Dx=1\"",
             "WindowWidth":1280,"Fullscreen":false,"maxmemorymb":1,"SomethingNew":{"a":[1,2]},"JoinServer":"mc.example.org:25565"}
            """, options, Core);
        Assert.Contains("\"JavaPath\": \"D:\\\\游戏\\\\java\\\\bin\\\\javaw.exe\"", written);
        Assert.DoesNotContain("AutoMemory", written);   // null 不写
        Assert.Contains("\"MaxMemoryMb\": 6144", written);

        // 真的走一遍保存再读取。
        var dir = new GameDirectory(Path.Combine(Path.GetTempPath(), "amber-json-" + Guid.NewGuid().ToString("N")));
        try
        {
            new InstanceSettings { Isolate = true, MaxMemoryMb = 2048, GameArgs = "--demo" }.Save(dir, "x");
            var loaded = InstanceSettings.Load(dir, "x");
            Assert.Equal((true, 2048, "--demo"), (loaded.Isolate, loaded.MaxMemoryMb, loaded.GameArgs));
        }
        finally
        {
            Directory.Delete(dir.Root, true);
        }
    }

    [Fact]
    public void Accounts_SameFormat_EnumsAsStringsOrNumbers()
    {
        var options = Field(typeof(AccountStore), "Options");
        var written = AssertSame<AccountStore.FileModel>("""
            {"SelectedId":"a1","Accounts":[
              {"Id":"a1","Kind":"Microsoft","Name":"Steve","Uuid":"069a79f4-44e9-4726-a5be-fca90e38aaf5","ProtectedAccessToken":"AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA","ProtectedRefreshToken":null,
               "AccessTokenExpires":"2026-11-04T08:00:00+13:00","Xuid":"2535","SkinUrl":null,"SlimSkin":true},
              {"Id":"b2","Kind":2,"Name":"Alex","Uuid":"00000000-0000-0000-0000-000000000001","ServerUrl":"https://skin.example.org/api/yggdrasil","ServerName":"示例皮肤站","ClientToken":"ct"},
              {"Id":"c3","Kind":"offline","Name":"Player","Uuid":"00000000-0000-0000-0000-000000000002","Extra":true}]}
            """, options, Core);
        Assert.Contains("\"Kind\": \"Microsoft\"", written);
        Assert.Contains("\"Kind\": \"External\"", written);   // 老文件里的数字也认，写回成名字
        Assert.Contains("\"Kind\": \"Offline\"", written);    // 名字不区分大小写
        Assert.DoesNotContain("AccessToken\"", written.Replace("ProtectedAccessToken\"", ""));   // 明文令牌不写盘
    }

    [Fact]
    public void ChatWireFormat_Unchanged()
    {
        // 聊天消息是两台电脑之间传的，新旧版本要能互通：字段名和枚举（数字）都不能变。
        var body = new ChatCipher.Body(ChatKind.Join, "史蒂夫", "你好 \"x\" <b>", 1_762_000_000_000);
        var envelope = new ChatCipher.Envelope("abcdef0123456789", "AAAAAAAAAAAAAAAA", "Zm9v");
        Assert.Equal(JsonSerializer.Serialize(body, Reflection(new JsonSerializerOptions())), JsonSerializer.Serialize(body, AmberJsonContext.Default.Body));
        Assert.Equal(JsonSerializer.Serialize(envelope, Reflection(new JsonSerializerOptions())), JsonSerializer.Serialize(envelope, AmberJsonContext.Default.Envelope));
        Assert.Contains("\"k\":1", JsonSerializer.Serialize(body, AmberJsonContext.Default.Body));

        // 和 v0.5.5 的发送方互通：用反射加密的消息，新版本解得开，反过来也一样。
        var room = RoomCode.Create(25565);
        var cipher = new ChatCipher(room);
        var message = new ChatMessage(ChatKind.Message, "abcdef0123456789", "Alex", "同一句话", DateTimeOffset.FromUnixTimeMilliseconds(1_762_000_000_000));
        var opened = cipher.Open(cipher.Seal(message));
        Assert.Equal(message, opened);
    }

    [Fact]
    public void ChunkStateAndArgs_SameFormat()
    {
        AssertSame<Downloader.ChunkState>("""{"Total":100,"Chunks":[{"Start":0,"End":49,"Done":true},{"Start":50,"End":99,"Done":false}],"x":0}""", new JsonSerializerOptions(), Core);
        AssertSame<List<string>>("""["--network-name","amber-abc","--ipv4","10.233.144.1","中文"]""", new JsonSerializerOptions(), Core);
    }

    [Fact]
    public void MojangJson_SameParse_CaseInsensitiveCommentsTrailingCommas()
    {
        var written = AssertSame<VersionJson>("""
            {
              // 有的整合包工具会写注释
              "ID": "1.21.1-custom", "inheritsFrom": "1.21.1", "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [ { "name": "net.fabricmc:fabric-loader:0.16.9", "url": "https://maven.fabricmc.net/",
                "rules": [ { "action": "allow", "os": { "name": "windows" } } ], }, ],
              "arguments": { "game": [ "--demo", { "rules": [ { "action": "allow", "features": { "is_demo_user": true } } ], "value": "--demo" } ], "jvm": [] },
              "unknownField": 1
            }
            """, Json.Options, Core);
        Assert.Contains("1.21.1-custom", written);
        Assert.Contains("fabric-loader:0.16.9", written);
        AssertSame<VersionManifest>("""{"latest":{"release":"1.21.1","snapshot":"24w45a"},"versions":[{"id":"1.21.1","type":"release","url":"https://piston-meta.mojang.com/v1/packages/x/1.21.1.json","time":"2024-08-08T12:24:45+00:00","releaseTime":"2024-08-08T12:24:45+00:00","sha1":"abc","complianceLevel":1}]}""", Json.Options, Core);
        AssertSame<AssetIndex>("""{"objects":{"icons/icon_16x16.png":{"hash":"bdf48ef6b5d0d23bbb02e17d04865216179f510a","size":3665}},"map_to_resources":false}""", Json.Options, Core);
    }

    /// <summary>
    /// 本机真实数据（不进仓库、CI 上没有就跳过）：测试游戏目录里所有版本 JSON 和资源索引、本机的 settings.json 和 accounts.json。
    /// 只比较两路结果是否相同，不输出内容；账号文件里的令牌是 DPAPI 加密串，这里不解密。
    /// </summary>
    [Fact]
    public void RealLocalFiles_SameParse()
    {
        var checkedFiles = 0;
        var game = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "测试游戏目录"));
        if (Directory.Exists(Path.Combine(game, "versions")))
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.Combine(game, "versions")))
            {
                var json = Path.Combine(dir, Path.GetFileName(dir) + ".json");
                if (File.Exists(json)) { AssertSame<VersionJson>(File.ReadAllText(json), Json.Options, Core); checkedFiles++; }
                var settings = Path.Combine(dir, InstanceSettings.FileName);
                if (File.Exists(settings)) { AssertSame<InstanceSettings>(File.ReadAllText(settings), Field(typeof(InstanceSettings), "Options"), Core); checkedFiles++; }
            }
            var indexes = Path.Combine(game, "assets", "indexes");
            if (Directory.Exists(indexes))
                foreach (var index in Directory.EnumerateFiles(indexes, "*.json")) { AssertSame<AssetIndex>(File.ReadAllText(index), Json.Options, Core); checkedFiles++; }
        }
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Amber");
        if (File.Exists(Path.Combine(data, "settings.json")))
        {
            AssertSame<AppSettings>(File.ReadAllText(Path.Combine(data, "settings.json")), AppSettingsJson.Options, AppSettingsJson.Generated);
            checkedFiles++;
        }
        if (File.Exists(Path.Combine(data, "accounts.json")))
        {
            AssertSame<AccountStore.FileModel>(File.ReadAllText(Path.Combine(data, "accounts.json")), Field(typeof(AccountStore), "Options"), Core);
            checkedFiles++;
        }
        // 只是让输出里看得到比较了多少个文件。
        Console.WriteLine($"RealLocalFiles_SameParse: {checkedFiles} files");
    }

    [Fact]
    public void AppSettings_SameFormat()
    {
        // 读的时候 v0.5.5 用默认 options（区分大小写），写的时候用缩进 + 不转义中文。
        var json = """{"GameDirectory":"D:\\游戏\\.minecraft","DownloadSource":"bmclapi","DownloadConcurrency":16,"AutoMemory":false,"MaxMemoryMb":8192,"WindowWidth":null,"Theme":"dark","future":1}""";
        var reflectionRead = JsonSerializer.Deserialize<AppSettings>(json, Reflection(new JsonSerializerOptions()));
        var generatedRead = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings);   // AppSettings.Load 用的就是这个
        Assert.Equal(JsonSerializer.Serialize(reflectionRead, Reflection(AppSettingsJson.Options)), JsonSerializer.Serialize(generatedRead, GeneratedOnly(AppSettingsJson.Options, AppSettingsJson.Generated)));
        AssertSame<AppSettings>(json, AppSettingsJson.Options, AppSettingsJson.Generated);
    }

    /// <summary>界面改版新加的设置：编译时生成的读写和反射一致；旧文件（没有这些字段）读出默认值。</summary>
    [Fact]
    public void AppSettings_RedesignFields()
    {
        var json = """
            {"Theme":"dark","RecentInstances":["1.21.1 Fabric","fabric-loader-0.20.1-1.21.1"],"SidebarCollapsed":true,"ReduceMotion":true,
             "AfterLaunch":"tray","CheckUpdatesOnStart":false,"TrashRetentionDays":3,
             "Instances":{"fabric-loader-0.20.1-1.21.1":{"DisplayName":"我的生存服","State":"incomplete"},"1.20.1":{"DisplayName":null,"State":null}}}
            """;
        AssertSame<AppSettings>(json, AppSettingsJson.Options, AppSettingsJson.Generated);
        var read = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings)!;
        Assert.Equal(["1.21.1 Fabric", "fabric-loader-0.20.1-1.21.1"], read.RecentInstances);
        Assert.True(read.SidebarCollapsed && read.ReduceMotion);
        Assert.Equal("tray", read.AfterLaunch);
        Assert.False(read.CheckUpdatesOnStart);
        Assert.Equal(3, read.TrashRetentionDays);
        Assert.Equal("我的生存服", read.Instances!["fabric-loader-0.20.1-1.21.1"].DisplayName);
        Assert.Equal("incomplete", read.Instances["fabric-loader-0.20.1-1.21.1"].State);

        // v0.5.x 写的文件：新字段取默认值。
        var old = JsonSerializer.Deserialize("""{"Theme":"light","Language":"en"}""", AppJsonContext.Default.AppSettings)!;
        Assert.Null(old.RecentInstances);
        Assert.False(old.SidebarCollapsed || old.ReduceMotion);
        Assert.Equal("minimize", old.AfterLaunch);
        Assert.True(old.CheckUpdatesOnStart);
        Assert.Equal(7, old.TrashRetentionDays);
        Assert.Null(old.Instances);
    }
}
