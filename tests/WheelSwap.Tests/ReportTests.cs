using System.Text.RegularExpressions;
using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>The HTML build report: written next to the output, self-contained, with pictures and textures.</summary>
public class ReportTests
{
    [Theory]
    [InlineData("2026 Aston Martin - SERPs Template v1.1.serps", "ferrari")]
    [InlineData("2026 My Team - SERPs Template v1.1.serps", "williams")]
    public void ReportIsWrittenAndComplete(string template, string donor)
    {
        var game = Env.Game();
        var tpl = Env.Load(Env.Template(template));
        var entries = tpl.CarEntries.Where(e => e.Problem is null).Take(2).ToList();
        var log = new SwapLog();
        var res = tpl.Build(game, donor, entries, null, log);
        var outPath = Path.Combine(Env.TempDir, $"report_{donor}.serps");
        var path = BuildReport.Write(game, tpl, res, log, outPath);
        try
        {
            Assert.Equal(BuildReport.PathFor(outPath), path);
            var html = File.ReadAllText(path);
            TestContext.Current.TestOutputHelper!.WriteLine($"{new FileInfo(path).Length / 1024} KB");
            Assert.Contains($"Verified · {res.Verification.Sum(v => v.Checks)} checks passed", html);
            Assert.DoesNotContain("could not be drawn", html);
            Assert.Equal(res.Entries.Count, Regex.Matches(html, "<section class=\"car\">").Count);
            // before + after, front + side for every car, each with real geometry
            var svgs = Regex.Matches(html, "<svg class=\"view\"[^>]*>(.*?)</svg>", RegexOptions.Singleline);
            Assert.Equal(4 * res.Entries.Count, svgs.Count);
            Assert.All(svgs, m => Assert.True(Regex.Matches(m.Groups[1].Value, "<path ").Count > 200, "a wheel picture has almost no triangles"));
            // texture thumbnails are valid PNGs
            var thumbs = Regex.Replace(html, "<script type=\"application/json\" class=\"pv-data\">.*?</script>", "", RegexOptions.Singleline);   // not the 3D preview data
            var pngs = Regex.Matches(thumbs, "data:image/png;base64,([A-Za-z0-9+/=]+)").Select(m => Convert.FromBase64String(m.Groups[1].Value)).ToList();
            Assert.True(pngs.Count >= 4 * res.Entries.Count, $"only {pngs.Count} texture previews");
            Assert.All(pngs, p => Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], p[..4]));
            // nothing loaded from the internet: the page works offline and can be shared as one file
            Assert.DoesNotMatch(@"(src|href)=""https?:", html);
        }
        finally { File.Delete(path); }
    }
}
