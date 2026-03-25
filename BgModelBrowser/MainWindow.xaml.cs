using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumina;
using Lumina.Data;
using Lumina.Excel.Sheets;
using Microsoft.Win32;

namespace BgModelBrowser;

/// <summary>Collapses element when string is null or empty.</summary>
public partial class StringToVisConverter : IValueConverter
{
    public static readonly StringToVisConverter Instance = new();
    public static readonly StringToVisConverter InverseInstance = new() { Invert = true };
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var empty = string.IsNullOrEmpty(value as string);
        return (empty ^ Invert) ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotImplementedException();
}

public class BgModelEntry : INotifyPropertyChanged
{
    public string Path { get; init; } = "";
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Directory => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";
    public string Expansion { get; init; } = "";
    public string ZoneType { get; init; } = "";
    public string Zone { get; init; } = "";
    public string DisplayName { get; set; } = "";

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set { _isFavorite = value; OnPropertyChanged(); OnPropertyChanged(nameof(FavoriteStar)); }
    }
    public string FavoriteStar => _isFavorite ? "\u2605" : "\u2606"; // ★ or ☆

    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static readonly Dictionary<string, string> ExpansionNames = new()
    {
        ["ffxiv"] = "A Realm Reborn",
        ["ex1"] = "Heavensward",
        ["ex2"] = "Stormblood",
        ["ex3"] = "Shadowbringers",
        ["ex4"] = "Endwalker",
        ["ex5"] = "Dawntrail",
    };

    private static readonly Dictionary<string, string> ZoneTypeNames = new()
    {
        ["fld"] = "Field",
        ["twn"] = "Town",
        ["dun"] = "Dungeon",
        ["hou"] = "Housing",
        ["pvp"] = "PvP",
        ["ind"] = "Indoor",
        ["ray"] = "Raid",
        ["tri"] = "Trial",
    };

    private static readonly Dictionary<string, string> VfxCategoryNames = new()
    {
        ["action"] = "Action",
        ["monster"] = "Monster",
        ["common"] = "Common",
        ["chara"] = "Character",
        ["cut"] = "Cutscene",
        ["omen"] = "Omen",
        ["resident"] = "Resident",
        ["locaction"] = "Location Action",
    };

    public static BgModelEntry FromPath(string path)
    {
        var parts = path.Split('/');
        // bg/{expansion}/{area}/{zoneType}/{zoneId}/bgparts/...
        var expKey = parts.Length > 1 ? parts[1] : "";
        var zoneTypeKey = parts.Length > 3 ? parts[3] : "";
        var zoneId = parts.Length > 4 ? parts[4] : "";

        // bgcommon paths have different structure
        if (path.StartsWith("bgcommon/hou/"))
        {
            // bgcommon/hou/indoor/general/0001/bgparts/fun_b0_m0001.mdl
            expKey = "housing";
            zoneTypeKey = parts.Length > 2 ? parts[2] : "";  // indoor/outdoor
            zoneId = parts.Length > 4 ? parts[4] : "";
        }
        else if (path.StartsWith("bgcommon/"))
        {
            expKey = "bgcommon";
            zoneTypeKey = parts.Length > 1 ? parts[1] : "";
            zoneId = parts.Length > 2 ? parts[2] : "";
        }
        else if (path.StartsWith("vfx/"))
        {
            // vfx/{category}/{id}/eff/...
            expKey = "vfx";
            zoneTypeKey = parts.Length > 1 ? parts[1] : "";  // action, monster, common, etc.
            zoneId = parts.Length > 2 ? parts[2] : "";
        }

        var expansion = expKey switch
        {
            "housing" => "Housing",
            "vfx" => "VFX",
            _ => ExpansionNames.GetValueOrDefault(expKey, expKey)
        };
        var zoneType = expKey == "vfx"
            ? VfxCategoryNames.GetValueOrDefault(zoneTypeKey, zoneTypeKey)
            : zoneTypeKey switch
            {
                "indoor" => "Indoor Furniture",
                "outdoor" => "Outdoor Furniture",
                _ => ZoneTypeNames.GetValueOrDefault(zoneTypeKey, zoneTypeKey)
            };

        return new BgModelEntry
        {
            Path = path,
            Expansion = expansion,
            ZoneType = zoneType,
            Zone = zoneId,
        };
    }
}

