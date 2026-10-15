using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;

namespace WheelSwap.Core;

public sealed class VerifyResult
{
    public int Checks { get; set; }
    public List<string> Failures { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool Ok => Failures.Count == 0;

    internal void Check(bool ok, string failure) { Checks++; if (!ok) Failures.Add(failure); }
}

/// <summary>
/// Independent checks on a finished build. Everything is re-read from the output ERP bytes (not the engine's
/// in-memory objects) and compared against the untouched template, the stock donor car and the game data.
/// </summary>
public static class BuildVerifier
{
    static readonly Regex ClipRef = new(@"/idf/([^/""]+)\.idf\?context=animation&clip=([A-Za-z0-9_]+)", RegexOptions.Compiled);

    public static VerifyResult Verify(GameData game, SerpsTemplate tpl, BuildResult res, BuiltEntry be)
    {
        var v = new VerifyResult();
        try { Run(game, tpl, res, be, v); }
        catch (Exception ex) { v.Failures.Add($"verifier crashed: {ex.GetType().Name}: {ex.Message}"); }
        return v;
    }

    static void Run(GameData game, SerpsTemplate tpl, BuildResult res, BuiltEntry be, VerifyResult v)
    {
        var outErp = ErpX.Load(res.Replaced[be.Entry.ZipPath]);
        var outIdx = ErpX.Index(outErp);
        var orig = ErpX.Index(ErpX.Load(tpl.Get(be.Entry.ZipPath)));
        var privNames = be.Renames.Values.Select(p => $"/idf/{p}.idf").ToList();
        bool IsPrivate(string id) => privNames.Any(p => id.Contains(p, StringComparison.Ordinal));

        // 1. hashes of everything new or changed
        int badHash = 0;
        foreach (var r in outErp.Resources)
            if ((!orig.TryGetValue(r.Identifier, out var o) || !o.Hash.AsSpan().SequenceEqual(r.Hash)) && !ErpX.HashValid(r)) badHash++;
        v.Check(badHash == 0, $"{badHash} new/changed resources have a wrong hash");

        // 2. the private model loads on its own from the output entry
        var pm = new CarModel(game, be.PrivateToken, [ErpX.Load(res.Replaced[be.Entry.ZipPath])]);
        v.Check(pm.MeshToken == be.Renames[be.MeshToken], $"private model geometry namespace is {pm.MeshToken}, expected {be.Renames[be.MeshToken]}");

        // 3. every private reference resolves inside the entry
        int missing = 0;
        foreach (var r in outErp.Resources.Where(r => IsPrivate(r.Identifier)))
            foreach (var f in r.Fragments)
                foreach (var e in ErpX.Eaids(f.GetDataArray(true)))
                    if (IsPrivate(e) && !outIdx.ContainsKey(e)) missing++;
        v.Check(missing == 0, $"{missing} references to the private model are missing");

        // 4. car-model layers point at the private model
        var layers = outErp.Resources.Where(r => r.Identifier.EndsWith("-carmodel.layer") || r.Identifier.EndsWith("-carshowroommodel.layer")).ToList();
        var toPriv = $"/idf/{be.PrivateToken}.idf?context=render";
        var toOld = $"/idf/{be.ModelToken}.idf?context=render";
        string Txt(ErpResource r) => System.Text.Encoding.ASCII.GetString(ErpX.Data(r));
        v.Check(layers.Any(r => r.Identifier.EndsWith("-carmodel.layer") && Txt(r).Contains(toPriv)), "no car-model layer points at the private model");
        v.Check(!layers.Any(r => Txt(r).Contains(toOld)), "a car-model layer still points at the original model");
        if (be.Entry.Kind == EntryKind.MyTeamLivery)
            v.Check(outIdx.ContainsKey("eaid://f1_2026_vehicle_package/teams/fom_car/wep/fom_car-carmodel.layer"), "MyTeam: the fom_car slot layer was not redirected");

        // 5. steering cluster nodes
        var target = tpl.LoadModel(game, be.Entry);
        var (dTok, dSeason) = GameData.ParseDonor(be.DonorToken);
        var donor = new CarModel(game, dTok, dSeason);
        var tn = NodeTable.Parse(target.Render); var dn = NodeTable.Parse(donor.Render); var pn = NodeTable.Parse(pm.Render);
        string? tables = null;
        try { NodeTable.ModelDelta(pm.Render, pm.Model, pn); } catch (InvalidDataException ex) { tables = ex.Message; }
        v.Check(tables is null, $"{tables} (the game reads both)");
        var tc = tn.SteeringCluster(); var dc = dn.SteeringCluster(); var pc = pn.SteeringCluster();
        v.Check(pc.Keys.ToHashSet().SetEquals(tc.Keys), "steering cluster node set changed");
        v.Check(Near(pn.SteerMaster.T, tn.SteerMaster.T, 1e-6f) && Near(pn.SteerMaster.Q, tn.SteerMaster.Q, 1e-6f),
                "steer_master moved (the wheel must stay on the target's steering column)");
        var dSM = Local(dn.SteerMaster); var pSM = Local(pn.SteerMaster);
        int badNodes = 0;
        foreach (var (key, p) in pc)
        {
            if (p.Index == pn.SteerMaster.Index) continue;
            var d = dc[key];
            if (key.Item2 == "steer_master" && key.Item1 != "steering_wheel")
            {
                var wd = (Local(d) * dSM).Translation; var wp = (Local(p) * pSM).Translation;
                if ((wd - wp).Length() > 1e-4f) badNodes++;   // driver position / gear stick keep the donor's world placement
            }
            else if (!Near(p.T, d.T, 1e-6f) || !Near(p.Q, d.Q, 1e-6f) || !Near(p.S, d.S, 1e-6f)) badNodes++;
        }
        v.Check(badNodes == 0, $"{badNodes} steering-cluster nodes do not match the donor");

        // 6-10. geometry
        var ibId = pm.Mesh("?lod=0&mesh").IndexBuffer;
        var ib = Indices(pm, ibId);
        int origIbCount = target.Buf(target.Mesh("?lod=0&mesh").IndexBuffer).Count;
        int badBounds = 0, badBones = 0, notDonor = 0, newRecords = 0;
        var clusterNodes = pc.Values.Where(x => x.Index != pn.SteerMaster.Index).Select(x => x.Index).ToHashSet();
        var donorPos = DonorVertexSets(donor, dn);
        var placedLod0 = new HashSet<(string Bone, int X, int Y, int Z)>();
        for (int lod = 0; lod < 7; lod++)
        {
            foreach (var q in new[] { $"?lod={lod}&mesh", $"?lod={lod}&steering_wheel&mesh" })
            {
                var m = pm.TryMesh(q);
                if (m is null || m.Empty) continue;
                foreach (var s in m.Subs.Where(s => s.ICount > 0))
                {
                    var streams = m.StreamEaids(s);
                    var counts = streams.Select(e => pm.Buf(e).Count).ToArray();
                    if (s.IOff + s.ICount > ib.Length || s.ICount % 3 != 0) { badBounds++; continue; }
                    uint maxI = 0;
                    for (int i = s.IOff; i < s.IOff + s.ICount; i++) maxI = Math.Max(maxI, ib[i]);
                    if (counts.Any(c => s.VOff + maxI >= c)) { badBounds++; continue; }
                    if (s.IOff < origIbCount || !q.EndsWith("&mesh") || q.Contains("steering_wheel")) continue;
                    // a new (appended) wheel submesh
                    newRecords++;
                    var bStream = streams.FirstOrDefault(e => pm.Buf(e).Fvf == BufInfo.FvfBones);
                    var posStream = streams.First(e => pm.Buf(e).Fvf == BufInfo.FvfPosition);
                    var bones = pm.Frags(bStream!)[1]; int bStride = pm.Buf(bStream!).Stride;
                    var pos = MemoryMarshal.Cast<byte, float>(pm.Frags(posStream)[1]);
                    var seen = new HashSet<uint>();
                    for (int i = s.IOff; i < s.IOff + s.ICount; i++)
                    {
                        uint vi = (uint)s.VOff + ib[i];
                        if (!seen.Add(vi)) continue;
                        int bone = bones[(int)vi * bStride];
                        if (bone >= pn.Palette.Length || !clusterNodes.Contains(pn.Palette[bone])) { badBones++; continue; }
                        var node = pn.Nodes[pn.Palette[bone]];
                        var key = (node.Name, Q(pos[(int)vi * 3]), Q(pos[(int)vi * 3 + 1]), Q(pos[(int)vi * 3 + 2]));
                        if (!donorPos.Contains(key)) notDonor++;
                        if (lod == 0) placedLod0.Add(key);
                    }
                }
            }
        }
        v.Check(badBounds == 0, $"{badBounds} submeshes point outside their index/vertex buffers");
        v.Check(badBones == 0, $"{badBones} new wheel vertices are not bound to a steering-cluster bone");
        v.Check(notDonor == 0, $"{notDonor} new wheel vertices are not donor wheel vertices (placement corrupted)");
        v.Check(newRecords > 0, "no wheel submesh points at the donor geometry");

        // the donor's main wheel body (steering wheel slot) must be fully present at LOD0
        var main = DonorSlotVertices(donor, dn, 0, WheelGrafter.NormSlot("steering wheel"));
        int lost = main.Count(k => !placedLod0.Contains(k));
        v.Check(lost == 0, $"{lost} of the donor's LOD0 wheel vertices are missing");

        // 7. the original wheel is hidden
        int visible = 0;
        for (int lod = 0; lod < 7; lod++)
            foreach (var part in WheelGrafter.Family)
            {
                var m = target.TryMesh($"?lod={lod}&{part}&mesh");
                if (m is null || m.Empty) continue;
                foreach (var s in m.Subs.Where(s => s.ICount > 0))
                    for (int i = s.IOff; i + 2 < s.IOff + s.ICount; i += 3)
                        if (!(ib[i] == ib[i + 1] && ib[i + 1] == ib[i + 2])) visible++;
            }
        v.Check(visible == 0, $"{visible} triangles of the original wheel are still visible");

        // 10. coverage per detail level (warning only: some cars have fewer wheel LODs)
        for (int lod = 1; lod < 7; lod++)
        {
            bool donorHas = WheelGrafter.Family.Any(p => donor.TryMesh($"?lod={lod}&{p}&mesh") is { Empty: false });
            var m = pm.TryMesh($"?lod={lod}&mesh");
            if (!donorHas || m is null || m.Empty) continue;
            if (!m.Subs.Any(s => s.ICount > 0 && s.IOff >= origIbCount))
                v.Warnings.Add($"LOD{lod}: no donor wheel geometry at this detail level");
        }

        // 11. driver animation
        v.Check(outIdx.TryGetValue(be.AnimsetId, out var anim), "driver animation set missing from the entry");
        if (anim is not null)
        {
            var json = AnimsetSwap.ToJson(ErpX.Data(anim)).Replace("\\u0026", "&");
            var clips = ClipRef.Matches(json).Select(m => (File: m.Groups[1].Value, Clip: m.Groups[2].Value)).ToList();
            var (aTok, aSeason) = GameData.ParseDonor(be.AnimationFrom);
            var incar = ErpX.Index(ErpX.Load(game.IncarDriverErpFor(aSeason)));
            var donorAnim = AnimsetSwap.ToJson(ErpX.Data(incar[GameData.AnimsetId(GameData.AnimsetName(aTok, aSeason))])).Replace("\\u0026", "&");
            var expectFile = ClipRef.Match(donorAnim).Groups[1].Value;
            v.Check(clips.Count > 0 && clips.All(c => c.File == expectFile), $"driver animation does not use the donor's clips ({expectFile})");
            string ClipId((string File, string Clip) c) => $"{GameData.AnimPrefix}{c.File}.idf?context=animation&clip={c.Clip}";
            int missingClips = clips.Count(c => !incar.ContainsKey(ClipId(c)));
            v.Check(missingClips == 0, $"{missingClips} driver animation clips do not exist in the game");
            if (aSeason != GameData.CurrentSeason)
            {
                // an older season's clips must travel in the entry, byte for byte as the game has them
                int notCarried = clips.Count(c => !outIdx.TryGetValue(ClipId(c), out var e) || !incar.TryGetValue(ClipId(c), out var g) || !e.Hash.AsSpan().SequenceEqual(g.Hash));
                v.Check(notCarried == 0, $"{notCarried} {aSeason} driver animation clips are not carried in the entry");
            }

            // what the game actually has while running the 2026 season: the 2026 in-car files plus this entry. Every
            // clip and everything a clip uses (its animstringtable) must resolve there, or the game crashes on track.
            var live = aSeason == GameData.CurrentSeason ? incar : ErpX.Index(ErpX.Load(game.IncarDriverErp));
            ErpResource? Live(string id) => outIdx.TryGetValue(id, out var e) ? e : live.TryGetValue(id, out var g) ? g : null;
            var unresolved = new List<string>();
            foreach (var c in clips)
            {
                var clip = Live(ClipId(c));
                if (clip is null) { unresolved.Add(ClipId(c)); continue; }
                foreach (var f in clip.Fragments)
                    foreach (var e in ErpX.Eaids(f.GetDataArray(true)))
                        if (Live(e) is null) unresolved.Add(e);
            }
            v.Check(unresolved.Count == 0, $"{unresolved.Distinct().Count()} driver animation resources would be missing in the {GameData.CurrentSeason} season: " +
                                           string.Join(", ", unresolved.Distinct().Take(3).Select(x => x.Split('/')[^1])));
        }

        // 12. wheel textures: the donor's pixels, embedded at the donor's resolution
        var donorTails = DonorTextureTails(donor);
        foreach (var t in be.TextureIds)
        {
            var ch = TextureSwap.ChannelOf(t);
            if (ch is null || !be.DonorTextureSizes.TryGetValue(ch, out var size)) continue;
            if (!outIdx.TryGetValue(t, out var srvRes)) { v.Check(false, $"texture {t.Split('/')[^1]} missing"); continue; }
            var srv = new ErpGfxSRVResource();
            srv.FromResource(srvRes);
            v.Check(!srv.SurfaceRes.HasMips, $"texture {t.Split('/')[^1]} still streams external mipmaps");
            v.Check(srv.SurfaceRes.Width == size.W && srv.SurfaceRes.Height == size.H,
                    $"texture {t.Split('/')[^1]} is {srv.SurfaceRes.Width}x{srv.SurfaceRes.Height}, donor is {size.W}x{size.H}");
            // the small mip levels the donor keeps inside its own ERP must be the tail of the embedded chain
            if (donorTails.TryGetValue(ch, out var tail) && outIdx.TryGetValue(srv.Fragment0.SurfaceResourceName, out var surf))
                v.Check(ErpX.Data(surf, 1).AsSpan().EndsWith(tail), $"texture {t.Split('/')[^1]} does not hold the donor's pixels");
        }
    }

