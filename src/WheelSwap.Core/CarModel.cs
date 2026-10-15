using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;

namespace WheelSwap.Core;

/// <summary>
/// The model a car actually renders, with a decompressed, editable cache of the fragments we touch.
/// Resources are layered: game team ERP(s) at the bottom, then SERPs template ERPs on top (a swapped
/// chassis lives in the template), so the same code handles stock cars and swapped chassis.
/// <para><see cref="Token"/> names the render/model resources the car-model layer points at
/// (<c>X.idf?render</c>); <see cref="MeshToken"/> names the namespace its meshes live in (a chassis swap
/// can keep the target's model name but point it at the donor chassis' renamed meshes).</para>
/// </summary>
public sealed class CarModel
{
    static readonly HashSet<string> ModelTypes = ["ModelProxyRes", "GfxModelRes", "GfxMeshRes", "GfxBufferRes"];
    static readonly Regex IdfName = new(@"/idf/([A-Za-z0-9_]+)\.idf[?&]", RegexOptions.Compiled);

    public string Token { get; }
    /// <summary>Season of the game package the car was read from (2026, or 2025 for the 2025 donor cars).</summary>
    public int Season { get; }
    public string MeshToken { get; }
    public Dictionary<string, ErpResource> Res { get; }
    public string RenderId { get; }
    public string ModelId { get; }
    string MeshPre { get; }

    readonly Dictionary<string, byte[][]> _cache = new(StringComparer.Ordinal);
    public HashSet<string> Dirty { get; } = new(StringComparer.Ordinal);

    /// <summary>Stock car straight from the game (2026 package unless <paramref name="season"/> says otherwise).</summary>
    public CarModel(GameData game, string token, int season = GameData.CurrentSeason) : this(game, token, [], season) { }

    /// <param name="layers">resource sets stacked over the game files, lowest priority first; each must be a
    /// freshly loaded ERP (its objects are edited and handed out as clones).</param>
    /// <param name="fallback">resource sets that only fill in what the game and <paramref name="layers"/> lack
    /// (e.g. another chassis option of the template).</param>
    public CarModel(GameData game, string token, IEnumerable<ErpFile> layers, int season = GameData.CurrentSeason, IEnumerable<ErpFile>? fallback = null)
    {
        Season = season;
        Token = token;
        Res = new(StringComparer.Ordinal);
        var stacked = layers.ToList();
        void Stack(ErpFile e) { foreach (var r in e.Resources) Res[r.Identifier] = r; }

        var teamErp = game.TeamErp(token, season);
        if (File.Exists(teamErp)) Stack(ErpX.Load(teamErp));
        foreach (var e in stacked) Stack(e);
        // fallback sets are only used when the model is not complete without them (their other resources, e.g. an
        // older chassis' extra parts, must not leak into this car)
        bool Complete()
        {
            var m = FindModelRes(token, "?model");
            if (FindModelRes(token, "?render") is null || m is null) return false;
            var l0 = ErpX.Eaids(ErpX.Data(Res[m])).FirstOrDefault(e => e.EndsWith("?lod=0&mesh", StringComparison.Ordinal));
            return l0 is not null && (Res.ContainsKey(l0) || File.Exists(game.TeamErp(IdfName.Match(l0).Groups[1].Value, season)));
        }
        if (fallback is not null && !Complete())
            foreach (var e in fallback) foreach (var r in e.Resources) Res.TryAdd(r.Identifier, r);

        RenderId = FindModelRes(token, "?render") ?? throw new InvalidDataException(File.Exists(teamErp)
            ? $"car model '{token}' was not found in the game's {Path.GetFileName(teamErp)} or in the template"
            : $"car model '{token}' is neither in this template nor in the game ({teamErp} does not exist). " +
              "Verify the game files in Steam, or check that the template is complete");
        ModelId = FindModelRes(token, "?model") ?? throw new InvalidDataException($"model {token}.idf?model not found");
        var lod0 = ErpX.Eaids(ErpX.Data(Res[ModelId])).FirstOrDefault(e => e.EndsWith("?lod=0&mesh", StringComparison.Ordinal))
                   ?? throw new InvalidDataException($"{token}.idf?model has no LOD0 mesh");
        MeshPre = lod0[..^"?lod=0&mesh".Length];
        MeshToken = IdfName.Match(lod0).Groups[1].Value;

        // geometry from another game car (e.g. a template that only re-points the model) -> pull it in underneath
        if (!Res.ContainsKey(lod0) && File.Exists(game.TeamErp(MeshToken, season)))
        {
            var all = new Dictionary<string, ErpResource>(Res, StringComparer.Ordinal);
            Res.Clear();
            Stack(ErpX.Load(game.TeamErp(MeshToken, season)));
            foreach (var (k, v) in all) Res[k] = v;
        }
        if (!Res.ContainsKey(lod0)) throw new InvalidDataException($"mesh {lod0} not found");
    }

