using System.Text;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Data.Pkg;

namespace WheelSwap.Core;

/// <summary>Points a car's in-car animation set at the donor team's clips (the Simplified-Swapper style
/// animset transplant). The animset keeps its own id and name; only the clip references change, so the
/// driver's hands, paddle and button presses follow the donor wheel's IK targets.</summary>
public static class AnimsetSwap
{
    static readonly Regex ClipRef = new(@"/idf/([^/""]+)\.idf\?context=animation", RegexOptions.Compiled);
    static readonly Regex ClipName = new(@"clip=([A-Za-z0-9_]+)", RegexOptions.Compiled);

    public static string ToJson(byte[] pkg)
    {
        using var ms = new MemoryStream(pkg, false);
        var f = PkgFile.ReadPkg(ms);
        using var o = new MemoryStream();
        f.WriteJson(o);
        return Encoding.UTF8.GetString(o.ToArray());
    }

    public static byte[] FromJson(string json)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var f = PkgFile.ReadJson(ms);
        using var o = new MemoryStream();
        f.WritePkg(o);
        return o.ToArray();
    }

    /// <summary>The clip file an animation set plays its clips from (e.g. driver_in_car_2026_ferrari), or null.</summary>
    public static string? ClipFile(byte[] animsetPkg)
    {
        var m = ClipRef.Match(ToJson(animsetPkg));
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Rewrite <paramref name="animset"/> in place so its clips come from <paramref name="donorAnimset"/>'s clip file.</summary>
    public static void Retarget(ErpResource animset, byte[] donorAnimsetPkg, SwapLog log)
    {
        var donorJson = ToJson(donorAnimsetPkg);
        var donorFiles = ClipRef.Matches(donorJson).Select(m => m.Groups[1].Value).Distinct().ToList();
        if (donorFiles.Count != 1) throw new InvalidDataException("donor animation set does not use exactly one clip file");
        var donorClips = ClipName.Matches(donorJson).Select(m => m.Groups[1].Value).ToHashSet();

        var json = ToJson(ErpX.Data(animset));
        var targetClips = ClipName.Matches(json).Select(m => m.Groups[1].Value).ToList();
        var missing = targetClips.Where(c => !donorClips.Contains(c)).ToList();
        if (missing.Count > 0) log.Warn($"Donor animation set lacks {missing.Count} clip(s): {string.Join(", ", missing)}");
        var before = ClipRef.Matches(json).Select(m => m.Groups[1].Value).Distinct();
        json = ClipRef.Replace(json, $"/idf/{donorFiles[0]}.idf?context=animation");
        animset.Fragments[0].SetData(FromJson(json), true);
        ErpX.Rehash(animset);
        log.Info($"Driver animation {animset.Identifier.Split('/')[^1].Replace(".idf?context=animset", "")}: " +
                 $"{string.Join(",", before)} -> {donorFiles[0]} ({targetClips.Count} clips)");
    }
}
