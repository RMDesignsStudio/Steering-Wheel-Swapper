using System.Buffers.Binary;
using System.Text;

namespace WheelSwap.Core;

public sealed class SubMesh
{
    public int Index;
    public string Name = "";
    public int IOff, ICount, VOff;
    public int[] Streams = [];
}

/// <summary>
/// F1 25 GfxMeshRes descriptor: 76-byte header, 88-byte submesh records, then the stream eaid
/// table (offset = 76 + u32@20) and the submesh slot-name table (offset = 76 + u32@28).
/// Record u32 layout: [0]=stream count, [3]=index offset, [4]=index count, [5]=base vertex,
/// f32[6..9]=bounding sphere, [12]=name offset, [14..]=stream indices.
/// </summary>
public sealed class MeshDesc
{
    public bool Empty;
    public List<string> Eaids = [];
    public List<SubMesh> Subs = [];
    public string IndexBuffer => Eaids[^1];

    public static MeshDesc Parse(byte[] d)
    {
        var m = new MeshDesc();
        if (d.AsSpan().IndexOfAnyExcept((byte)0) < 0) { m.Empty = true; return m; }
        int nsub = (int)U32(d, 0), nstream = (int)U32(d, 4);
        int namesOff = (int)U32(d, 28);
        int eoff = 76 + (int)U32(d, 20);
        if (!d.AsSpan(eoff).StartsWith("eaid://"u8)) throw new InvalidDataException("mesh stream table not at header offset");
        int p = eoff;
        for (int i = 0; i <= nstream; i++)
        {
            int e = Array.IndexOf(d, (byte)0, p);
            m.Eaids.Add(Encoding.ASCII.GetString(d, p, e - p));
            p = e + 1;
        }
        for (int i = 0; i < nsub; i++)
        {
            int b = 76 + i * 88;
            int ns = (int)U32(d, b);
            var s = new SubMesh
            {
                Index = i,
                IOff = (int)U32(d, b + 12),
                ICount = (int)U32(d, b + 16),
                VOff = (int)U32(d, b + 20),
                Streams = Enumerable.Range(0, ns).Select(k => (int)U32(d, b + 56 + k * 4)).ToArray(),
            };
            int no = 76 + namesOff + (int)U32(d, b + 48);
            int ne = Array.IndexOf(d, (byte)0, no);
            s.Name = ne > no ? Encoding.ASCII.GetString(d, no, ne - no) : "";
            m.Subs.Add(s);
        }
        return m;
    }

    public string[] StreamEaids(SubMesh s) => s.Streams.Select(k => Eaids[k]).ToArray();

    public static void PatchRecord(byte[] d, int sub, int ioff, int icount, int voff, float cx, float cy, float cz, float r)
    {
        int b = 76 + sub * 88;
        W32(d, b + 12, (uint)ioff); W32(d, b + 16, (uint)icount); W32(d, b + 20, (uint)voff);
        WF(d, b + 24, cx); WF(d, b + 28, cy); WF(d, b + 32, cz); WF(d, b + 36, r);
    }

    static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
    static void W32(byte[] d, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
    static void WF(byte[] d, int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(o), v);
}

/// <summary>GfxBufferRes header (fragment 0): type, stride, count, fvf.</summary>
public readonly record struct BufInfo(int Stride, int Count, uint Fvf)
{
    public static BufInfo Parse(byte[] h) => new(
        BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(4)),
        BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(8)),
        BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12)));

    public const uint FvfPosition = 0x27;
    public const uint FvfHalf4 = 0x23;   // normal / tangent / uv (4 x f16)
    public const uint FvfBones = 0x21;   // byte0 = bone palette index (part info, 16-bit x4)
    public const uint FvfBones8 = 0x0d;  // the same part info as 8-bit x4 (mirrors, rain beads): byte0 = bone palette index
    public const uint FvfHalf2 = 0x1d;   // second UV on carbon slots (2 x f16)
}