    string? FindModelRes(string token, string q) =>
        Res.Keys.FirstOrDefault(k => k.EndsWith($"/idf/{token}.idf{q}", StringComparison.Ordinal));

    public bool Has(string query) => Res.ContainsKey(MeshPre + query);
    public string MeshId(string query) => MeshPre + query;

    public byte[][] Frags(string eaid)
    {
        if (!_cache.TryGetValue(eaid, out var f))
        {
            f = Res[eaid].Fragments.Select(x => x.GetDataArray(true)).ToArray();
            _cache[eaid] = f;
        }
        return f;
    }

    public void Set(string eaid, int frag, byte[] data) { Frags(eaid)[frag] = data; Dirty.Add(eaid); }
    public void Touch(string eaid) => Dirty.Add(eaid);

    public MeshDesc Mesh(string query) => MeshDesc.Parse(Frags(MeshId(query))[0]);
    public MeshDesc? TryMesh(string query) => Has(query) ? Mesh(query) : null;
    public BufInfo Buf(string eaid) => BufInfo.Parse(Frags(eaid)[0]);

    public byte[] Render => Frags(RenderId)[0];
    public byte[] Model => Frags(ModelId)[0];

    /// <summary>Write cached edits back into the resources and rehash them.</summary>
    public void Commit()
    {
        foreach (var id in Dirty)
        {
            var r = Res[id]; var f = _cache[id];
            for (int i = 0; i < f.Length; i++) r.Fragments[i].SetData(f[i], true);
            ErpX.Rehash(r);
        }
    }

    /// <summary>Every model resource the car needs: all resources of the model and mesh namespaces (part
    /// shells included) plus whatever they reference, followed transitively.</summary>
    public List<ErpResource> Closure()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        // seeds: the geometry namespace in full; of the model namespace only the model/render shells
        // (for a swapped chassis its stock meshes are unused leftovers from the game files)
        foreach (var (id, r) in Res)
        {
            bool seed = ModelTypes.Contains(r.ResourceType) && id.Contains($"/idf/{MeshToken}.idf", StringComparison.Ordinal)
                     || r.ResourceType is "ModelProxyRes" or "GfxModelRes" && id.Contains($"/idf/{Token}.idf", StringComparison.Ordinal);
            if (seed && seen.Add(id)) queue.Enqueue(id);
        }
        while (queue.Count > 0)
        {
            var r = Res[queue.Dequeue()];
            foreach (var f in r.Fragments)
                foreach (var e in ErpX.Eaids(f.GetDataArray(true)))
                    if (Res.TryGetValue(e, out var x) && ModelTypes.Contains(x.ResourceType) && seen.Add(e)) queue.Enqueue(e);
        }
        return seen.Select(id => Res[id]).ToList();
    }

    public static IEnumerable<string> Namespaces(IEnumerable<ErpResource> rs) =>
        rs.Select(r => IdfName.Match(r.Identifier)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct();
}
