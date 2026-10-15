using System.Text.RegularExpressions;

namespace WheelSwap.Core;

/// <summary>A wheel donor: a team's car of one season. <see cref="Id"/> is the token for 2026 cars and
/// "token@2025" for the 2025 cars (e.g. ferrari@2025); <see cref="Label"/> adds the year for 2025.</summary>
public sealed record Team(string Token, string Name, int Season = GameData.CurrentSeason)
{
    public string Id => Season == GameData.CurrentSeason ? Token : $"{Token}@{Season}";
    public string Label => Season == GameData.CurrentSeason ? Name : $"{Name} {Season}";
}

/// <summary>Locations and naming inside an F1 25 install (2026 vehicle package, plus the 2025 cars as wheel donors).</summary>
public sealed class GameData
{
    public static readonly Team[] Teams2026 =
    [
        new("fom_car", "FOM chassis"),
        new("force_india", "Aston Martin"),
        new("ferrari", "Ferrari"),
        new("gm_cadillac", "Cadillac"),
        new("haas", "Haas"),
        new("lotus", "Alpine"),
        new("mclaren", "McLaren"),
        new("mercedes", "Mercedes"),
        new("redbull", "Red Bull"),
        new("sauber", "Audi"),
        new("toro_rosso", "Racing Bulls"),
        new("williams", "Williams"),
    ];

    public const int CurrentSeason = 2026;

    /// <summary>The 2025 cars (still in the game's f1_2025_vehicle_package) offered as wheel donors. No 2025 Cadillac
    /// or Audi; the 2025 FOM car has no model of its own.</summary>
    public static readonly Team[] Teams2025 =
    [
        new("force_india", "Aston Martin", 2025),
        new("ferrari", "Ferrari", 2025),
        new("haas", "Haas", 2025),
        new("lotus", "Alpine", 2025),
        new("mclaren", "McLaren", 2025),
        new("mercedes", "Mercedes", 2025),
        new("redbull", "Red Bull", 2025),
        new("sauber", "Kick Sauber", 2025),
        new("toro_rosso", "Racing Bulls", 2025),
        new("williams", "Williams", 2025),
    ];

    /// <summary>Every wheel donor: the 2026 cars, then the 2025 cars.</summary>
    public static readonly Team[] Donors = [.. Teams2026, .. Teams2025];

    /// <summary>"ferrari" -> (ferrari, 2026); "ferrari@2025" -> (ferrari, 2025).</summary>
    public static (string Token, int Season) ParseDonor(string id)
    {
        var at = id.IndexOf('@');
        return at < 0 ? (id, CurrentSeason) : (id[..at], int.Parse(id[(at + 1)..]));
    }

    public static bool IsDonor(string id) => Donors.Any(d => d.Id == id);

    /// <summary>"26 Ferrari Wheel", "25 Aston Martin Wheel": the ending a build gives the file and option folder names.</summary>
    public static string WheelSuffix(string donorId)
    {
        var t = Donors.FirstOrDefault(d => d.Id == donorId);
        var (_, season) = ParseDonor(donorId);
        return $"{season % 100:00} {t?.Name ?? donorId} Wheel";
    }

    /// <summary>A name without the wheel ending an earlier build gave it (" - 26 McLaren Wheel", or the older
    /// " - McLaren wheel" / " - McLaren 2025 wheel"), so a re-swap replaces the ending instead of adding another.</summary>
    public static string StripWheelSuffix(string name)
    {
        var names = string.Join("|", Donors.Select(x => x.Name).Distinct().Select(Regex.Escape).OrderByDescending(x => x.Length));
        return Regex.Replace(name, $@" - (?:\d\d (?:{names}) Wheel|(?:{names})(?: \d{{4}})? wheel)$", "", RegexOptions.IgnoreCase);
    }

    /// <summary>"Ferrari" or "Ferrari 2025".</summary>
    public static string DonorName(string id) => Donors.FirstOrDefault(d => d.Id == id)?.Label ?? id;

    /// <summary>Team slots the MyTeam car is spawned from.</summary>
    public static readonly string[] MyTeamSlots =
        ["fom_car", "myteam_audi", "myteam_ferrari", "myteam_honda", "myteam_mercedes", "myteam_redbull_ford", "myteam_renault"];

    public string GameDir { get; }
    public string Assets => Path.Combine(GameDir, "2025_asset_groups");

