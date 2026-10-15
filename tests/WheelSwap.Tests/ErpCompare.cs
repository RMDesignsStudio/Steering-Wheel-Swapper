using System.Buffers.Binary;
using System.Text.Json.Nodes;
using EgoEngineLibrary.Archive.Erp;
using WheelSwap.Core;

namespace WheelSwap.Tests;

/// <summary>Resource-by-resource comparison of two ERPs, with each side's private model names normalised.</summary>
static class ErpCompare
{
    public sealed record Diff(int Same, int Noise, int Json, List<string> Real, List<string> OnlyTool, List<string> OnlyV6);

    public static Diff Compare(ErpFile tool, IReadOnlyList<string> toolTokens, ErpFile v6, IReadOnlyList<string> v6Tokens)
    {
        string NormT(string s) { foreach (var t in toolTokens) s = s.Replace(t, "PRIV"); return s; }
        string NormV(string s) { foreach (var t in v6Tokens) s = s.Replace(t, "PRIV"); return s; }
        var t = tool.Resources.ToDictionary(r => NormT(r.Identifier));
        var v = v6.Resources.ToDictionary(r => NormV(r.Identifier));
        int same = 0, noise = 0, json = 0;
        var real = new List<string>();
        foreach (var (k, tr) in t)
        {
            if (!v.TryGetValue(k, out var vr)) continue;
            var a = tr.Fragments.Select(f => Norm(f.GetDataArray(true), toolTokens)).ToArray();
            var b = vr.Fragments.Select(f => Norm(f.GetDataArray(true), v6Tokens)).ToArray();
            if (a.Length == b.Length && a.Zip(b).All(p => p.First.AsSpan().SequenceEqual(p.Second))) same++;
            else if (tr.ResourceType == "AnimSet" && JsonNode.DeepEquals(JsonNode.Parse(AnimsetSwap.ToJson(ErpX.Data(tr))), JsonNode.Parse(AnimsetSwap.ToJson(ErpX.Data(vr))))) json++;
            else if (a.Length == b.Length && a.Zip(b).All(p => FloatNoise(p.First, p.Second))) noise++;
            else real.Add($"{tr.ResourceType} {k.Split('/')[^1]}");
        }
        return new Diff(same, noise, json, real, t.Keys.Except(v.Keys).ToList(), v.Keys.Except(t.Keys).ToList());
    }

    static byte[] Norm(byte[] d, IEnumerable<string> tokens)
    {
        foreach (var tok in tokens) ErpX.ReplaceBytes(d, tok, new string('#', tok.Length));
        return d;
    }

    /// <summary>Same length, and every differing 4-byte word is a float within 1e-5 (positions, bounds)
    /// or a pair of half floats within 1e-3 (normals, tangents, UVs).</summary>
    static bool FloatNoise(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int w = 0; w + 4 <= a.Length; w += 4)
        {
            if (a.AsSpan(w, 4).SequenceEqual(b.AsSpan(w, 4))) continue;
            float fa = BinaryPrimitives.ReadSingleLittleEndian(a.AsSpan(w)), fb = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(w));
            if (float.IsFinite(fa) && float.IsFinite(fb) && MathF.Abs(fa - fb) <= 1e-5f * MathF.Max(1, MathF.Abs(fa))) continue;
            bool halves = true;
            for (int h = 0; h < 4; h += 2)
            {
                float ha = (float)BinaryPrimitives.ReadHalfLittleEndian(a.AsSpan(w + h)), hb = (float)BinaryPrimitives.ReadHalfLittleEndian(b.AsSpan(w + h));
                if (!(float.IsFinite(ha) && float.IsFinite(hb) && MathF.Abs(ha - hb) <= 1e-3f * MathF.Max(1, MathF.Abs(ha)))) halves = false;
            }
            if (!halves) return false;
        }
        return a.Length % 4 == 0 || a.AsSpan(a.Length & ~3).SequenceEqual(b.AsSpan(b.Length & ~3));
    }
}
