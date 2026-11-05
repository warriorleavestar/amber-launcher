using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Amber.Core.Auth;
using Amber.Core.Game;
using Amber.Core.Launch;
using Amber.Core.Multiplayer;
using Amber.Core.Net;

namespace Amber.Core;

/// <summary>
/// 编译时生成的 JSON 读写代码（不靠运行时反射），Native AOT 编译的版本也能用。
/// 要序列化的新类型加到这里；普通（非 AOT）版本里漏加的类型会退回反射，行为不变。
/// </summary>
[JsonSerializable(typeof(VersionJson))]
[JsonSerializable(typeof(List<Library>))]
[JsonSerializable(typeof(List<Rule>))]
[JsonSerializable(typeof(AssetIndex))]
[JsonSerializable(typeof(VersionManifest))]
[JsonSerializable(typeof(InstanceSettings))]
[JsonSerializable(typeof(AccountStore.FileModel))]
[JsonSerializable(typeof(ChatCipher.Envelope))]
[JsonSerializable(typeof(ChatCipher.Body))]
[JsonSerializable(typeof(Downloader.ChunkState))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Amber.Core.Multiplayer.GuardCommand))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<string[]>))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonNode))]
internal sealed partial class AmberJsonContext : JsonSerializerContext
{
    /// <summary>给已有的 JsonSerializerOptions 用：先查生成的代码；允许反射时（普通版本）没生成的类型再用反射。</summary>
    public static IJsonTypeInfoResolver Resolver => AmberJson.WithReflectionFallback(Default);
}

public static class AmberJson
{
    /// <summary>JSON 请求体（application/json; charset=utf-8）。</summary>
    public static System.Net.Http.Json.JsonContent Body(JsonObject body) => System.Net.Http.Json.JsonContent.Create(body, AmberJsonContext.Default.JsonObject);

    /// <summary>从 options 取某个类型的读写信息，代替带 options 的泛型 Serialize / Deserialize。</summary>
    public static JsonTypeInfo<T> Info<T>(this JsonSerializerOptions options) => (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));

    /// <summary>
    /// 普通版本里在生成的代码之后接上反射，保证没登记的类型照样能读写；
    /// AOT 版本里反射默认关掉，这个分支会被编译器整个去掉。
    /// </summary>
    public static IJsonTypeInfoResolver WithReflectionFallback(IJsonTypeInfoResolver generated) =>
        JsonSerializer.IsReflectionEnabledByDefault
#pragma warning disable IL2026, IL3050 // 只在允许反射时走到
            ? JsonTypeInfoResolver.Combine(generated, new DefaultJsonTypeInfoResolver())
#pragma warning restore IL2026, IL3050
            : generated;
}
