using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>
/// Every 2026 donor wheel onto every supported car entry of every template (stock team templates, MyTeam,
/// swapped-chassis templates). Each build is verified by BuildVerifier, written to a temp .serps, re-opened,
/// and re-read by the independent Python reader.
/// </summary>
public class MatrixTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var tpl in MatrixTemplates())
            foreach (var t in GameData.Donors)
                data.Add(tpl, t.Id);
        if (data.Count == 0) data.Add("(no templates found)", "");
        return data;
    }

    /// <summary>Paths relative to their search root, as "root|relative" so test names stay short.</summary>
    static IEnumerable<string> MatrixTemplates()
    {
        foreach (var dir in new[] { Env.TemplateDir, Path.Combine(Env.TemplateDir, "Swapped Templates") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "2026 *.serps").Order())
                yield return $"templates|{Path.GetRelativePath(Env.TemplateDir, f)}";
        }
        foreach (var rel in SwappedChassis)
            if (File.Exists(Path.Combine(Env.SwappedDir, rel))) yield return $"swapped|{rel}";
    }

    // the user's own chassis-swap builds (ErpProbe / hand-made), each stacking model resources differently
    static readonly string[] SwappedChassis =
    [
        @"RB TO VCARB\2026 Red Bull to Racing Bulls Chassis (with hand-fix) - v1.1.serps",
        @"2026 Alpine to Mercedes Chassis - SELF-CONTAINED v7.0.serps",
        @"Audi to RB22\2026 Audi to Red Bull Chassis - v1.2 (audi cockpit).serps",
        @"Audi to RB22\2026 Audi to Red Bull Chassis - SELF-CONTAINED v1.0.serps",
    ];

    static string Resolve(string key)
    {
        var (root, rel) = (key.Split('|')[0], key.Split('|')[^1]);
        var p = Path.Combine(root == "templates" ? Env.TemplateDir : Env.SwappedDir, rel);
        if (!File.Exists(p)) Assert.Skip($"template not found: {p}");
        return p;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Swap(string template, string donor)
    {
        if (donor == "") Assert.Skip("no templates found (set WHEELSWAP_TEMPLATES)");
        var game = Env.Game();
        var tpl = Env.Load(Resolve(template));
        var entries = tpl.CarEntries.Where(e => e.Problem is null).ToList();
        Assert.NotEmpty(entries);
        var outPath = Path.Combine(Env.TempDir, $"matrix_{Guid.NewGuid():N}.serps");
        var log = new SwapLog();
        var output = TestContext.Current.TestOutputHelper!;
        try
        {
            BuildResult res;
            try { res = tpl.Build(game, donor, entries, outPath, log); }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Nothing to do"))
            {
                // every entry already uses this donor's wheel: that is the only acceptable reason to build nothing
                Assert.All(entries, e => Assert.Contains(log.Warnings, w => w.StartsWith(e.FileName) && w.Contains("already has that steering wheel")));
                output.WriteLine("all entries already have this wheel");
                return;
            }
            finally { output.WriteLine(Env.Log(log)); }

            // every entry is either built or skipped because it already has this wheel
            foreach (var e in entries)
                Assert.True(res.Entries.Any(b => b.Entry.ZipPath == e.ZipPath) ||
                            log.Warnings.Any(w => w.StartsWith(e.FileName) && w.Contains("already has that steering wheel")),
                            $"{e.FileName} was neither built nor skipped");
            Assert.Contains(log.All, l => l.StartsWith("Verified: "));
            // every detail level the donor has must get donor geometry (LOD3 used to lose the wheel on some cars)
            Assert.DoesNotContain(log.Warnings, w => w.Contains("no donor wheel geometry at this detail level"));

            // the written .serps re-opens, and each built entry is recognised as this tool's output of the same car
            var back = new SerpsTemplate(outPath);
            Assert.Equal(tpl.CarEntries.Count, back.CarEntries.Count);
            foreach (var be in res.Entries)
            {
                var ce = back.CarEntries.Single(c => c.ZipPath == res.OutputPath(be.Entry.ZipPath));
                Assert.Null(ce.Problem);
                Assert.Equal(be.Entry.Kind, ce.Kind);
                Assert.Equal(be.ModelToken, ce.ModelToken);
                Assert.NotNull(ce.PreviousSwap);
                Assert.Equal(be.Renames.ToDictionary(kv => kv.Value, kv => kv.Key).OrderBy(kv => kv.Key), ce.PreviousSwap!.OrderBy(kv => kv.Key));
                Assert.Equal(res.Replaced[be.Entry.ZipPath], back.Get(res.OutputPath(be.Entry.ZipPath)));
                // the option folder names the new wheel (unless it also holds a car that did not get it)
                if (be.Entry.Folder.Length > 0 && tpl.CarEntries.Where(c => c.Folder == be.Entry.Folder).All(c => res.Entries.Any(b => b.Entry.ZipPath == c.ZipPath)))
                    Assert.EndsWith(" - " + GameData.WheelSuffix(donor), ce.Folder);
            }

            // the second, independent reader agrees
            if (SecondReader.Available)
            {
                var (code, text) = SecondReader.Check(outPath, tpl.SourcePath, res.Entries.SelectMany(b => b.Renames.Values).Distinct(),
                                                      res.Replaced.Keys.Select(k => res.OutputPath(k) + "|" + k));
                output.WriteLine("second reader: " + text);
                Assert.True(code == 0, "second reader: " + text);
            }
            else output.WriteLine("second reader skipped: py -3 with zstandard not available");
        }
        finally { File.Delete(outPath); }
    }
}
