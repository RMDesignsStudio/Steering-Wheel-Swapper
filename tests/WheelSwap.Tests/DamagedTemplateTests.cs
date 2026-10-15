using System.IO.Compression;
using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>
/// Damaged or unusual templates give a clear message, damaged cars are listed (disabled) instead of vanishing, and a
/// failed build never leaves a file behind. Damaged copies are made in a temp folder; the real templates are only read.
/// </summary>
public class DamagedTemplateTests
{
    const string Aston = "2026 Aston Martin - SERPs Template v1.1.serps";

    static string Temp(string name) => Path.Combine(Env.TempDir, name);

    /// <summary>Copy of a template with entries rewritten (return null to drop an entry).</summary>
    static string Variant(string template, string name, Func<string, byte[], byte[]?> edit)
    {
        var dst = Temp(name);
        using var src = ZipFile.OpenRead(Env.Template(template));
        using var fs = File.Create(dst);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        zip.Comment = src.Comment;
        foreach (var e in src.Entries)
        {
            using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
            var data = edit(e.FullName, ms.ToArray());
            if (data is null) continue;
            using var o = zip.CreateEntry(e.FullName, CompressionLevel.NoCompression).Open();
            o.Write(data);
        }
        return dst;
    }

    static bool IsFom(string n) => n.EndsWith("astonmartin_car_FOM_livery-and-chassis.erp");

    [Theory]
    [InlineData("random")]
    [InlineData("empty")]
    [InlineData("truncated")]
    public void NotAZip(string kind)
    {
        var p = Temp($"bad_{kind}.serps");
        File.WriteAllBytes(p, kind switch
        {
            "random" => Enumerable.Range(0, 100_000).Select(i => (byte)(i * 31 + 7)).ToArray(),
            "empty" => [],
            _ => File.ReadAllBytes(Env.Template(Aston)).AsSpan(0, 40_000_000).ToArray(),
        });
        var ex = Assert.Throws<InvalidDataException>(() => new SerpsTemplate(p));
        Assert.Contains("is not a readable SERPs template", ex.Message);
    }

    [Fact]
    public void MissingFile() =>
        Assert.Throws<FileNotFoundException>(() => new SerpsTemplate(Temp("does_not_exist.serps")));

    [Theory]
    [InlineData("truncated")]
    [InlineData("garbage")]
    public void DamagedCarErpIsListedAndTheRestStillWorks(string kind)
    {
        var p = Variant(Aston, $"damaged_erp_{kind}.serps", (n, d) => !IsFom(n) ? d
            : kind == "truncated" ? d.AsSpan(0, d.Length / 2).ToArray()
            : [.. "ERPK"u8, .. Enumerable.Repeat((byte)0xEE, 5000)]);
        var tpl = new SerpsTemplate(p);
        var bad = Assert.Single(tpl.CarEntries, e => IsFom(e.ZipPath));
        Assert.NotNull(bad.Problem);
        Assert.Contains("damaged", bad.Problem);

        var game = Env.Game();
        var team = tpl.CarEntries.Single(e => e.Kind == EntryKind.TeamChassis);
        Assert.Null(team.Problem);
        tpl.Build(game, "ferrari", [team], null, new SwapLog());   // the undamaged car still builds and verifies

        var ex = Assert.Throws<InvalidOperationException>(() => tpl.Build(game, "ferrari", [bad], Temp("never.serps"), new SwapLog()));
        Assert.StartsWith(bad.FileName, ex.Message);
        Assert.False(File.Exists(Temp("never.serps")));
    }

