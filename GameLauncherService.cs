using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace IslaDinamica;

public record GameEntry(
    string Id,
    string Name,
    string LaunchUri,
    DateTime LastPlayedUtc,
    bool HasRealLastPlayed,
    BitmapImage? Cover);

/// <summary>
/// Detecta automáticamente los últimos juegos jugados en Steam (leyendo appmanifest_*.acf y
/// LastPlayed en localconfig.vdf) y carga sus carátulas desde la caché local de Steam
/// (appcache\librarycache) con respaldo en CDN local.
/// </summary>
public sealed class GameLauncherService
{
    static readonly HashSet<string> s_ignoredAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "228980", // Steamworks Common Redistributables
        "250820", // SteamVR
    };

    static readonly Regex s_vdfPathRegex = new(@"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex s_lastPlayedRegex = new(@"""LastPlayed""\s+""(\d+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(5) };

    readonly Dictionary<string, BitmapImage?> _coverCache = new(StringComparer.OrdinalIgnoreCase);
    readonly string _diskCoverCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IslaDinamica", "covers");

    List<GameEntry> _cachedGames = new();
    DateTime _lastScanUtc = DateTime.MinValue;

    public event Action<IReadOnlyList<GameEntry>>? Changed;

    public IReadOnlyList<GameEntry> GetRecentGames(int maxCount = 4, bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedGames.Count > 0 && (DateTime.UtcNow - _lastScanUtc).TotalSeconds < 20)
            return _cachedGames;

        _lastScanUtc = DateTime.UtcNow;
        var list = ScanSteamGames(maxCount);
        _cachedGames = list;
        return list;
    }

    public Task RefreshAsync(int maxCount = 4) => Task.Run(async () =>
    {
        var games = ScanSteamGames(maxCount);

        // Si a algún juego le falta carátula en la caché local de Steam, intenta descargarla una vez
        bool updatedCovers = false;
        for (int i = 0; i < games.Count; i++)
        {
            var g = games[i];
            if (g.Cover != null) continue;
            var downloaded = await TryFetchSteamCoverAsync(g.Id);
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

    public void Launch(string launchUri)
    {
        if (string.IsNullOrWhiteSpace(launchUri)) return;
        try
        {
            Process.Start(new ProcessStartInfo(launchUri) { UseShellExecute = true });
        }
        catch { }
    }

    List<GameEntry> ScanSteamGames(int maxCount)
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
                    if (s_ignoredAppIds.Contains(appId) || IsIgnoredGameName(name)) continue;

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

        var ordered = installed
            .Select(kv =>
            {
                string appId = kv.Key;
                bool hasPlayed = lastPlayedMap.TryGetValue(appId, out long unix) && unix > 0;
                DateTime timeUtc = hasPlayed
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                    : kv.Value.FallbackTimeUtc;
                return (AppId: appId, kv.Value.Name, TimeUtc: timeUtc, HasPlayed: hasPlayed);
            })
            .OrderByDescending(x => x.HasPlayed)
            .ThenByDescending(x => x.TimeUtc)
            .Take(maxCount)
            .ToList();

        foreach (var item in ordered)
        {
            BitmapImage? cover;
            lock (_coverCache)
            {
                if (!_coverCache.TryGetValue(item.AppId, out cover) || cover == null)
                {
                    cover = TryLoadLocalCover(steamRoot, item.AppId);
                    if (cover != null) _coverCache[item.AppId] = cover;
                }
            }

            result.Add(new GameEntry(
                item.AppId,
                item.Name,
                $"steam://rungameid/{item.AppId}",
                item.TimeUtc,
                item.HasPlayed,
                cover));
        }

        return result;
    }

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

    /// <summary>
    /// Busca todos los bloques "&lt;appId&gt;" { ... } en localconfig.vdf respetando llaves anidadas
    /// y devuelve el mayor valor de "LastPlayed".
    /// </summary>
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

            // Verificar que después de "<appId>" el siguiente carácter no-espacio sea '{'
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

    BitmapImage? TryLoadLocalCover(string steamRoot, string appId)
    {
        string libCache = Path.Combine(steamRoot, "appcache", "librarycache");
        if (Directory.Exists(libCache))
        {
            // 1. Formato plano en appcache\librarycache
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

            // 2. Subcarpeta appcache\librarycache\<appId>\ (versiones nuevas de Steam)
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

        // 3. Caché propia en %LocalAppData%\IslaDinamica\covers
        string diskCached = Path.Combine(_diskCoverCacheDir, $"{appId}.jpg");
        return TryDecodeBitmap(diskCached);
    }

    async Task<BitmapImage?> TryFetchSteamCoverAsync(string appId)
    {
        try
        {
            Directory.CreateDirectory(_diskCoverCacheDir);
            string dest = Path.Combine(_diskCoverCacheDir, $"{appId}.jpg");
            if (!File.Exists(dest))
            {
                string url = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";
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

    public static string FormatRelativeTime(DateTime utcTime, bool hasRealLastPlayed)
    {
        if (!hasRealLastPlayed) return "Instalado en Steam";
        var diff = DateTime.UtcNow - utcTime;
        if (diff.TotalMinutes < 2) return "Jugado hace un momento";
        if (diff.TotalMinutes < 60) return $"Jugado hace {(int)diff.TotalMinutes} min";
        if (diff.TotalHours < 24) return $"Jugado hace {(int)diff.TotalHours} h";
        if (diff.TotalDays < 2) return "Jugado ayer";
        if (diff.TotalDays < 30) return $"Jugado hace {(int)diff.TotalDays} días";
        return "Jugado el " + utcTime.ToLocalTime().ToString("d MMM");
    }
}