public partial class MainWindow : Window
{
    private GameData? _gameData;
    private List<BgModelEntry> _allModels = new();
    private List<BgModelEntry> _allVfx = new();
    private List<BgModelEntry> _filteredModels = new();
    private int _currentPage;
    private const int PageSize = 200;
    private string _activeExpansion = "All";
    private string _activeZoneType = "All";
    private CancellationTokenSource _renderCts = new();
    private HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private bool _vfxMode;

    private static string FavoritesPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BgModelBrowser", "favorites.json");

    public MainWindow()
    {
        LoadFavorites();
        InitializeComponent();
        DetectGamePath();
        UpdatePageDisplay();
        SetDataLoaded(false);
        Loaded += async (_, _) => await TryLoadFromCache();
    }

    private void SetDataLoaded(bool loaded)
    {
        var vis = loaded ? Visibility.Visible : Visibility.Collapsed;
        ModeBar.Visibility = vis;
        ExpansionBar.Visibility = vis;
        ZoneTypeBar.Visibility = vis;
        SearchBar.Visibility = vis;
        PageBar.Visibility = vis;
    }

    private async Task TryLoadFromCache()
    {
        try
        {
            var sqpackPath = GamePathBox.Text.Trim();
            if (!System.IO.Directory.Exists(sqpackPath)) return;

            var cacheFile = GetCachePath(sqpackPath);
            var cached = TryLoadCache(cacheFile);
            if (cached == null || cached.Models.Count == 0) return;

            StatusText.Text = "Loading from cache...";
            _gameData = new GameData(sqpackPath, new LuminaOptions { PanicOnSheetChecksumMismatch = false });

            _housingNames = new(StringComparer.OrdinalIgnoreCase);
            await Task.Run(BuildHousingNameLookup);

            _allModels = cached.Models.Select(p =>
            {
                var entry = BgModelEntry.FromPath(p);
                if (_housingNames.TryGetValue(p, out var n)) entry.DisplayName = n;
                entry.IsFavorite = _favorites.Contains(p);
                return entry;
            }).ToList();

            _allVfx = (cached.Vfx ?? new()).Select(p =>
            {
                var entry = BgModelEntry.FromPath(p);
                entry.IsFavorite = _favorites.Contains(p);
                return entry;
            }).ToList();

            _currentPage = 0;
            SetDataLoaded(true);
            BuildTabs();
            ApplyFilter();
            StatusText.Text = $"Loaded {_allModels.Count} models, {_allVfx.Count} VFX from cache. Click Scan to refresh.";
            CancelAndRestartRendering();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Cache load failed: {ex.Message}. Click Scan.";
        }
    }

