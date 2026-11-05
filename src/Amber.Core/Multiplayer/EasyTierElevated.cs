using Amber.Core.Localization;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 完整局域网模式：EasyTier 建虚拟网卡（wintun），每个人有一个虚拟 IP，任何局域网游戏和程序都能互通。
/// 建网卡要管理员权限，所以只在用户打开这个模式时，通过 Windows 的权限确认（UAC）启动一个小的守护脚本，
/// 由它运行 easytier-core。非管理员的 Amber 结束不了管理员进程，所以要这样绕一层。
///
/// 不落盘：参数（含房间密钥）、停止和重启命令都经命名管道传给守护脚本，easytier-core 的输出也经管道回来，只留在内存里。
/// 守护脚本本身用 -EncodedCommand 传（里面没有密钥），不写任何文件。管道只允许当前用户和管理员组连接，
/// 连上后先核对对方的进程号就是刚启动的守护脚本，才发参数。Amber 退出或崩溃时命令管道断开，守护脚本结束 easytier-core 后自己退出。
/// </summary>
// 只能经 Start 创建，Start 先检查是不是 Windows：调用方不用再各自判断。
#pragma warning disable CA1416
public sealed class EasyTierElevatedSession : IEasyTierSession
{
    private readonly string _cliPath;
    private readonly ElevatedGuard _guard;

    public EasyTierOptions Options { get; private set; }

    private EasyTierElevatedSession(EasyTierOptions options, string cliPath, ElevatedGuard guard)
    {
        Options = options;
        _cliPath = cliPath;
        _guard = guard;
    }

    /// <summary>守护脚本退出了，或者没能在期限内连上（这时它也不会再做任何事）：调用方都当作已经停了。</summary>
    public bool HasExited => _guard.HasExited || _guard.ConnectFailed;

    public IReadOnlyList<string> RecentOutput => _guard.RecentOutput;

