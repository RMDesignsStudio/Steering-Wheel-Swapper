using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using EgoEngineLibrary.Archive.Erp;

namespace WheelSwap.Core;

/// <summary>
/// Full steering-wheel swap on a private copy of the target car's model ("v6" method, proven in game):
///  1. every node under steer_master (wheel, paddle/dial pivots, hand + button IK targets) gets the
///     donor's transform, except steer_master itself, so the wheel stays on the target's column and the
///     donor in-car animation's hand targets ride along with it; non-wheel children of steer_master
///     (driver position, gear stick) keep the donor's world position;
///  2. the donor wheel family (wheel, paddles, dials) is appended to the target's shared vertex/index
///     buffers with vertex bones remapped by node name, the target wheel family triangles are made
///     degenerate, and the target wheel submeshes (merged LOD meshes + steering_wheel part meshes) are
///     repointed at the new geometry;
///  3. every <c>target.idf</c> resource is cloned as <c>private.idf</c> (same length), so nothing global changes.
/// </summary>
public static class WheelGrafter
{
    public static readonly string[] Family = ["steering_wheel", "left_paddle", "right_paddle", "mix_dial", "tyre_dial"];

    /// <summary>Wheel pieces painted with the car body livery: meaningless on another car's paint.</summary>
    static readonly HashSet<string> SkipSlots = ["body"];

    public static string NormSlot(string s) => GameData.Normalize(s).Replace("steeringweel", "steeringwheel");

    sealed record Block(int IOff, int ICount, int VOff, float Cx, float Cy, float Cz, float R);

