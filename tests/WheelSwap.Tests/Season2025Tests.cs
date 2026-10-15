using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>
/// 2025 wheels as donors (the game's f1_2025_vehicle_package): the wheel, textures and the 2025 driver clips go in,
/// the clips travel inside the entry, and re-swaps between seasons stay clean.
/// </summary>
public class Season2025Tests
{
    const string Aston = "2026 Aston Martin - SERPs Template v1.1.serps";

    static List<CarEntry> Fom(SerpsTemplate t) => t.CarEntries.Where(e => e.Kind == EntryKind.FomChassis).ToList();

    [Fact]
    public void DonorIds()
    {
        Assert.Equal(("ferrari", 2025), GameData.ParseDonor("ferrari@2025"));
        Assert.Equal(("ferrari", 2026), GameData.ParseDonor("ferrari"));
        Assert.Equal("Ferrari 2025", GameData.DonorName("ferrari@2025"));
        Assert.Equal("driver_in_car_2025_forceindia", GameData.AnimsetName("force_india", 2025));
        Assert.Equal(22, GameData.Donors.Length);
        Assert.Equal(GameData.Donors.Length, GameData.Donors.Select(d => d.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("ferrari@2025")]
    [InlineData("force_india@2025")]   // a different wheel from the car's own 2026 one: built, not skipped
    [InlineData("haas@2025")]
    public void TwentyFiveWheelGoesIn(string donor)
    {
        var game = Env.Game();
        var tpl = Env.Load(Env.Template(Aston));
        var log = new SwapLog();
        var res = tpl.Build(game, donor, tpl.CarEntries.Where(e => e.Problem is null).ToList(), null, log);   // verified
        TestContext.Current.TestOutputHelper!.WriteLine(Env.Log(log));
        var (tok, _) = GameData.ParseDonor(donor);
        var clipFile = GameData.AnimsetName(tok, 2025);
        Assert.Equal(tpl.CarEntries.Count(e => e.Problem is null), res.Entries.Count);
        foreach (var be in res.Entries)
        {
            var erp = ErpX.Load(res.Replaced[be.Entry.ZipPath]);
            var clips = erp.Resources.Count(r => r.ResourceType == "AnimClip" && r.Identifier.Contains($"/idf/{clipFile}.idf?context=animation"));
            Assert.True(clips >= 20, $"only {clips} {clipFile} clips carried in {be.Entry.FileName}");
            Assert.Contains($"/idf/{clipFile}.idf?context=animation", AnimsetSwap.ToJson(ErpX.Data(erp.Resources.First(r => r.Identifier == be.AnimsetId))));
        }
    }

    [Fact]
    public void TwentyFiveAndTwentySixWheelsDiffer()
    {
        // same team, different seasons: different private names and different geometry/textures
        var game = Env.Game();
        var tpl = Env.Load(Env.Template(Aston));
        var a = tpl.BuildInMemory(game, "ferrari", Fom(tpl), new SwapLog()).Entries.Single();
        var b = tpl.BuildInMemory(game, "ferrari@2025", Fom(tpl), new SwapLog()).Entries.Single();
        Assert.NotEqual(a.PrivateToken, b.PrivateToken);
    }

    [Fact]
    public void ReSwapAcrossSeasons()
    {
        var game = Env.Game();
        var tpl = Env.Load(Env.Template(Aston));
        var first = Path.Combine(Env.TempDir, "season_2025.serps");
        tpl.Build(game, "ferrari@2025", Fom(tpl), first, new SwapLog());

        var t1 = new SerpsTemplate(first);
        var e1 = Fom(t1).Single();
        Assert.NotNull(e1.PreviousSwap);

        // same 2025 wheel again: skipped
        var log = new SwapLog();
        Assert.Throws<InvalidOperationException>(() => t1.BuildInMemory(game, "ferrari@2025", Fom(t1), log));
        Assert.Contains(log.Warnings, w => w.EndsWith("already has that steering wheel; skipped."));

        // to the 2026 Ferrari wheel: the 2025 clips leave with the old wheel, result equals a direct swap
        log = new SwapLog();
        var re = t1.Build(game, "ferrari", Fom(t1), null, log);
        Assert.Contains(log.All, l => l.StartsWith("Earlier Steering Wheel Swapper build (Ferrari 2025 wheel)"));
        var direct = tpl.BuildInMemory(game, "ferrari", Fom(tpl), new SwapLog());
        var reErp = ErpX.Load(re.Replaced[e1.ZipPath]);
        var key = direct.Replaced.Keys.Single(k => k.EndsWith("/" + e1.FileName));   // the output's option folder carries the wheel name
        Assert.DoesNotContain(reErp.Resources, r => r.Identifier.Contains("driver_in_car_2025_"));
        var d = ErpCompare.Compare(reErp, re.Entries.SelectMany(x => x.Renames.Values).ToList(),
                                   ErpX.Load(direct.Replaced[key]), direct.Entries.SelectMany(x => x.Renames.Values).ToList());
        Assert.Empty(d.OnlyTool);
        Assert.Empty(d.OnlyV6);
        Assert.True(d.Real.Count == 0, string.Join(", ", d.Real.Take(10)));
    }

    [Fact]
    public void VerifierCatchesMissingAnimStringTable()
    {
        // the in-game crash of the first 2025 build: clips carried, but not the animstringtable they name their bones with
        var game = Env.Game();
        var tpl = Env.Load(Env.Template(Aston));
        var res = tpl.BuildInMemory(game, "ferrari@2025", Fom(tpl), new SwapLog());
        var be = res.Entries.Single();
        var erp = ErpX.Load(res.Replaced[be.Entry.ZipPath]);
        Assert.Contains(erp.Resources, r => r.Identifier.EndsWith("driver_in_car_2025_ferrari.idf?context=animstringtable"));
        erp.Resources.RemoveAll(r => r.ResourceType == "ACStringTable" && r.Identifier.Contains("driver_in_car_2025_ferrari"));
        var copy = new BuildResult { DonorToken = res.DonorToken };
        copy.Replaced[be.Entry.ZipPath] = ErpX.Save(erp);
        copy.Entries.Add(be);
        var v = BuildVerifier.Verify(game, tpl, copy, be);
        Assert.Contains(v.Failures, f => f.Contains("driver animation resources would be missing in the 2026 season"));
    }

    [Fact]
    public void ReSwapRemovesCarriedStringTable()
    {
        var game = Env.Game();
        var tpl = Env.Load(Env.Template(Aston));
        var first = Path.Combine(Env.TempDir, "season_2025_st.serps");
        tpl.Build(game, "ferrari@2025", Fom(tpl), first, new SwapLog());
        var t1 = new SerpsTemplate(first);
        var re = t1.BuildInMemory(game, "williams", Fom(t1), new SwapLog());
        Assert.DoesNotContain(ErpX.Load(re.Replaced.Values.First()).Resources, r => r.Identifier.Contains("driver_in_car_2025_"));
    }

    [Fact]
    public void VerifierCatchesMissingTwentyFiveClips()
    {
        var game = Env.Game();
        var tpl = Env.Load(Env.Template(Aston));
        var res = tpl.BuildInMemory(game, "ferrari@2025", Fom(tpl), new SwapLog());
        var be = res.Entries.Single();
        var erp = ErpX.Load(res.Replaced[be.Entry.ZipPath]);
        erp.Resources.RemoveAll(r => r.ResourceType == "AnimClip" && r.Identifier.Contains("driver_in_car_2025_ferrari"));
        var copy = new BuildResult { DonorToken = res.DonorToken };
        copy.Replaced[be.Entry.ZipPath] = ErpX.Save(erp);
        copy.Entries.Add(be);
        var v = BuildVerifier.Verify(game, tpl, copy, be);
        Assert.Contains(v.Failures, f => f.Contains("2025 driver animation clips are not carried in the entry"));
    }
}