    public Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default) => EasyTierSession.QueryPeersAsync(_cliPath, Options.RpcPort, ct);

    /// <summary>弹出 UAC 确认。用户拒绝时抛 <see cref="OperationCanceledException"/>。workDir 只用来清掉旧版本留下的文件（旧版本把参数写在这里）。</summary>
    public static EasyTierElevatedSession Start(EasyTierInstaller installer, EasyTierOptions options, string workDir)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(L.T("core.mp.component.windowsonly"));
        if (!options.Tun) throw new ArgumentException(L.T("core.mp.component.tunrequired"));
        LegacyFiles.Remove(workDir);
        var guard = ElevatedGuard.Start(installer.CorePath, EasyTierArgs.Build(options), EasyTierArgs.Environment(options), elevate: true);
        return new EasyTierElevatedSession(options, installer.CliPath, guard);
    }

    /// <summary>换参数重启 easytier-core（换出口、换路径、测试矩阵用），不用再弹权限确认：经命令管道发新参数。</summary>
    public void Restart(EasyTierOptions options)
    {
        _guard.Start(EasyTierArgs.Build(options), EasyTierArgs.Environment(options));
        Options = options;
    }

    /// <summary>守护脚本还没按最近一次的参数重启好。</summary>
    public bool RestartPending => _guard.StartPending;

    /// <summary>让守护脚本结束 easytier-core 后退出。</summary>
    public void Dispose() => _guard.Dispose();

    /// <summary>
    /// 旧版本（写文件传参数）留下的 run-lan 目录：参数、重启文件和日志里都可能有房间密钥，启动时清掉，只删这几种文件。
    /// 每个都先覆盖再删（尽量不在磁盘上留下原文）。不跟着联接或符号链接走：目录本身、上一级、子目录和文件是重解析点的都跳过；
    /// 有不止一个硬链接的文件也跳过（覆盖它会改到别处的同一份内容）。一个文件出错只跳过这个文件。
    /// </summary>
    internal static class LegacyFiles
    {
        /// <summary>覆盖时每次写的块大小：大文件不用一次分配整块内存。</summary>
        internal const int ChunkSize = 64 * 1024;

        private static readonly string[] Names = ["args.json", "guard.ps1", "stop", "stop.restart", "stop.restart.tmp", "exited", "easytier.log", "easytier.log.err", "easytier.log.guard"];

        private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        public static int Remove(string workDir)
        {
            var removed = 0;
            if (!Directory.Exists(workDir)) return 0;
            try
            {
                if (IsReparsePoint(workDir) || Path.GetDirectoryName(Path.GetFullPath(workDir)) is { } parent && IsReparsePoint(parent)) return 0;
                foreach (var dir in Directory.EnumerateDirectories(workDir).Append(workDir).ToList())
                {
                    if (IsReparsePoint(dir)) continue;
                    foreach (var name in Names)
                    {
                        var path = Path.Combine(dir, name);
                        if (!File.Exists(path)) continue;
                        try
                        {
                            if (IsReparsePoint(path)) continue;
                            if (!Overwrite(path)) continue;
                            File.Delete(path);
                            removed++;
                        }
                        catch (Exception) { /* 这个文件跳过，接着处理别的 */ }
                    }
                    if (dir != workDir && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        try { Directory.Delete(dir); } catch (IOException) { }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return removed;
        }

        /// <summary>用零覆盖整个文件；文件有不止一个硬链接时什么也不做，返回 false。</summary>
        private static bool Overwrite(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (LinkCount(stream) != 1) return false;
            var zeros = new byte[ChunkSize];
            var left = stream.Length;
            while (left > 0)
            {
                var n = (int)Math.Min(left, ChunkSize);
                stream.Write(zeros, 0, n);
                left -= n;
            }
            stream.Flush(true);
            return true;
        }

        /// <summary>文件的硬链接数；取不到时返回 0（当作不安全，跳过）。</summary>
        internal static uint LinkCount(FileStream stream) =>
            GetFileInformationByHandle(stream.SafeFileHandle, out var info) ? info.NumberOfLinks : 0;

        [StructLayout(LayoutKind.Sequential, Pack = 4)] // FILETIME 是两个 DWORD，按 4 字节对齐
        private struct FileInformation
        {
            public uint FileAttributes;
            public long CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation info);
    }
}

#pragma warning restore CA1416

/// <summary>
/// 守护脚本和它的四根命名管道（命令、标准输出、标准错误、“已启动”回执）。
/// Start 在 UAC 确认后就返回（和以前一样不卡界面），管道在后台连接；连上并核对进程号之前不发任何命令。
/// 两边互相核对：Amber 核对连上来的是刚启动的守护脚本（不是就断开它接着等），守护脚本核对管道是 Amber 建的（进程号和所有者），
/// 不是就退出。管道在守护脚本退出之前一直留着，别的账号没法趁机建同名管道。
/// elevate 为 false 时不弹 UAC，直接以当前权限运行（测试用）。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ElevatedGuard : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly Process _process;
    private readonly NamedPipeServerStream _cmd;
    private readonly NamedPipeServerStream[] _pipes;
    private readonly Queue<string> _output = new();
    private readonly Task _connect;
    private readonly Task[] _readers;
    private readonly object _gate = new();
    private Task _chain;
    private StreamWriter? _cmdWriter;
    private int _sent;
    private int _acked;
    private volatile bool _disposed;
    private readonly TimeSpan _connectTimeout;

    /// <summary>传给守护脚本的命令行（测试用：确认里面没有密钥）。</summary>
    internal string CommandLine { get; }

    private ElevatedGuard(Process process, string commandLine, NamedPipeServerStream[] pipes, TimeSpan connectTimeout)
    {
        _connectTimeout = connectTimeout;
        _process = process;
        CommandLine = commandLine;
        _pipes = pipes;
        _cmd = pipes[0];
        _connect = ConnectAsync();
        _chain = _connect;
        _readers =
        [
            ReadLinesAsync(pipes[1], Keep),
            ReadLinesAsync(pipes[2], Keep),
            ReadLinesAsync(pipes[3], line => { if (line.StartsWith("started ", StringComparison.Ordinal) && int.TryParse(line[8..], out var n)) Volatile.Write(ref _acked, n); }),
        ];
    }

    /// <summary>守护脚本已经退出（它退出前会结束 easytier-core）。</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>Amber 等守护脚本连管道超时了。之后晚到的守护脚本一连上就被关掉管道，读到结尾自己退出。</summary>
    public bool ConnectFailed => _connect.IsFaulted || _connect.IsCanceled;

    public bool StartPending => !HasExited && Volatile.Read(ref _acked) < Volatile.Read(ref _sent);

    public IReadOnlyList<string> RecentOutput { get { lock (_output) return _output.ToList(); } }

    private void Keep(string line)
    {
        lock (_output)
        {
            _output.Enqueue(line);
            while (_output.Count > 200) _output.Dequeue();
        }
    }

    /// <summary>Amber 等守护脚本连上四根管道的总时间。守护脚本自己的连接期限更短，先放弃。</summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>守护脚本连四根管道的总期限（秒）。</summary>
    internal const int GuardConnectSeconds = 20;

    /// <summary>
    /// 启动守护脚本（elevate 时弹 UAC，这一步会等用户确认）。用户拒绝时抛 <see cref="OperationCanceledException"/>。
    /// env 是给 easytier-core 的环境变量（房间密钥），和参数一样只经管道传。serverPidForTest：测试用，让守护脚本以为管道该由别的进程建。
    /// </summary>
    public static ElevatedGuard Start(string corePath, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, bool elevate, int? serverPidForTest = null,
        TimeSpan? connectTimeoutForTest = null)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        string Name(string kind) => $"amber-et-{id}-{kind}";
        var pipes = new[]
        {
            CreatePipe(Name("cmd"), PipeDirection.Out),
            CreatePipe(Name("out"), PipeDirection.In),
            CreatePipe(Name("err"), PipeDirection.In),
            CreatePipe(Name("ack"), PipeDirection.In),
        };
        try
        {
            // 先换掉 __ID__ 再放路径；路径用 UTF-16 的 Base64 传（PowerShell 的单引号字符串还会被 ‘ ’ ‚ ‛ 结束）。
            var script = GuardScript
                .Replace("__ID__", id)
                .Replace("__AMBERPID__", (serverPidForTest ?? Environment.ProcessId).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("__SID__", WindowsIdentity.GetCurrent().User!.Value)
                .Replace("__TIMEOUT__", GuardConnectSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("__CORE64__", Convert.ToBase64String(Encoding.Unicode.GetBytes(corePath)));
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var commandLine = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded;
            var info = new ProcessStartInfo("powershell.exe", commandLine)
            {
                UseShellExecute = elevate,
                CreateNoWindow = !elevate,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevate) info.Verb = "runas";
            Process process;
            try
            {
                process = Process.Start(info) ?? throw new InvalidOperationException(L.T("core.mp.component.startfailed"));
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException(L.T("core.mp.component.elevationdeclined"));
            }
            var guard = new ElevatedGuard(process, commandLine, pipes, connectTimeoutForTest ?? ConnectTimeout) { PipeId = id };
            guard.Start(args, env);
            return guard;
        }
        catch
        {
            foreach (var p in pipes) p.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 等四根管道都连上，并核对连上来的是刚启动的守护脚本：别的程序抢先连上时断开它、不发任何东西，接着等守护脚本。
    /// 超时后管道也不马上关（关早了别的账号就能建同名管道冒充 Amber）：接着等，守护脚本晚到了一连上就关掉那根管道，
    /// 它读到结尾就退出，不会启动 easytier-core。
    /// </summary>
    private async Task ConnectAsync()
    {
        using var timeout = new CancellationTokenSource(_connectTimeout);
        try
        {
            foreach (var pipe in _pipes)
            {
                while (true)
                {
                    await pipe.WaitForConnectionAsync(timeout.Token);
                    if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) && client == (uint)_process.Id) break;
                    Keep(L.T("core.mp.component.guardmismatch"));
                    pipe.Disconnect();
                }
            }
            _cmdWriter = new StreamWriter(_cmd, Utf8) { AutoFlush = true, NewLine = "\n" };
        }
        catch (Exception e)
        {
            Keep(L.F("core.mp.component.guardfailed", e.Message));
            _ = RejectLateGuardAsync();
            throw;
        }
    }

    /// <summary>超时之后：守护脚本连上哪根管道就关哪根（已经连上的马上关），别的程序连上就断开接着等；守护脚本退出或 5 分钟后全部关掉。</summary>
    private async Task RejectLateGuardAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _ = _process.WaitForExitAsync(stop.Token).ContinueWith(_ => { try { stop.Cancel(); } catch (ObjectDisposedException) { } }, TaskScheduler.Default);
        try
        {
            foreach (var pipe in _pipes)
            {
                while (true)
                {
                    if (!pipe.IsConnected) await pipe.WaitForConnectionAsync(stop.Token);
                    if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) && client == (uint)_process.Id) break;
                    pipe.Disconnect();
                }
                pipe.Dispose();
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            foreach (var p in _pipes) p.Dispose();
        }
    }

    /// <summary>命令按顺序发：连上之后才真正写进管道。已经 Dispose 的话排着的启动命令不发（只发停止），免得先带着密钥把 easytier-core 启动起来。</summary>
    private void Enqueue(Action<StreamWriter> write, bool isStart = false)
    {
        lock (_gate)
        {
            _chain = _chain.ContinueWith(_ =>
            {
                if (!_connect.IsCompletedSuccessfully || _cmdWriter is not { } writer) return;
                if (isStart && _disposed) return;
                try { write(writer); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>（重新）启动 easytier-core：参数和环境变量只经管道传过去。</summary>
    public void Start(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env)
    {
        var n = Interlocked.Increment(ref _sent);
        var message = JsonSerializer.Serialize(new GuardCommand("start", n, WindowsCommandLine.Join(args), new Dictionary<string, string>(env)), AmberJsonContext.Default.GuardCommand);
        Enqueue(w => w.WriteLine(message), isStart: true);
    }

    /// <summary>
    /// 让守护脚本结束 easytier-core 后退出。还没连上时也不关管道：等连上后发停止命令（连不上的话守护脚本到期自己退出，退出后再关）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var stop = JsonSerializer.Serialize(new GuardCommand("stop", 0, "", null), AmberJsonContext.Default.GuardCommand);
        Enqueue(w =>
        {
            w.WriteLine(stop);
            // 关掉命令管道：就算停止命令没送到，守护脚本看到管道断开也会结束 easytier-core。
            w.Dispose();
        });
    }

    /// <summary>等守护脚本退出、输出读完（测试用）。</summary>
    internal bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout) && Task.WaitAll(_readers, timeout);

    /// <summary>守护脚本的进程号（测试用）。</summary>
    internal int ProcessId => _process.Id;

    /// <summary>管道名里的随机部分（测试用：模拟别的程序抢先连上）。</summary>
    internal string PipeId { get; private init; } = "";

    /// <summary>不发停止命令、直接关掉所有管道，模拟 Amber 崩溃（测试用）。</summary>
    internal void AbandonForTest()
    {
        _disposed = true;
        foreach (var p in _pipes) p.Dispose();
    }

    private async Task ReadLinesAsync(NamedPipeServerStream pipe, Action<string> onLine)
    {
        try
        {
            await _connect;
            using var reader = new StreamReader(pipe, Utf8);
            while (await reader.ReadLineAsync() is { } line) onLine(line);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
    }

    /// <summary>只允许当前用户和管理员组（UAC 提权后的进程属于管理员组；“换个管理员账号确认”时用户不同）连接。</summary>
    private static NamedPipeServerStream CreatePipe(string name, PipeDirection direction)
    {
        var security = new PipeSecurity();
        var rights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), rights, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, direction, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    /// <summary>
    /// 守护脚本（Windows PowerShell 5.1，以管理员身份运行）：连上四根管道，按命令启动、重启、结束 easytier-core，
    /// 输出原样经管道送回。命令管道断开（Amber 退出或崩溃）或 easytier-core 自己退出时结束。不写任何文件。
    /// </summary>
    internal const string GuardScript = """
        $ErrorActionPreference = 'Stop'
        $Core = [System.Text.Encoding]::Unicode.GetString([System.Convert]::FromBase64String('__CORE64__'))
        $AmberPid = [uint32]__AMBERPID__
        $UserSid = '__SID__'
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $limitMs = __TIMEOUT__ * 1000
        $enc = New-Object System.Text.UTF8Encoding($false)
        # GetNamedPipeServerProcessId via an in-memory P/Invoke (Add-Type would write temp files).
        $asm = [AppDomain]::CurrentDomain.DefineDynamicAssembly((New-Object System.Reflection.AssemblyName('AmberGuardNative')), [System.Reflection.Emit.AssemblyBuilderAccess]::Run)
        $type = $asm.DefineDynamicModule('AmberGuardNative').DefineType('AmberGuardNative', 'Public, Class')
        $method = $type.DefinePInvokeMethod('GetNamedPipeServerProcessId', 'kernel32.dll', [System.Reflection.MethodAttributes]'Public, Static, PinvokeImpl', [System.Reflection.CallingConventions]::Standard, [bool], [Type[]]@([IntPtr], [uint32].MakeByRefType()), [System.Runtime.InteropServices.CallingConvention]::Winapi, [System.Runtime.InteropServices.CharSet]::Unicode)
        $method.SetImplementationFlags([System.Reflection.MethodImplAttributes]::PreserveSig)
        $native = $type.CreateType()
        # Check the pipe was created by Amber (server PID, owner is the user or Administrators) before reading anything; exit otherwise.
        function Connect-Pipe([string]$kind, $direction) {
            $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', "amber-et-__ID__-$kind", $direction, [System.IO.Pipes.PipeOptions]::None, [System.Security.Principal.TokenImpersonationLevel]::Identification)
            $left = [int]($limitMs - $clock.ElapsedMilliseconds)
            if ($left -le 0) { throw 'connect timeout' }
            $pipe.Connect($left)
            $server = [uint32]0
            if (-not $native::GetNamedPipeServerProcessId($pipe.SafePipeHandle.DangerousGetHandle(), [ref]$server) -or $server -ne $AmberPid) { $pipe.Dispose(); throw 'pipe server mismatch' }
            $owner = $pipe.GetAccessControl().GetOwner([System.Security.Principal.SecurityIdentifier]).Value
            if ($owner -ne $UserSid -and $owner -ne 'S-1-5-32-544') { $pipe.Dispose(); throw 'pipe owner mismatch' }
            return $pipe
        }
        $cmdPipe = Connect-Pipe 'cmd' ([System.IO.Pipes.PipeDirection]::In)
        $outPipe = Connect-Pipe 'out' ([System.IO.Pipes.PipeDirection]::Out)
        $errPipe = Connect-Pipe 'err' ([System.IO.Pipes.PipeDirection]::Out)
        $ackPipe = Connect-Pipe 'ack' ([System.IO.Pipes.PipeDirection]::Out)
        $reader = New-Object System.IO.StreamReader($cmdPipe, $enc)
        $ack = New-Object System.IO.StreamWriter($ackPipe, $enc)
        $ack.AutoFlush = $true
        $script:p = $null
        $script:copies = @()
        function Stop-Core {
            if ($script:p -and -not $script:p.HasExited) {
                try { $script:p.Kill() } catch {}
                $script:p.WaitForExit(5000) | Out-Null
            }
            foreach ($c in $script:copies) { try { $c.Wait(3000) | Out-Null } catch {} }
            $script:copies = @()
        }
        function Start-Core([string]$arguments, $env) {
            $psi = New-Object System.Diagnostics.ProcessStartInfo($Core)
            $psi.Arguments = $arguments
            # Only the allow-listed key reaches easytier-core.
            if ($env) { foreach ($prop in $env.PSObject.Properties) { if ($prop.Name -ceq 'ET_NETWORK_SECRET') { $psi.EnvironmentVariables[$prop.Name] = [string]$prop.Value } } }
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $script:p = [System.Diagnostics.Process]::Start($psi)
            $script:copies = @($script:p.StandardOutput.BaseStream.CopyToAsync($outPipe), $script:p.StandardError.BaseStream.CopyToAsync($errPipe))
        }
        try {
            $pending = $reader.ReadLineAsync()
            while ($true) {
                if ($pending.Wait(500)) {
                    $line = $pending.Result
                    if ($null -eq $line) { break }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.cmd -eq 'stop') { break }
                    if ($msg.cmd -eq 'start') {
                        Stop-Core
                        Start-Core $msg.args $msg.env
                        $ack.WriteLine('started ' + $msg.n)
                    }
                    $pending = $reader.ReadLineAsync()
                } elseif ($script:p -and $script:p.HasExited) {
                    break
                }
            }
        } catch {
        } finally {
            Stop-Core
            foreach ($s in @($ack, $outPipe, $errPipe, $ackPipe, $cmdPipe)) { try { $s.Dispose() } catch {} }
        }
        """;
}

/// <summary>守护脚本的一条命令（JSON 一行）。</summary>
public sealed record GuardCommand(
    [property: System.Text.Json.Serialization.JsonPropertyName("cmd")] string Cmd,
    [property: System.Text.Json.Serialization.JsonPropertyName("n")] int N,
    [property: System.Text.Json.Serialization.JsonPropertyName("args")] string Args,
    [property: System.Text.Json.Serialization.JsonPropertyName("env")] Dictionary<string, string>? Env);

/// <summary>按 Windows 程序解析命令行的规则（CommandLineToArgvW / MSVC）把参数拼成一个字符串。</summary>
public static class WindowsCommandLine
{
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '\n' or '\v' or '"')) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // 引号前面的反斜杠要加倍，引号本身转义。
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        // 结尾的反斜杠后面紧跟收尾引号，也要加倍。
        sb.Append('\\', backslashes * 2);
        return sb.Append('"').ToString();
    }
}
