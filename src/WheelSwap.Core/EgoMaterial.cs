using System.Buffers.Binary;
using System.Text;

namespace WheelSwap.Core;

/// <summary>
/// A GfxMaterialRes: shader, float parameters and texture samplers.
/// Layout: u32 entry count, 7 more header u32s, then 36-byte entries (u32 name offset, u32 class
/// 1 = value / 2 = texture, u32 type, u32 count, u32 size, u32, u32, u32 data offset, u32), then the value
/// block, then the shader eaid followed by the parameter names (name offsets count from just after the
/// shader string), then the texture eaids in sampler order.
/// </summary>
public sealed class EgoMaterial
{
    public string Shader = "";
    public Dictionary<string, float[]> Values { get; } = new(StringComparer.Ordinal);
    /// <summary>Sampler name (e.g. me_diffuseMap) -> texture eaid.</summary>
    public Dictionary<string, string> Textures { get; } = new(StringComparer.Ordinal);

    /// <summary>Shader file name without folder and extension, e.g. "vehicle_metallic_paint".</summary>
    public string ShaderName => Path.GetFileNameWithoutExtension(Shader.Split('/')[^1]);

    public float Value(string name, float fallback) => Values.TryGetValue(name, out var v) && v.Length > 0 ? v[0] : fallback;

    public static EgoMaterial Parse(byte[] d)
    {
        var m = new EgoMaterial();
        int n = (int)U32(d, 0);
        int dataBase = 0x20 + n * 36;
        int sh = dataBase <= d.Length ? d.AsSpan(dataBase).IndexOf("eaid://"u8) : -1;
        if (n <= 0 || dataBase > d.Length || sh < 0) throw new InvalidDataException("not a material");
        sh += dataBase;
        int she = Array.IndexOf(d, (byte)0, sh);
        m.Shader = Encoding.ASCII.GetString(d, sh, she - sh);
        int names = she + 1;
        var samplers = new List<string>();
        int lastName = names;
        for (int i = 0; i < n; i++)
        {
            int b = 0x20 + i * 36;
            int no = names + (int)U32(d, b);
            int ne = Array.IndexOf(d, (byte)0, no);
            if (no >= d.Length || ne < 0) continue;
            lastName = Math.Max(lastName, ne + 1);
            var name = Encoding.ASCII.GetString(d, no, ne - no);
            uint cls = U32(d, b + 4), size = U32(d, b + 16), off = U32(d, b + 28);
            if (cls == 2) samplers.Add(name);
            else if (cls == 1 && (U32(d, b + 8) & 0xFFFF) == 3 && size is > 0 and <= 64 && dataBase + off + size <= d.Length)
            {
                var f = new float[size / 4];
                for (int k = 0; k < f.Length; k++) f[k] = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(dataBase + (int)off + k * 4));
                m.Values[name] = f;
            }
        }
        // texture eaids follow the names, in sampler order
        var tex = ErpX.Eaids(d[lastName..]).ToList();
        for (int i = 0; i < samplers.Count && i < tex.Count; i++) m.Textures[samplers[i]] = tex[i];
        return m;
    }

    /// <summary>A GfxMatBindRes (.emb): slot name -> material eaid. Layout: u32 slot count, u32 distinct
    /// material count, u32 offset of the material names (from byte 12), then (u32 slot name offset, u32 material name offset)
    /// per slot, then the slot names, then the material eaids.</summary>
    public static Dictionary<string, string> ParseBinding(byte[] d)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int n = (int)U32(d, 0);
        int slotBase = 12 + n * 8;
        int matBase = 12 + (int)U32(d, 8);
        if (n <= 0 || matBase >= d.Length) return map;
        for (int i = 0; i < n; i++)
        {
            var slot = Str(d, slotBase + (int)U32(d, 12 + i * 8));
            var mat = Str(d, matBase + (int)U32(d, 16 + i * 8));
            if (slot.Length > 0 && mat.StartsWith("eaid://", StringComparison.Ordinal)) map.TryAdd(slot, mat);
        }
        return map;
    }

    static string Str(byte[] d, int o)
    {
        if (o < 0 || o >= d.Length) return "";
        int e = Array.IndexOf(d, (byte)0, o);
        return e < 0 ? "" : Encoding.ASCII.GetString(d, o, e - o);
    }

    static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
}
