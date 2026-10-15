using System.Buffers.Binary;
using System.Text;

namespace WheelSwap.Core;

public sealed class Node
{
    public int Index, Parent, Offset;
    public string Name = "";
    public float[] T = new float[3], Q = new float[4], S = new float[3];
}

/// <summary>
/// Node graph of a model's ?render (ModelProxyRes): u16 node count, u16 palette count, then
/// 120-byte node records from offset 12 (u32 name offset, i32 parent, f32 T[3] Q[4] S[3] ...),
/// then the u16 bone palette (vertex bone index -> node), then the name strings.
/// The ?model resource carries an identical node table at a fixed delta.
/// </summary>
public sealed class NodeTable
{
    public List<Node> Nodes = [];
    public ushort[] Palette = [];

    public static NodeTable Parse(byte[] d)
    {
        var t = new NodeTable();
        int n = BinaryPrimitives.ReadUInt16LittleEndian(d), n2 = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2));
        int soff = 12 + n * 120 + n2 * 2;
        for (int i = 0; i < n; i++)
        {
            int b = 12 + i * 120;
            var node = new Node { Index = i, Offset = b, Parent = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(b + 4)) };
            for (int k = 0; k < 3; k++) node.T[k] = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(b + 8 + k * 4));
            for (int k = 0; k < 4; k++) node.Q[k] = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(b + 20 + k * 4));
            for (int k = 0; k < 3; k++) node.S[k] = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(b + 36 + k * 4));
            int no = soff + (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(b));
            int ne = Array.IndexOf(d, (byte)0, no);
            node.Name = Encoding.ASCII.GetString(d, no, ne - no);
            t.Nodes.Add(node);
        }
        t.Palette = new ushort[n2];
        for (int i = 0; i < n2; i++) t.Palette[i] = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(12 + n * 120 + i * 2));
        return t;
    }

    public string ParentName(Node n) => n.Parent >= 0 ? Nodes[n.Parent].Name : "";
    public (string, string) Key(Node n) => (n.Name, ParentName(n));

    public Node SteerMaster => Nodes.Single(n => n.Name == "steer_master");

    /// <summary>steer_master and everything below it, keyed by (name, parent name).</summary>
    public Dictionary<(string, string), Node> SteeringCluster()
    {
        int root = SteerMaster.Index;
        var res = new Dictionary<(string, string), Node>();
        foreach (var n in Nodes)
        {
            int j = n.Index;
            while (j >= 0 && j != root) j = Nodes[j].Parent;
            if (j == root) res[Key(n)] = n;
        }
        return res;
    }

    public int PaletteIndexOf(int nodeIndex) => Array.IndexOf(Palette, (ushort)nodeIndex);

    /// <summary>Offset delta of the identical node table inside the ?model payload.</summary>
    public static int ModelDelta(byte[] render, byte[] model, NodeTable t)
    {
        var first = render.AsSpan(12, 120);
        int at = model.AsSpan().IndexOf(first);
        if (at < 0) throw new InvalidDataException("node table not found in ?model");
        int delta = at - 12;
        foreach (var n in t.Nodes)
            if (!render.AsSpan(n.Offset, 120).SequenceEqual(model.AsSpan(n.Offset + delta, 120)))
                throw new InvalidDataException("?model node table differs from ?render");
        return delta;
    }

    /// <summary>40 bytes of T/Q/S at record offset + 8.</summary>
    public static void CopyTrs(byte[] src, int srcOff, byte[] dst, int dstOff) =>
        src.AsSpan(srcOff + 8, 40).CopyTo(dst.AsSpan(dstOff + 8));

    public static void WriteTQ(byte[] d, int off, System.Numerics.Vector3 t, System.Numerics.Quaternion q)
    {
        float[] v = [t.X, t.Y, t.Z, q.X, q.Y, q.Z, q.W];
        for (int k = 0; k < 7; k++) BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(off + 8 + k * 4), v[k]);
    }
}
