using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;

namespace WheelSwap.Core;

/// <summary>Steering-wheel textures: read the donor's full-resolution textures (embedding the external
/// .mipmaps data) and write them over the textures a template entry supplies for its wheel.</summary>
public static class TextureSwap
{
    // decal map is "decals_da" on most cars, "decal_da" on Alpine / Mercedes
    static readonly Regex Channel = new(@"steering_wheel_(d|n|s|decals?_da)\.tif$", RegexOptions.Compiled);

    public static string? ChannelOf(string texId)
    {
        var m = Channel.Match(texId);
        return !m.Success ? null : m.Groups[1].Value.StartsWith("decal") ? "decals_da" : m.Groups[1].Value;
    }

    public static Dictionary<string, DdsFile> LoadDonor(GameData game, CarModel donor, SwapLog log)
    {
        var matId = donor.Res.Keys.FirstOrDefault(k => k.EndsWith($"/materials/{donor.Token}_steeringwheel.material", StringComparison.Ordinal))
                    ?? donor.Res.Keys.FirstOrDefault(k => k.EndsWith("steeringwheel.material", StringComparison.Ordinal))
                    ?? throw new InvalidDataException($"{donor.Token}: steering wheel material not found");
        var result = new Dictionary<string, DdsFile>();
        foreach (var tex in ErpX.Eaids(ErpX.Data(donor.Res[matId])).Where(e => e.EndsWith(".tif")).Distinct())
        {
            var ch = ChannelOf(tex);
            if (ch is null || result.ContainsKey(ch) || !donor.Res.TryGetValue(tex, out var res)) continue;
            var srv = new ErpGfxSRVResource();
            srv.FromResource(res);
            Stream? mips = null;
            if (srv.SurfaceRes.HasMips)
            {
                var p = game.MipmapsPath(srv.SurfaceRes.Frag2.MipMapFileName);
                if (p is not null && srv.SurfaceRes.HasValidMips) mips = File.OpenRead(p);
                else log.Warn($"Donor texture {Path.GetFileName(tex)}: full-resolution data not found, using the low-resolution copy.");
            }
            using (mips) result[ch] = srv.ToDdsFile(mips, false, 0);
            log.Info($"Donor texture {ch}: {result[ch].header.width}x{result[ch].header.height}");
        }
        foreach (var ch in new[] { "d", "n", "s", "decals_da" })
            if (!result.ContainsKey(ch)) log.Warn($"Donor has no steering wheel '{ch}' texture.");
        return result;
    }

    /// <summary>Replace one texture (SRV + surface) inside an entry ERP with the given image, fully embedded.</summary>
    public static void Apply(ErpFile entry, ErpResource srvRes, DdsFile dds)
    {
        var probe = new ErpGfxSRVResource();
        probe.FromResource(srvRes);
        var surface = entry.FindResource(probe.Fragment0.SurfaceResourceName);
        if (surface.Fragments.Count == 3) surface.Fragments.RemoveAt(2);   // drop external-mipmaps table
        var srv = new ErpGfxSRVResource();
        srv.FromResource(srvRes);
        // surface has no external-mipmaps table now, so the import embeds the whole chain and leaves dds untouched
        dds.ToErpGfxSRVResource(srv, null!, false, 0);
        srv.ToResource(srvRes);
        ErpX.Rehash(srvRes); ErpX.Rehash(surface);
    }
}
