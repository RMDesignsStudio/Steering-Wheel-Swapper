using System.Diagnostics;
using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>
/// Where the tests find the game, the templates and the in-game-confirmed reference build. Override with the
/// WHEELSWAP_GAME / WHEELSWAP_TEMPLATES / WHEELSWAP_SWAPPED / WHEELSWAP_GOLDEN environment variables. Tests
/// skip (never fail) when a location is missing. Every input is only read; outputs go to a temp folder.
/// </summary>
static class Env
{
    public static string GameDir => Var("WHEELSWAP_GAME", @"D:\SteamLibrary\steamapps\common\F1 25");
    public static string TemplateDir => Var("WHEELSWAP_TEMPLATES", new[]
    {
        @"E:\F1 25 - 2026 Season Pack Mods\SERPs Files\.SERPs Files",
        @"E:\F1 25 - 2026 Season Pack Mods\SERPs Files\MOD BUILDER\.SERPs Files",   // before the 2026-10-12 reorganisation
    }.FirstOrDefault(Directory.Exists) ?? @"E:\F1 25 - 2026 Season Pack Mods\SERPs Files\.SERPs Files");
    public static string SwappedDir => Var("WHEELSWAP_SWAPPED", "");
    public static string GoldenDir => Var("WHEELSWAP_GOLDEN", "");

    public static string TempDir { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "WheelSwapTests", Process.GetCurrentProcess().Id.ToString())).FullName;

    static string Var(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    static GameData? _game;
    public static GameData Game()
    {
        if (!File.Exists(Path.Combine(GameDir, "F1_25.exe")) && !Directory.Exists(Path.Combine(GameDir, "2025_asset_groups")))
            Assert.Skip($"F1 25 not found at {GameDir} (set WHEELSWAP_GAME)");
        return _game ??= new GameData(GameDir);
    }

    public static string Template(string file)
    {
        var p = Path.Combine(TemplateDir, file);
        if (!File.Exists(p)) Assert.Skip($"template not found: {p}");
        return p;
    }

    // Templates are ~150 MB each; the matrix runs donor after donor on the same template, so keep the last one.
    static (string Path, SerpsTemplate Tpl)? _last;
    static readonly Lock _lock = new();
    public static SerpsTemplate Load(string path)
    {
        lock (_lock)
        {
            if (_last is { } l && l.Path == path) return l.Tpl;
            _last = null;
            var t = new SerpsTemplate(path);
            _last = (path, t);
            return t;
        }
    }

    public static string Log(SwapLog log) => string.Join(Environment.NewLine, log.All);
}
