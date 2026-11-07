using Amber.Core;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amber.Core.Auth;
using Amber.Core.Game;

namespace Amber.Core.Launch;

/// <summary>全局和单个版本共用的启动设置。版本设置里为 null 的项沿用全局值。</summary>
public sealed class InstanceSettings
{
    public bool? Isolate { get; set; }
    public string? JavaPath { get; set; }
    public bool? AutoMemory { get; set; }
    public int? MaxMemoryMb { get; set; }
    public string? JvmArgs { get; set; }
    public string? GameArgs { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool? Fullscreen { get; set; }
    public string? PreLaunchCommand { get; set; }
    public string? PostExitCommand { get; set; }
    public string? JoinServer { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = AmberJsonContext.Resolver,
    };

    public const string FileName = "amber-instance.json";

    public static InstanceSettings Load(GameDirectory dir, string id)
    {
        var path = Path.Combine(dir.VersionDir(id), FileName);
        if (!File.Exists(path)) return new InstanceSettings();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), Options.Info<InstanceSettings>()) ?? new InstanceSettings();
        }
        catch (JsonException)
        {
            return new InstanceSettings();
        }
    }

    public void Save(GameDirectory dir, string id)
    {
        Directory.CreateDirectory(dir.VersionDir(id));
        File.WriteAllText(Path.Combine(dir.VersionDir(id), FileName), JsonSerializer.Serialize(this, Options.Info<InstanceSettings>()));
    }

    /// <summary>
    /// 按这份（已合并全局的）设置生成启动参数：内存（含自动内存）、工作目录（版本隔离）、JVM 和游戏参数、窗口。
    /// 启动器的正式启动、Connector 预处理、导出启动脚本和命令行都用它，各处的设置不会对不上。
    /// </summary>
    public LaunchOptions ToLaunchOptions(GameDirectory dir, string id, string javaPath, GameSession session, IReadOnlyList<string> sessionJvmArgs)
    {
        var workDir = Isolate == true ? dir.VersionDir(id) : dir.Root;
        var memory = AutoMemory == true
            ? MemoryPlanner.AutoMemoryMb(new Amber.Core.Content.ModManager(Path.Combine(workDir, "mods")).CountEnabled(), MemoryPlanner.FreePhysicalMb())
            : MaxMemoryMb ?? 4096;
        return new LaunchOptions
        {
            VersionId = id,
            JavaPath = javaPath,
            Session = session,
            ExtraJvmArgs = [.. sessionJvmArgs, .. ArgumentSplitter.Split(JvmArgs)],
            ExtraGameArgs = ArgumentSplitter.Split(GameArgs),
            MaxMemoryMb = memory,
            WorkingDirectory = Isolate == true ? workDir : null,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            Fullscreen = Fullscreen == true,
            LauncherVersion = UpdateChecker.CurrentVersion,
        };
    }

    /// <summary>this 是版本设置，fallback 是全局设置。</summary>
    public InstanceSettings Over(InstanceSettings fallback) => new()
    {
        Isolate = Isolate ?? fallback.Isolate,
        JavaPath = Empty(JavaPath) ?? fallback.JavaPath,
        AutoMemory = AutoMemory ?? fallback.AutoMemory,
        MaxMemoryMb = MaxMemoryMb ?? fallback.MaxMemoryMb,
        JvmArgs = Empty(JvmArgs) ?? fallback.JvmArgs,
        GameArgs = Empty(GameArgs) ?? fallback.GameArgs,
        WindowWidth = WindowWidth ?? fallback.WindowWidth,
        WindowHeight = WindowHeight ?? fallback.WindowHeight,
        Fullscreen = Fullscreen ?? fallback.Fullscreen,
        PreLaunchCommand = Empty(PreLaunchCommand) ?? fallback.PreLaunchCommand,
        PostExitCommand = Empty(PostExitCommand) ?? fallback.PostExitCommand,
        JoinServer = Empty(JoinServer) ?? fallback.JoinServer,
    };

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

public static class MemoryPlanner
{
    public static long FreePhysicalMb()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status)) return (long)(status.AvailPhys / 1024 / 1024);
        }
        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (ProcMemInfoMb(File.ReadAllText("/proc/meminfo"), "MemAvailable") is { } available) return available;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (OperatingSystem.IsMacOS() && MacAvailableMemoryMb() is { } macAvailable) return macAvailable;
        var info = GC.GetGCMemoryInfo();
        return (info.TotalAvailableMemoryBytes - info.MemoryLoadBytes) / 1024 / 1024;
    }

    public static long TotalPhysicalMb()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (ProcMemInfoMb(File.ReadAllText("/proc/meminfo"), "MemTotal") is { } total) return total;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (OperatingSystem.IsMacOS() && TrySysctl("hw.memsize", out var bytes)) return (long)(bytes / 1024 / 1024);
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
    }

    internal static long? ProcMemInfoMb(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0 || !line[..colon].Equals(key, StringComparison.Ordinal)) continue;
            var value = line[(colon + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            return long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var kb)
                ? kb / 1024
                : null;
        }
        return null;
    }

    private static long? MacAvailableMemoryMb()
    {
        if (!TrySysctl("hw.pagesize", out var pageSize) ||
            !TrySysctl("vm.page_free_count", out var free) ||
            !TrySysctl("vm.page_inactive_count", out var inactive)) return null;
        TrySysctl("vm.page_purgeable_count", out var purgeable);
        return checked((long)((free + inactive + purgeable) * pageSize / 1024 / 1024));
    }

    private static bool TrySysctl(string name, out ulong value)
    {
        value = 0;
        if (!OperatingSystem.IsMacOS()) return false;
        nuint length = sizeof(ulong);
        return SysctlByName(name, out value, ref length, IntPtr.Zero, 0) == 0 && length > 0;
    }

    /// <summary>
    /// 自动内存：原版 2.5 GB、加模组后按数量加。留出系统余量，不超过可用内存。
    /// 算出来小于 1 GB 时仍给 1 GB，游戏再少跑不起来。
    /// </summary>
    public static int AutoMemoryMb(int modCount, long freeMb)
    {
        var wanted = modCount switch
        {
            0 => 2560,
            < 30 => 3072,
            < 100 => 4096,
            < 200 => 6144,
            _ => 8192,
        };
        var usable = freeMb - 512;
        return (int)Math.Max(1024, Math.Min(wanted, usable));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, out ulong oldValue, ref nuint oldLength, IntPtr newValue, nuint newLength);
}

public static class ArgumentSplitter
{
    /// <summary>按空格拆参数，双引号里的空格保留（JVM 参数输入框用）。</summary>
    public static List<string> Split(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var current = new StringBuilder();
        var quoted = false;
        var has = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                has = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (has) result.Add(current.ToString());
                current.Clear();
                has = false;
            }
            else
            {
                current.Append(c);
                has = true;
            }
        }
        if (has) result.Add(current.ToString());
        return result;
    }
}