    /// <param name="privateName">maps each model namespace (idf name) to a same-length private name</param>
    /// <returns>the cloned model resources, the private name of the render/model namespace, and all renames</returns>
    public static (List<ErpResource> Clones, string PrivateToken, Dictionary<string, string> Renames) Run(CarModel target, CarModel donor, Func<string, string> privateName, SwapLog log)
    {

        // ---------- 1. steering cluster nodes ----------
        var tRender = target.Render; var tModel = target.Model;
        var tn = NodeTable.Parse(tRender); var dn = NodeTable.Parse(donor.Render);
        int delta = NodeTable.ModelDelta(tRender, tModel, tn);
        var tc = tn.SteeringCluster(); var dc = dn.SteeringCluster();
        if (!tc.Keys.ToHashSet().SetEquals(dc.Keys))
            throw new InvalidDataException("The donor and target steering clusters have different nodes: " +
                string.Join(", ", tc.Keys.ToHashSet().SymmetricExceptWith2(dc.Keys)));

        var tSM = tn.SteerMaster; var dSM = dn.SteerMaster;
        var tSMm = Local(tSM); var dSMm = Local(dSM);
        Matrix4x4.Invert(tSMm, out var tSMinv);
        foreach (var (key, tnode) in tc)
        {
            if (tnode.Index == tSM.Index) continue;
            var dnode = dc[key];
            NodeTable.CopyTrs(donor.Render, dnode.Offset, tRender, tnode.Offset);
            NodeTable.CopyTrs(donor.Render, dnode.Offset, tModel, tnode.Offset + delta);
            if (key.Item2 == "steer_master" && key.Item1 != "steering_wheel")
            {
                // driver position / gear stick: keep the donor's world placement
                var world = Local(dnode) * dSMm;
                var local = world * tSMinv;
                Matrix4x4.Decompose(local, out _, out var q, out var t);
                NodeTable.WriteTQ(tRender, tnode.Offset, t, q); NodeTable.WriteTQ(tModel, tnode.Offset + delta, t, q);
            }
        }
        target.Touch(target.RenderId); target.Touch(target.ModelId);
        log.Info($"Copied {tc.Count - 1} steering-cluster nodes from {donor.Token}; the wheel stays on the {target.Token} steering column " +
                 $"(column {tSM.T[1] * 1000:F0} mm high, donor {dSM.T[1] * 1000:F0} mm).");

        var boneMap = new Dictionary<int, byte>();
        byte MapBone(int b)
        {
            if (boneMap.TryGetValue(b, out var m)) return m;
            var node = dn.Nodes[dn.Palette[b]];
            if (!tc.TryGetValue(dn.Key(node), out var tnode)) throw new InvalidDataException($"donor wheel vertex bound to '{node.Name}', outside the steering cluster");
            int ti = tn.PaletteIndexOf(tnode.Index);
            if (ti < 0 || ti > 255) throw new InvalidDataException($"target has no bone palette entry for '{node.Name}'");
            return boneMap[b] = (byte)ti;
        }

        // ---------- 2. geometry ----------
        var ibId = target.Mesh("?lod=0&mesh").IndexBuffer;
        int ibStride = target.Buf(ibId).Stride;
        var ibOrig = ReadIndices(target, ibId);
        var ibExtra = new List<uint>();
        var pending = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int Count(string e) => counts.TryGetValue(e, out var c) ? c : (counts[e] = target.Buf(e).Count);
        var blocks = new Dictionary<(int, string), Block>();
        var famRanges = new Dictionary<int, List<(int A, int B)>>();
        var donorIdx = new Dictionary<string, uint[]>(StringComparer.Ordinal);

        bool InsideFamily(int lod, int a, int b)
        {
            var cov = new bool[b - a];
            foreach (var (x, y) in famRanges[lod])
                for (int i = Math.Max(a, x); i < Math.Min(b, y); i++) cov[i - a] = true;
            return cov.All(c => c);
        }

        for (int lod = 0; lod < 7; lod++)
        {
            var merged = target.TryMesh($"?lod={lod}&mesh");
            if (merged is null || merged.Empty) continue;
            if (merged.IndexBuffer != ibId) throw new InvalidDataException($"LOD{lod} uses a different index buffer");
            var ranges = famRanges[lod] = [];
            foreach (var part in Family)
            {
                var m = target.TryMesh($"?lod={lod}&{part}&mesh");
                if (m is null || m.Empty) continue;
                ranges.AddRange(m.Subs.Where(s => s.ICount > 0).Select(s => (s.IOff, s.IOff + s.ICount)));
            }
            // target wheel submeshes that can be repointed (wholly wheel geometry)
            var avail = new Dictionary<string, SubMesh>();
            foreach (var s in merged.Subs.Where(s => s.ICount > 0 && InsideFamily(lod, s.IOff, s.IOff + s.ICount)))
                avail.TryAdd(NormSlot(s.Name), s);

            var groups = new Dictionary<string, List<(MeshDesc M, SubMesh S)>>();   // keyed by TARGET slot
            bool donorHasLod = false;
            foreach (var part in Family)
            {
                var m = donor.TryMesh($"?lod={lod}&{part}&mesh");
                if (m is null || m.Empty) continue;
                foreach (var s in m.Subs.Where(s => s.ICount > 0))
                {
                    donorHasLod = true;
                    var dslot = NormSlot(s.Name);
                    if (SkipSlots.Contains(dslot)) { log.Info($"LOD{lod}: donor '{s.Name}' piece skipped (painted with the donor's livery)."); continue; }
                    var tslot = Resolve(dslot, avail);
                    if (tslot is null)
                    {
                        var msg = dslot == "number"
                            ? $"LOD{lod}: the donor wheel's driver number can't be placed on this car's wheel, so it is left out (the wheel shows no number)."
                            : $"LOD{lod}: the target car has no wheel '{s.Name}' slot; that part of the donor wheel is left out.";
                        if (lod == 0) log.Warn(msg); else log.Info(msg);
                        continue;
                    }
                    if (tslot != dslot) log.Info($"LOD{lod}: donor '{s.Name}' drawn with the target's '{avail[tslot].Name}' material.");
                    if (!groups.TryGetValue(tslot, out var g)) groups[tslot] = g = [];
                    g.Add((m, s));
                }
            }
            if (ranges.Count == 0 && !donorHasLod) continue;
            if (!donorHasLod) { log.Warn($"LOD{lod}: the donor has no wheel at this detail level; the target wheel is hidden there."); continue; }

            foreach (var (slot, allItems) in groups.OrderBy(g => g.Key))
            {
                var tsub = avail[slot];
                var tstreams = merged.StreamEaids(tsub);
                var items = new List<(MeshDesc M, SubMesh S, int[] Plan)>();
                foreach (var it in allItems)
                {
                    var (plan, why) = StreamPlan(donor, target, it, tstreams);
                    if (plan is not null) items.Add((it.M, it.S, plan));
                    else log.Warn($"LOD{lod} '{it.S.Name}': {why}; that part of the donor wheel is left out.");
                }
                if (items.Count == 0) continue;
                int baseV = Count(tstreams[0]);
                if (tstreams.Any(e => Count(e) != baseV)) throw new InvalidDataException($"LOD{lod} '{slot}': target vertex streams have different counts");
                var chunks = tstreams.Select(_ => new List<byte[]>()).ToArray();
                var newIdx = new List<uint>();
                var pos = new List<float>();
                int nverts = 0;
                foreach (var (m, s, plan) in items)
                {
                    var sstreams = m.StreamEaids(s);
                    if (!donorIdx.TryGetValue(m.IndexBuffer, out var dib)) donorIdx[m.IndexBuffer] = dib = ReadIndices(donor, m.IndexBuffer);
                    var global = new int[s.ICount];
                    for (int i = 0; i < s.ICount; i++) global[i] = (int)dib[s.IOff + i] + s.VOff;
                    var uniq = global.Distinct().Order().ToArray();
                    var remap = new Dictionary<int, int>(uniq.Length);
                    for (int i = 0; i < uniq.Length; i++) remap[uniq[i]] = i;
                    for (int k = 0; k < tstreams.Length; k++)
                    {
                        if (plan[k] < 0)
                        {
                            // stream the donor piece lacks (the carbon slots' constant second UV): repeat the
                            // target slot's own value for every vertex
                            var tb = target.Buf(tstreams[k]);
                            var fill = target.Frags(tstreams[k])[1].AsSpan(((int)ibOrig[tsub.IOff] + tsub.VOff) * tb.Stride, tb.Stride).ToArray();
                            var filled = new byte[uniq.Length * tb.Stride];
                            for (int i = 0; i < uniq.Length; i++) fill.CopyTo(filled, i * tb.Stride);
                            chunks[k].Add(filled);
                            continue;
                        }
                        var sb = donor.Buf(sstreams[plan[k]]);
                        var src = donor.Frags(sstreams[plan[k]])[1];
                        var rows = new byte[uniq.Length * sb.Stride];
                        for (int i = 0; i < uniq.Length; i++) Buffer.BlockCopy(src, uniq[i] * sb.Stride, rows, i * sb.Stride, sb.Stride);
                        if (sb.Fvf == BufInfo.FvfBones)
                            for (int i = 0; i < uniq.Length; i++) rows[i * sb.Stride] = MapBone(rows[i * sb.Stride]);
                        if (sb.Fvf == BufInfo.FvfPosition && sb.Stride == 12)
                            pos.AddRange(MemoryMarshal.Cast<byte, float>(rows).ToArray());
                        chunks[k].Add(rows);
                    }
                    foreach (var g in global) newIdx.Add((uint)(remap[g] + nverts));
                    nverts += uniq.Length;
                }
                if (ibStride == 2 && nverts > 65536) throw new InvalidDataException($"LOD{lod} '{slot}': too many vertices for 16-bit indices");
                for (int k = 0; k < tstreams.Length; k++)
                {
                    if (!pending.TryGetValue(tstreams[k], out var pl)) pending[tstreams[k]] = pl = [];
                    pl.AddRange(chunks[k]); counts[tstreams[k]] = baseV + nverts;
                }
                int ioff = ibOrig.Length + ibExtra.Count;
                ibExtra.AddRange(newIdx);
                var (cx, cy, cz, r) = Sphere(pos);
                blocks[(lod, slot)] = new Block(ioff, newIdx.Count, baseV, cx, cy, cz, r);
                log.Info($"LOD{lod} '{tsub.Name}': {nverts} donor vertices, {newIdx.Count / 3} triangles");
            }
        }

        // hide the target's own wheel family, append the new indices / vertices
        int hidden = 0;
        foreach (var (_, ranges) in famRanges)
            foreach (var (a, b) in ranges) { Array.Clear(ibOrig, a, b - a); hidden += b - a; }
        var ibNew = new uint[ibOrig.Length + ibExtra.Count];
        ibOrig.CopyTo(ibNew, 0); ibExtra.CopyTo(ibNew, ibOrig.Length);
        target.Set(ibId, 1, WriteIndices(ibNew, ibStride));
        SetCount(target, ibId, ibNew.Length);
        foreach (var (e, chunks) in pending)
        {
            var old = target.Frags(e)[1];
            var all = new byte[old.Length + chunks.Sum(c => c.Length)];
            old.CopyTo(all, 0); int p = old.Length;
            foreach (var c in chunks) { c.CopyTo(all, p); p += c.Length; }
            target.Set(e, 1, all);
            SetCount(target, e, counts[e]);
            if (all.Length != counts[e] * target.Buf(e).Stride) throw new InvalidDataException("vertex buffer size mismatch");
        }
        log.Info($"Hid {hidden / 3} triangles of the original wheel.");

        // repoint the wheel submeshes
        var used = new HashSet<(int, string)>();
        foreach (var lod in famRanges.Keys)
        {
            foreach (var q in new[] { $"?lod={lod}&mesh", $"?lod={lod}&steering_wheel&mesh" })
            {
                if (!target.Has(q)) continue;
                var d = target.Frags(target.MeshId(q))[0];
                var m = MeshDesc.Parse(d);
                if (m.Empty) continue;
                foreach (var s in m.Subs.Where(s => s.ICount > 0))
                {
                    var key = (lod, NormSlot(s.Name));
                    if (!blocks.TryGetValue(key, out var bl) || !InsideFamily(lod, s.IOff, s.IOff + s.ICount)) continue;
                    MeshDesc.PatchRecord(d, s.Index, bl.IOff, bl.ICount, bl.VOff, bl.Cx, bl.Cy, bl.Cz, bl.R);
                    used.Add(key);
                }
                target.Touch(target.MeshId(q));
            }
        }
        foreach (var k in blocks.Keys.Where(k => !used.Contains(k)))
            throw new InvalidDataException($"LOD{k.Item1}: donor '{k.Item2}' geometry could not be attached");

        target.Commit();

        // ---------- 3. clone as a private model ----------
        // every model resource the car uses (its own namespace + a swapped chassis' mesh namespace + anything
        // referenced) is renamed per namespace to a same-length private name, so nothing global changes and a
        // swapped chassis carried by the template is left untouched
        var closure = target.Closure();
        var names = CarModel.Namespaces(closure).ToDictionary(n => n, n =>
        {
            var p = privateName(n);
            if (p.Length != n.Length || p == n) throw new ArgumentException($"bad private name for {n}");
            return p;
        });
        var clones = new List<ErpResource>();
        foreach (var r in closure)
        {
            bool changed = false;
            foreach (var f in r.Fragments)
            {
                var data = f.GetDataArray(true);
                int hits = 0;
                foreach (var (n, p) in names) hits += ErpX.ReplaceInEaids(data, n + ".idf", p + ".idf");
                if (hits > 0) { f.SetData(data, true); changed = true; }
            }
            foreach (var (n, p) in names) r.Identifier = r.Identifier.Replace($"/idf/{n}.idf", $"/idf/{p}.idf", StringComparison.Ordinal);
            if (changed) ErpX.Rehash(r);
            clones.Add(r);
        }
        log.Info($"Private model: {string.Join(", ", names.Select(kv => $"{kv.Key} -> {kv.Value}"))} ({clones.Count} resources).");
        return (clones, names[target.Token], names);
    }