    [Fact]
    public void CorruptDataInsideAnErpFailsCleanly()
    {
        // valid table of contents, but the compressed data of the car's mesh resources is overwritten
        var p = Variant(Aston, "corrupt_data.serps", (n, d) =>
        {
            if (!IsFom(n)) return d;
            var erp = ErpX.Load(d);
            var res = erp.Resources.First(r => r.Identifier.EndsWith("-carmodel.layer"));
            var bytes = ErpX.Save(erp);
            // flip bytes across the whole data region except the header/TOC: some fragment will fail to decompress
            for (int i = bytes.Length / 2; i < bytes.Length; i += 97) bytes[i] ^= 0x5A;
            _ = res;
            return bytes;
        });
        SerpsTemplate tpl;
        try { tpl = new SerpsTemplate(p); }
        catch (InvalidDataException) { return; }   // also acceptable: rejected while reading
        var fom = tpl.CarEntries.SingleOrDefault(e => IsFom(e.ZipPath));
        if (fom is null || fom.Problem is not null) return;   // flagged as damaged
        var outPath = Temp("corrupt_out.serps");
        var ex = Assert.ThrowsAny<Exception>(() => tpl.Build(Env.Game(), "ferrari", [fom], outPath, new SwapLog()));
        Assert.StartsWith(fom.FileName, ex.Message);
        Assert.False(File.Exists(outPath));
        Assert.False(File.Exists(outPath + ".tmp"));
    }

    [Fact]
    public void LayerPointingAtAMissingModel()
    {
        var p = Variant(Aston, "missing_model.serps", (n, d) =>
        {
            if (!IsFom(n)) return d;
            var erp = ErpX.Load(d);
            foreach (var r in erp.Resources.Where(r => r.Identifier.EndsWith("model.layer")))
            {
                var x = ErpX.Data(r);
                if (ErpX.ReplaceBytes(x, "/idf/fom_car.idf?context=render", "/idf/zzz_car.idf?context=render") == 0) continue;
                r.Fragments[0].SetData(x, true); ErpX.Rehash(r);
            }
            return ErpX.Save(erp);
        });
        var tpl = new SerpsTemplate(p);
        var e = tpl.CarEntries.Single(c => IsFom(c.ZipPath));
        Assert.Equal("zzz_car", e.ModelToken);
        var outPath = Temp("missing_model_out.serps");
        var ex = Assert.ThrowsAny<Exception>(() => tpl.Build(Env.Game(), "ferrari", [e], outPath, new SwapLog()));
        TestContext.Current.TestOutputHelper!.WriteLine(ex.Message);
        Assert.StartsWith(e.FileName, ex.Message);
        Assert.Contains("zzz_car", ex.Message);
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void TemplateWithoutCars()
    {
        var p = Path.Combine(Env.TemplateDir, "Old 2026 SERPs - v1.0", "2026 UI Textures - SERPs Template.serps");
        if (!File.Exists(p)) Assert.Skip("UI textures template not found");
        Assert.Empty(new SerpsTemplate(p).CarEntries);
    }

    [Fact]
    public void FailedWriteLeavesNothingBehind()
    {
        var tpl = Env.Load(Env.Template(Aston));
        var fom = tpl.CarEntries.Single(e => e.Kind == EntryKind.FomChassis);
        var game = Env.Game();

        // missing output folder
        var noDir = Path.Combine(Env.TempDir, "no_such_folder", "out.serps");
        Assert.ThrowsAny<IOException>(() => tpl.Build(game, "ferrari", [fom], noDir, new SwapLog()));
        Assert.False(Directory.Exists(Path.GetDirectoryName(noDir)));

        // output file held open by another program: the old file stays as it was, no .tmp left
        var locked = Temp("locked.serps");
        File.WriteAllText(locked, "previous build");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.ThrowsAny<IOException>(() => tpl.Build(game, "ferrari", [fom], locked, new SwapLog()));
            Assert.Contains("open in another program", ex.Message);
        }
        Assert.Equal("previous build", File.ReadAllText(locked));
        Assert.False(File.Exists(locked + ".tmp"));
    }

    [Fact]
    public void OutputCannotBeTheTemplateItself()
    {
        var copy = Temp("self.serps");
        File.Copy(Env.Template(Aston), copy, true);
        var before = File.GetLastWriteTimeUtc(copy);
        var tpl = new SerpsTemplate(copy);
        var fom = tpl.CarEntries.Single(e => e.Kind == EntryKind.FomChassis);
        Assert.Throws<InvalidOperationException>(() => tpl.Build(Env.Game(), "ferrari", [fom], copy, new SwapLog()));
        Assert.Equal(before, File.GetLastWriteTimeUtc(copy));
    }
}
