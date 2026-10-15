using System.Numerics;
using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using EgoEngineLibrary.Graphics.Dds;

namespace WheelSwap.Core;

/// <summary>
/// A small still picture of a car's stock steering wheel at a 3/4 angle (the app shows it when you hover a
/// "steering wheel from" option). Drawn by a tiny software rasteriser straight from the player's own game files: the
/// wheel's LOD0 pieces placed by their bones, its diffuse map with the decal sheet on the second UV, carbon and
/// screens as flat dark materials, a key / fill / sky light, supersampled and framed on what was drawn. Transparent background, PNG.
/// </summary>
public static class WheelThumbnail
{
    /// <summary>The cached picture for a donor, rendering it first if needed (cache key: donor, size and the team ERP's
    /// timestamp, so a game update re-renders it).</summary>
    public static string CachedPath(GameData game, string donorId, int width, int height, string cacheDir)
    {
        var (token, season) = GameData.ParseDonor(donorId);
        var stamp = File.GetLastWriteTimeUtc(game.TeamErp(token, season)).Ticks;
        var path = Path.Combine(cacheDir, $"{donorId.Replace('@', '_')}_{width}x{height}_{stamp:x}.png");
        if (File.Exists(path)) return path;
        var png = Render(game, donorId, width, height);
        Directory.CreateDirectory(cacheDir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, png);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    record struct Tri(Vector3 A, Vector3 B, Vector3 C, Vector3 NA, Vector3 NB, Vector3 NC, Vector4 UA, Vector4 UB, Vector4 UC, int Kind);
    sealed record Img(int W, int H, byte[] Rgba);

    const int KWheel = 0, KCarbon = 1, KScreen = 2;

    public static byte[] Render(GameData game, string donorId, int width = 480, int height = 320)
    {
        var (token, season) = GameData.ParseDonor(donorId);
        var car = new CarModel(game, token, season);
        var nt = NodeTable.Parse(car.Render);
        var world = PreviewScene.NodeWorld(nt);
        var sw = nt.SteeringCluster().TryGetValue(("steering_wheel", "steer_master"), out var swn) ? swn : nt.SteerMaster;
        Matrix4x4.Invert(world(sw.Index), out var toWheel);

        var tex = TextureSwap.LoadDonor(game, car, new SwapLog());
        var diffuse = tex.TryGetValue("d", out var dd) ? Decode(dd, 1024) : null;
        var decals = tex.TryGetValue("decals_da", out var dl) ? Decode(dl, 1024) : null;

        var tris = new List<Tri>();
        foreach (var part in WheelGrafter.Family)
        {
            if (car.TryMesh($"?lod=0&{part}&mesh") is not { Empty: false } m) continue;
            foreach (var s in m.Subs.Where(s => s.ICount > 0)) Collect(car, m, s, nt, world, toWheel, tris);
        }
        if (tris.Count == 0) throw new InvalidDataException($"{donorId}: no steering wheel geometry");
        return Rasterise(tris, diffuse, decals, width, height);
    }

    static void Collect(CarModel car, MeshDesc m, SubMesh s, NodeTable nt, Func<int, Matrix4x4> world, Matrix4x4 toWheel, List<Tri> tris)
    {
        var st = m.StreamEaids(s);
        var pe = st.FirstOrDefault(e => car.Buf(e).Fvf == BufInfo.FvfPosition);
        var halfs = st.Where(e => car.Buf(e).Fvf == BufInfo.FvfHalf4).ToArray();
        if (pe is null || halfs.Length < 3) return;
        var be = st.FirstOrDefault(e => car.Buf(e).Fvf == BufInfo.FvfBones);
        var pos = MemoryMarshal.Cast<byte, float>(car.Frags(pe)[1]).ToArray();
        var hn = car.Frags(halfs[0])[1]; var hu = car.Frags(halfs[2])[1];
        int sn = car.Buf(halfs[0]).Stride, su = car.Buf(halfs[2]).Stride;
        var bones = be is null ? null : car.Frags(be)[1]; int bs = be is null ? 0 : car.Buf(be).Stride;
        var ib = car.Frags(m.IndexBuffer)[1]; bool wide = car.Buf(m.IndexBuffer).Stride == 4;
        var slot = WheelGrafter.NormSlot(s.Name);
        int kind = slot.StartsWith("carbon") ? KCarbon : slot is "lcd" or "digirevs" ? KScreen : KWheel;
        var mats = new Dictionary<int, Matrix4x4>();
        float H(byte[] d, int o) => (float)BitConverter.ToHalf(d, o);
        (Vector3, Vector3, Vector4) V(int i)
        {
            int v = (wide ? (int)BitConverter.ToUInt32(ib, i * 4) : BitConverter.ToUInt16(ib, i * 2)) + s.VOff;
            int pal = bones is null ? -1 : bones[v * bs];
            if (!mats.TryGetValue(pal, out var mtx))
                mats[pal] = mtx = (pal >= 0 && pal < nt.Palette.Length ? world(nt.Palette[pal]) : Matrix4x4.Identity) * toWheel;
            var p = Vector3.Transform(new Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]), mtx);
            var n = Vector3.TransformNormal(new Vector3(H(hn, v * sn), H(hn, v * sn + 2), H(hn, v * sn + 4)), mtx);
            return (p, n, new Vector4(H(hu, v * su), H(hu, v * su + 2), H(hu, v * su + 4), H(hu, v * su + 6)));
        }
        for (int i = s.IOff; i + 2 < s.IOff + s.ICount; i += 3)
        {
            var (a, na, ua) = V(i); var (b, nb, ub) = V(i + 1); var (c, nc, uc) = V(i + 2);
            if (a == b || b == c || a == c) continue;
            tris.Add(new Tri(a, b, c, na, nb, nc, ua, ub, uc, kind));
        }
    }

    static Img? Decode(DdsFile dds, int maxSize)
    {
        try
        {
            using var ms = new MemoryStream();
            dds.Write(ms);
            ms.Position = 0;
            var levels = new BcDecoder().DecodeAllMipMaps2D(ms);
            var img = levels.FirstOrDefault(l => l.Width <= maxSize && l.Height <= maxSize);
            if (img.IsEmpty) img = levels[^1];
            var rgba = new byte[img.Width * img.Height * 4];
            for (int y = 0; y < img.Height; y++) MemoryMarshal.AsBytes(img.Span.GetRowSpan(y)).CopyTo(rgba.AsSpan(y * img.Width * 4));
            return new Img(img.Width, img.Height, rgba);
        }
        catch (Exception) { return null; }
    }

    static Vector4 Sample(Img t, float u, float v, bool wrap)
    {
        if (wrap) { u -= MathF.Floor(u); v -= MathF.Floor(v); }
        else if (u < 0 || v < 0 || u > 1 || v > 1) return Vector4.Zero;
        float x = u * t.W - 0.5f, y = v * t.H - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        Vector4 Px(int px, int py)
        {
            px = ((px % t.W) + t.W) % t.W; py = ((py % t.H) + t.H) % t.H;
            int o = (py * t.W + px) * 4;
            return new Vector4(t.Rgba[o], t.Rgba[o + 1], t.Rgba[o + 2], t.Rgba[o + 3]) / 255f;
        }
        return Vector4.Lerp(Vector4.Lerp(Px(x0, y0), Px(x0 + 1, y0), fx), Vector4.Lerp(Px(x0, y0 + 1), Px(x0 + 1, y0 + 1), fx), fy);
    }

    static Vector3 Rgb(Vector4 c) => new(c.X, c.Y, c.Z);
    static Vector3 Lin(Vector3 c) => new(MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f));

    static byte[] Rasterise(List<Tri> tris, Img? diffuse, Img? decals, int width, int height)
    {
        const int SS = 3;
        int W = width * SS, Hh = height * SS;
        // camera: the preview's 3/4 view in steering-wheel space (driver at -z), framed on the wheel
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var t in tris) { lo = Vector3.Min(lo, Vector3.Min(t.A, Vector3.Min(t.B, t.C))); hi = Vector3.Max(hi, Vector3.Max(t.A, Vector3.Max(t.B, t.C))); }
        var target = (lo + hi) / 2;
        float radius = (hi - lo).Length() / 2, fov = 30f * MathF.PI / 180f, yaw = 0.75f, pitch = 0.32f;
        float dist = radius / MathF.Sin(fov / 2) * 1.1f;
        var dir = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), -MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
        var eye = target - dir * dist;
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(fov, (float)W / Hh, 0.01f, dist + radius * 3);
        var vp = view * proj;

        var depth = new float[W * Hh]; Array.Fill(depth, float.MaxValue);
        var color = new Vector3[W * Hh];
        var cover = new bool[W * Hh];
        var key = Vector3.Normalize(new Vector3(-0.35f, 0.8f, -0.6f)); var fill = Vector3.Normalize(new Vector3(0.6f, 0.25f, 0.45f));

        Vector3 Shade(int kind, Vector3 n, Vector3 p, Vector4 uv)
        {
            var v = Vector3.Normalize(eye - p);
            if (Vector3.Dot(n, v) < 0) n = -n;
            Vector3 alb; float spec, shin;
            switch (kind)
            {
                case KCarbon: alb = new Vector3(0.025f); spec = 0.35f; shin = 60; break;
                case KScreen: alb = new Vector3(0.008f); spec = 0.5f; shin = 120; break;
                default:
                    alb = diffuse is null ? new Vector3(0.15f) : Lin(Rgb(Sample(diffuse, uv.X, uv.Y, true)));
                    spec = 0.18f; shin = 30; break;
            }
            if (decals is not null && kind != KScreen)
            {
                var l = Sample(decals, uv.Z, uv.W, false);
                if (l.W > 0) alb = Vector3.Lerp(alb, Lin(Rgb(l)), l.W);
            }
            var c = alb * (0.32f * (0.65f + 0.35f * n.Y));
            foreach (var (l, k) in new[] { (key, 2.6f), (fill, 0.8f) })
            {
                float nl = MathF.Max(Vector3.Dot(n, l), 0);
                var h = Vector3.Normalize(l + v);
                c += (alb * nl + new Vector3(spec * MathF.Pow(MathF.Max(Vector3.Dot(n, h), 0), shin))) * k * 0.45f;
            }
            return c;
        }

        foreach (var t in tris)
        {
            var ca = Vector4.Transform(new Vector4(t.A, 1), vp); var cb = Vector4.Transform(new Vector4(t.B, 1), vp); var cc = Vector4.Transform(new Vector4(t.C, 1), vp);
            if (ca.W <= 0.01f || cb.W <= 0.01f || cc.W <= 0.01f) continue;
            Vector3 S(Vector4 q) => new((q.X / q.W * 0.5f + 0.5f) * W, (1 - (q.Y / q.W * 0.5f + 0.5f)) * Hh, q.Z / q.W);
            var a = S(ca); var b = S(cb); var c = S(cc);
            float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (MathF.Abs(area) < 1e-6f) continue;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(Hh - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            float iwa = 1 / ca.W, iwb = 1 / cb.W, iwc = 1 / cc.W;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) / area;
                    float w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                    int o = y * W + x;
                    if (z >= depth[o]) continue;
                    depth[o] = z;
                    // perspective-correct attributes
                    float pa = w0 * iwa, pb = w1 * iwb, pc = w2 * iwc, ps = pa + pb + pc;
                    pa /= ps; pb /= ps; pc /= ps;
                    var n = Vector3.Normalize(t.NA * pa + t.NB * pb + t.NC * pc);
                    var p = t.A * pa + t.B * pb + t.C * pc;
                    var uv = t.UA * pa + t.UB * pb + t.UC * pc;
                    color[o] = Shade(t.Kind, float.IsNaN(n.X) ? Vector3.UnitY : n, p, uv);
                    cover[o] = true;
                }
        }

        // frame by what was drawn (stray pieces off the wheel would skew a bounding-box framing): the covered area, padded,
        // area-averaged into the output; filmic-ish tone curve, sRGB, alpha = coverage
        int cx0 = W, cy0 = Hh, cx1 = -1, cy1 = -1;
        for (int y = 0; y < Hh; y++)
            for (int x = 0; x < W; x++)
                if (cover[y * W + x]) { cx0 = Math.Min(cx0, x); cx1 = Math.Max(cx1, x); cy0 = Math.Min(cy0, y); cy1 = Math.Max(cy1, y); }
        var rgba = new byte[width * height * 4];
        if (cx1 < 0) return BuildReport.Png(width, height, rgba);
        float bw = cx1 - cx0 + 1, bh = cy1 - cy0 + 1;
        float scale = MathF.Max(bw / (width * 0.9f), bh / (height * 0.9f));   // source pixels per output pixel
        float ox = (cx0 + cx1 + 1) / 2f - width * scale / 2, oy = (cy0 + cy1 + 1) / 2f - height * scale / 2;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int sx0 = (int)MathF.Floor(ox + x * scale), sx1 = (int)MathF.Ceiling(ox + (x + 1) * scale);
                int sy0 = (int)MathF.Floor(oy + y * scale), sy1 = (int)MathF.Ceiling(oy + (y + 1) * scale);
                var sum = Vector3.Zero; int n = 0, all = 0;
                for (int sy = sy0; sy < sy1; sy++)
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        all++;
                        if (sx < 0 || sy < 0 || sx >= W || sy >= Hh || !cover[sy * W + sx]) continue;
                        var c = color[sy * W + sx];
                        sum += c * (c * 2.51f + new Vector3(0.03f)) / (c * (c * 2.43f + new Vector3(0.59f)) + new Vector3(0.14f));
                        n++;
                    }
                if (n == 0) continue;
                var col = sum / n;
                int q = (y * width + x) * 4;
                rgba[q] = (byte)(MathF.Pow(Math.Clamp(col.X, 0, 1), 1 / 2.2f) * 255);
                rgba[q + 1] = (byte)(MathF.Pow(Math.Clamp(col.Y, 0, 1), 1 / 2.2f) * 255);
                rgba[q + 2] = (byte)(MathF.Pow(Math.Clamp(col.Z, 0, 1), 1 / 2.2f) * 255);
                rgba[q + 3] = (byte)(255 * n / Math.Max(all, 1));
            }
        return BuildReport.Png(width, height, rgba);
    }
}