    static Dictionary<string, byte[]> DonorTextureTails(CarModel donor)
    {
        var res = new Dictionary<string, byte[]>();
        var mat = donor.Res.Keys.FirstOrDefault(k => k.EndsWith($"/materials/{donor.Token}_steeringwheel.material", StringComparison.Ordinal))
                  ?? donor.Res.Keys.FirstOrDefault(k => k.EndsWith("steeringwheel.material", StringComparison.Ordinal));
        if (mat is null) return res;
        foreach (var tex in ErpX.Eaids(ErpX.Data(donor.Res[mat])).Where(e => e.EndsWith(".tif")).Distinct())
        {
            if (TextureSwap.ChannelOf(tex) is not { } ch || res.ContainsKey(ch) || !donor.Res.TryGetValue(tex, out var r)) continue;
            var srv = new ErpGfxSRVResource();
            srv.FromResource(r);
            if (donor.Res.TryGetValue(srv.Fragment0.SurfaceResourceName, out var s) && s.Fragments.Count > 1) res[ch] = ErpX.Data(s, 1);
        }
        return res;
    }

    // donor wheel-family vertices keyed by (bone node name, quantised position)
    static HashSet<(string, int, int, int)> DonorVertexSets(CarModel donor, NodeTable dn)
    {
        var set = new HashSet<(string, int, int, int)>();
        for (int lod = 0; lod < 7; lod++)
            foreach (var part in WheelGrafter.Family)
            {
                var m = donor.TryMesh($"?lod={lod}&{part}&mesh");
                if (m is null || m.Empty) continue;
                foreach (var s in m.Subs.Where(s => s.ICount > 0)) AddVerts(donor, dn, m, s, set);
            }
        return set;
    }

