using System.Text.Json;
using System.Text.RegularExpressions;
using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>The 3D preview: self-contained, with real geometry, materials and textures for the wheel before / after
/// and (on the preview page) the whole car with its tyres.</summary>
public class PreviewTests
{
    static JsonElement Data(string html)
    {
        var json = Regex.Match(html, "<script type=\"application/json\" class=\"pv-data\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        return JsonDocument.Parse(json.Replace("<\\/", "</")).RootElement.Clone();
    }

    /// <summary>The page's binary data: zlib + base64.</summary>
    static byte[] Unpack(string b64)
    {
        using var z = new System.IO.Compression.ZLibStream(new MemoryStream(Convert.FromBase64String(b64)), System.IO.Compression.CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }

    static readonly Dictionary<string, int> BlockBytes = new() { ["bc1"] = 8, ["bc2"] = 16, ["bc3"] = 16, ["bc4"] = 8, ["bc5"] = 16, ["bc7"] = 16 };

    /// <summary>Every group: valid material, whole vertices, indices inside the vertices; returns the triangle count.</summary>
    static int CheckGroups(JsonElement groups, int materials, List<JsonElement> geometry, float limit)
    {
        int tris = 0;
        foreach (var grp in groups.EnumerateArray())
        {
            Assert.InRange(grp.GetProperty("m").GetInt32(), 0, materials - 1);
            int gi = grp.GetProperty("g").GetInt32();
            Assert.InRange(gi, 0, geometry.Count - 1);
            var g = geometry[gi];
            var pos = Unpack(g.GetProperty("pos").GetString()!);
            var idx = Unpack(g.GetProperty("idx").GetString()!);
            int verts = pos.Length / 12;
            Assert.Equal(0, pos.Length % 12);
            foreach (var k in new[] { "nrm", "tan", "uv" }) Assert.Equal(verts * 8, Unpack(g.GetProperty(k).GetString()!).Length);   // half4
            bool w32 = g.GetProperty("w32").GetBoolean();
            int count = g.GetProperty("count").GetInt32();
            Assert.Equal(count * (w32 ? 4 : 2), idx.Length);
            for (int i = 0; i < count; i++) Assert.True((w32 ? BitConverter.ToUInt32(idx, i * 4) : BitConverter.ToUInt16(idx, i * 2)) < verts, "index past the vertices");
            for (int i = 0; i < pos.Length; i += 4) Assert.True(Math.Abs(BitConverter.ToSingle(pos, i)) < limit, "vertex far outside the car");
            tris += count / 3;
        }
        return tris;
    }

    [Theory]
    [InlineData("2026 Ferrari - SERPs Template v1.1.serps", "williams")]
    [InlineData("2026 Aston Martin - SERPs Template v1.1.serps", "redbull@2025")]
    public void PageHoldsWheelAndCar(string template, string donor)
    {
        var game = Env.Game();
        var tpl = Env.Load(Env.Template(template));
        var entries = tpl.CarEntries.Where(e => e.Problem is null).Take(2).ToList();
        var res = tpl.Build(game, donor, entries, null, new SwapLog());
        var html = WheelPreview.Page(game, tpl, res);
        TestContext.Current.TestOutputHelper!.WriteLine($"{html.Length / 1024 / 1024} MB for {res.Entries.Count} cars");

        Assert.Contains("<canvas class=\"pv-canvas\">", html);
        Assert.Contains("data-mode=\"car\"", html);
        Assert.DoesNotMatch(@"(src|href)=""https?:", html);   // works offline
        Assert.True(html.Length < 60_000_000 * res.Entries.Count, "preview page unexpectedly large");

        var data = Data(html);
        var mats = data.GetProperty("mats").EnumerateArray().ToList();
        var tex = data.GetProperty("tex").EnumerateArray().ToList();
        var geo = data.GetProperty("geo").EnumerateArray().ToList();
        // textures: a known block format with whole mip levels, or decoded RGBA
        foreach (var t in tex)
        {
            var f = t.GetProperty("f").GetString()!;
            if (f == "none") continue;
            int w = t.GetProperty("w").GetInt32(), h = t.GetProperty("h").GetInt32();
            var levels = t.GetProperty("l").EnumerateArray().Select(l => Unpack(l.GetString()!)).ToList();
            Assert.NotEmpty(levels);
            if (f == "rgba") { Assert.Equal(w * h * 4, levels[0].Length); continue; }
            Assert.True(BlockBytes.ContainsKey(f), $"unknown texture format {f}");
            for (int i = 0; i < levels.Count; i++)
                Assert.Equal(Math.Max(1, (Math.Max(1, w >> i) + 3) / 4) * Math.Max(1, (Math.Max(1, h >> i) + 3) / 4) * BlockBytes[f], levels[i].Length);
        }
        foreach (var m in mats)
            foreach (var t in m.GetProperty("t").EnumerateObject()) Assert.InRange(t.Value.GetInt32(), -1, tex.Count - 1);
        // the car's own livery: a paint material with its paint texture
        Assert.Contains(mats, m => m.GetProperty("k").GetInt32() == 1 && m.GetProperty("t").GetProperty("d").GetInt32() >= 0);

        var cars = data.GetProperty("cars").EnumerateArray().ToList();
        Assert.Equal(res.Entries.Count, cars.Count);
        foreach (var car in cars)
        {
            Assert.True(CheckGroups(car.GetProperty("after"), mats.Count, geo, 4f) > 1000, "new wheel has almost no triangles");
            Assert.True(CheckGroups(car.GetProperty("before"), mats.Count, geo, 4f) > 1000, "old wheel has almost no triangles");
            var body = car.GetProperty("body");
            Assert.True(CheckGroups(body, mats.Count, geo, 4f) > 100_000, "car body has almost no triangles");
            Assert.Equal(4, body.EnumerateArray().Count(g => g.GetProperty("slot").GetString() == "tread"));   // four tyres
            var box = car.GetProperty("box").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            Assert.InRange(box[5] - box[2], 4.5f, 6.5f);   // an F1 car is about 5.5 m long
            Assert.Equal(16, car.GetProperty("toWheel").GetArrayLength());
        }
    }

    [Theory]
    [InlineData("ferrari")]
    [InlineData("williams@2025")]
    public void WheelPictureIsDrawnAndCached(string donor)
    {
        var game = Env.Game();
        var dir = Path.Combine(Env.TempDir, "wheel_pictures");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var path = WheelThumbnail.CachedPath(game, donor, 240, 160, dir);
        var png = File.ReadAllBytes(path);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.True(png.Length > 10_000, "the picture is almost empty");
        // second call: served from the cache, not rendered again
        var t = File.GetLastWriteTimeUtc(path);
        Assert.Equal(path, WheelThumbnail.CachedPath(game, donor, 240, 160, dir));
        Assert.Equal(t, File.GetLastWriteTimeUtc(path));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ReportHasA3DSection()
    {
        var game = Env.Game();
        var tpl = Env.Load(Env.Template("2026 Aston Martin - SERPs Template v1.1.serps"));
        var log = new SwapLog();
        var res = tpl.Build(game, "mclaren", tpl.CarEntries.Where(e => e.Problem is null).Take(1).ToList(), null, log);
        var outPath = Path.Combine(Env.TempDir, "report_preview.serps");
        var path = BuildReport.Write(game, tpl, res, log, outPath);
        try
        {
            var html = File.ReadAllText(path);
            Assert.Contains("<h3>3D preview", html);
            Assert.Contains($"id=\"pv-{res.Entries[0].PrivateToken}\"", html);
            Assert.DoesNotContain("3D preview not available", html);
            // wheel only in the report (the whole car is on the preview page)
            Assert.DoesNotContain("data-mode=\"car\"", html);
            var car = Data(html).GetProperty("cars")[0];
            Assert.Equal(JsonValueKind.Null, car.GetProperty("body").ValueKind);
            Assert.NotEmpty(car.GetProperty("after").EnumerateArray());
        }
        finally { File.Delete(path); }
    }
}
