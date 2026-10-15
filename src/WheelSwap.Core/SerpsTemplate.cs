using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;

namespace WheelSwap.Core;

public enum EntryKind { TeamChassis, FomChassis, MyTeamLivery }

/// <summary>What was done to one car entry (for verification and tests).</summary>
public sealed record BuiltEntry(CarEntry Entry, string DonorToken, string ModelToken, string MeshToken, string PrivateToken,
                                Dictionary<string, string> Renames, List<string> TextureIds, string AnimsetId,
                                Dictionary<string, (int W, int H)> DonorTextureSizes, string? AnimationDonor = null)
{
    /// <summary>Whose in-car driver animations the car now plays (normally the wheel donor's).</summary>
    public string AnimationFrom => AnimationDonor ?? DonorToken;
}

public sealed class BuildResult
{
    public string DonorToken { get; init; } = "";
    /// <summary>zip path -> new ERP bytes (car entries + any sibling ERP that was fixed)</summary>
    public Dictionary<string, byte[]> Replaced { get; } = new();
    public List<BuiltEntry> Entries { get; } = [];
    /// <summary>Verification of each built entry (same order as <see cref="Entries"/>; filled by Build).</summary>
    public List<VerifyResult> Verification { get; } = [];
    /// <summary>Zip paths renamed in the written .serps (old -> new): option folders get the wheel ending, e.g.
    /// "ERPs/03--01--Livery - FOM Chassis/x.erp" -> "ERPs/03--01--Livery - FOM Chassis - 26 Ferrari Wheel/x.erp".</summary>
    public Dictionary<string, string> Renamed { get; } = new(StringComparer.Ordinal);
    /// <summary>The path an entry of the template has in the written .serps.</summary>
    public string OutputPath(string zipPath) => Renamed.GetValueOrDefault(zipPath, zipPath);
}

public sealed class CarEntry
{
    public required string ZipPath { get; init; }
    public required EntryKind Kind { get; set; }
    public required string ModelToken { get; set; }    // model the car-model layer points at (X.idf?context=render);
                                                       // for an earlier Wheel Swapper output: the original model
    /// <summary>Set when this entry is an earlier Steering Wheel Swapper output: its private model namespaces
    /// -> the namespaces they were cloned from. A new build strips them first, so swaps never stack.</summary>
    public Dictionary<string, string>? PreviousSwap { get; set; }
    public string? SlotToken { get; init; }            // team slot of the layer (teams/<slot>/wep/...)
    public string? AnimsetId { get; init; }            // animset carried by the entry, if any
    public List<string> MatBindings { get; init; } = [];  // material binding (emb) ids the car uses
    public List<string> SteeringTextures { get; init; } = [];
    /// <summary>The car is not on its team's own model: the layer points at another model (e.g. an MCL40 or
    /// "swapcar" model) or the model's geometry comes from another car (e.g. lotus -> mercswap).</summary>
    public bool Swapped { get; set; }
    public string? Problem { get; set; }
    public string FileName => ZipPath.Split('/')[^1];
    public string Folder => ZipPath.Contains('/') ? ZipPath[..ZipPath.LastIndexOf('/')] : "";

    /// <summary>The chassis option as the template names it, from its folder: "02--01--Livery - Release Default
    /// Chassis" -> "Release Default Chassis". Null for templates without option folders.</summary>
    public string? OptionName
    {
        get
        {
            var f = Folder.Split('/')[^1];
            var m = System.Text.RegularExpressions.Regex.Match(f, @"^\d+--\d+--\s*(?:Livery\s*-\s*)?(.+)$");
            return m.Success && m.Groups[1].Value.Trim().Length > 0 ? m.Groups[1].Value.Trim() : null;
        }
    }

    /// <summary>"Release Default Chassis", "MyTeam · FOM Chassis", or without option folders "Team chassis · Williams".</summary>
    public string Title => Kind switch
    {
        EntryKind.MyTeamLivery => "MyTeam · " + (OptionName ?? "FOM chassis"),
        EntryKind.FomChassis => OptionName ?? "FOM chassis",
        _ => OptionName ?? $"Team chassis · {TeamName(SlotToken ?? ModelToken)}",
    };

    public string Describe => Title + (Swapped ? " — swapped chassis" : "") + (PreviousSwap is not null ? " — wheel already swapped" : "") + "  —  " + FileName;

    static string TeamName(string tok) => GameData.TeamName(tok);
}

/// <summary>A SERPs template (.serps = zip of ERPs + template.serps, zip comment = injection target).</summary>
public sealed class SerpsTemplate
{
    static readonly Regex ModelRef = new(@"teams/[a-z0-9_]+/idf/([a-z0-9_]+)\.idf\?context=render", RegexOptions.Compiled);
    static readonly Regex SlotRef = new(@"teams/([a-z0-9_]+)/wep/[a-z0-9_]+-carmodel\.layer$", RegexOptions.Compiled);
    static readonly Regex SteerMatRef = new(@"eaid://[ -~]+?steeringwheel\.material", RegexOptions.Compiled);
    static readonly HashSet<string> ModelTypes = ["ModelProxyRes", "GfxModelRes", "GfxMeshRes", "GfxBufferRes"];
    static readonly Regex IdfSuffix = new(@"/idf/([A-Za-z0-9_]+)\.idf(.*)$", RegexOptions.Compiled);

    public string SourcePath { get; }
    public string Name => Path.GetFileNameWithoutExtension(SourcePath);
    public string? Comment { get; }
    readonly List<(string Name, byte[] Data, bool Stored)> _entries = [];
    readonly List<string> _modelErps = [];   // template ERPs that carry model resources (swapped chassis)
    public List<CarEntry> CarEntries { get; } = [];

