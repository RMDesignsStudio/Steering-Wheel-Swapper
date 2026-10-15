using System.IO.Compression;
using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>The independent Python reader accepts a good build and rejects a damaged one.</summary>
public class SecondReaderTests
{
    [Fact]
    public void AcceptsGoodAndRejectsDamaged()
    {
        if (!SecondReader.Available) Assert.Skip("py -3 with zstandard not available");
        var game = Env.Game();
        var tpl = Env.Load(Env.Template("2026 Aston Martin - SERPs Template v1.1.serps"));
        var res = tpl.BuildInMemory(game, "ferrari", tpl.CarEntries.Where(e => e.Kind == EntryKind.FomChassis).ToList(), new SwapLog());
        var be = res.Entries.Single();
        var good = res.Replaced[be.Entry.ZipPath];

        var erp = ErpX.Load(good);
        erp.Resources.First(r => r.Identifier.Contains($"/idf/{be.PrivateToken}.idf?render")).Hash = new byte[16];
        var badHash = ErpX.Save(erp);

        erp = ErpX.Load(good);
        erp.Resources.RemoveAll(r => r.Identifier.EndsWith($"/idf/{be.Renames[be.MeshToken]}.idf?lod=0&mesh"));
        var missingRef = ErpX.Save(erp);

        var output = TestContext.Current.TestOutputHelper!;
        foreach (var (name, bytes, expectOk) in new[] { ("good", good, true), ("bad hash", badHash, false), ("missing mesh", missingRef, false) })
        {
            var path = Path.Combine(Env.TempDir, $"reader_{Guid.NewGuid():N}.serps");
            try
            {
                using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
                using (var s = z.CreateEntry(be.Entry.ZipPath).Open()) s.Write(bytes);
                var (code, text) = SecondReader.Check(path, tpl.SourcePath, be.Renames.Values, [be.Entry.ZipPath]);
                output.WriteLine($"{name}: {text}");
                Assert.True((code == 0) == expectOk, $"{name}: {text}");
            }
            finally { File.Delete(path); }
        }
    }
}