    private void DetectGamePath()
    {
        try
        {
            var configPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher", "launcherConfigV3.json");

            if (File.Exists(configPath))
            {
                var json = JsonDocument.Parse(File.ReadAllText(configPath));
                if (json.RootElement.TryGetProperty("GamePath", out var gp))
                {
                    var gamePath = gp.GetString();
                    if (!string.IsNullOrEmpty(gamePath))
                    {
                        var sqpackPath = System.IO.Path.Combine(gamePath, "game", "sqpack");
                        if (System.IO.Directory.Exists(sqpackPath))
                        {
                            GamePathBox.Text = sqpackPath;
                            return;
                        }
                    }
                }
            }
        }
        catch { }

        // Fallback: common install paths
        var commonPaths = new[]
        {
            @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack",
            @"C:\Games\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack",
        };

        foreach (var p in commonPaths)
        {
            if (System.IO.Directory.Exists(p))
            {
                GamePathBox.Text = p;
                return;
            }
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select FFXIV sqpack directory" };
        if (dlg.ShowDialog() == true)
            GamePathBox.Text = dlg.FolderName;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        var sqpackPath = GamePathBox.Text.Trim();
        if (!System.IO.Directory.Exists(sqpackPath))
        {
            MessageBox.Show("Invalid sqpack directory.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ScanBtn.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = "Initializing game data...";

        try
        {
            _gameData = new GameData(sqpackPath, new LuminaOptions { PanicOnSheetChecksumMismatch = false });

            // Scan button always does a fresh scan
            var (models, vfx) = await Task.Run(() => ScanForBgModels());
            _allModels = models;
            _allVfx = vfx;

            // Save to cache for next startup
            var cacheFile = GetCachePath(sqpackPath);
            SaveCache(cacheFile, new ScanCache
            {
                Models = _allModels.Select(m => m.Path).ToList(),
                Vfx = _allVfx.Select(v => v.Path).ToList()
            });

            // Apply favorites to all loaded models
            foreach (var m in _allModels)
                m.IsFavorite = _favorites.Contains(m.Path);
            foreach (var v in _allVfx)
                v.IsFavorite = _favorites.Contains(v.Path);

            _currentPage = 0;
            SetDataLoaded(true);
            BuildTabs();
            ApplyFilter();

            StatusText.Text = $"Found {_allModels.Count} models, {_allVfx.Count} VFX. Loading thumbnails...";

            CancelAndRestartRendering();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Scan failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Scan failed.";
        }
        finally
        {
            ScanBtn.IsEnabled = true;
            ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    // --- Cache ---

    private static string GetCachePath(string sqpackPath)
    {
        // Stable hash — .NET randomizes string.GetHashCode() per process
        var hash = StableHash(sqpackPath.ToLowerInvariant()).ToString("X8");
        var cacheDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BgModelBrowser");
        System.IO.Directory.CreateDirectory(cacheDir);
        return System.IO.Path.Combine(cacheDir, $"scan_cache_{hash}.json");
    }

    private static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (var c in s) { h ^= c; h *= 16777619; }
        return h;
    }

    private class ScanCache
    {
        public List<string> Models { get; set; } = new();
        public List<string> Vfx { get; set; } = new();
    }

    private static ScanCache? TryLoadCache(string cacheFile)
    {
        try
        {
            if (!File.Exists(cacheFile)) return null;
            if (File.GetLastWriteTime(cacheFile) < DateTime.Now.AddDays(-7)) return null;
            var json = File.ReadAllText(cacheFile);

            // Try new format first
            try
            {
                return JsonSerializer.Deserialize<ScanCache>(json);
            }
            catch
            {
                // Fallback: old format was just List<string> of model paths
                var oldList = JsonSerializer.Deserialize<List<string>>(json);
                if (oldList != null)
                    return new ScanCache { Models = oldList };
                return null;
            }
        }
        catch { return null; }
    }

    private static void SaveCache(string cacheFile, ScanCache cache)
    {
        try { File.WriteAllText(cacheFile, JsonSerializer.Serialize(cache)); }
        catch { }
    }

    // --- Favorites ---

    private void LoadFavorites()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(FavoritesPath)!;
            System.IO.Directory.CreateDirectory(dir);
            if (File.Exists(FavoritesPath))
                _favorites = new HashSet<string>(
                    JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FavoritesPath)) ?? [],
                    StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }

    private void SaveFavorites()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(FavoritesPath)!;
            System.IO.Directory.CreateDirectory(dir);
            File.WriteAllText(FavoritesPath, JsonSerializer.Serialize(_favorites.ToList()));
        }
        catch { }
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not BgModelEntry entry) return;

        entry.IsFavorite = !entry.IsFavorite;

        if (entry.IsFavorite)
            _favorites.Add(entry.Path);
        else
            _favorites.Remove(entry.Path);

        SaveFavorites();
        UpdateFavoritesTabCount();

