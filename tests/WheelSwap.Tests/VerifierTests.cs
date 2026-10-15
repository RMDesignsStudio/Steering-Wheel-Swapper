using System.Buffers.Binary;
using System.Runtime.InteropServices;
using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>
/// The verifier must pass a good build and catch each kind of damage. One real build (Ferrari wheel ->
/// Aston FOM chassis) is made once, then each test breaks one thing in a copy of the output and expects the
/// matching failure.
/// </summary>
public class VerifierTests
{
    static (SerpsTemplate Tpl, BuildResult Res)? _build;
    static readonly Lock _lock = new();

    static (GameData Game, SerpsTemplate Tpl, BuildResult Res, BuiltEntry Be) Good()
    {
        var game = Env.Game();
        lock (_lock)
        {
            if (_build is null)
            {
                var tpl = new SerpsTemplate(Env.Template("2026 Aston Martin - SERPs Template v1.1.serps"));
                var res = tpl.BuildInMemory(game, "ferrari", tpl.CarEntries.Where(e => e.Kind == EntryKind.FomChassis).ToList(), new SwapLog());
                _build = (tpl, res);
            }
            return (game, _build.Value.Tpl, _build.Value.Res, _build.Value.Res.Entries.Single());
        }
    }

    /// <summary>Copy of the build with one ERP rewritten by <paramref name="damage"/> (given the private model).</summary>
    static VerifyResult Broken(Action<CarModel, GameData, SerpsTemplate, BuiltEntry> damage)
    {
        var (game, tpl, res, be) = Good();
        var erp = ErpX.Load(res.Replaced[be.Entry.ZipPath]);
        var pm = new CarModel(game, be.PrivateToken, [erp]);
        damage(pm, game, tpl, be);
        pm.Commit();
        var copy = new BuildResult { DonorToken = res.DonorToken };
        foreach (var (k, v) in res.Replaced) copy.Replaced[k] = v;
        copy.Replaced[be.Entry.ZipPath] = ErpX.Save(erp);
        copy.Entries.Add(be);
        return BuildVerifier.Verify(game, tpl, copy, be);
    }

    static void Expect(VerifyResult v, string text)
    {
        TestContext.Current.TestOutputHelper!.WriteLine(string.Join(Environment.NewLine, v.Failures));
        Assert.Contains(v.Failures, f => f.Contains(text));
    }

    [Fact]
    public void GoodBuildPasses()
    {
        var (game, tpl, res, be) = Good();
        var v = BuildVerifier.Verify(game, tpl, res, be);
        Assert.True(v.Ok, string.Join(Environment.NewLine, v.Failures));
        Assert.True(v.Checks >= 20, $"only {v.Checks} checks ran");
    }

    [Fact]
    public void UndamagedRoundTripPasses() => Assert.True(Broken((_, _, _, _) => { }).Ok);

    static (MeshDesc M, SubMesh S, string Bones, string Ib) FirstNewWheelSub(CarModel pm, GameData game, SerpsTemplate tpl, BuiltEntry be)
    {
        int origCount = tpl.LoadModel(game, be.Entry).Buf(tpl.LoadModel(game, be.Entry).Mesh("?lod=0&mesh").IndexBuffer).Count;
        var m = pm.Mesh("?lod=0&mesh");
        var s = m.Subs.First(x => x.ICount > 0 && x.IOff >= origCount);
        return (m, s, m.StreamEaids(s).First(e => pm.Buf(e).Fvf == BufInfo.FvfBones), m.IndexBuffer);
    }

    [Fact]
    public void CatchesWheelVertexOnWrongBone() => Expect(Broken((pm, game, tpl, be) =>
    {
        var (m, s, bones, ib) = FirstNewWheelSub(pm, game, tpl, be);
        var nt = NodeTable.Parse(pm.Render);
        var cluster = nt.SteeringCluster().Values.Select(n => n.Index).ToHashSet();
        int foreign = Enumerable.Range(0, nt.Palette.Length).First(i => !cluster.Contains(nt.Palette[i]));
        var d = pm.Frags(bones)[1].ToArray();
        uint vi = (uint)s.VOff + Index(pm, ib, s.IOff);
        d[(int)vi * pm.Buf(bones).Stride] = (byte)foreign;
        pm.Set(bones, 1, d);
    }), "not bound to a steering-cluster bone");

    [Fact]
    public void CatchesOriginalWheelLeftVisible() => Expect(Broken((pm, game, tpl, be) =>
    {
        var orig = tpl.LoadModel(game, be.Entry);
        var ibId = pm.Mesh("?lod=0&mesh").IndexBuffer;
        var sub = orig.Mesh("?lod=0&steering_wheel&mesh").Subs.First(x => x.ICount > 0);
        var src = orig.Frags(orig.Mesh("?lod=0&mesh").IndexBuffer)[1];
        var d = pm.Frags(ibId)[1].ToArray();
        int w = pm.Buf(ibId).Stride;
        src.AsSpan(sub.IOff * w, 3 * w).CopyTo(d.AsSpan(sub.IOff * w));   // restore one original triangle
        pm.Set(ibId, 1, d);
    }), "triangles of the original wheel are still visible");

