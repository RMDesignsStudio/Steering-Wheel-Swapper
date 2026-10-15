using System.Security.Cryptography;
using System.Text;
using EgoEngineLibrary.Archive.Erp;

namespace WheelSwap.Core;

/// <summary>Small helpers around EgoEngineLibrary's ErpFile.</summary>
public static class ErpX
{
    public static ErpFile Load(string path)
    {
        var erp = new ErpFile();
        using var fs = File.OpenRead(path);
        Read(erp, fs);
        return erp;
    }

    public static ErpFile Load(byte[] data)
    {
        var erp = new ErpFile();
        using var ms = new MemoryStream(data, writable: false);
        Read(erp, ms);
        return erp;
    }

    // The reader zero-fills data past the end of a short file instead of failing, so check that every
    // fragment lies inside the file (a truncated / cut-off file would otherwise load as silent zeros).
    static void Read(ErpFile erp, Stream s)
    {
        ulong len = (ulong)s.Length;   // the reader closes the stream
        try { erp.Read(s); }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"not a readable ERP file ({ex.Message})", ex);
        }
        foreach (var r in erp.Resources)
            foreach (var f in r.Fragments)
                if (erp.ResourceOffset + f.Offset + f.PackedSize > len)
                    throw new InvalidDataException($"the ERP file is truncated ({r.Identifier.Split('/')[^1]} runs past the end of the file)");
    }

    public static byte[] Save(ErpFile erp)
    {
        erp.UpdateOffsets();
        using var ms = new MemoryStream();
        erp.Write(ms);
        return ms.ToArray();
    }

    public static Dictionary<string, ErpResource> Index(ErpFile erp)
    {
        var d = new Dictionary<string, ErpResource>(StringComparer.Ordinal);
        foreach (var r in erp.Resources) d[r.Identifier] = r;
        return d;
    }

    public static byte[] Data(ErpResource r, int frag = 0) => r.Fragments[frag].GetDataArray(true);

    /// <summary>Resource hash used by the game = MD5 of all decompressed fragments.</summary>
    public static void Rehash(ErpResource r)
    {
        using var ms = new MemoryStream();
        foreach (var f in r.Fragments) ms.Write(f.GetDataArray(true));
        r.Hash = MD5.HashData(ms.ToArray());
    }

    public static bool HashValid(ErpResource r)
    {
        using var ms = new MemoryStream();
        foreach (var f in r.Fragments) ms.Write(f.GetDataArray(true));
        return MD5.HashData(ms.ToArray()).AsSpan().SequenceEqual(r.Hash);
    }

    /// <summary>Move a resource object into another ERP (fix up parent links).</summary>
    public static void Adopt(ErpFile erp, ErpResource r)
    {
        r.ParentFile = erp;
        foreach (var f in r.Fragments) f.ParentFile = erp;
    }

    public static void AddOrReplace(ErpFile erp, ErpResource r)
    {
        Adopt(erp, r);
        for (int i = 0; i < erp.Resources.Count; i++)
        {
            if (erp.Resources[i].Identifier == r.Identifier) { erp.Resources[i] = r; return; }
        }
        erp.Resources.Add(r);
    }

    /// <summary>All eaid:// strings inside a payload.</summary>
    public static IEnumerable<string> Eaids(byte[] d)
    {
        var e = "eaid://"u8.ToArray();
        for (int i = 0; i + e.Length <= d.Length; i++)
        {
            if (!d.AsSpan(i, e.Length).SequenceEqual(e)) continue;
            int j = i;
            while (j < d.Length && d[j] >= 0x20 && d[j] < 0x7F) j++;
            yield return Encoding.ASCII.GetString(d, i, j - i);
            i = j;
        }
    }

    /// <summary>Same-length replace of <paramref name="from"/> inside eaid:// runs only. Returns hit count.</summary>
    public static int ReplaceInEaids(byte[] d, string from, string to)
    {
        if (from.Length != to.Length) throw new ArgumentException("same-length replacement only");
        var e = "eaid://"u8.ToArray();
        var a = Encoding.ASCII.GetBytes(from); var b = Encoding.ASCII.GetBytes(to);
        int hits = 0;
        for (int i = 0; i + e.Length <= d.Length; i++)
        {
            if (!d.AsSpan(i, e.Length).SequenceEqual(e)) continue;
            int j = i + e.Length;
            while (j < d.Length && d[j] >= 0x20 && d[j] < 0x7F)
            {
                if (j + a.Length <= d.Length && d.AsSpan(j, a.Length).SequenceEqual(a))
                {
                    b.CopyTo(d, j); hits++; j += a.Length;
                }
                else j++;
            }
            i = j;
        }
        return hits;
    }

    /// <summary>Same-length raw byte replace (anywhere). Returns hit count.</summary>
    public static int ReplaceBytes(byte[] d, string from, string to)
    {
        if (from.Length != to.Length) throw new ArgumentException("same-length replacement only");
        var a = Encoding.ASCII.GetBytes(from); var b = Encoding.ASCII.GetBytes(to);
        int hits = 0;
        for (int i = 0; i + a.Length <= d.Length; i++)
        {
            if (d.AsSpan(i, a.Length).SequenceEqual(a)) { b.CopyTo(d, i); hits++; i += a.Length - 1; }
        }
        return hits;
    }
}