    public GameData(string gameDir)
    {
        GameDir = gameDir;
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            throw new DirectoryNotFoundException($"The F1 25 game folder was not found: {gameDir}. Use Detect or Browse… to pick it.");
        if (!Directory.Exists(Path.Combine(Assets, "f1_2026_vehicle_package")))
            throw new DirectoryNotFoundException($"{gameDir} is not an F1 25 game folder with the 2026 season " +
                                                 "(2025_asset_groups\\f1_2026_vehicle_package is missing). Pick the folder that contains F1_25.exe.");
    }

    public static string TeamName(string token) => Teams2026.FirstOrDefault(t => t.Token == token)?.Name ?? token;

    static readonly HashSet<string> ModelTypes = ["ModelProxyRes", "GfxModelRes", "GfxMeshRes", "GfxBufferRes"];
    readonly Dictionary<string, string?> _modded = new();

    /// <summary>
    /// A warning when the game's team ERP has been modified in its model or steering-wheel data (mod managers leave
    /// the stock file as <c>X.erp.bak</c>): the wheel would then be taken from the mod, not the stock car. Changes
    /// elsewhere (e.g. handling .vtf files from a performance mod) don't matter and give no warning.
    /// </summary>
    public string? ModdedNote(string token, int season = CurrentSeason)
    {
        lock (_modded)
        {
            var key = $"{token}@{season}";
            if (_modded.TryGetValue(key, out var cached)) return cached;
            string? note = null;
            var cur = TeamErp(token, season);
            var who = season == CurrentSeason ? TeamName(token) : $"{TeamName(token)} {season}";
            var bak = cur + ".bak";
            if (File.Exists(cur) && File.Exists(bak))
            {
                try
                {
                    var a = ErpX.Index(ErpX.Load(bak));
                    var b = ErpX.Index(ErpX.Load(cur));
                    static bool Relevant(EgoEngineLibrary.Archive.Erp.ErpResource r) =>
                        ModelTypes.Contains(r.ResourceType) || r.Identifier.Contains("steering", StringComparison.Ordinal);
                    int changed = b.Values.Count(r => Relevant(r) && (!a.TryGetValue(r.Identifier, out var o) || !o.Hash.AsSpan().SequenceEqual(r.Hash)))
                                + a.Values.Count(r => Relevant(r) && !b.ContainsKey(r.Identifier));
                    if (changed > 0)
                        note = $"The game's {who} files are modded ({changed} model/steering wheel resources differ from {Path.GetFileName(bak)}); " +
                               "the wheel and car shape are taken from the modded files.";
                }
                catch (Exception ex) { note = $"Could not compare the game's {who} files with {Path.GetFileName(bak)}: {ex.Message}"; }
            }
            return _modded[key] = note;
        }
    }

    public string TeamErp(string token, int season = CurrentSeason) =>
        Path.Combine(Assets, $"f1_{season}_vehicle_package", "teams", token, "wep", token + ".erp");

    public string IncarDriverErp => IncarDriverErpFor(CurrentSeason);

    public string IncarDriverErpFor(int season) =>
        Path.Combine(Assets, "animation_package", "scenes", "incardriver", season.ToString(), "incardriver_f1.erp");

    /// <summary>Full path of a texture's external .mipmaps file (paths in the ERP are package-relative).</summary>
    public string? MipmapsPath(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        foreach (var root in new[] { Assets, GameDir })
        {
            var p = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public const string AnimPrefix = "eaid://animation_package/animations_driver_in_car/driver_steering_anims/idf/";

    /// <summary>In-car driver animation set used by a car model / team slot (driver_in_car_...).</summary>
    public static string AnimsetName(string token, int season = CurrentSeason) => token switch
    {
        "fom_car" => $"driver_in_car_fom_{season % 100}",
        _ => $"driver_in_car_{season}_" + token.Replace("force_india", "forceindia"),
    };

    public static string AnimsetId(string animsetName) => AnimPrefix + animsetName + ".idf?context=animset";

    public static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Best-effort Steam library scan for "F1 25".</summary>
    public static string? DetectGameDir()
    {
        var candidates = new List<string>();
        var steamRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Steam",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Steam",
        };
        foreach (var root in steamRoots)
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                candidates.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            candidates.Add(root);
        }
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            candidates.Add(Path.Combine(d.Name, "SteamLibrary"));
            candidates.Add(Path.Combine(d.Name, "Program Files (x86)", "Steam"));
        }
        foreach (var lib in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(lib, "steamapps", "common", "F1 25");
            if (Directory.Exists(Path.Combine(dir, "2025_asset_groups", "f1_2026_vehicle_package"))) return dir;
        }
        return null;
    }
}