    /// <summary>Target slot for a donor slot: same name if the target has a wheel submesh for it, otherwise
    /// another carbon slot for carbon parts. The wheel body (steering wheel / carbon) falls back to the other body
    /// material, so a detail level never loses the whole wheel (e.g. Red Bull LOD3 has no 'steering wheel' slot,
    /// Mercedes LOD3 no carbon slot). Screen, flag, rev-light and number pieces are left out instead (null).</summary>
    static string? Resolve(string dslot, Dictionary<string, SubMesh> avail)
    {
        if (avail.ContainsKey(dslot)) return dslot;
        string? Carbon() => avail.ContainsKey("carbontile4") ? "carbontile4" : avail.Keys.Where(k => k.StartsWith("carbontile")).Order().FirstOrDefault();
        if (dslot.StartsWith("carbontile")) return Carbon() ?? (avail.ContainsKey("steeringwheel") ? "steeringwheel" : null);
        if (dslot == "steeringwheel") return Carbon();
        return null;
    }

    /// <summary>For each target vertex stream, the donor stream feeding it (same stride + format, taken in order),
    /// or -1 for the carbon slots' constant second UV (half2, fvf 0x1d) when the donor piece has none. Donor
    /// streams the target slot does not use are dropped. Identical layouts give the identity plan.</summary>
    static (int[]? Plan, string? Why) StreamPlan(CarModel donor, CarModel target, (MeshDesc M, SubMesh S) item, string[] tstreams)
    {
        var ss = item.M.StreamEaids(item.S).Select(e => donor.Buf(e)).ToArray();
        var used = new bool[ss.Length];
        var plan = new int[tstreams.Length];
        for (int k = 0; k < tstreams.Length; k++)
        {
            var b = target.Buf(tstreams[k]);
            int j = Array.FindIndex(ss, x => x.Stride == b.Stride && x.Fvf == b.Fvf);
            while (j >= 0 && used[j]) j = Array.FindIndex(ss, j + 1, x => x.Stride == b.Stride && x.Fvf == b.Fvf);
            if (j >= 0) { used[j] = true; plan[k] = j; }
            else if (b.Fvf == BufInfo.FvfHalf2 && b.Stride == 4) plan[k] = -1;
            else return (null, $"vertex layout differs (the target has a {b.Stride}-byte format {b.Fvf:x} stream the donor piece lacks)");
        }
        // anything dropped must be optional data, never position / bones
        for (int j = 0; j < ss.Length; j++)
            if (!used[j] && (ss[j].Fvf == BufInfo.FvfPosition || ss[j].Fvf == BufInfo.FvfBones))
                return (null, $"vertex layout differs (donor stream {j} has no place in the target)");
        return (plan, null);
    }

