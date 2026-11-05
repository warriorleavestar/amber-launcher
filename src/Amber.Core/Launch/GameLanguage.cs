using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amber.Core.Game;
using Amber.Core.Localization;

namespace Amber.Core.Launch;

/// <summary>首次启动前按系统语言填写游戏选项，已有的语言选择由玩家自己管理。</summary>
internal static class GameLanguage
{
    private static readonly object OptionsGate = new();

    internal static void Prepare(GameDirectory directory, VersionJson version, string workDir)
    {
        try
        {
            var optionsPath = Path.Combine(workDir, "options.txt");
            if (File.Exists(optionsPath) && File.ReadLines(optionsPath).Any(IsLanguage)) return;
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexId = version.AssetIndex?.Id ?? version.Assets;
            if (indexId is not null && File.Exists(directory.AssetIndexPath(indexId)))
            {
                using var stream = File.OpenRead(directory.AssetIndexPath(indexId));
                var index = JsonSerializer.Deserialize(stream, Json.Options.Info<AssetIndex>());
                if (index is not null)
                    foreach (var name in index.Objects.Keys) AddLanguage(name, available);
            }

            var jarPath = directory.VersionJarPath(version.Jar ?? version.Id);
            if (File.Exists(jarPath))
            {
                try
                {
                    using var jar = ZipFile.OpenRead(jarPath);
                    foreach (var entry in jar.Entries) AddLanguage(entry.FullName, available);
                }
                catch (InvalidDataException) { } // 不让语言预设改变已有的启动校验流程。
            }
            var language = Match(CultureInfo.CurrentUICulture, available);
            if (language is not null) WriteDefault(workDir, language);
        }
        catch (IOException) { } // 语言预设失败时保留游戏自身的默认行为。
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }

    private static void AddLanguage(string name, HashSet<string> available)
    {
        if (!(name.StartsWith("minecraft/lang/", StringComparison.Ordinal)
            || name.StartsWith("assets/minecraft/lang/", StringComparison.Ordinal)
            || name.StartsWith("lang/", StringComparison.Ordinal))) return;
        if (!(name.EndsWith(".json", StringComparison.Ordinal) || name.EndsWith(".lang", StringComparison.Ordinal))) return;
        var code = Path.GetFileNameWithoutExtension(name);
        if (code.Length is > 0 and <= 32 && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            available.Add(code);
    }

    internal static string? Match(CultureInfo culture, IEnumerable<string> available)
    {
        var codes = available.ToDictionary(c => c, StringComparer.OrdinalIgnoreCase);
        string? Find(string name) => codes.GetValueOrDefault(name.Replace('-', '_'));

        if (Find(culture.Name) is { } exact) return exact;
        if (culture.TwoLetterISOLanguageName == "zh")
            return Find(L.FromCulture(culture) == "zh-Hant" ? "zh-TW" : "zh-CN") ?? Find("en-US");

        for (var current = culture; current.Name.Length > 0; current = current.Parent)
        {
            if (Find(current.Name) is { } match) return match;
            if (current.IsNeutralCulture)
            {
                try
                {
                    if (Find(CultureInfo.CreateSpecificCulture(current.Name).Name) is { } specific) return specific;
                }
                catch (CultureNotFoundException) { }
            }
        }
        if (culture.TwoLetterISOLanguageName == "nb" && Find("no-NO") is { } norwegian)
            return norwegian;
        return Find("en-US");
    }

    internal static void WriteDefault(string workDir, string language)
    {
        lock (OptionsGate)
        {
            var path = Path.Combine(workDir, "options.txt");
            try
            {
                if (File.Exists(path) && File.ReadLines(path).Any(IsLanguage)) return;
                using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                using var reader = new StreamReader(file, new UTF8Encoding(false), true, leaveOpen: true);
                var content = reader.ReadToEnd();
                if (content.Split('\n').Any(IsLanguage)) return;
                file.Position = file.Length;
                using var writer = new StreamWriter(file, reader.CurrentEncoding, leaveOpen: true);
                if (content.Length > 0 && !content.EndsWith('\n')) writer.WriteLine();
                writer.WriteLine("lang:" + language);
            }
            catch (IOException) { } // 另一局游戏或启动器正在写选项时，交给下次启动再设置。
            catch (UnauthorizedAccessException) { } // 只读选项不影响游戏启动。
        }
    }

    private static bool IsLanguage(string line)
    {
        var option = line.Trim();
        return option.StartsWith("lang:", StringComparison.Ordinal) && option.Length > "lang:".Length;
    }
}
