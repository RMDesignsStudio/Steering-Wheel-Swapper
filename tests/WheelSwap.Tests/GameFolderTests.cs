using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>
/// Missing, partial, damaged and modded game installs. Uses small fake game folders in temp (copies of only the
/// files a FOM-chassis build needs); the real game folder is only read.
/// </summary>
public class GameFolderTests
{
    const string Aston = "2026 Aston Martin - SERPs Template v1.1.serps";
    const string Teams = @"2025_asset_groups\f1_2026_vehicle_package\teams";
    const string Incar = @"2025_asset_groups\animation_package\scenes\incardriver\2026\incardriver_f1.erp";

    /// <summary>A fake install with fom_car, force_india and ferrari (+ the in-car animations). Stock files only:
    /// where the real game has an X.erp.bak (mod manager backup), that is copied as X.erp.</summary>
    static string FakeGame(string name, params string[] teams)
    {
        var real = Env.Game().GameDir;
        var dst = Path.Combine(Env.TempDir, "game_" + name);
        if (Directory.Exists(dst)) Directory.Delete(dst, true);
        foreach (var t in teams.Length > 0 ? teams : ["fom_car", "force_india", "ferrari"])
        {
            var src = Path.Combine(real, Teams, t);
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".bak") || f.EndsWith(".txt")) continue;
                var to = Path.Combine(dst, Teams, t, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(File.Exists(f + ".bak") ? f + ".bak" : f, to);
            }
        }
        var inc = Path.Combine(dst, Incar);
        Directory.CreateDirectory(Path.GetDirectoryName(inc)!);
        File.Copy(Path.Combine(real, Incar), inc);
        return dst;
    }

    static string Ferrari(string game) => Path.Combine(game, Teams, "ferrari", "wep", "ferrari.erp");

    static (SerpsTemplate, CarEntry) Fom()
    {
        var tpl = Env.Load(Env.Template(Aston));
        return (tpl, tpl.CarEntries.Single(e => e.Kind == EntryKind.FomChassis));
    }

    [Fact]
    public void MissingFolder()
    {
        var ex = Assert.Throws<DirectoryNotFoundException>(() => new GameData(Path.Combine(Env.TempDir, "no_game_here")));
        Assert.Contains("was not found", ex.Message);
        Assert.Throws<DirectoryNotFoundException>(() => new GameData(""));
    }

    [Fact]
    public void WrongFolder()
    {
        var ex = Assert.Throws<DirectoryNotFoundException>(() => new GameData(Env.TempDir));
        Assert.Contains("is not an F1 25 game folder", ex.Message);
    }

    [Fact]
    public void StockFakeGameBuilds()
    {
        var game = new GameData(FakeGame("stock"));
        var (tpl, fom) = Fom();
        var log = new SwapLog();
        tpl.Build(game, "ferrari", [fom], null, log);
        Assert.DoesNotContain(log.Warnings, w => w.Contains("modded"));
    }

    [Fact]
    public void MissingDonorCar()
    {
        var game = new GameData(FakeGame("no_williams"));
        var (tpl, fom) = Fom();
        var ex = Assert.Throws<FileNotFoundException>(() => tpl.Build(game, "williams", [fom], null, new SwapLog()));
        Assert.Contains("Williams car files are missing", ex.Message);
    }

    [Fact]
    public void MissingDriverAnimations()
    {
        var dir = FakeGame("no_incar");
        File.Delete(Path.Combine(dir, Incar));
        var (tpl, fom) = Fom();
        var ex = Assert.Throws<FileNotFoundException>(() => tpl.Build(new GameData(dir), "ferrari", [fom], null, new SwapLog()));
        Assert.Contains("in-car driver animations are missing", ex.Message);
    }

    [Fact]
    public void DamagedDonorFile()
    {
        var dir = FakeGame("damaged_ferrari");
        File.WriteAllBytes(Ferrari(dir), [.. "ERPK"u8, .. new byte[4096]]);
        var (tpl, fom) = Fom();
        var ex = Assert.Throws<InvalidDataException>(() => tpl.Build(new GameData(dir), "ferrari", [fom], null, new SwapLog()));
        Assert.Contains("Ferrari car file could not be read", ex.Message);
    }

    [Fact]
    public void MissingTargetCar()
    {
        // the FOM chassis comes from the game: without fom_car the build must stop with a clear message
        var dir = FakeGame("no_fom", "force_india", "ferrari");
        var (tpl, fom) = Fom();
        var ex = Assert.ThrowsAny<Exception>(() => tpl.Build(new GameData(dir), "ferrari", [fom], null, new SwapLog()));
        TestContext.Current.TestOutputHelper!.WriteLine(ex.Message);
        Assert.StartsWith(fom.FileName, ex.Message);
        Assert.Contains("fom_car", ex.Message);
    }

    [Fact]
    public void ModdedDonorWarns()
    {
        // mod-manager layout: ferrari.erp.bak = stock, ferrari.erp = a mod that changed the steering wheel texture
        var dir = FakeGame("modded_ferrari");
        var cur = Ferrari(dir);
        File.Copy(cur, cur + ".bak");
        var erp = ErpX.Load(cur);
        var surf = erp.Resources.First(r => r.Identifier.Contains("steering_wheel_d") && r.Fragments.Count >= 2);   // pixel data
        var d = ErpX.Data(surf, 1); d[^1] ^= 1; surf.Fragments[1].SetData(d, true); ErpX.Rehash(surf);
        File.WriteAllBytes(cur, ErpX.Save(erp));

        var (tpl, fom) = Fom();
        var log = new SwapLog();
        tpl.Build(new GameData(dir), "ferrari", [fom], null, log);
        Assert.Contains(log.Warnings, w => w.StartsWith("The game's Ferrari files are modded"));
    }

    [Fact]
    public void HandlingOnlyModsDoNotWarn()
    {
        // the real install: mod managers rewrote every team ERP but only changed handling (.vtf) data
        var game = Env.Game();
        var bak = game.TeamErp("ferrari") + ".bak";
        if (!File.Exists(bak)) Assert.Skip("this game install has no ferrari.erp.bak");
        var a = ErpX.Index(ErpX.Load(bak)); var b = ErpX.Index(ErpX.Load(game.TeamErp("ferrari")));
        var changed = b.Values.Where(r => !a.TryGetValue(r.Identifier, out var o) || !o.Hash.AsSpan().SequenceEqual(r.Hash)).ToList();
        if (changed.Any(r => r.ResourceType != "VTF")) Assert.Skip("this install's ferrari.erp has more than handling changes");
        Assert.Null(game.ModdedNote("ferrari"));
    }
}
