using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>
/// Chassis options of a Team Simplified template: each option folder is one choice in SERPs, so an option's model
/// data never leaks into another option, only real swaps are called swapped, and entries are named after their option.
/// Uses the Williams template with two default chassis (release / post-patch) and an MCL40 swap.
/// </summary>
public class TemplateOptionsTests
{
    const string Williams = @"Swapped Templates\2026 Williams - SERPs Template v1.1 - MCL40 Silverstone Chassis.serps";

    static SerpsTemplate Load() => Env.Load(Env.Template(Williams));
    static CarEntry Entry(SerpsTemplate t, string part) => t.CarEntries.Single(e => e.FileName.Contains(part));

    [Fact]
    public void OnlyTheRealSwapIsSwapped()
    {
        var t = Load();
        Assert.False(Entry(t, "default_release").Swapped);   // Williams' own (older) chassis, carried by the template
        Assert.False(Entry(t, "default_patch").Swapped);
        Assert.False(Entry(t, "_FOM_").Swapped);
        Assert.True(Entry(t, "_swapped_").Swapped);           // the MCL40 chassis
    }

    [Fact]
    public void EntriesAreNamedAfterTheirOption()
    {
        var t = Load();
        Assert.Equal("Release Default Chassis", Entry(t, "default_release").Title);
        Assert.Equal("Post-Patch Default Chassis", Entry(t, "default_patch").Title);
        Assert.Equal("FOM Chassis", Entry(t, "_FOM_").Title);
        Assert.StartsWith("McLaren", Entry(t, "_swapped_").Title);
    }

    [Fact]
    public void AnotherOptionsModelDoesNotLeakIn()
    {
        // the release option carries an older williams model; the post-patch option must use the game's own
        var game = Env.Game();
        var t = Load();
        var patch = t.LoadModel(game, Entry(t, "default_patch"));
        var stock = new CarModel(game, "williams");
        Assert.True(patch.Res[patch.RenderId].Hash.AsSpan().SequenceEqual(stock.Res[stock.RenderId].Hash), "post-patch is not on the game's williams model");
        Assert.Equal(stock.Res.Keys.Where(k => k.Contains("/idf/williams.idf")).Order(), patch.Res.Keys.Where(k => k.Contains("/idf/williams.idf")).Order());
        var release = t.LoadModel(game, Entry(t, "default_release"));
        Assert.False(release.Res[release.RenderId].Hash.AsSpan().SequenceEqual(stock.Res[stock.RenderId].Hash), "release should be on its own older copy");
    }

    [Fact]
    public void SameWheelIsJudgedByTheWheelItself()
    {
        var game = Env.Game();
        var t = Load();
        var log = new SwapLog();
        var res = t.BuildInMemory(game, "williams", t.CarEntries.Where(e => e.Problem is null).ToList(), log);
        Assert.Contains(log.Warnings, w => w == $"{Entry(t, "default_patch").FileName} already has that steering wheel; skipped.");
        Assert.Contains(res.Entries, b => b.Entry.FileName.Contains("default_release"));   // older wheel: replaced
        Assert.DoesNotContain(res.Entries, b => b.Entry.FileName.Contains("default_patch"));
    }
}
