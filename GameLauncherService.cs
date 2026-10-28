using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace IslaDinamica;

public record GameEntry(
    string Id,
    string Name,
    string Platform,       // "Steam", "Epic", "Riot"
    string LaunchUri,
    string? LaunchArgs,
    DateTime LastPlayedUtc,
    bool HasRealLastPlayed,
    BitmapImage? Cover,
    string? ExePath = null);

/// <summary>
/// Detecta automáticamente los últimos juegos jugados en Steam, Epic Games y Riot Games (LoL, Valorant, etc.),
/// unificándolos por fecha real de última partida y obteniendo sus carátulas o iconos en alta calidad.
/// </summary>
public sealed class GameLauncherService
{
    static readonly HashSet<string> s_ignoredSteamAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "228980", // Steamworks Common Redistributables
        "250820", // SteamVR
    };

    static readonly Regex s_vdfPathRegex = new(@"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex s_lastPlayedRegex = new(@"""LastPlayed""\s+""(\d+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex s_yamlInstallPathRegex = new(@"product_install_full_path\s*:\s*""?([^""\r\n]+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(5) };

    readonly Dictionary<string, BitmapImage?> _coverCache = new(StringComparer.OrdinalIgnoreCase);
    readonly object _historyLock = new();
    Dictionary<string, long> _localHistory = new(StringComparer.OrdinalIgnoreCase);
    bool _historyLoaded;

    readonly string _appDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IslaDinamica");
    string DiskCoverCacheDir => Path.Combine(_appDataDir, "covers");
    string HistoryFilePath => Path.Combine(_appDataDir, "game_history.json");

    List<GameEntry> _cachedGames = new();
    DateTime _lastScanUtc = DateTime.MinValue;

    public event Action<IReadOnlyList<GameEntry>>? Changed;

    public IReadOnlyList<GameEntry> GetRecentGames(int maxCount = 4, bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedGames.Count > 0 && (DateTime.UtcNow - _lastScanUtc).TotalSeconds < 15)
            return _cachedGames;

        _lastScanUtc = DateTime.UtcNow;
        var list = ScanAllPlatforms(maxCount);
        _cachedGames = list;
        return list;
    }

    public Task RefreshAsync(int maxCount = 4) => Task.Run(async () =>
    {
        var games = ScanAllPlatforms(maxCount);

        bool updatedCovers = false;
        for (int i = 0; i < games.Count; i++)
        {
            var g = games[i];
            if (g.Cover != null) continue;
            var downloaded = await TryFetchRemoteCoverAsync(g);
            if (downloaded != null)
            {
                lock (_coverCache) _coverCache[g.Id] = downloaded;
                games[i] = g with { Cover = downloaded };
                updatedCovers = true;
            }
        }

        _cachedGames = games;
        _lastScanUtc = DateTime.UtcNow;
        if (updatedCovers || games.Count >= 0)
            Changed?.Invoke(games);
    });

    /// <summary>
    /// Comprueba de forma ligera si alguno de los juegos detectados se está ejecutando ahora mismo
    /// para actualizar su fecha de "Último jugado" automáticamente (útil para Epic y Riot).
    /// </summary>
    public void CheckRunningGames()
    {
        var snapshot = _cachedGames;
        if (snapshot.Count == 0) return;

        try
        {
            bool anyUpdated = false;
            foreach (var g in snapshot)
            {
                if (IsGameProcessRunning(g))
                {
                    RecordPlayedNow(g.Id);
                    anyUpdated = true;
                }
            }
            if (anyUpdated) _ = RefreshAsync(4);
        }
        catch { }
    }

    public void Launch(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchUri)) return;
        try
        {
            RecordPlayedNow(game.Id);
            var psi = new ProcessStartInfo(game.LaunchUri)
            {
                UseShellExecute = true,
                Arguments = game.LaunchArgs ?? ""
            };
            Process.Start(psi);
            _ = RefreshAsync(4);
        }
        catch
        {
            if (!string.IsNullOrEmpty(game.ExePath) && File.Exists(game.ExePath))
            {
                try { Process.Start(new ProcessStartInfo(game.ExePath) { UseShellExecute = true }); }
                catch { }
            }
        }
    }

    void EnsureHistoryLoaded()
    {
        lock (_historyLock)
        {
            if (_historyLoaded) return;
            _historyLoaded = true;
            try
            {
                if (File.Exists(HistoryFilePath))
                {
                    var json = File.ReadAllText(HistoryFilePath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
                    if (dict != null)
                        _localHistory = new Dictionary<string, long>(dict, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
        }
    }

    void RecordPlayedNow(string id)
    {
        EnsureHistoryLoaded();
        lock (_historyLock)
        {
            _localHistory[id] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            try
            {
                Directory.CreateDirectory(_appDataDir);
                File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(_localHistory));
            }
            catch { }
        }
    }

    DateTime? GetHistoryTimeUtc(string id)
    {
        EnsureHistoryLoaded();
        lock (_historyLock)
        {
            if (_localHistory.TryGetValue(id, out long unix) && unix > 0)
                return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        }
        return null;
    }

    static bool IsGameProcessRunning(GameEntry g)
    {
        if (g.Id.Equals("riot:valorant", StringComparison.OrdinalIgnoreCase))
            return Process.GetProcessesByName("VALORANT-Win64-Shipping").Length > 0;
        if (g.Id.Equals("riot:league_of_legends", StringComparison.OrdinalIgnoreCase))
            return Process.GetProcessesByName("LeagueClientUx").Length > 0 ||
                   Process.GetProcessesByName("League of Legends").Length > 0;

        if (!string.IsNullOrEmpty(g.ExePath))
        {
            string procName = Path.GetFileNameWithoutExtension(g.ExePath);
            if (!string.IsNullOrEmpty(procName))
                return Process.GetProcessesByName(procName).Length > 0;
        }
        return false;
    }

    List<GameEntry> ScanAllPlatforms(int maxCount)
    {
        EnsureHistoryLoaded();
        var all = new List<GameEntry>();

        try { all.AddRange(ScanSteamGames()); } catch { }
        try { all.AddRange(ScanEpicGames()); } catch { }
        try { all.AddRange(ScanRiotGames()); } catch { }

        return all
            .OrderByDescending(g => g.HasRealLastPlayed)
            .ThenByDescending(g => g.LastPlayedUtc)
            .Take(maxCount)
            .ToList();
    }

    // ==================== 1. STEAM ====================
    List<GameEntry> ScanSteamGames()
    {
        var result = new List<GameEntry>();
        string? steamRoot = GetSteamRootPath();
        if (string.IsNullOrEmpty(steamRoot) || !Directory.Exists(steamRoot))
            return result;

        var steamAppsDirs = GetSteamAppsDirectories(steamRoot);
        var installed = new Dictionary<string, (string Name, DateTime FallbackTimeUtc)>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in steamAppsDirs)
        {
            IEnumerable<string> acfFiles;
            try { acfFiles = Directory.EnumerateFiles(dir, "appmanifest_*.acf"); }
            catch { continue; }

            foreach (var acf in acfFiles)
            {
                try
                {
                    string text = File.ReadAllText(acf);
                    string appId = ExtractVdfValue(text, "appid");
                    string name = ExtractVdfValue(text, "name");
                    if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name)) continue;
                    if (s_ignoredSteamAppIds.Contains(appId) || IsIgnoredGameName(name)) continue;

                    long lastUpdatedUnix = long.TryParse(ExtractVdfValue(text, "LastUpdated"), out var lu) ? lu : 0;
                    DateTime fallback = lastUpdatedUnix > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(lastUpdatedUnix).UtcDateTime
                        : File.GetLastWriteTimeUtc(acf);

                    installed[appId] = (name, fallback);
                }
                catch { }
            }
        }

        if (installed.Count == 0) return result;

        var lastPlayedMap = ReadLastPlayedFromLocalConfigs(steamRoot, installed.Keys);

        foreach (var kv in installed)
        {
            string appId = kv.Key;
            bool hasPlayed = lastPlayedMap.TryGetValue(appId, out long unix) && unix > 0;
            DateTime timeUtc = hasPlayed
                ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                : kv.Value.FallbackTimeUtc;

            var hist = GetHistoryTimeUtc(appId);
            if (hist.HasValue && hist.Value > timeUtc)
            {
                timeUtc = hist.Value;
                hasPlayed = true;
            }

            BitmapImage? cover;
            lock (_coverCache)
            {
                if (!_coverCache.TryGetValue(appId, out cover) || cover == null)
                {
                    cover = TryLoadSteamLocalCover(steamRoot, appId);
                    if (cover != null) _coverCache[appId] = cover;
                }
            }

            result.Add(new GameEntry(
                appId,
                kv.Value.Name,
                "Steam",
                $"steam://rungameid/{appId}",
                null,
                timeUtc,
                hasPlayed,
                cover));
        }

        return result;
    }

    // ==================== 2. EPIC GAMES ====================
    List<GameEntry> ScanEpicGames()
    {
        var result = new List<GameEntry>();
        string manifestsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifestsDir)) return result;

        IEnumerable<string> itemFiles;
        try { itemFiles = Directory.EnumerateFiles(manifestsDir, "*.item"); }
        catch { return result; }

        foreach (var file in itemFiles)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                string displayName = GetJsonString(root, "DisplayName");
                string appName = GetJsonString(root, "AppName");
                string installLoc = GetJsonString(root, "InstallLocation");
                string launchExe = GetJsonString(root, "LaunchExecutable");
                string ns = GetJsonString(root, "CatalogNamespace");
                string itemId = GetJsonString(root, "CatalogItemId");

                if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(appName)) continue;
                if (!string.IsNullOrEmpty(launchExe) && launchExe.Contains("UnrealEngine", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrEmpty(installLoc) || !Directory.Exists(installLoc)) continue;

                // Evitar plugins/DLC sin ejecutable propio
                string? exePath = !string.IsNullOrEmpty(launchExe)
                    ? Path.Combine(installLoc, launchExe.Replace('/', '\\'))
                    : null;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) continue;

                string id = $"epic:{appName}";
                var (lastTimeUtc, hasPlayed) = GetEpicLastPlayedUtc(id, installLoc, exePath, file);

                string launchTarget = !string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(itemId)
                    ? $"com.epicgames.launcher://apps/{ns}%3A{itemId}%3A{appName}?action=launch&silent=true"
                    : $"com.epicgames.launcher://apps/{appName}?action=launch&silent=true";

                BitmapImage? cover;
                lock (_coverCache)
                {
                    if (!_coverCache.TryGetValue(id, out cover) || cover == null)
                    {
                        cover = TryLoadFolderSplashOrIcon(id, installLoc, exePath);
                        if (cover != null) _coverCache[id] = cover;
                    }
                }

                result.Add(new GameEntry(
                    id,
                    displayName,
                    "Epic",
                    launchTarget,
                    null,
                    lastTimeUtc,
                    hasPlayed,
                    cover,
                    exePath));
            }
            catch { }
        }

        return result;
    }

    (DateTime TimeUtc, bool HasPlayed) GetEpicLastPlayedUtc(string id, string installLoc, string exePath, string itemFile)
    {
        DateTime best = DateTime.MinValue;
        bool hasPlayed = false;

        var hist = GetHistoryTimeUtc(id);
        if (hist.HasValue)
        {
            best = hist.Value;
            hasPlayed = true;
        }

        // Revisar carpetas de logs o guardados dentro del juego si existen
        try
        {
            foreach (var logFile in Directory.EnumerateFiles(installLoc, "*.log", SearchOption.AllDirectories).Take(25))
            {
                var wt = File.GetLastWriteTimeUtc(logFile);
                if (wt > best) { best = wt; hasPlayed = true; }
            }
        }
        catch { }

        if (!hasPlayed)
        {
            try
            {
                var exeAccess = File.GetLastAccessTimeUtc(exePath);
                var itemWrite = File.GetLastWriteTimeUtc(itemFile);
                best = exeAccess > itemWrite ? exeAccess : itemWrite;
                hasPlayed = exeAccess > itemWrite.AddMinutes(10);
            }
            catch { best = DateTime.UtcNow.AddDays(-30); }
        }

        return (best, hasPlayed);
    }

    // ==================== 3. RIOT GAMES (LoL, VALORANT, TFT, LoR, 2XKO) ====================
    List<GameEntry> ScanRiotGames()
    {
        var result = new List<GameEntry>();
        string? riotClientExe = GetRiotClientExecutablePath();

        var products = new (string Key, string DefaultName, string DefaultDir, string ExeRelPath)[]
        {
            ("valorant", "VALORANT", @"C:\Riot Games\VALORANT\live", @"ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"),
            ("league_of_legends", "League of Legends", @"C:\Riot Games\League of Legends", @"LeagueClient.exe"),
            ("bacon", "Legends of Runeterra", @"C:\Riot Games\LoR\live", @"LoR.exe"),
            ("lion", "2XKO", @"C:\Riot Games\2XKO\live", @"2XKO.exe")
        };

        string metadataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Riot Games", "Metadata");

        foreach (var prod in products)
        {
            string? installDir = null;

            // 1. Buscar ruta real en C:\ProgramData\Riot Games\Metadata\<key>.live\*.product_settings.yaml
            string prodMetaDir = Path.Combine(metadataRoot, $"{prod.Key}.live");
            if (Directory.Exists(prodMetaDir))
            {
                try
                {
                    foreach (var yaml in Directory.EnumerateFiles(prodMetaDir, "*.product_settings.yaml"))
                    {
                        var m = s_yamlInstallPathRegex.Match(File.ReadAllText(yaml));
                        if (m.Success)
                        {
                            string candidate = m.Groups[1].Value.Trim().Replace('/', '\\');
                            if (Directory.Exists(candidate))
                            {
                                installDir = candidate;
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            // 2. Respaldo en C:\Riot Games\...
            if (string.IsNullOrEmpty(installDir) && Directory.Exists(prod.DefaultDir))
                installDir = prod.DefaultDir;

            if (string.IsNullOrEmpty(installDir)) continue;

            string exePath = Path.Combine(installDir, prod.ExeRelPath);
            if (!File.Exists(exePath))
            {
                // Por si la ruta de Metadata apunta un nivel más arriba o más abajo
                string altExe = Path.Combine(installDir, "live", prod.ExeRelPath);
                if (File.Exists(altExe)) exePath = altExe;
                else continue;
            }

            string id = $"riot:{prod.Key}";
            var (lastTimeUtc, hasPlayed) = GetRiotLastPlayedUtc(id, prod.Key, installDir, exePath);

            string launchUri = !string.IsNullOrEmpty(riotClientExe) && File.Exists(riotClientExe)
                ? riotClientExe
                : exePath;
            string? launchArgs = !string.IsNullOrEmpty(riotClientExe) && File.Exists(riotClientExe)
                ? $"--launch-product={prod.Key} --launch-patchline=live"
                : null;

            BitmapImage? cover;
            lock (_coverCache)
            {
                if (!_coverCache.TryGetValue(id, out cover) || cover == null)
                {
                    cover = TryLoadFolderSplashOrIcon(id, installDir, exePath);
                    if (cover != null) _coverCache[id] = cover;
                }
            }

            result.Add(new GameEntry(
                id,
                prod.DefaultName,
                "Riot",
                launchUri,
                launchArgs,
                lastTimeUtc,
                hasPlayed,
                cover,
                exePath));
        }

        return result;
    }

    (DateTime TimeUtc, bool HasPlayed) GetRiotLastPlayedUtc(string id, string productKey, string installDir, string exePath)
    {
        DateTime best = DateTime.MinValue;
        bool hasPlayed = false;

        var hist = GetHistoryTimeUtc(id);
        if (hist.HasValue)
        {
            best = hist.Value;
            hasPlayed = true;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (productKey.Equals("valorant", StringComparison.OrdinalIgnoreCase))
        {
            // VALORANT actualiza ShooterGame.log en cada sesión de juego
            string valLogsDir = Path.Combine(localAppData, "VALORANT", "Saved", "Logs");
            CheckNewestFileInDir(valLogsDir, "*.log", ref best, ref hasPlayed);
        }
        else if (productKey.Equals("league_of_legends", StringComparison.OrdinalIgnoreCase))
        {
            // League of Legends escribe logs en <InstallDir>\Logs\LeagueClient Logs y GameLogs
            CheckNewestFileInDir(Path.Combine(installDir, "Logs", "LeagueClient Logs"), "*.log", ref best, ref hasPlayed);
            CheckNewestFileInDir(Path.Combine(installDir, "Logs", "GameLogs"), "*.log", ref best, ref hasPlayed);
            string cfgYaml = Path.Combine(installDir, "Config", "LeagueClientSettings.yaml");
            if (File.Exists(cfgYaml))
            {
                var wt = File.GetLastWriteTimeUtc(cfgYaml);
                if (wt > best) { best = wt; hasPlayed = true; }
            }
        }

        if (!hasPlayed)
        {
            try
            {
                best = File.GetLastWriteTimeUtc(exePath);
            }
            catch { best = DateTime.UtcNow.AddDays(-30); }
        }

        return (best, hasPlayed);
    }

    static void CheckNewestFileInDir(string dir, string pattern, ref DateTime best, ref bool hasPlayed)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
            {
                var wt = File.GetLastWriteTimeUtc(f);
                if (wt > best)
                {
                    best = wt;
                    hasPlayed = true;
                }
            }
        }
        catch { }
    }

    static string? GetRiotClientExecutablePath()
    {
        string installsJson = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Riot Games", "RiotClientInstalls.json");

        if (File.Exists(installsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(installsJson));
                var root = doc.RootElement;
                foreach (var prop in new[] { "rc_default", "rc_live", "rc_beta" })
                {
                    if (root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String)
                    {
                        string? p = el.GetString()?.Replace('/', '\\');
                        if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                    }
                }
            }
            catch { }
        }

        string fallback = @"C:\Riot Games\Riot Client\RiotClientServices.exe";
        return File.Exists(fallback) ? fallback : null;
    }

    // ==================== CARÁTULAS E ICONOS ====================
    BitmapImage? TryLoadFolderSplashOrIcon(string id, string installDir, string exePath)
    {
        string safeKey = id.Replace(':', '_');
        string cachedJpg = Path.Combine(DiskCoverCacheDir, $"{safeKey}.jpg");
        var fromCache = TryDecodeBitmap(cachedJpg);
        if (fromCache != null) return fromCache;

        // 1. Buscar imágenes Splash (Unreal Engine / EasyAntiCheat / Riot suelen incluir SplashScreen.png o Splash.bmp)
        try
        {
            var splashFiles = Directory.EnumerateFiles(installDir, "*Splash*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .ToList();

            string? bestSplash = splashFiles.FirstOrDefault(f => f.Contains("SplashScreen", StringComparison.OrdinalIgnoreCase))
                              ?? splashFiles.FirstOrDefault();
            if (bestSplash != null)
            {
                var bmp = TryDecodeBitmap(bestSplash);
                if (bmp != null) return bmp;
            }
        }
        catch { }

        // 2. Extraer el icono nativo del ejecutable del juego (.exe) como respaldo inmediato
        if (File.Exists(exePath))
        {
            try
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (ico != null)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(
                        ico.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(96, 96));
                    src.Freeze();

                    using var ms = new MemoryStream();
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(src));
                    enc.Save(ms);
                    ms.Position = 0;

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch { }
        }

        return null;
    }

    BitmapImage? TryLoadSteamLocalCover(string steamRoot, string appId)
    {
        string libCache = Path.Combine(steamRoot, "appcache", "librarycache");
        if (Directory.Exists(libCache))
        {
            string[] flatCandidates =
            {
                Path.Combine(libCache, $"{appId}_header.jpg"),
                Path.Combine(libCache, $"{appId}_library_600x900.jpg"),
                Path.Combine(libCache, $"{appId}_icon.jpg")
            };
            foreach (var c in flatCandidates)
            {
                var img = TryDecodeBitmap(c);
                if (img != null) return img;
            }

            string appSubDir = Path.Combine(libCache, appId);
            if (Directory.Exists(appSubDir))
            {
                try
                {
                    var files = Directory.GetFiles(appSubDir, "*.jpg", SearchOption.AllDirectories);
                    string? best = files.FirstOrDefault(f => f.EndsWith("header.jpg", StringComparison.OrdinalIgnoreCase))
                                ?? files.FirstOrDefault(f => f.Contains("header", StringComparison.OrdinalIgnoreCase))
                                ?? files.FirstOrDefault(f => f.Contains("600x900", StringComparison.OrdinalIgnoreCase))
                                ?? files.FirstOrDefault(f => f.Contains("capsule", StringComparison.OrdinalIgnoreCase))
                                ?? files.FirstOrDefault();
                    if (best != null)
                    {
                        var img = TryDecodeBitmap(best);
                        if (img != null) return img;
                    }
                }
                catch { }
            }
        }

        string diskCached = Path.Combine(DiskCoverCacheDir, $"{appId}.jpg");
        return TryDecodeBitmap(diskCached);
    }

    async Task<BitmapImage?> TryFetchRemoteCoverAsync(GameEntry g)
    {
        try
        {
            Directory.CreateDirectory(DiskCoverCacheDir);
            string safeKey = g.Id.Replace(':', '_');
            string dest = Path.Combine(DiskCoverCacheDir, $"{safeKey}.jpg");
            if (!File.Exists(dest))
            {
                string? url = g.Platform == "Steam"
                    ? $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{g.Id}/header.jpg"
                    : null;
                if (string.IsNullOrEmpty(url)) return null;

                byte[] bytes = await s_http.GetByteArrayAsync(url);
                if (bytes.Length > 512)
                    await File.WriteAllBytesAsync(dest, bytes);
            }
            return TryDecodeBitmap(dest);
        }
        catch
        {
            return null;
        }
    }

    static BitmapImage? TryDecodeBitmap(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < 128) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 220;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // ==================== AUXILIARES STEAM / VDF ====================
    static bool IsIgnoredGameName(string name) =>
        name.StartsWith("Steamworks ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase);

    static Dictionary<string, long> ReadLastPlayedFromLocalConfigs(string steamRoot, IEnumerable<string> installedAppIds)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        string userdataDir = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdataDir)) return map;

        List<string> configFiles;
        try
        {
            configFiles = Directory.EnumerateDirectories(userdataDir)
                .Select(d => Path.Combine(d, "config", "localconfig.vdf"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch { return map; }

        foreach (var cfg in configFiles)
        {
            string content;
            try
            {
                using var fs = new FileStream(cfg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                content = sr.ReadToEnd();
            }
            catch { continue; }

            foreach (var appId in installedAppIds)
            {
                long ts = FindMaxLastPlayedForApp(content, appId);
                if (ts > 0 && (!map.TryGetValue(appId, out long prev) || ts > prev))
                    map[appId] = ts;
            }
        }

        return map;
    }

    static long FindMaxLastPlayedForApp(string vdf, string appId)
    {
        string token = $"\"{appId}\"";
        int searchFrom = 0;
        long best = 0;

        while (searchFrom < vdf.Length)
        {
            int idx = vdf.IndexOf(token, searchFrom, StringComparison.Ordinal);
            if (idx < 0) break;
            searchFrom = idx + token.Length;

            int p = searchFrom;
            while (p < vdf.Length && char.IsWhiteSpace(vdf[p])) p++;
            if (p >= vdf.Length || vdf[p] != '{') continue;

            int blockStart = p;
            int depth = 0;
            int blockEnd = -1;
            bool inQuotes = false;

            for (int i = blockStart; i < vdf.Length; i++)
            {
                char c = vdf[i];
                if (c == '"' && (i == 0 || vdf[i - 1] != '\\'))
                {
                    inQuotes = !inQuotes;
                }
                else if (!inQuotes)
                {
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            blockEnd = i;
                            break;
                        }
                    }
                }
            }

            if (blockEnd > blockStart)
            {
                string slice = vdf.Substring(blockStart, blockEnd - blockStart + 1);
                var m = s_lastPlayedRegex.Match(slice);
                if (m.Success && long.TryParse(m.Groups[1].Value, out long ts) && ts > best)
                    best = ts;
                searchFrom = blockEnd + 1;
            }
        }

        return best;
    }

    static List<string> GetSteamAppsDirectories(string steamRoot)
    {
        var result = new List<string>();
        string mainSteamApps = Path.Combine(steamRoot, "steamapps");
        if (Directory.Exists(mainSteamApps))
            result.Add(mainSteamApps);

        string vdf = Path.Combine(mainSteamApps, "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                string text = File.ReadAllText(vdf);
                foreach (Match m in s_vdfPathRegex.Matches(text))
                {
                    string libPath = m.Groups[1].Value.Replace("\\\\", "\\");
                    string sa = Path.Combine(libPath, "steamapps");
                    if (Directory.Exists(sa) && !result.Contains(sa, StringComparer.OrdinalIgnoreCase))
                        result.Add(sa);
                }
            }
            catch { }
        }
        return result;
    }

    static string? GetSteamRootPath()
    {
        string? steamPath = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamPath = key?.GetValue("SteamPath") as string;
        }
        catch { }

        if (string.IsNullOrEmpty(steamPath))
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                steamPath = key?.GetValue("InstallPath") as string;
            }
            catch { }
        }

        return steamPath?.Replace('/', '\\');
    }

    static string ExtractVdfValue(string content, string key)
    {
        string pattern = $"\"{key}\"";
        int idx = content.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        int firstQuote = content.IndexOf('"', idx + pattern.Length);
        if (firstQuote < 0) return "";
        int secondQuote = content.IndexOf('"', firstQuote + 1);
        if (secondQuote < 0) return "";
        return content.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
    }

    static string GetJsonString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    public static string FormatRelativeTime(DateTime utcTime, bool hasRealLastPlayed, string platform)
    {
        if (!hasRealLastPlayed) return $"Instalado en {platform}";
        var diff = DateTime.UtcNow - utcTime;
        if (diff.TotalMinutes < 2) return $"Jugado ahora · {platform}";
        if (diff.TotalMinutes < 60) return $"Hace {(int)diff.TotalMinutes} min · {platform}";
        if (diff.TotalHours < 24) return $"Hace {(int)diff.TotalHours} h · {platform}";
        if (diff.TotalDays < 2) return $"Ayer · {platform}";
        if (diff.TotalDays < 30) return $"Hace {(int)diff.TotalDays} días · {platform}";
        return utcTime.ToLocalTime().ToString("d MMM") + $" · {platform}";
    }
}