    [Fact]
    public void CatchesSubmeshOutsideBuffers() => Expect(Broken((pm, game, tpl, be) =>
    {
        var (_, s, _, _) = FirstNewWheelSub(pm, game, tpl, be);
        var id = pm.MeshId("?lod=0&mesh");
        var d = pm.Frags(id)[0].ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(76 + s.Index * 88 + 20), 50_000_000);   // base vertex far past the end
        pm.Set(id, 0, d);
    }), "outside their index/vertex buffers");

    [Fact]
    public void CatchesMovedWheelGeometry() => Expect(Broken((pm, game, tpl, be) =>
    {
        var (m, s, _, ib) = FirstNewWheelSub(pm, game, tpl, be);
        var pos = m.StreamEaids(s).First(e => pm.Buf(e).Fvf == BufInfo.FvfPosition);
        var d = pm.Frags(pos)[1].ToArray();
        var f = MemoryMarshal.Cast<byte, float>(d.AsSpan());
        uint vi = (uint)s.VOff + Index(pm, ib, s.IOff);
        f[(int)vi * 3 + 1] += 0.01f;   // 1 cm
        pm.Set(pos, 1, d);
    }), "are not donor wheel vertices");

    [Fact]
    public void CatchesSteerMasterMoved() => Expect(Broken((pm, _, _, _) =>
    {
        var nt = NodeTable.Parse(pm.Render);
        int delta = NodeTable.ModelDelta(pm.Render, pm.Model, nt);
        var sm = nt.SteerMaster;
        foreach (var (id, off) in new[] { (pm.RenderId, sm.Offset), (pm.ModelId, sm.Offset + delta) })
        {
            var d = pm.Frags(id)[0].ToArray();
            var t = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(off + 8));
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(off + 8), t + 0.05f);
            pm.Set(id, 0, d);
        }
    }), "steer_master moved");

    [Fact]
    public void CatchesModelAndRenderNodeTablesDisagreeing() => Expect(Broken((pm, _, _, _) =>
    {
        var nt = NodeTable.Parse(pm.Render);
        var d = pm.Frags(pm.RenderId)[0].ToArray();
        int off = nt.SteeringCluster()[("steering_wheel", "steer_master")].Offset;
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(off + 8), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(off + 8)) + 0.05f);
        pm.Set(pm.RenderId, 0, d);
    }), "?model node table differs");

    [Fact]
    public void CatchesBadHash()
    {
        var (game, tpl, res, be) = Good();
        var erp = ErpX.Load(res.Replaced[be.Entry.ZipPath]);
        var r = erp.Resources.First(x => x.Identifier.Contains($"/idf/{be.PrivateToken}.idf?render"));
        r.Hash = new byte[16];
        var copy = new BuildResult { DonorToken = res.DonorToken };
        copy.Replaced[be.Entry.ZipPath] = ErpX.Save(erp);
        copy.Entries.Add(be);
        Expect(BuildVerifier.Verify(game, tpl, copy, be), "wrong hash");
    }

    [Fact]
    public void CatchesLayerStillOnOriginalModel() =>
        Expect(Reverted((r, _) => r.Identifier.EndsWith("-carmodel.layer")), "still points at the original model");

    [Fact]
    public void CatchesDriverAnimationNotRetargeted() =>
        Expect(Reverted((r, be) => r.Identifier == be.AnimsetId), "driver animation does not use the donor's clips");

    [Fact]
    public void CatchesWheelTextureNotSwapped()
    {
        var (_, tpl, _, be) = Good();
        var tex = be.TextureIds.First(t => TextureSwap.ChannelOf(t) == "d");
        var srv = new EgoEngineLibrary.Archive.Erp.Data.ErpGfxSRVResource();
        srv.FromResource(ErpX.Index(ErpX.Load(tpl.Get(be.Entry.ZipPath)))[tex]);
        var surface = srv.Fragment0.SurfaceResourceName;
        Expect(Reverted((r, _) => r.Identifier == tex || r.Identifier == surface), "does not hold the donor's pixels");
    }

    /// <summary>The build with the picked resources put back to the template's untouched version.</summary>
    static VerifyResult Reverted(Func<EgoEngineLibrary.Archive.Erp.ErpResource, BuiltEntry, bool> pick)
    {
        var (game, tpl, res, be) = Good();
        var erp = ErpX.Load(res.Replaced[be.Entry.ZipPath]);
        var orig = ErpX.Load(tpl.Get(be.Entry.ZipPath)).Resources.Where(r => pick(r, be)).ToList();
        Assert.NotEmpty(orig);
        foreach (var r in orig) ErpX.AddOrReplace(erp, r);
        var copy = new BuildResult { DonorToken = res.DonorToken };
        copy.Replaced[be.Entry.ZipPath] = ErpX.Save(erp);
        copy.Entries.Add(be);
        return BuildVerifier.Verify(game, tpl, copy, be);
    }

    static uint Index(CarModel pm, string ib, int at) => pm.Buf(ib).Stride == 2
        ? BinaryPrimitives.ReadUInt16LittleEndian(pm.Frags(ib)[1].AsSpan(at * 2))
        : BinaryPrimitives.ReadUInt32LittleEndian(pm.Frags(ib)[1].AsSpan(at * 4));
}
