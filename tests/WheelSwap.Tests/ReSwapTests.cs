using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>
/// Feeding a Steering Wheel Swapper output back in: it is recognised, the earlier private model is stripped, and a
/// re-swap gives exactly what a direct swap of the original template gives (no stacking, no growth).
/// </summary>
public class ReSwapTests
{
    const string Aston = "2026 Aston Martin - SERPs Template v1.1.serps";
    const string MyTeam = "2026 My Team - SERPs Template v1.1.serps";
    const string Haas = "2026 Haas - SERPs Template v1.1.serps";   // 4-letter model name: private name is all hex
    // swapped chassis (Alpine model on Mercedes geometry "mercswap"): merc5f74 could be mercedes or mercswap by name
    const string AlpMerc = @"swapped|2026 Alpine to Mercedes Chassis - SELF-CONTAINED v7.0.serps";
    const string Azure = "01--00--26-myteam_car_livery_paint-azure-cds_disabled-decal-editor.erp";

    static readonly Dictionary<string, string> _outputs = new();
    static readonly Lock _lock = new();

    /// <summary>Ferrari wheel written into a temp copy of the template (all supported entries, or just MyTeam azure).</summary>
    static string FerrariOutput(string template)
    {
        lock (_lock)
        {
            if (_outputs.TryGetValue(template, out var p)) return p;
            var tpl = Env.Load(Path(template));
            p = System.IO.Path.Combine(Env.TempDir, $"reswap_{_outputs.Count}.serps");
            tpl.Build(Env.Game(), "ferrari", Pick(tpl), p, new SwapLog());
            return _outputs[template] = p;
        }
    }

    static string Path(string template)
    {
        if (!template.StartsWith("swapped|")) return Env.Template(template);
        var p = System.IO.Path.Combine(Env.SwappedDir, template["swapped|".Length..]);
        if (!File.Exists(p)) Assert.Skip($"template not found: {p}");
        return p;
    }

    static List<CarEntry> Pick(SerpsTemplate t) =>
        t.CarEntries.Where(e => e.Problem is null && (e.Kind != EntryKind.MyTeamLivery || e.FileName == Azure)).ToList();

    [Theory]
    [InlineData(Aston)]
    [InlineData(MyTeam)]
    [InlineData(Haas)]
    [InlineData(AlpMerc)]
    public void OutputIsRecognised(string template)
    {
        var game = Env.Game();
        var orig = Env.Load(Path(template));
        var outTpl = new SerpsTemplate(FerrariOutput(template));
        foreach (var o in Pick(orig))
        {
            var e = outTpl.CarEntries.Single(c => c.FileName == o.FileName);   // its option folder now names the wheel
            Assert.NotNull(e.PreviousSwap);
            Assert.Null(e.Problem);
            Assert.Equal(o.Kind, e.Kind);
            Assert.Equal(o.ModelToken, e.ModelToken);
            Assert.Equal(o.Swapped, e.Swapped);
            Assert.Contains("wheel already swapped", e.Describe);
        }
        _ = game;
    }

