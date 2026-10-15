using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>
/// Cars whose LOD3 lacks the donor piece's slot used to show no wheel there. The body pieces now fall back to
/// the other body slot (steering wheel / carbon), converting the vertex layout (5 vs 6 streams).
/// </summary>
public class LodCoverageTests
{
    [Theory]
    [InlineData("2026 Red Bull - SERPs Template v1.1.serps", "mercedes", "steering wheel", "carbon_tile_4")]   // 5 -> 6 streams
    [InlineData("2026 Mercedes - SERPs Template v1.1.serps", "redbull", "carbon_tile_4", "steering wheel")]    // 6 -> 5 streams
    public void Lod3KeepsAWheel(string template, string donor, string donorPiece, string targetSlot)
    {
        var game = Env.Game();
        var tpl = Env.Load(Env.Template(template));
        var log = new SwapLog();
        tpl.Build(game, donor, tpl.CarEntries.Where(e => e.Kind == EntryKind.TeamChassis).ToList(), null, log);
        TestContext.Current.TestOutputHelper!.WriteLine(Env.Log(log));
        Assert.Contains(log.All, l => l == $"LOD3: donor '{donorPiece}' drawn with the target's '{targetSlot}' material.");
        Assert.Contains(log.All, l => l.StartsWith($"LOD3 '{targetSlot}': ") && l.Contains("donor vertices"));
        Assert.DoesNotContain(log.Warnings, w => w.Contains("no donor wheel geometry"));
    }
}
