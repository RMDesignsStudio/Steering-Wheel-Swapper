using WheelSwap.Core;

// wheelswap inspect <template.serps>
// wheelswap build   <template.serps> --donor TEAM (--out OUT.serps | --no-write) [--entries a,b] [--game DIR] [--report]
// exit codes: 0 ok, 1 usage, 2 failed (message on stderr, nothing written)
if (args.Length < 2) { Console.WriteLine("usage: wheelswap inspect|build <template.serps> [--donor TEAM] [--out PATH | --no-write] [--entries substr,..] [--game DIR] [--report] [--preview FILE.html] [--anim-from TEAM]"); return 1; }
string? Opt(string n) { var i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

// wheelswap thumb <donor|all> <out dir> [--game DIR]   (the app's hover pictures of each wheel)
if (args[0] == "thumb")
{
    var g = new GameData(Opt("--game") ?? GameData.DetectGameDir() ?? throw new DirectoryNotFoundException("F1 25 not found; pass --game"));
    var ids = args[1] == "all" ? GameData.Donors.Select(d => d.Id).ToList() : [args[1]];
    var dir = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : ".";
    Directory.CreateDirectory(dir);
    foreach (var id in ids)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { File.WriteAllBytes(Path.Combine(dir, id.Replace('@', '_') + ".png"), WheelThumbnail.Render(g, id)); Console.WriteLine($"{id}: {sw.ElapsedMilliseconds} ms"); }
        catch (Exception ex) { Console.WriteLine($"{id}: FAILED {ex.Message}"); }
    }
    return 0;
}

try
{
    var tpl = new SerpsTemplate(args[1]);
    Console.WriteLine($"Template: {tpl.Name}  (injection: {tpl.Comment})");
    for (int i = 0; i < tpl.CarEntries.Count; i++)
    {
        var c = tpl.CarEntries[i];
        Console.WriteLine($"  [{i}] {c.Describe}{(c.Problem is null ? "" : "   (unsupported: " + c.Problem + ")")}");
    }
    if (args[0] == "inspect") return 0;

    var donor = Opt("--donor");
    if (donor is null || !GameData.IsDonor(donor))
    {
        Console.Error.WriteLine("--donor must be one of: " + string.Join(", ", GameData.Donors.Select(t => t.Id)));
        return 1;
    }
    var outPath = args.Contains("--no-write") ? null : Opt("--out");
    if (outPath is null && !args.Contains("--no-write")) { Console.Error.WriteLine("--out or --no-write required"); return 1; }
    var gameDir = Opt("--game") ?? GameData.DetectGameDir() ?? throw new DirectoryNotFoundException("F1 25 not found; pass --game");
    var game = new GameData(gameDir);
    var filters = (Opt("--entries") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
    var sel = tpl.CarEntries.Where(c => c.Problem is null && (filters.Length == 0 || filters.Any(f => c.FileName.Contains(f, StringComparison.OrdinalIgnoreCase)))).ToList();
    var log = new SwapLog();
    log.Line += (s, w) => Console.WriteLine((w ? "WARNING: " : "") + s);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var animFrom = Opt("--anim-from");   // diagnostics: driver animations from another donor
    if (animFrom is not null && !GameData.IsDonor(animFrom)) { Console.Error.WriteLine("--anim-from must be a donor id"); return 1; }
    var res = tpl.Build(game, donor, sel, outPath, log, animFrom);
    if (args.Contains("--report") && outPath is not null) Console.WriteLine("Report: " + BuildReport.Write(game, tpl, res, log, outPath));
    if (Opt("--preview") is { } preview)
    {
        File.WriteAllText(preview, WheelPreview.Page(game, tpl, res), new System.Text.UTF8Encoding(false));
        Console.WriteLine("Preview: " + Path.GetFullPath(preview));
    }
    Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F1}s, {log.Warnings.Count} warning(s).");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 2;
}