    [Theory]
    [InlineData(Aston, "williams")]
    [InlineData(MyTeam, "williams")]
    [InlineData(Aston, "gm_cadillac")]   // 32-bit index buffers
    [InlineData(Haas, "williams")]
    [InlineData(AlpMerc, "williams")]
    public void ReSwapEqualsDirectSwap(string template, string donor)
    {
        var game = Env.Game();
        var orig = Env.Load(Path(template));
        var direct = orig.BuildInMemory(game, donor, Pick(orig), new SwapLog());

        var outTpl = new SerpsTemplate(FerrariOutput(template));
        var log = new SwapLog();
        var re = outTpl.Build(game, donor, Pick(outTpl), null, log);   // verified
        TestContext.Current.TestOutputHelper!.WriteLine(Env.Log(log));
        // every ERP the re-swap rewrites is one a direct swap rewrites too; any the direct swap rewrites in addition
        // (the MyTeam numbers ERP binding fix) must already be in that state in the earlier output.
        // Option folders carry the wheel name in the earlier output, so ERPs are matched by file name.
        var reByFile = re.Replaced.ToDictionary(kv => kv.Key.Split('/')[^1], kv => kv.Value);
        var directByFile = direct.Replaced.ToDictionary(kv => kv.Key.Split('/')[^1], kv => kv.Value);
        Assert.Empty(reByFile.Keys.Except(directByFile.Keys));
        foreach (var file in directByFile.Keys.Except(reByFile.Keys))
            Assert.Equal(directByFile[file], outTpl.Get(outTpl.EntryNames.Single(n => n.EndsWith("/" + file) || n == file)));
        Assert.All(re.Entries, be => Assert.Contains(log.All, l => l.StartsWith("Earlier Steering Wheel Swapper build (Ferrari wheel)")));

        var reTokens = re.Entries.SelectMany(b => b.Renames.Values).Distinct().ToList();
        var directTokens = direct.Entries.SelectMany(b => b.Renames.Values).Distinct().ToList();
        foreach (var key in reByFile.Keys)
        {
            var a = ErpX.Load(reByFile[key]); var b = ErpX.Load(directByFile[key]);
            var d = ErpCompare.Compare(a, reTokens, b, directTokens);
            TestContext.Current.TestOutputHelper!.WriteLine($"{key.Split('/')[^1]}: {d.Same} identical, {d.Noise} float noise, {d.Json} JSON-identical");
            Assert.True(d.OnlyTool.Count == 0, "left over from the earlier build: " + string.Join(", ", d.OnlyTool.Take(5)));
            Assert.True(d.OnlyV6.Count == 0, "missing compared with a direct swap: " + string.Join(", ", d.OnlyV6.Take(5)));
            Assert.True(d.Real.Count == 0, "differs from a direct swap: " + string.Join(", ", d.Real.Take(10)));
        }
    }

    [Fact]
    public void SameDonorAgainIsSkipped()
    {
        var outTpl = new SerpsTemplate(FerrariOutput(Aston));
        var log = new SwapLog();
        var ex = Assert.Throws<InvalidOperationException>(() => outTpl.BuildInMemory(Env.Game(), "ferrari", Pick(outTpl), log));
        Assert.StartsWith("Nothing to do", ex.Message);
        Assert.All(Pick(outTpl), e => Assert.Contains(log.Warnings, w => w == $"{e.FileName} already has that steering wheel; skipped."));
    }

    [Fact]
    public void BackToTheCarsOwnWheel()
    {
        // an Aston output given the Aston wheel again: rebuilt (it currently has the Ferrari wheel), not skipped
        var outTpl = new SerpsTemplate(FerrariOutput(Aston));
        var team = outTpl.CarEntries.Single(e => e.Kind == EntryKind.TeamChassis);
        var log = new SwapLog();
        var res = outTpl.Build(Env.Game(), "force_india", [team], null, log);
        var be = Assert.Single(res.Entries);
        var oldPriv = team.PreviousSwap!.Keys;
        Assert.DoesNotContain(ErpX.Load(res.Replaced[team.ZipPath]).Resources, r => oldPriv.Any(p => r.Identifier.Contains($"/idf/{p}.idf")));
        Assert.Contains(log.All, l => l.StartsWith("Verified: "));
    }

    [Fact]
    public void RepeatedReSwapsDoNotGrow()
    {
        var game = Env.Game();
        var path = FerrariOutput(Aston);
        long first = new FileInfo(path).Length;
        var sizes = new List<long>();
        foreach (var donor in new[] { "mclaren", "ferrari" })
        {
            var next = System.IO.Path.Combine(Env.TempDir, $"reswap_chain_{donor}.serps");
            var t = new SerpsTemplate(path);
            t.Build(game, donor, Pick(t), next, new SwapLog());
            sizes.Add(new FileInfo(next).Length);
            path = next;
        }
        TestContext.Current.TestOutputHelper!.WriteLine($"ferrari {first}, +mclaren {sizes[0]}, +ferrari {sizes[1]}");
        // back on the Ferrari wheel after a detour: same size as the first Ferrari build (zip compression may wobble a little)
        Assert.InRange(sizes[1], first * 0.995, first * 1.005);
    }
}
