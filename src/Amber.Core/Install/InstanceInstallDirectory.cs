using Amber.Core.Game;
using Amber.Core.Localization;

namespace Amber.Core.Install;

/// <summary>只接管本次安装新建的目录；失败回滚不能删除已有实例。</summary>
internal sealed class InstanceInstallDirectory : IDisposable
{
    public string Path { get; }
    private bool _complete;

    private InstanceInstallDirectory(string path) => Path = path;

    internal static string Normalize(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    internal static bool Same(string first, string second) => string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);

    internal static void CheckBase(GameDirectory dir, string id, string mcVersion)
    {
        if (Same(dir.VersionDir(id), dir.VersionDir(mcVersion)))
            throw new InvalidDataException(L.F("core.install.targetbase", id));
    }

    public static InstanceInstallDirectory Reserve(string path)
    {
        path = Normalize(path);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException(L.F("core.install.targetexists", path));
        var parent = System.IO.Path.GetDirectoryName(path) ?? throw new InvalidDataException(L.F("core.install.targetexists", path));
        Directory.CreateDirectory(parent);
        var staging = System.IO.Path.Combine(parent, ".amber-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            // Move 不允许覆盖目录或文件；检查之后有另一个安装抢占名字时，也必须失败。
            Directory.Move(staging, path);
            return new InstanceInstallDirectory(path);
        }
        catch (IOException e)
        {
            throw new IOException(L.F("core.install.targetexists", path), e);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging);
        }
    }

    public void Complete() => _complete = true;

    public void Dispose()
    {
        if (_complete) return;
        try { Directory.Delete(Path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* 清理失败保留本次的半成品，不影响原异常。 */ }
    }
}
