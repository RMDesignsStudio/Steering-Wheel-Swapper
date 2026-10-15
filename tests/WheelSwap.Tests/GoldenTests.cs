using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using EgoEngineLibrary.Archive.Erp;
using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>
/// Aston Martin wheel -> FOM chassis (Aston template) and -> MyTeam must reproduce the v6 build that was
/// confirmed in-game: same resources, same bytes, except float rounding noise and a re-serialised (but
/// JSON-identical) driver animation set.
/// </summary>
public class GoldenTests
{
    const string V6 = @"output\v6_full";

    [Fact]
    public void AstonWheelOnAstonFomChassis_MatchesV6() =>
        Golden("2026 Aston Martin - SERPs Template v1.1.serps", e => e.Kind == EntryKind.FomChassis,
               [("01--00--26-astonmartin_car_FOM_livery-and-chassis.erp", "fom_amw")]);

    [Fact]
    public void AstonWheelOnMyTeam_MatchesV6() =>
        Golden("2026 My Team - SERPs Template v1.1.serps", e => e.FileName == "01--00--26-myteam_car_livery_paint-azure-cds_disabled-decal-editor.erp",
               [("01--00--26-myteam_car_livery_paint-azure-cds_disabled-decal-editor.erp", "fom_myw"),
                ("03--00--26-myteam_car_numbers.erp", null)]);

    static void Golden(string template, Func<CarEntry, bool> pick, (string File, string? V6Token)[] expected)
    {
        var game = Env.Game();
        var v6Dir = Path.Combine(Env.GoldenDir, V6);
        if (!Directory.Exists(v6Dir)) Assert.Skip($"v6 reference build not found at {v6Dir}");
        var tpl = Env.Load(Env.Template(template));
        var log = new SwapLog();
        var res = tpl.Build(game, "force_india", tpl.CarEntries.Where(pick).ToList(), null, log);
        var output = TestContext.Current.TestOutputHelper!;

        Assert.Equal(expected.Select(x => x.File).Order(), res.Replaced.Keys.Select(k => k.Split('/')[^1]).Order());
        var priv = res.Entries.SelectMany(b => b.Renames.Values).Distinct().ToList();
        foreach (var (file, v6Token) in expected)
        {
            var tool = ErpX.Load(res.Replaced.Single(kv => kv.Key.EndsWith(file)).Value);
            var v6 = ErpX.Load(Path.Combine(v6Dir, file));
            var d = ErpCompare.Compare(tool, priv, v6, v6Token is null ? [] : [v6Token]);
            output.WriteLine($"{file}: {d.Same} identical, {d.Noise} float noise only, {d.Json} JSON-identical");
            Assert.Empty(d.OnlyTool);
            Assert.Empty(d.OnlyV6);
            Assert.True(d.Real.Count == 0, "real differences: " + string.Join(", ", d.Real.Take(10)));
            Assert.True(d.Same > 0.9 * (d.Same + d.Noise + d.Json), "too few byte-identical resources");
        }
    }
}