    static uint[] ReadIndices(CarModel car, string eaid)
    {
        var data = car.Frags(eaid)[1];
        return car.Buf(eaid).Stride switch
        {
            2 => MemoryMarshal.Cast<byte, ushort>(data).ToArray().Select(x => (uint)x).ToArray(),
            4 => MemoryMarshal.Cast<byte, uint>(data).ToArray(),
            var s => throw new InvalidDataException($"unsupported index size {s}"),
        };
    }

    static byte[] WriteIndices(uint[] idx, int stride)
    {
        if (stride == 4) return MemoryMarshal.AsBytes(idx.AsSpan()).ToArray();
        var s = new ushort[idx.Length];
        for (int i = 0; i < idx.Length; i++) s[i] = checked((ushort)idx[i]);
        return MemoryMarshal.AsBytes(s.AsSpan()).ToArray();
    }

    static Matrix4x4 Local(Node n) =>
        Matrix4x4.CreateScale(n.S[0], n.S[1], n.S[2]) *
        Matrix4x4.CreateFromQuaternion(new Quaternion(n.Q[0], n.Q[1], n.Q[2], n.Q[3])) *
        Matrix4x4.CreateTranslation(n.T[0], n.T[1], n.T[2]);

    static void SetCount(CarModel car, string eaid, int count)
    {
        var h = (byte[])car.Frags(eaid)[0].Clone();
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), count);
        car.Set(eaid, 0, h);
    }

    static (float, float, float, float) Sphere(List<float> p)
    {
        if (p.Count == 0) return (0, 0, 0, 0);
        float[] mn = [float.MaxValue, float.MaxValue, float.MaxValue], mx = [float.MinValue, float.MinValue, float.MinValue];
        for (int i = 0; i < p.Count; i += 3)
            for (int k = 0; k < 3; k++) { mn[k] = Math.Min(mn[k], p[i + k]); mx[k] = Math.Max(mx[k], p[i + k]); }
        float cx = (mn[0] + mx[0]) / 2, cy = (mn[1] + mx[1]) / 2, cz = (mn[2] + mx[2]) / 2, r = 0;
        for (int i = 0; i < p.Count; i += 3)
        {
            float dx = p[i] - cx, dy = p[i + 1] - cy, dz = p[i + 2] - cz;
            r = Math.Max(r, MathF.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        return (cx, cy, cz, r);
    }
}

static class SetExt
{
    public static IEnumerable<T> SymmetricExceptWith2<T>(this HashSet<T> a, IEnumerable<T> b)
    {
        var c = new HashSet<T>(a); c.SymmetricExceptWith(b); return c;
    }
}