        if (_activeExpansion == "\u2605 Favorites")
            ApplyFilter();
    }

    private void UpdateFavoritesTabCount()
    {
        foreach (var child in ExpansionTabPanel.Children)
        {
            if (child is RadioButton rb && rb.Tag as string == "\u2605 Favorites")
            {
                var count = ActiveList.Count(m => m.IsFavorite);
                rb.Content = $"\u2605 Favorites ({count})";
                break;
            }
        }
    }

    // --- Scanning ---

    private int _scanProgress;

    private void UpdateScanStatus(string phase, int pct, int found)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ProgressBar.Value = pct;
            StatusText.Text = $"{phase} — {found} found";
        });
    }

    private (List<BgModelEntry> Models, List<BgModelEntry> Vfx) ScanForBgModels()
    {
        var foundPaths = new ConcurrentBag<string>();

        // Get territory bg paths from Excel
        var sheet = _gameData!.GetExcelSheet<TerritoryType>();
        if (sheet == null) return (new List<BgModelEntry>(), new List<BgModelEntry>());
        var basePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in sheet)
        {
            var bg = row.Bg.ToString();
            if (string.IsNullOrWhiteSpace(bg)) continue;

            var levelIdx = bg.IndexOf("/level/", StringComparison.OrdinalIgnoreCase);
            if (levelIdx > 0)
                basePaths.Add(bg[..levelIdx]);
        }

        var basePathList = basePaths.ToList();
        var total = basePathList.Count;

        // Phase 1: Parallel brute-force enumerate bgparts
        _scanProgress = 0;
        Parallel.ForEach(basePathList, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            basePath =>
            {
                var localFound = new List<string>();
                EnumerateBgParts(basePath, localFound);
                foreach (var p in localFound) foundPaths.Add(p);

                var done = Interlocked.Increment(ref _scanProgress);
                if (done % 5 == 0 || done == total)
                    UpdateScanStatus($"[1/3] bgparts ({done}/{total})", (int)(40.0 * done / total), foundPaths.Count);
            });

        // Phase 2: Parallel scan LGB/LVB binaries
        _scanProgress = 0;
        Parallel.ForEach(basePathList, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            basePath =>
            {
                var localFound = new List<string>();
                ScanZoneLevelFiles(basePath, localFound);
                foreach (var p in localFound) foundPaths.Add(p);

                var done = Interlocked.Increment(ref _scanProgress);
                if (done % 10 == 0 || done == total)
                    UpdateScanStatus($"[2/3] level files ({done}/{total})", 40 + (int)(30.0 * done / total), foundPaths.Count);
            });

        // Phase 3: Parallel housing enumeration
        UpdateScanStatus("[3/3] Housing objects...", 70, foundPaths.Count);
        EnumerateHousingModels(foundPaths);

        // Deduplicate and build entries
        var deduped = new HashSet<string>(foundPaths, StringComparer.OrdinalIgnoreCase);
        var allPaths = deduped.OrderBy(p => p).ToList();

        var models = allPaths
            .Where(p => p.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                var entry = BgModelEntry.FromPath(p);
                if (_housingNames.TryGetValue(p, out var name))
                    entry.DisplayName = name;
                return entry;
            })
            .ToList();

        var vfx = allPaths
            .Where(p => p.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase))
            .Select(p => BgModelEntry.FromPath(p))
            .ToList();

        return (models, vfx);
    }

    // Common bgparts model suffixes: 3-letter type code + 2-digit number
    private static readonly string[] BgPartTypes =
    {
        "flr", "wal", "cel", "rof", "pil", "fen", "col", "obj", "gnd", "tgt",
        "bri", "arc", "dor", "lmp", "sig", "stg", "gar", "pot", "box", "bar",
        "sno", "rck", "tre", "grs", "ivy", "riv", "fal", "mtn", "sea", "lak",
        "cav", "cry", "gem", "ore", "met", "wod", "sto", "brk", "til", "mrb",
        "gls", "fab", "ppr", "crt", "stn", "plt", "twr", "hil", "clb", "bnk",
        "shr", "bsh", "log", "mss", "vne", "flo", "lef", "wtr", "fnt", "lgt",
        "chn", "rpe", "flg", "bnr", "cld", "trc", "ral", "pip", "whl", "anc",
        "can", "tnt", "cpt", "blt", "bks", "crn", "edg", "rim", "top", "btm",
        "inn", "out", "mid", "sde", "cnt", "end", "pth", "rdg", "wdw", "stp",
    };

    private void EnumerateBgParts(string basePath, List<string> foundPaths)
    {
        var parts = basePath.Split('/');
        if (parts.Length == 0) return;
        var zoneId = parts[^1];
        var bgpartsDir = $"bg/{basePath}/bgparts";

        // Quick check: does this zone have bgparts at all?
        bool hasAny = false;
        foreach (var block in new[] { "a0", "b0", "c0", "d0" })
        {
            foreach (var type in BgPartTypes)
            {
                if (_gameData!.FileExists($"{bgpartsDir}/{zoneId}_{block}_{type}01.mdl"))
                { hasAny = true; break; }
            }
            if (hasAny) break;
        }
        if (!hasAny) return;

        // Enumerate all blocks × types × numbers
        for (char letter = 'a'; letter <= 'z'; letter++)
        {
            for (int digit = 0; digit <= 9; digit++)
            {
                var block = $"{letter}{digit}";
                foreach (var type in BgPartTypes)
                {
                    for (int num = 1; num <= 50; num++)
                    {
                        var path = $"{bgpartsDir}/{zoneId}_{block}_{type}{num:D2}.mdl";
                        if (_gameData!.FileExists(path))
                            foundPaths.Add(path);
                    }
                }
            }
        }
    }

    // Maps housing model paths to display names
    private Dictionary<string, string> _housingNames = new(StringComparer.OrdinalIgnoreCase);

    private void BuildHousingNameLookup()
    {
        // HousingFurniture sheet: maps ModelKey → Item name (indoor)
        try
        {
            var furnitureSheet = _gameData!.GetExcelSheet<HousingFurniture>();
            if (furnitureSheet != null)
            {
                foreach (var row in furnitureSheet)
                {
                    try
                    {
                        var modelKey = row.ModelKey;
                        if (modelKey == 0) continue;
                        var itemName = row.Item.ValueNullable?.Name.ToString();
                        if (string.IsNullOrEmpty(itemName)) continue;

                        var idStr = modelKey.ToString("D4");
                        // Map all possible path patterns to this name
                        foreach (var sub in new[] { "general", "company" })
                            foreach (var dir in new[] { "bgparts", "asset" })
                                foreach (var block in new[] { "b0", "b1", "b2" })
                                    _housingNames[$"bgcommon/hou/indoor/{sub}/{idStr}/{dir}/fun_{block}_m{idStr}.mdl"] = itemName;
                    }
                    catch { }
                }
            }
        }
        catch { }

        // HousingYardObject sheet: maps ModelKey → Item name (outdoor)
        try
        {
            var yardSheet = _gameData!.GetExcelSheet<HousingYardObject>();
            if (yardSheet != null)
            {
                foreach (var row in yardSheet)
                {
                    try
                    {
                        var modelKey = row.ModelKey;
                        if (modelKey == 0) continue;
                        var itemName = row.Item.ValueNullable?.Name.ToString();
                        if (string.IsNullOrEmpty(itemName)) continue;

                        var idStr = modelKey.ToString("D4");
                        foreach (var sub in new[] { "general", "company" })
                            foreach (var dir in new[] { "bgparts", "asset" })
                                foreach (var block in new[] { "b0", "b1", "b2" })
                                    _housingNames[$"bgcommon/hou/outdoor/{sub}/{idStr}/{dir}/gar_{block}_m{idStr}.mdl"] = itemName;
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private void EnumerateHousingModels(ConcurrentBag<string> foundPaths)
    {
        BuildHousingNameLookup();

        var housingPatterns = new (string location, string subdir, string prefix)[]
        {
            ("indoor", "general", "fun"),
            ("indoor", "company", "fun"),
            ("outdoor", "general", "gar"),
            ("outdoor", "company", "gar"),
        };

        _scanProgress = 0;
        var totalPatterns = housingPatterns.Length;

        Parallel.ForEach(housingPatterns, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            pattern =>
            {
                var (location, subdir, prefix) = pattern;
                for (int id = 1; id <= 9999; id++)
                {
                    var idStr = id.ToString("D4");
                    var baseDir = $"bgcommon/hou/{location}/{subdir}/{idStr}";

                    foreach (var dir in new[] { "bgparts", "asset" })
                    {
                        foreach (var block in new[] { "b0", "b1", "b2" })
                        {
                            var path = $"{baseDir}/{dir}/{prefix}_{block}_m{idStr}.mdl";
                            if (_gameData!.FileExists(path))
                                foundPaths.Add(path);
                        }
                    }
                }

                var done = Interlocked.Increment(ref _scanProgress);
                UpdateScanStatus($"[3/3] Housing ({done}/{totalPatterns})", 70 + (int)(30.0 * done / totalPatterns), foundPaths.Count);
            });
    }

    private void ScanZoneLevelFiles(string basePath, List<string> foundPaths)
    {
        // Try many LGB/LVB filenames
        var lgbNames = new[]
        {
            "bg", "planmap", "planevent", "planner", "sound", "bg_env", "vfx",
            "light", "envset", "mapobj", "asset", "planmap_s", "planevent_s"
        };

        var filesToScan = new List<string>();

        foreach (var name in lgbNames)
        {
            filesToScan.Add($"bg/{basePath}/level/{name}.lgb");
            filesToScan.Add($"bg/{basePath}/level/{name}.lvb");
            for (int n = 0; n < 20; n++)
            {
                filesToScan.Add($"bg/{basePath}/level/{name}_{n}.lgb");
                filesToScan.Add($"bg/{basePath}/level/{name}_{n}.lvb");
            }
        }

        foreach (var filePath in filesToScan)
        {
            try
            {
                if (!_gameData!.FileExists(filePath)) continue;

                var file = _gameData.GetFile<FileResource>(filePath);
                if (file?.Data == null || file.Data.Length == 0) continue;

                ScanBinaryForPaths(file.Data, foundPaths, ".mdl", ".avfx");
            }
            catch { }
        }
    }

    private static void ScanBinaryForPaths(byte[] data, List<string> foundPaths, params string[] extensions)
    {
        for (int i = 0; i < data.Length - 10; i++)
        {
            // Look for common path prefixes: bg/, bgcommon/, vfx/
            var isBg = data[i] == 'b' && data[i + 1] == 'g' && (data[i + 2] == '/' || data[i + 2] == 'c');
            var isVfx = data[i] == 'v' && data[i + 1] == 'f' && data[i + 2] == 'x' && data[i + 3] == '/';
            if (!isBg && !isVfx) continue;

            var end = i;
            while (end < data.Length && data[end] != 0 && data[end] >= 0x20 && data[end] <= 0x7E)
                end++;

            var len = end - i;
            if (len < 10 || len > 512) continue;
            if (end >= data.Length || data[end] != 0) continue;

            var path = Encoding.ASCII.GetString(data, i, len);
            foreach (var ext in extensions)
            {
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && path.Contains('/'))
                {
                    foundPaths.Add(path);
                    break;
                }
            }
        }
    }

    // --- Tabs ---

    private void BuildTabs()
    {
        // Expansion tabs
        ExpansionTabPanel.Children.Clear();
        var expansions = ActiveList.Select(m => m.Expansion).Distinct().OrderBy(e => e).ToList();
        AddTab(ExpansionTabPanel, "All", "expansion", true);
        AddTab(ExpansionTabPanel, "\u2605 Favorites", "expansion", false, _favorites.Count);
        foreach (var exp in expansions)
        {
            var count = ActiveList.Count(m => m.Expansion == exp);
            AddTab(ExpansionTabPanel, exp, "expansion", false, count);
        }

        // Zone type tabs (will be rebuilt when expansion changes)
        RebuildZoneTypeTabs();
    }

    private void RebuildZoneTypeTabs()
    {
        ZoneTypeTabPanel.Children.Clear();

        var source = _activeExpansion == "All"
            ? ActiveList
            : ActiveList.Where(m => m.Expansion == _activeExpansion);

        var zoneTypes = source.Select(m => m.ZoneType).Where(z => !string.IsNullOrEmpty(z))
            .Distinct().OrderBy(z => z).ToList();

        AddTab(ZoneTypeTabPanel, "All", "zonetype", true);
        foreach (var zt in zoneTypes)
        {
            var count = source.Count(m => m.ZoneType == zt);
            AddTab(ZoneTypeTabPanel, zt, "zonetype", false, count);
        }
    }

    private void AddTab(StackPanel panel, string label, string group, bool isChecked, int count = -1)
    {
        var display = count >= 0 ? $"{label} ({count})" : label;
        var rb = new RadioButton
        {
            Content = display,
            Tag = label,
            GroupName = group,
            IsChecked = isChecked,
            Style = (Style)FindResource(group == "expansion" ? "TabBtn" : "SubTabBtn"),
        };
        rb.Checked += TabChanged;
        panel.Children.Add(rb);
    }

    private void TabChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        var label = rb.Tag as string ?? "All";

        if (rb.GroupName == "expansion")
        {
            _activeExpansion = label;
            _activeZoneType = "All";
            RebuildZoneTypeTabs();
        }
        else
        {
            _activeZoneType = label;
        }

        _currentPage = 0;
        ApplyFilter();
        CancelAndRestartRendering();
    }

    private List<BgModelEntry> ActiveList => _vfxMode ? _allVfx : _allModels;

    private void BrowseMode_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        if (!IsLoaded) return;
        _vfxMode = rb == ModeVfx;
        _currentPage = 0;
        _activeExpansion = "All";
        _activeZoneType = "All";
        SearchBox.Text = "";
        SearchPlaceholder.Text = _vfxMode
            ? "Search VFX by name or path..."
            : "Search models by name or path...";
        BuildTabs();
        ApplyFilter();
        CancelAndRestartRendering();
    }

    // --- Filtering & Pagination ---

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _currentPage = 0;
        ApplyFilter();
        CancelAndRestartRendering();
    }

    private void ApplyFilter()
    {
        var search = SearchBox.Text.Trim();

        IEnumerable<BgModelEntry> result = ActiveList;

        if (_activeExpansion == "\u2605 Favorites")
            result = result.Where(m => m.IsFavorite);
        else if (_activeExpansion != "All")
            result = result.Where(m => m.Expansion == _activeExpansion);

        if (_activeZoneType != "All")
            result = result.Where(m => m.ZoneType == _activeZoneType);

        if (!string.IsNullOrEmpty(search))
            result = result.Where(m =>
                m.Path.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                m.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase));

        _filteredModels = result.ToList();

        UpdatePageDisplay();
        ShowCurrentPage();
    }

    private void ShowCurrentPage()
    {
        var page = _filteredModels
            .Skip(_currentPage * PageSize)
            .Take(PageSize)
            .ToList();

        ModelGrid.ItemsSource = page;
    }

    private void UpdatePageDisplay()
    {
        var totalPages = Math.Max(1, (_filteredModels.Count + PageSize - 1) / PageSize);
        var label = _vfxMode ? "VFX" : "models";
        PageInfo.Text = $"Page {_currentPage + 1} of {totalPages}  ({_filteredModels.Count} {label})";
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 0)
        {
            _currentPage--;
            UpdatePageDisplay();
            ShowCurrentPage();
            CancelAndRestartRendering();
        }
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        var totalPages = (_filteredModels.Count + PageSize - 1) / PageSize;
        if (_currentPage < totalPages - 1)
        {
            _currentPage++;
            UpdatePageDisplay();
            ShowCurrentPage();
            CancelAndRestartRendering();
        }
    }

    private void CancelAndRestartRendering()
    {
        _renderCts.Cancel();
        _renderCts.Dispose();
        _renderCts = new CancellationTokenSource();
        _ = LoadPageThumbnailsAsync(_renderCts.Token);
    }

    private async Task LoadPageThumbnailsAsync(CancellationToken ct = default)
    {
        if (_gameData == null) return;

        var pageModels = _filteredModels
            .Skip(_currentPage * PageSize)
            .Take(PageSize)
            .Where(m => m.Thumbnail == null)
            .ToList();

        if (pageModels.Count == 0) return;

        var total = pageModels.Count;
        var rendered = 0;

        foreach (var model in pageModels)
        {
            if (ct.IsCancellationRequested) return;

            rendered++;
            StatusText.Text = _vfxMode
                ? $"Loading VFX texture {rendered}/{total} — {model.FileName}"
                : $"Rendering 3D preview {rendered}/{total} — {model.DisplayName switch { "" => model.FileName, var n => n }}";

            if (_vfxMode)
            {
                // VFX: extract texture from AVFX file
                var gameData = _gameData;
                var path = model.Path;
                var preview = await Task.Run(() =>
                {
                    if (ct.IsCancellationRequested) return null;
                    return ModelRenderer.LoadVfxPreview(gameData, path);
                }, ct).ContinueWith(t => t.IsFaulted || t.IsCanceled ? null : t.Result);

                if (ct.IsCancellationRequested) return;
                if (preview != null)
                    model.Thumbnail = preview;
                else
                {
                    var placeholder = CreateVfxPlaceholder(128);
                    if (placeholder != null) model.Thumbnail = placeholder;
                }
            }
            else
            {
                // Load geometry + texture on background thread
                var gameData = _gameData;
                var path = model.Path;
                var loadResult = await Task.Run(() =>
                {
                    if (ct.IsCancellationRequested) return null;
                    return ModelRenderer.LoadModel(gameData, path);
                }, ct).ContinueWith(t => t.IsFaulted || t.IsCanceled ? null : t.Result);

                if (ct.IsCancellationRequested) return;

                if (loadResult != null)
                {
                    var preview = ModelRenderer.RenderPreview(loadResult.Value);
                    if (preview != null)
                        model.Thumbnail = preview;
                }
            }

            await Task.Yield();
        }

        if (!ct.IsCancellationRequested)
        {
            var label = _vfxMode ? "VFX" : "models";
            StatusText.Text = $"Found {ActiveList.Count} {label}. Page {_currentPage + 1} rendered.";
        }
    }

    private static BitmapSource CreateVfxPlaceholder(int size)
    {
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var i = (y * size + x) * 4;
            var t = (double)y / size;
            pixels[i + 0] = (byte)(0x30 + t * 0x20); // B
            pixels[i + 1] = (byte)(0x11);              // G
            pixels[i + 2] = (byte)(0x40 + t * 0x30); // R — purple tint
            pixels[i + 3] = 255;
        }
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bmp.Freeze();
        return bmp;
    }

    // --- Context menu ---

    private void SubTabScroll_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string path }) return;

        var menu = new ContextMenu();
        var fileName = System.IO.Path.GetFileName(path);
        var dirPath = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

        var copyPath = new MenuItem { Header = "Copy Full Path" };
        copyPath.Click += (_, _) => Clipboard.SetText(path);
        menu.Items.Add(copyPath);

        var copyFile = new MenuItem { Header = $"Copy Filename ({fileName})" };
        copyFile.Click += (_, _) => Clipboard.SetText(fileName);
        menu.Items.Add(copyFile);

        var copyDir = new MenuItem { Header = "Copy Directory" };
        copyDir.Click += (_, _) => Clipboard.SetText(dirPath);
        menu.Items.Add(copyDir);

        menu.IsOpen = true;
    }
}