    static HashSet<(string, int, int, int)> DonorSlotVertices(CarModel donor, NodeTable dn, int lod, string slot)
    {
        var set = new HashSet<(string, int, int, int)>();
        var m = donor.TryMesh($"?lod={lod}&steering_wheel&mesh");
        if (m is null || m.Empty) return set;
        foreach (var s in m.Subs.Where(s => s.ICount > 0 && WheelGrafter.NormSlot(s.Name) == slot)) AddVerts(donor, dn, m, s, set);
        return set;
    }

    static void AddVerts(CarModel car, NodeTable nt, MeshDesc m, SubMesh s, HashSet<(string, int, int, int)> set)
    {
        var streams = m.StreamEaids(s);
        var b = streams.FirstOrDefault(e => car.Buf(e).Fvf == BufInfo.FvfBones);
        var p = streams.FirstOrDefault(e => car.Buf(e).Fvf == BufInfo.FvfPosition);
        if (b is null || p is null) return;
        var ib = Indices(car, m.IndexBuffer);
        var bones = car.Frags(b)[1]; int bs = car.Buf(b).Stride;
        var pos = MemoryMarshal.Cast<byte, float>(car.Frags(p)[1]);
        for (int i = s.IOff; i < s.IOff + s.ICount; i++)
        {
            int vi = (int)ib[i] + s.VOff;
            int bone = bones[vi * bs];
            set.Add((nt.Nodes[nt.Palette[bone]].Name, Q(pos[vi * 3]), Q(pos[vi * 3 + 1]), Q(pos[vi * 3 + 2])));
        }
    }

    static int Q(float f) => (int)MathF.Round(f * 100000f);   // 0.01 mm

    static uint[] Indices(CarModel car, string eaid)
    {
        var d = car.Frags(eaid)[1];
        return car.Buf(eaid).Stride == 2
            ? MemoryMarshal.Cast<byte, ushort>(d).ToArray().Select(x => (uint)x).ToArray()
            : MemoryMarshal.Cast<byte, uint>(d).ToArray();
    }

    static bool Near(float[] a, float[] b, float eps)
    {
        for (int i = 0; i < a.Length; i++) if (MathF.Abs(a[i] - b[i]) > eps) return false;
        return true;
    }

    static Matrix4x4 Local(Node n) =>
        Matrix4x4.CreateScale(n.S[0], n.S[1], n.S[2]) *
        Matrix4x4.CreateFromQuaternion(new Quaternion(n.Q[0], n.Q[1], n.Q[2], n.Q[3])) *
        Matrix4x4.CreateTranslation(n.T[0], n.T[1], n.T[2]);
}
