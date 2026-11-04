using Amber.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 完整局域网模式：EasyTier 建虚拟网卡（wintun），每个人有一个虚拟 IP，任何局域网游戏和程序都能互通。
/// 建网卡要管理员权限，所以只在用户打开这个模式时，通过 Windows 的权限确认（UAC）启动一个小的守护脚本，
/// 由它运行 easytier-core：Amber 退出（包括崩溃）或写入停止标记时，守护脚本结束 easytier-core 后自己退出。
/// 非管理员的 Amber 结束不了管理员进程，所以要这样绕一层。
/// </summary>
public sealed class EasyTierElevatedSession : IEasyTierSession
{
    private readonly string _cliPath;
    private readonly string _stopFile;
    private readonly string _exitedFile;
    private readonly string _logFile;

    public EasyTierOptions Options { get; private set; }

    private EasyTierElevatedSession(EasyTierOptions options, string cliPath, string workDir)
    {
        Options = options;
        _cliPath = cliPath;
        _stopFile = Path.Combine(workDir, "stop");
        _exitedFile = Path.Combine(workDir, "exited");
        _logFile = Path.Combine(workDir, "easytier.log");
    }

    public bool HasExited => File.Exists(_exitedFile);

    public IReadOnlyList<string> RecentOutput
    {
        get
        {
            try
            {
                using var stream = new FileStream(_logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return lines.TakeLast(200).ToList();
            }
            catch (IOException)
            {
                return [];
            }
        }
    }

    public Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default) => EasyTierSession.QueryPeersAsync(_cliPath, Options.RpcPort, ct);

    /// <summary>弹出 UAC 确认。用户拒绝时抛 <see cref="OperationCanceledException"/>。</summary>
    public static EasyTierElevatedSession Start(EasyTierInstaller installer, EasyTierOptions options, string workDir)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("完整局域网模式目前只支持 Windows");
        if (!options.Tun) throw new ArgumentException("完整局域网模式要用 Tun 选项");
        // 每次一个新目录：换出口时旧的守护脚本可能还没退出。
        workDir = Path.Combine(workDir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        var session = new EasyTierElevatedSession(options, installer.CliPath, workDir);

        // 参数写进 JSON，避免在命令行里拼接引号；脚本每次现写，不复用旧文件。
        var argsFile = Path.Combine(workDir, "args.json");
        File.WriteAllText(argsFile, JsonSerializer.Serialize(EasyTierArgs.Build(options), AmberJsonContext.Default.ListString));
        var script = Path.Combine(workDir, "guard.ps1");
        File.WriteAllText(script, GuardScript, new UTF8Encoding(true));

        var arguments = string.Join(' ',
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
            "-File", Quote(script),
            "-Core", Quote(installer.CorePath),
            "-ArgsFile", Quote(argsFile),
            "-ParentId", Environment.ProcessId.ToString(),
            "-StopFile", Quote(session._stopFile),
            "-ExitedFile", Quote(session._exitedFile),
            "-LogFile", Quote(session._logFile));
        var info = new ProcessStartInfo("powershell.exe", arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workDir,
        };
        try
        {
            Process.Start(info)?.Dispose();
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("你在 Windows 的权限确认里选了“否”，没有开启完整局域网模式");
        }
        return session;
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// 换参数重启 easytier-core（换出口、换路径、测试矩阵用），不用再弹权限确认：
    /// 写一个重启文件，守护脚本看到后结束旧进程、按新参数启动，再删掉这个文件。
    /// </summary>
    public void Restart(EasyTierOptions options)
    {
        // 测试矩阵里也会切回默认模式（不建网卡），守护脚本照样能跑。
        var restart = _stopFile + ".restart";
        var temp = restart + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(EasyTierArgs.Build(options), AmberJsonContext.Default.ListString));
        File.Move(temp, restart, true);
        Options = options;
    }

    /// <summary>守护脚本还没处理完重启请求。</summary>
    public bool RestartPending => File.Exists(_stopFile + ".restart");

    /// <summary>写停止标记，守护脚本会在一秒内结束 easytier-core。</summary>
    public void Dispose()
    {
        try
        {
            File.WriteAllText(_stopFile, "stop");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 守护脚本（以管理员身份运行）：启动 easytier-core，把输出写进日志；
    /// 每秒检查一次 Amber 进程还在不在、有没有停止标记，任一满足就结束 easytier-core。
    /// </summary>
    internal const string GuardScript = """
        param([string]$Core, [string]$ArgsFile, [int]$ParentId, [string]$StopFile, [string]$ExitedFile, [string]$LogFile)
        $ErrorActionPreference = "Stop"
        $RestartFile = $StopFile + ".restart"
        function Start-Core([string]$argsPath) {
            $list = Get-Content -Raw -Encoding UTF8 $argsPath | ConvertFrom-Json
            $quoted = $list | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }
            return Start-Process -FilePath $Core -ArgumentList ($quoted -join ' ') -NoNewWindow -PassThru -RedirectStandardOutput $LogFile -RedirectStandardError ($LogFile + ".err")
        }
        try {
            $p = Start-Core $ArgsFile
            while ($true) {
                Start-Sleep -Milliseconds 500
                $parent = Get-Process -Id $ParentId -ErrorAction SilentlyContinue
                if (-not $parent -or (Test-Path $StopFile)) {
                    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                    $p.WaitForExit(5000) | Out-Null
                    break
                }
                if (Test-Path $RestartFile) {
                    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                    $p.WaitForExit(5000) | Out-Null
                    $p = Start-Core $RestartFile
                    Remove-Item $RestartFile -Force
                    Add-Content -Path ($LogFile + ".guard") -Value ("restarted " + (Get-Date -Format o))
                } elseif ($p.HasExited) {
                    break
                }
            }
        } catch {
            Add-Content -Path ($LogFile + ".guard") -Value ("guard error: " + $_)
        } finally {
            Set-Content -Path $ExitedFile -Value "exited"
        }
        """;
}