    public SerpsTemplate(string path)
    {
        SourcePath = path;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            Comment = zip.Comment;
            foreach (var e in zip.Entries)
            {
                using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
                _entries.Add((e.FullName, ms.ToArray(), e.Length > 0 && e.CompressedLength == e.Length));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException and not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a readable SERPs template: the file is damaged, incomplete " +
                                           $"or not a .serps (zip) file. ({ex.Message})", ex);
        }
        var nsByErp = new Dictionary<string, Dictionary<string, HashSet<string>>>();   // car ERP -> model namespace -> its resource suffixes
        foreach (var (name, data, _) in _entries)
        {
            if (!name.EndsWith(".erp", StringComparison.OrdinalIgnoreCase) || !name.Contains("_car_")) continue;
            ErpFile erp;
            CarEntry? ce;
            try
            {
                erp = ErpX.Load(data);
                ce = Inspect(name, erp);
            }
            catch (Exception ex)
            {
                // a damaged car ERP is listed (disabled) instead of silently disappearing
                if (LooksLikeCarEntry(name))
                    CarEntries.Add(new CarEntry { ZipPath = name, Kind = KindFromName(name), ModelToken = "?", Problem = $"the ERP file is damaged ({ex.Message})" });
                continue;
            }
            var ids = erp.Resources.Where(r => ModelTypes.Contains(r.ResourceType)).Select(r => r.Identifier).ToList();
            if (ids.Count > 0) _modelErps.Add(name);
            var byNs = nsByErp[name] = new();
            // which geometry each carried model uses (its ?model's LOD0 mesh namespace)
            var geo = _modelGeometry[name] = new(StringComparer.Ordinal);
            foreach (var r in erp.Resources.Where(r => r.ResourceType == "GfxModelRes" && r.Identifier.EndsWith(".idf?model", StringComparison.Ordinal)))
            {
                var ns = IdfSuffix.Match(r.Identifier).Groups[1].Value;
                var lod0 = ErpX.Eaids(ErpX.Data(r)).FirstOrDefault(e => e.EndsWith("?lod=0&mesh", StringComparison.Ordinal));
                if (lod0 is not null && IdfSuffix.Match(lod0) is { Success: true } lm) geo[ns] = lm.Groups[1].Value;
            }
            foreach (var r in erp.Resources.Where(r => ModelTypes.Contains(r.ResourceType)))
                if (IdfSuffix.Match(r.Identifier) is { Success: true } m)
                    (byNs.TryGetValue(m.Groups[1].Value, out var set) ? set : byNs[m.Groups[1].Value] = []).Add(m.Groups[2].Value);
            if (ce is not null) CarEntries.Add(ce);
        }
        DetectPreviousSwaps(nsByErp);
        foreach (var ce in CarEntries.Where(c => c.Problem is null)) ce.Swapped = IsSwapped(ce);
    }

    readonly Dictionary<string, Dictionary<string, string>> _modelGeometry = new();   // ERP -> model namespace -> LOD0 mesh namespace

    HashSet<string> CarEntryFolders => CarEntries.Where(c => c.Folder.Length > 0).Select(c => c.Folder).ToHashSet();

    /// <summary>
    /// Whether another template ERP's model resources apply to this car. Team Simplified templates put each chassis
    /// option in its own folder (Release / Post-Patch / FOM / a swapped chassis) and SERPs installs one of them, so an
    /// entry uses its own folder and folders that hold no chassis option; templates without option folders (all ERPs
    /// at the top level) use everything, as their swap pieces are spread over several ERPs.
    /// </summary>
    bool AppliesTo(string erpName, CarEntry ce)
    {
        var folder = erpName.Contains('/') ? erpName[..erpName.LastIndexOf('/')] : "";
        return folder == ce.Folder || !CarEntryFolders.Contains(folder);
    }

    bool IsSwapped(CarEntry ce)
    {
        var own = ce.Kind == EntryKind.TeamChassis ? ce.SlotToken ?? ce.ModelToken : "fom_car";
        if (ce.ModelToken != own) return true;                      // on another model (MCL40 chassis, "swapcar", ...)
        // the team's own model, but its geometry taken from another car (e.g. lotus -> mercswap)
        foreach (var (erp, geo) in _modelGeometry)
            if ((erp == ce.ZipPath || AppliesTo(erp, ce)) && geo.TryGetValue(ce.ModelToken, out var mesh) && mesh != ce.ModelToken
                && ce.PreviousSwap?.ContainsKey(mesh) != true)
                return true;
        return false;
    }

    bool IsPrivateOf(string erp, string ns) => CarEntries.Any(c => c.ZipPath == erp && c.PreviousSwap?.ContainsKey(ns) == true);

    static bool LooksLikeCarEntry(string name) =>
        Regex.IsMatch(name.Split('/')[^1], @"_car_(.*livery|.*chassis|.*pkg)", RegexOptions.IgnoreCase) && !name.Contains("numbers", StringComparison.OrdinalIgnoreCase);

    static EntryKind KindFromName(string name) =>
        name.Contains("myteam_car_livery", StringComparison.OrdinalIgnoreCase) ? EntryKind.MyTeamLivery
        : name.Contains("_FOM_", StringComparison.OrdinalIgnoreCase) ? EntryKind.FomChassis : EntryKind.TeamChassis;

    /// <summary>
    /// Recognise entries written by this tool: they carry a model namespace P that is a same-length private copy
    /// of another namespace C (P = C with its last 4 characters replaced by hex, see <see cref="PrivateName"/>), and
    /// the car's layer points at P. C is a 2026 game car or a namespace carried by the template.
    /// </summary>
    void DetectPreviousSwaps(Dictionary<string, Dictionary<string, HashSet<string>>> nsByErp)
    {
        var known = GameData.Teams2026.Select(t => t.Token).Concat(GameData.MyTeamSlots).ToHashSet();
        var carried = new Dictionary<string, HashSet<string>>();   // namespaces the template carries -> resource suffixes
        foreach (var byNs in nsByErp.Values)
            foreach (var (ns, suf) in byNs)
            {
                known.Add(ns);
                if (!carried.TryGetValue(ns, out var all)) carried[ns] = all = [];
                all.UnionWith(suf);
            }
        foreach (var ce in CarEntries.Where(c => c.Problem is null))
        {
            if (!nsByErp.TryGetValue(ce.ZipPath, out var ownNs)) continue;
            var own = ownNs.Keys.ToHashSet();
            var map = new Dictionary<string, string>();
            foreach (var p in own.Where(LooksPrivate))
            {
                var cands = known.Where(c => c != p && !own.Where(LooksPrivate).Contains(c) && PrivateLike(p, c)).ToList();
                // several names fit (e.g. merc5f74: the game's mercedes or the template's mercswap): a private copy
                // has exactly the resources of what it was cloned from, so prefer the carried namespace that matches
                if (cands.Count > 1)
                {
                    var shaped = cands.Where(c => carried.TryGetValue(c, out var suf) && suf.SetEquals(ownNs[p])).ToList();
                    if (shaped.Count == 1) cands = shaped;
                }
                if (cands.Count == 1) map[p] = cands[0];
                else if (cands.Count > 1) ce.Problem = $"this entry was written by Steering Wheel Swapper, but its original model is ambiguous ({string.Join(", ", cands)})";
            }
            bool layerIsPrivate = ce.Kind == EntryKind.MyTeamLivery ? map.Count > 0 : map.ContainsKey(ce.ModelToken);
            if (!layerIsPrivate) continue;
            ce.PreviousSwap = map;
            if (ce.Kind != EntryKind.MyTeamLivery) ce.ModelToken = map[ce.ModelToken];
            if (ce.Kind == EntryKind.TeamChassis && ce.ModelToken == "fom_car") ce.Kind = EntryKind.FomChassis;
        }
    }

    // PrivateName: names longer than 4 keep all but the last 4 characters (+4 hex); names of 4 or fewer become
    // all hex (first character 'w'/'x' when needed), e.g. haas -> w983
    static bool Hex(char ch) => ch is >= '0' and <= '9' or >= 'a' and <= 'f';

    static bool LooksPrivate(string ns) => ns.Length >= 5
        ? ns[^4..].All(Hex)
        : ns.Length > 0 && (Hex(ns[0]) || ns[0] is 'w' or 'x') && ns[1..].All(Hex);

    static bool PrivateLike(string p, string c) =>
        p.Length == c.Length && (c.Length <= 4 || p[1..^4] == c[1..^4] && (p[0] == c[0] || p[0] is 'w' or 'x'));

    public byte[] Get(string name) => _entries.First(e => e.Name == name).Data;

    /// <summary>Every zip entry path of the template.</summary>
    public IReadOnlyList<string> EntryNames => _entries.Select(e => e.Name).ToList();

    static CarEntry? Inspect(string name, ErpFile erp)
    {
        var tex = erp.Resources.Where(r => r.ResourceType == "GfxSRVResource" && TextureSwap.ChannelOf(r.Identifier) is not null)
                               .Select(r => r.Identifier).ToList();
        var layer = erp.Resources.FirstOrDefault(r => r.Identifier.EndsWith("-carmodel.layer", StringComparison.Ordinal));
        var animset = erp.Resources.FirstOrDefault(r => r.ResourceType == "AnimSet")?.Identifier;
        // MyTeam first: once swapped, a MyTeam entry also carries the fom_car slot's car-model layer
        if (name.Contains("myteam_car_livery", StringComparison.OrdinalIgnoreCase))
            return new CarEntry
            {
                ZipPath = name, Kind = EntryKind.MyTeamLivery, ModelToken = "fom_car", SlotToken = "fom_car", SteeringTextures = tex,
                MatBindings = erp.Resources.Where(r => r.ResourceType == "GfxMatBindRes").Select(r => r.Identifier).ToList(),
            };
        if (layer is not null)
        {
            var payload = ErpX.Data(layer);
            var m = ModelRef.Match(Encoding.ASCII.GetString(payload));
            if (!m.Success) return null;
            var tok = m.Groups[1].Value;
            return new CarEntry
            {
                ZipPath = name, Kind = tok == "fom_car" ? EntryKind.FomChassis : EntryKind.TeamChassis, ModelToken = tok,
                SlotToken = SlotRef.Match(layer.Identifier) is { Success: true } s ? s.Groups[1].Value : null,
                AnimsetId = animset, SteeringTextures = tex,
                MatBindings = ErpX.Eaids(payload).Where(e => e.Contains(".emb?context=")).Distinct().ToList(),
            };
        }
        return null;
    }

    /// <summary>Same-length private name for a model namespace, stable per (namespace, donor, template).</summary>
    public string PrivateName(string ns, string donor)
    {
        var h = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{ns}|{donor}|{Name}"))).ToLowerInvariant();
        var p = ns.Length > 4 ? ns[..^4] + h[..4] : h[..ns.Length];
        if (!char.IsLetter(p[0])) p = "w" + p[1..];
        return p == ns ? "x" + p[1..] : p;
    }

    /// <summary>Finds resources by id across the entry being edited, the other template ERPs and the game.
    /// Objects returned from outside the entry are fresh copies and may be adopted into it.</summary>
    sealed class Finder(SerpsTemplate tpl, ErpFile entry, string entryName, Dictionary<string, byte[]> replaced, GameData game, IEnumerable<string> gameTokens)
    {
        Dictionary<string, ErpResource>? _entry, _tpl, _game;

        public (ErpResource Res, bool InEntry)? Find(string id)
        {
            _entry ??= ErpX.Index(entry);
            if (_entry.TryGetValue(id, out var r)) return (r, true);
            if (_tpl is null)
            {
                _tpl = new(StringComparer.Ordinal);
                foreach (var (n, _, _) in tpl._entries.Where(e => e.Name.EndsWith(".erp") && e.Name != entryName))
                {
                    try { foreach (var x in ErpX.Load(replaced.GetValueOrDefault(n) ?? tpl.Get(n)).Resources) _tpl.TryAdd(x.Identifier, x); } catch { }
                }
            }
            if (_tpl.TryGetValue(id, out r)) return (r, false);
            if (_game is null)
            {
                _game = new(StringComparer.Ordinal);
                foreach (var t in gameTokens.Distinct())
                    if (File.Exists(game.TeamErp(t)))
                        foreach (var x in ErpX.Load(game.TeamErp(t)).Resources) _game.TryAdd(x.Identifier, x);
            }
            return _game.TryGetValue(id, out r) ? (r, false) : null;
        }
    }

    /// <summary>Swap the donor's steering wheel into the selected entries and write a new .serps (null = dry run).
    /// Every built entry is checked by <see cref="BuildVerifier"/> before anything is written.
    /// <paramref name="animationDonor"/>: take the in-car driver animations from another donor (diagnostics; null = the wheel donor).</summary>
    public BuildResult Build(GameData game, string donorToken, IReadOnlyList<CarEntry> selected, string? outPath, SwapLog log, string? animationDonor = null)
    {
        var res = BuildInMemory(game, donorToken, selected, log, animationDonor);
        int checks = 0;
        var failures = new List<string>();
        foreach (var be in res.Entries)
        {
            var v = BuildVerifier.Verify(game, this, res, be);
            res.Verification.Add(v);
            checks += v.Checks;
            failures.AddRange(v.Failures.Select(f => $"{be.Entry.FileName}: {f}"));
            foreach (var w in v.Warnings) log.Warn($"{be.Entry.FileName}: {w}");
        }
        if (failures.Count > 0) throw new InvalidDataException("Verification failed:\n  " + string.Join("\n  ", failures));
        log.Info($"Verified: {checks} checks passed on {res.Entries.Count} car entr{(res.Entries.Count == 1 ? "y" : "ies")}.");
        if (outPath is null) { log.Info("Dry run: nothing written."); return res; }
        if (Path.GetFullPath(outPath).Equals(Path.GetFullPath(SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The output must be a new file, not the template itself.");
        RenameOptionFolders(res, log);
        Write(outPath, res.Replaced, res.Renamed);
        log.Info($"Wrote {outPath}");
        return res;
    }

    /// <summary>Do the swap and return the changed ERPs without writing anything (no verification).</summary>
    public BuildResult BuildInMemory(GameData game, string donorToken, IReadOnlyList<CarEntry> selected, SwapLog log, string? animationDonor = null)
    {
        if (selected.Count == 0) throw new InvalidOperationException("No cars selected.");
        var res = new BuildResult { DonorToken = donorToken };
        BuildWheel(game, donorToken, animationDonor ?? donorToken, selected, res, log);
        if (res.Replaced.Count == 0) throw new InvalidOperationException("Nothing to do: every selected car already has that steering wheel.");
        return res;
    }

    /// <summary>The car's real (pre-swap) model: game files, then every template ERP carrying model resources,
    /// the entry itself last. <paramref name="replaced"/> = ERPs already rewritten in this build.</summary>
    public CarModel LoadModel(GameData game, CarEntry ce, IReadOnlyDictionary<string, byte[]>? replaced = null)
    {
        ErpFile Fresh(string n) => ErpX.Load(replaced?.GetValueOrDefault(n) ?? Get(n));
        // other chassis options of the template are alternatives: they may only fill in what is missing
        var layers = _modelErps.Where(n => n != ce.ZipPath && AppliesTo(n, ce)).Select(Fresh).ToList();
        var fallback = _modelErps.Where(n => n != ce.ZipPath && !AppliesTo(n, ce)).Select(Fresh).ToList();
        if (_modelErps.Contains(ce.ZipPath)) layers.Add(Fresh(ce.ZipPath));
        return new CarModel(game, ce.ModelToken, layers, fallback: fallback);
    }

    void BuildWheel(GameData game, string donorToken, string animDonor, IReadOnlyList<CarEntry> selected, BuildResult result, SwapLog log)
    {
        var replaced = result.Replaced;
        foreach (var ce in selected)
            if (ce.Problem is not null) throw new InvalidOperationException($"{ce.FileName}: {ce.Problem}");
        // donor: a team's car of one season ("ferrari" = 2026, "ferrari@2025")
        var (dTok, dSeason) = GameData.ParseDonor(donorToken);
        bool oldSeason = dSeason != GameData.CurrentSeason;
        var donorTex = TextureSwap.LoadDonor(game, GameCar(game, dTok, dSeason, log), log);
        // driver animations: normally the wheel donor's own
        var (aTok, aSeason) = GameData.ParseDonor(animDonor);
        bool oldAnim = aSeason != GameData.CurrentSeason;
        if (animDonor != donorToken) log.Info($"Driver animations taken from {GameData.DonorName(animDonor)} (not the wheel donor).");
        var incar = IncarDriver(game, GameData.CurrentSeason);
        var donorIncar = oldAnim ? IncarDriver(game, aSeason) : incar;
        var donorAnimId = GameData.AnimsetId(GameData.AnimsetName(aTok, aSeason));
        if (!donorIncar.TryGetValue(donorAnimId, out var donorAnim)) throw new InvalidDataException($"No in-car animation set for {GameData.DonorName(animDonor)}");
        var donorAnimPkg = ErpX.Data(donorAnim);
        var donorClipFile = AnimsetSwap.ClipFile(donorAnimPkg);
        // clip file -> donor id, to tell which wheel an earlier build gave a car (2025 clip files too)
        var clipOwner = new Dictionary<string, string>();
        foreach (var season in new[] { GameData.CurrentSeason, 2025 })
        {
            var inc = season == GameData.CurrentSeason ? incar : season == aSeason ? donorIncar : TryIncarDriver(game, season);
            if (inc is null) continue;
            foreach (var t in GameData.Donors.Where(d => d.Season == season))
                if (inc.TryGetValue(GameData.AnimsetId(GameData.AnimsetName(t.Token, season)), out var a) && AnimsetSwap.ClipFile(ErpX.Data(a)) is { } f)
                    clipOwner.TryAdd(f, t.Id);
        }
        // clip files of other seasons (embedded into entries by earlier builds; removed again on a re-swap)
        var foreignClipFiles = clipOwner.Where(kv => kv.Value.Contains('@')).Select(kv => kv.Key).ToHashSet();
        ErpFile Fresh(string n) => ErpX.Load(replaced.GetValueOrDefault(n) ?? Get(n));

        foreach (var ce in selected)
        {
            try { BuildEntry(ce); }
            catch (Exception ex) when (!ex.Message.StartsWith(ce.FileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{ce.FileName}: {ex.Message}", ex);
            }
        }

        void BuildEntry(CarEntry ce)
        {
            log.Info($"=== {ce.FileName} ({ce.Describe.Split("  —")[0]}) ===");
            var entry = Fresh(ce.ZipPath);

            // 0. an earlier output of this tool: strip its private model so the new wheel replaces the old one
            if (ce.PreviousSwap is { } prev)
            {
                var was = PreviousDonor(entry, ce, clipOwner);
                if (was == donorToken) { log.Warn($"{ce.FileName} already has that steering wheel; skipped."); return; }
                int removed = Unswap(entry, prev, foreignClipFiles);
                log.Info($"Earlier Steering Wheel Swapper build ({(was is null ? "unknown" : GameData.DonorName(was))} wheel): " +
                         $"removed its private model ({removed} resources), so the new wheel replaces it.");
                replaced[ce.ZipPath] = ErpX.Save(entry);   // the cleaned entry is this build's starting point
                entry = Fresh(ce.ZipPath);
            }
            var idx = ErpX.Index(entry);

            // 1. the car's real model: game files, then every template ERP carrying model resources,
            //    the entry itself last (a swapped chassis lives in the template)
            var target = LoadModel(game, ce, replaced);
            string meshToken = target.MeshToken;
            var donorCar = new CarModel(game, dTok, dSeason);
            if (ce.PreviousSwap is null && SameWheel(target, donorCar)) { log.Warn($"{ce.FileName} already has that steering wheel; skipped."); return; }
            if (ce.Swapped) log.Info($"Swapped chassis: model {target.Token}.idf, geometry {target.MeshToken}.idf (from the template).");
            foreach (var tok in new[] { target.Token, target.MeshToken }.Distinct())
                if (game.ModdedNote(tok) is { } note) log.Warn(note);
            var (clones, priv, renames) = WheelGrafter.Run(target, donorCar, n => PrivateName(n, donorToken), log);
            foreach (var c in clones)
            {
                if (idx.ContainsKey(c.Identifier)) throw new InvalidOperationException($"{ce.FileName} already contains {c.Identifier}");
                ErpX.AddOrReplace(entry, c);
            }

            // 2. car model layers -> private model
            string from = $"/idf/{ce.ModelToken}.idf?context=render", to = $"/idf/{priv}.idf?context=render";
            if (ce.Kind == EntryKind.MyTeamLivery)
            {
                // the MyTeam car is spawned from the 2026 fom_car slot (myteam_<engine> slots covered too)
                foreach (var slot in GameData.MyTeamSlots)
                {
                    var path = game.TeamErp(slot);
                    if (!File.Exists(path)) continue;
                    var g = ErpX.Index(ErpX.Load(path));
                    foreach (var suf in new[] { "-carmodel.layer", "-carshowroommodel.layer" })
                    {
                        var id = $"eaid://f1_2026_vehicle_package/teams/{slot}/wep/{slot}{suf}";
                        if (!g.TryGetValue(id, out var lr)) continue;
                        var d = ErpX.Data(lr);
                        if (ErpX.ReplaceBytes(d, from, to) == 0) continue;
                        lr.Fragments[0].SetData(d, true); ErpX.Rehash(lr);
                        ErpX.AddOrReplace(entry, lr);
                        log.Info($"Car model layer {slot}{suf} -> {priv}");
                    }
                }
            }
            else
            {
                int n = 0;
                foreach (var r in entry.Resources.Where(r => r.Identifier.EndsWith("-carmodel.layer", StringComparison.Ordinal) ||
                                                             r.Identifier.EndsWith("-carshowroommodel.layer", StringComparison.Ordinal)))
                {
                    var d = ErpX.Data(r);
                    if (ErpX.ReplaceBytes(d, from, to) == 0) continue;
                    r.Fragments[0].SetData(d, true); ErpX.Rehash(r); n++;
                    log.Info($"Car model layer {r.Identifier.Split('/')[^1]} -> {priv}");
                }
                if (n == 0) throw new InvalidDataException("no car model layer points at the target model");
            }

            var finder = new Finder(this, entry, ce.ZipPath, replaced, game,
                new[] { ce.SlotToken, ce.ModelToken, target.MeshToken }.Where(t => t is not null)!);

            // 3. driver animation set -> donor clips
            ErpResource animset;
            if (ce.AnimsetId is not null) animset = idx[ce.AnimsetId];
            else
            {
                var animTok = ce.Kind == EntryKind.MyTeamLivery ? "fom_car" : ce.SlotToken ?? ce.ModelToken;
                var id = GameData.AnimsetId(GameData.AnimsetName(animTok));
                var found = finder.Find(id);
                animset = found?.Res ?? (incar.TryGetValue(id, out var g) ? g : throw new InvalidDataException($"no driver animation set {id}"));
                if (found is not { InEntry: true }) ErpX.AddOrReplace(entry, animset);
                if (ce.Kind == EntryKind.MyTeamLivery)
                    log.Warn($"MyTeam uses the generic FOM driver animation set ({GameData.AnimsetName(animTok)}); any other car using it will also get the new hand animations.");
                else log.Info($"Added the {GameData.AnimsetName(animTok)} driver animation set to the entry.");
            }
            AnimsetSwap.Retarget(animset, donorAnimPkg, log);
            var animsetId = animset.Identifier;
            if (oldAnim && donorClipFile is not null)
            {
                // the game only loads a season's driver clips in that season: carry the donor's clips in the entry
                // (SERPs injects every resource, so the 2026 car finds them), together with everything they use
                // (each clip names its bones through the clip file's animstringtable; without it the game crashes
                // when the in-car animation starts). Fresh copies for this entry.
                var source = ErpX.Load(game.IncarDriverErpFor(aSeason));
                var byId = ErpX.Index(source);
                var queue = new Queue<ErpResource>(source.Resources
                    .Where(r => r.ResourceType == "AnimClip" && r.Identifier.Contains($"/idf/{donorClipFile}.idf?context=animation", StringComparison.Ordinal)));
                var carried = new List<ErpResource>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (queue.Count > 0)
                {
                    var r = queue.Dequeue();
                    if (!seen.Add(r.Identifier)) continue;
                    carried.Add(r);
                    foreach (var f in r.Fragments)
                        foreach (var e in ErpX.Eaids(f.GetDataArray(true)))
                            if (!incar.ContainsKey(e) && byId.TryGetValue(e, out var dep)) queue.Enqueue(dep);   // not in the 2026 game files
                }
                foreach (var c in carried) ErpX.AddOrReplace(entry, c);
                int clipCount = carried.Count(c => c.ResourceType == "AnimClip");
                var extra = carried.Where(c => c.ResourceType != "AnimClip").Select(c => c.ResourceType).Distinct().ToList();
                log.Info($"Driver animation clips: embedded {clipCount} {aSeason} clips ({donorClipFile})" +
                         (extra.Count > 0 ? $" and their {string.Join(", ", extra)}" : "") + $" so the hands work in the {GameData.CurrentSeason} season.");
            }

            // 4. steering wheel textures: the ones the car's material binding really uses
            var texIds = ResolveSteeringTextures(ce, finder, log);
            foreach (var ch in new[] { "d", "n", "s", "decals_da" }.Where(ch => texIds.All(t => TextureSwap.ChannelOf(t) != ch)))
                log.Warn($"{ce.FileName}: the car's wheel material has no '{ch}' texture; that layer keeps the target car's artwork.");
            foreach (var t in texIds)
            {
                var ch = TextureSwap.ChannelOf(t)!;
                if (!donorTex.TryGetValue(ch, out var dds)) continue;
                var srv = EnsureInEntry(entry, t, finder);
                TextureSwap.Apply(entry, srv, dds);
                log.Info($"Texture {t.Split('/')[^1]} <- donor {ch} ({dds.header.width}x{dds.header.height})");
            }

            replaced[ce.ZipPath] = ErpX.Save(entry);
            Validate(replaced[ce.ZipPath], clones.Select(c => c.Identifier).ToHashSet(), renames.Values, log);
            result.Entries.Add(new BuiltEntry(ce, donorToken, ce.ModelToken, meshToken, priv, renames, texIds, animsetId,
                texIds.Select(TextureSwap.ChannelOf).Where(c => c is not null && donorTex.ContainsKey(c))
                      .ToDictionary(c => c!, c => ((int)donorTex[c!].header.width, (int)donorTex[c!].header.height)), animDonor));

            // 5. sibling entries that bind the wheel to a material this template does not define
            FixSiblingBindings(ce, entry, texIds, replaced, log);
        }
    }

    /// <summary>A stock car from the game, with clear errors for missing / unreadable files and a warning for modded ones.</summary>
    static CarModel GameCar(GameData game, string token, int season, SwapLog log)
    {
        var path = game.TeamErp(token, season);
        var name = season == GameData.CurrentSeason ? GameData.TeamName(token) : $"{season} {GameData.TeamName(token)}";
        if (!File.Exists(path))
            throw new FileNotFoundException($"The game's {name} car files are missing ({path}). Check the game folder, or verify the game files in Steam.");
        if (game.ModdedNote(token, season) is { } note) log.Warn(note);
        try { return new CarModel(game, token, season); }
        catch (Exception ex)
        {
            throw new InvalidDataException($"The game's {name} car file could not be read ({path}): {ex.Message}. " +
                                           "It may be damaged or replaced by a mod; verify the game files in Steam.", ex);
        }
    }

    /// <summary>
    /// The car already has exactly the donor's wheel: same geometry namespace, and every wheel mesh and the buffers it
    /// draws from are byte-identical (a template's older copy of a team's own model, e.g. Williams' release chassis,
    /// has a different wheel and is built, not skipped).
    /// </summary>
    static bool SameWheel(CarModel target, CarModel donor)
    {
        if (target.MeshToken != donor.MeshToken) return false;
        for (int lod = 0; lod < 7; lod++)
            foreach (var part in WheelGrafter.Family)
            {
                var q = $"?lod={lod}&{part}&mesh";
                bool a = target.Has(q), b = donor.Has(q);
                if (a != b) return false;
                if (!a) continue;
                var tm = target.Res[target.MeshId(q)]; var dm = donor.Res[donor.MeshId(q)];
                if (!tm.Hash.AsSpan().SequenceEqual(dm.Hash)) return false;
                foreach (var e in target.Mesh(q).Eaids)
                    if (!target.Res.TryGetValue(e, out var x) || !donor.Res.TryGetValue(e, out var y) || !x.Hash.AsSpan().SequenceEqual(y.Hash)) return false;
            }
        return true;
    }

    /// <summary>The game's in-car driver animation file of a season (clear errors if missing / unreadable).</summary>
    static Dictionary<string, ErpResource> IncarDriver(GameData game, int season)
    {
        var path = game.IncarDriverErpFor(season);
        if (!File.Exists(path))
            throw new FileNotFoundException($"The game's {(season == GameData.CurrentSeason ? "" : season + " ")}in-car driver animations are missing ({path}). Check the game folder, or verify the game files in Steam.");
        try { return ErpX.Index(ErpX.Load(path)); }
        catch (Exception ex) { throw new InvalidDataException($"The game's in-car driver animation file could not be read ({path}): {ex.Message}. Verify the game files in Steam.", ex); }
    }

    static Dictionary<string, ErpResource>? TryIncarDriver(GameData game, int season)
    {
        try { return File.Exists(game.IncarDriverErpFor(season)) ? ErpX.Index(ErpX.Load(game.IncarDriverErpFor(season))) : null; }
        catch { return null; }
    }

    /// <summary>Which donor's wheel (id, e.g. ferrari@2025) an earlier build gave this entry (from the clip file its animation set plays).</summary>
    static string? PreviousDonor(ErpFile entry, CarEntry ce, Dictionary<string, string> clipOwner)
    {
        var sets = entry.Resources.Where(r => r.ResourceType == "AnimSet").OrderByDescending(r => r.Identifier == ce.AnimsetId);
        foreach (var r in sets)
            if (AnimsetSwap.ClipFile(ErpX.Data(r)) is { } f && clipOwner.TryGetValue(f, out var t)) return t;
        return null;
    }

    /// <summary>Remove an earlier build's private model from the entry and point everything back at the
    /// namespaces it was cloned from (same length, so references are patched in place).</summary>
    static int Unswap(ErpFile entry, Dictionary<string, string> privToOrig, IReadOnlySet<string> foreignClipFiles)
    {
        int removed = entry.Resources.RemoveAll(r => privToOrig.Keys.Any(p => r.Identifier.Contains($"/idf/{p}.idf", StringComparison.Ordinal))
            // older-season clip files carried by an earlier build: their clips and string table
            || r.ResourceType is "AnimClip" or "ACStringTable" && foreignClipFiles.Any(f => r.Identifier.Contains($"/idf/{f}.idf?context=anim", StringComparison.Ordinal)));
        foreach (var r in entry.Resources)
        {
            bool changed = false;
            foreach (var f in r.Fragments)
            {
                var d = f.GetDataArray(true);
                int hits = 0;
                foreach (var (p, o) in privToOrig) hits += ErpX.ReplaceInEaids(d, $"/idf/{p}.idf", $"/idf/{o}.idf");
                if (hits > 0) { f.SetData(d, true); changed = true; }
            }
            if (changed) ErpX.Rehash(r);
        }
        return removed;
    }

    /// <summary>Follow the car's material binding(s) to its steering-wheel material(s) and their textures.</summary>
    static List<string> ResolveSteeringTextures(CarEntry ce, Finder finder, SwapLog log)
    {
        var mats = new HashSet<string>(StringComparer.Ordinal);
        foreach (var emb in ce.MatBindings)
        {
            var r = finder.Find(emb);
            if (r is null) continue;
            foreach (var m in ErpX.Eaids(ErpX.Data(r.Value.Res)).Where(e => e.EndsWith("steeringwheel.material", StringComparison.Ordinal))) mats.Add(m);
        }
        var tex = new List<string>();
        foreach (var m in mats)
        {
            var r = finder.Find(m);
            if (r is null) { log.Warn($"Wheel material {m.Split('/')[^1]} not found."); continue; }
            foreach (var t in ErpX.Eaids(ErpX.Data(r.Value.Res)).Where(t => TextureSwap.ChannelOf(t) is not null))
                if (!tex.Contains(t)) tex.Add(t);
        }
        if (tex.Count == 0)
        {
            if (ce.SteeringTextures.Count == 0) throw new InvalidDataException($"{ce.FileName}: could not find the car's steering wheel textures");
            log.Info("Wheel material not resolved; using the steering wheel textures carried by the entry.");
            return ce.SteeringTextures;
        }
        log.Info($"Wheel material {string.Join(", ", mats.Select(m => m.Split('/')[^1]))} -> {tex.Count} textures");
        return tex;
    }

    /// <summary>Make sure a texture (SRV + surface) is in the entry, copying it from the template or game if needed.</summary>
    static ErpResource EnsureInEntry(ErpFile entry, string srvId, Finder finder)
    {
        var found = finder.Find(srvId) ?? throw new InvalidDataException($"texture {srvId} not found");
        if (found.InEntry) return found.Res;
        var probe = new ErpGfxSRVResource();
        probe.FromResource(found.Res);
        var surf = finder.Find(probe.Fragment0.SurfaceResourceName) ?? throw new InvalidDataException($"texture surface for {srvId} not found");
        if (!surf.InEntry) ErpX.AddOrReplace(entry, surf.Res);
        ErpX.AddOrReplace(entry, found.Res);
        return found.Res;
    }

    void FixSiblingBindings(CarEntry ce, ErpFile entry, List<string> texIds, Dictionary<string, byte[]> replaced, SwapLog log)
    {
        var texSet = texIds.ToHashSet();
        var ownMat = entry.Resources.FirstOrDefault(r => r.ResourceType == "GfxMaterialRes" &&
                                                         ErpX.Eaids(ErpX.Data(r)).Any(texSet.Contains))?.Identifier;
        if (ownMat is null) return;
        var folder = ce.ZipPath[..(ce.ZipPath.LastIndexOf('/') + 1)];
        var siblings = _entries.Where(e => e.Name.StartsWith(folder) && e.Name != ce.ZipPath && e.Name.EndsWith(".erp")).Select(e => e.Name).ToList();
        var defined = new HashSet<string>();
        var loaded = new Dictionary<string, ErpFile>();
        foreach (var s in siblings.Append(ce.ZipPath))
        {
            var erp = s == ce.ZipPath ? entry : ErpX.Load(replaced.GetValueOrDefault(s) ?? Get(s));
            loaded[s] = erp;
            foreach (var r in erp.Resources.Where(r => r.ResourceType == "GfxMaterialRes")) defined.Add(r.Identifier);
        }
        foreach (var s in siblings)
        {
            var erp = loaded[s]; int fixedCtx = 0; var olds = new HashSet<string>();
            foreach (var r in erp.Resources.Where(r => r.ResourceType == "GfxMatBindRes"))
            {
                var d = ErpX.Data(r); bool ch = false;
                foreach (var m in SteerMatRef.Matches(Encoding.ASCII.GetString(d)).Select(m => m.Value).Distinct())
                {
                    if (m == ownMat || defined.Contains(m) || m.Length != ownMat.Length) continue;
                    if (ErpX.ReplaceBytes(d, m, ownMat) > 0) { ch = true; olds.Add(m); }
                }
                if (ch) { r.Fragments[0].SetData(d, true); ErpX.Rehash(r); fixedCtx++; }
            }
            if (fixedCtx > 0)
            {
                replaced[s] = ErpX.Save(erp);
                log.Info($"{s.Split('/')[^1]}: {fixedCtx} material contexts bound the wheel to " +
                         $"{string.Join(", ", olds.Select(o => o.Split('/')[^1]))} (not in this template) -> {ownMat.Split('/')[^1]}");
            }
        }
    }

    static void Validate(byte[] erpBytes, HashSet<string> cloneIds, IEnumerable<string> privateNames, SwapLog log)
    {
        var erp = ErpX.Load(erpBytes);
        var ids = erp.Resources.Select(r => r.Identifier).ToHashSet();
        var privs = privateNames.Select(p => $"/idf/{p}.idf").ToList();
        int missing = 0, badHash = 0;
        foreach (var r in erp.Resources.Where(r => cloneIds.Contains(r.Identifier)))
        {
            if (!ErpX.HashValid(r)) badHash++;
            foreach (var f in r.Fragments)
                foreach (var e in ErpX.Eaids(f.GetDataArray(true)))
                    if (privs.Any(p => e.Contains(p)) && !ids.Contains(e)) missing++;
        }
        if (cloneIds.Any(c => !ids.Contains(c))) missing++;
        if (missing > 0 || badHash > 0) throw new InvalidDataException($"validation failed: {missing} missing references, {badHash} bad hashes");
        log.Info($"Validated: {erp.Resources.Count} resources, private model self-contained.");
    }

    /// <summary>
    /// Option folders whose cars all got the new wheel are named after it ("Livery - FOM Chassis - 26 Ferrari Wheel"),
    /// so SERPs shows which wheel each option has; an earlier wheel ending is replaced. A folder that also holds a car
    /// that was not given the wheel (e.g. MyTeam's two paints share one folder) keeps its name.
    /// </summary>
    void RenameOptionFolders(BuildResult res, SwapLog log)
    {
        var built = res.Entries.Select(b => b.Entry.ZipPath).ToHashSet();
        var suffix = GameData.WheelSuffix(res.DonorToken);
        foreach (var folder in res.Entries.Select(b => b.Entry.Folder).Where(f => f.Length > 0).Distinct())
        {
            var others = CarEntries.Where(c => c.Folder == folder && !built.Contains(c.ZipPath)).ToList();
            var leaf = folder[(folder.LastIndexOf('/') + 1)..];
            if (others.Count > 0)
            {
                log.Info($"Folder '{leaf}' keeps its name: it also holds {string.Join(", ", others.Select(o => o.FileName))}, which did not get the new wheel.");
                continue;
            }
            var newFolder = folder[..(folder.Length - leaf.Length)] + GameData.StripWheelSuffix(leaf) + " - " + suffix;
            if (newFolder == folder) continue;
            foreach (var (name, _, _) in _entries.Where(e => e.Name == folder + "/" || e.Name.StartsWith(folder + "/", StringComparison.Ordinal)))
                res.Renamed[name] = newFolder + name[folder.Length..];
            log.Info($"Folder '{leaf}' -> '{newFolder[(newFolder.LastIndexOf('/') + 1)..]}'");
        }
    }

    void Write(string outPath, Dictionary<string, byte[]> replaced, IReadOnlyDictionary<string, string> renamed)
    {
        // written next to the target and moved into place, so a failure never leaves a half-written .serps
        var tmp = outPath + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (Comment is not null) zip.Comment = Comment;
                foreach (var (name, data, stored) in _entries)
                {
                    var bytes = replaced.GetValueOrDefault(name) ?? data;
                    var e = zip.CreateEntry(renamed.GetValueOrDefault(name, name), stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    if (name.EndsWith('/')) continue;
                    using var s = e.Open(); s.Write(bytes);
                }
            }
            try { File.Move(tmp, outPath, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Could not save {Path.GetFileName(outPath)}: it is open in another program or read-only. " +
                                      "Close it (or pick another name) and build again.", ex);
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
