using System.Diagnostics;
using System.Security.Cryptography;
using Amber.Core.Launch;

namespace Amber.Core.Content;

public sealed record WarmupResult(bool Done, string Detail);

/// <summary>
/// Sinytra Connector 第一次加载 Fabric 模组时要先转换（重映射）它，结果放在实例的 .cache/connector/ 里，
/// 旁边的 .input 文件记着“Connector 版本,端,原 jar 的 SHA-256”；之后只要原 jar 没变就直接用缓存。
/// Connector 只能在游戏的模组加载过程里运行，没有单独的转换工具，所以“预先处理”就是：
/// 启动一次游戏，等 Connector 转换完、游戏开始加载自己的东西时就结束它，下次正式启动就快了。
/// </summary>
public static class ConnectorWarmup
{
    public static string CacheDir(string workDir) => Path.Combine(workDir, ".cache", "connector");

    /// <summary>会经 Connector 运行、但还没转换过（或者换了新版本）的 Fabric 模组。</summary>
    public static List<LocalMod> Pending(string workDir, IEnumerable<LocalMod> mods, string? loader)
    {
        var candidates = mods.Where(m => m.Enabled && m.Requirements is { } r && ModCompat.RunsViaConnector(r, loader)).ToList();
        // 还没有缓存目录（从没经 Connector 启动过）时全部都要处理，不用算哈希。
        if (candidates.Count == 0 || !Directory.Exists(CacheDir(workDir))) return candidates;
        // 换了 Connector 版本后它会重新转换全部模组，旧版本留下的缓存不算。
        var connectorVersion = mods.FirstOrDefault(m => m.Enabled && m.Requirements?.Ids.Contains("connector", StringComparer.OrdinalIgnoreCase) == true)?.Version;
        var cached = CachedHashes(workDir, connectorVersion);
        return candidates.Where(m => !cached.Contains(Sha256(m.Path))).ToList();
    }

    /// <summary>.input 文件的内容是“Connector 版本,端,原 jar 的 SHA-256”。connectorVersion 为 null 时不比版本。</summary>
    internal static HashSet<string> CachedHashes(string workDir, string? connectorVersion)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = CacheDir(workDir);
        if (!Directory.Exists(dir)) return set;
        foreach (var file in Directory.EnumerateFiles(dir, "*.input"))
        {
            try
            {
                var parts = File.ReadAllText(file).Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length < 3 || parts[^1].Length != 64) continue;
                if (connectorVersion is not null && !parts[0].Equals(connectorVersion, StringComparison.OrdinalIgnoreCase)) continue;
                set.Add(parts[^1]);
            }
            catch (IOException) { }
        }
        return set;
    }

    private sealed record HashEntry(long Size, DateTime Modified, string Sha256);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashEntry> Hashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按大小和修改时间缓存，勾选启用 / 禁用时不用把几百 MB 的模组重新算一遍。</summary>
    private static string Sha256(string path)
    {
        var info = new FileInfo(path);
        if (Hashes.TryGetValue(path, out var hit) && hit.Size == info.Length && hit.Modified == info.LastWriteTimeUtc) return hit.Sha256;
        using var stream = File.OpenRead(path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        Hashes[path] = new HashEntry(info.Length, info.LastWriteTimeUtc, sha);
        return sha;
    }

    /// <summary>Connector 处理完、游戏开始自己的初始化时会打这两种日志之一。</summary>
    internal static bool IsPastModDiscovery(string line) =>
        line.Contains("transformable", StringComparison.Ordinal) && line.Contains("made module", StringComparison.Ordinal)
        || line.Contains("Reloading ResourceManager", StringComparison.Ordinal);

    /// <summary>启动一次游戏做预处理，转换完就结束游戏。progress 收到“已转换 N 个”之类的进度。</summary>
    public static async Task<WarmupResult> RunAsync(GameLauncher launcher, LaunchOptions options, IProgress<string>? progress, TimeSpan timeout, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mapped = 0;
        var tail = new Queue<string>();
        var process = launcher.Launch(options, line =>
        {
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 20) tail.Dequeue();
            }
            if (line.Contains("_mapped_", StringComparison.Ordinal) && line.Contains("Found mod file", StringComparison.Ordinal))
                progress?.Report($"Connector 已处理 {Interlocked.Increment(ref mapped)} 个模组……");
            if (IsPastModDiscovery(line)) finished.TrySetResult(true);
        });
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            var exited = process.WaitForExitAsync(limit.Token);
            var first = await Task.WhenAny(finished.Task, exited);
            if (first == finished.Task)
                return new WarmupResult(true, $"处理完了，Connector 转换了 {mapped} 个模组，用时 {watch.Elapsed.TotalSeconds:F0} 秒");
            await exited;
            string last;
            lock (tail) last = string.Join(" / ", tail.TakeLast(3));
            return new WarmupResult(false, "游戏在处理完之前就退出了：" + last);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WarmupResult(false, $"{timeout.TotalMinutes:F0} 分钟内没有处理完");
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch (InvalidOperationException) { }
        }
    }
}
