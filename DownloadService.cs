using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace IslaDinamica;

public enum DownloadStatus { Active, Paused, Completed, Cancelled }

/// <param name="ActiveCount">Descargas que siguen en curso o en pausa después de este evento.</param>
/// <param name="TotalBytes">Tamaño total si se conoce (ej. Steam), o 0 si es desconocido.</param>
/// <param name="SpeedBytesPerSec">Velocidad en tiempo real en bytes/segundo.</param>
/// <param name="SourceApp">Fuente opcional (ej. "Steam") o null para descargas generales.</param>
/// <param name="AppId">ID de la app en Steam si aplica.</param>
public record DownloadUpdate(
    DownloadStatus Status,
    string Name,
    long Bytes,
    int ActiveCount,
    string? FinalPath,
    long TotalBytes = 0,
    long SpeedBytesPerSec = 0,
    string? SourceApp = null,
    string? AppId = null);

/// <summary>
/// Vigila la carpeta Descargas y las bibliotecas de Steam en tiempo real, permitiendo
/// pausar y reanudar descargas directamente desde la isla.
/// </summary>
public class DownloadService : IDisposable
{
    static readonly string[] TempExt = { ".crdownload", ".part", ".opdownload", ".download", ".tmp" };
    static readonly Regex s_vdfPathRegex = new("\"path\"\\s+\"([^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex s_steamLogRegex = new(@"AppID\s+(\d+)\s+(?:state changed\s*:\s*(.+)|App update changed\s*:\s*(.+))", RegexOptions.Compiled);

    readonly object _lock = new();
    readonly Dictionary<string, long> _active = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _recentCompleted = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SteamLiveTracker> _steamTrackers = new(StringComparer.OrdinalIgnoreCase);

    FileSystemWatcher? _watcher;
    System.Threading.Timer? _timer;
    string _lastName = "";
    long _lastBrowserBytes;
    long _lastNetBytes;
    long _smoothedSpeed;
    bool _browserPaused;
    DateTime _lastPollTime = DateTime.UtcNow;

    // Caché para evitar lecturas innecesarias de disco/registro en reposo
    string? _cachedSteamRoot;
    List<string>? _cachedSteamDirs;
    DateTime _lastSteamDirCacheUtc = DateTime.MinValue;
    long _lastSteamLogLength = -1;
    HashSet<string> _cachedSuspendedApps = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> _cachedUninstalledApps = new(StringComparer.OrdinalIgnoreCase);
    int _idleTickCounter;

    class SteamLiveTracker
    {
        public string AppId = "";
        public string Name = "";
        public long AcfBaselineBytes;
        public long EstimatedBytes;
        public long TotalBytes;
        public string InstallDir = "";
        public bool IsPaused;
        public DateTime? ManualOverrideUntilUtc;
    }

    public event Action<DownloadUpdate>? Changed;

    public void Start()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(folder))
        {
            try
            {
                foreach (var f in Directory.GetFiles(folder))
                {
                    if (IsTemp(f))
                    {
                        _active[f] = GetRealFileSize(f);
                        _lastName = CleanName(f);
                    }
                }
            }
            catch { }

            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
            };
            _watcher.Created += OnCreated;
            _watcher.Renamed += OnRenamed;
            _watcher.Deleted += OnDeleted;
            _watcher.EnableRaisingEvents = true;
        }

        _lastNetBytes = GetTotalNetworkBytesReceived();
        _lastPollTime = DateTime.UtcNow;
        _timer = new System.Threading.Timer(_ => Poll(), null, 300, 500);
    }

    /// <summary>
    /// Pausa o continúa la descarga actual (Steam o navegador).
    /// </summary>
    public void TogglePauseResume()
    {
        SteamLiveTracker? steamTarget = null;
        bool hasBrowser = false;

        lock (_lock)
        {
            hasBrowser = _active.Count > 0;
            steamTarget = _steamTrackers.Values.FirstOrDefault(t => !t.IsPaused)
                          ?? _steamTrackers.Values.FirstOrDefault();
        }

        if (steamTarget != null && !hasBrowser)
        {
            bool newPausedState = !steamTarget.IsPaused;
            lock (_lock)
            {
                steamTarget.IsPaused = newPausedState;
                steamTarget.ManualOverrideUntilUtc = DateTime.UtcNow.AddSeconds(4);
            }

            EnsureSteamDirectoriesCached();
            if (!string.IsNullOrEmpty(_cachedSteamRoot))
            {
                string steamExe = Path.Combine(_cachedSteamRoot, "steam.exe");
                if (File.Exists(steamExe))
                {
                    // app_download_enable 0/1 pausa/reanuda el planificador de descargas de Steam sin abrir asistentes;
                    // si estaba suspendida individualmente, app_install <AppId> la reactiva en la cola.
                    string args = newPausedState
                        ? $"+app_download_enable 0 +app_update_cancel {steamTarget.AppId}"
                        : $"+app_download_enable 1 +app_install {steamTarget.AppId}";

                    try
                    {
                        Process.Start(new ProcessStartInfo(steamExe, args)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
                    catch { }
                }
            }

            Poll();
            return;
        }

        if (hasBrowser)
        {
            _browserPaused = !_browserPaused;
            ToggleBrowserDownloadViaProcesses(_browserPaused);
            Poll();
        }
    }

    /// <summary>
    /// Para descargas de navegador, busca si hay una ventana de navegador activa y envía el atajo
    /// o suspende/reanuda los sockets de transferencia sin congelar la interfaz.
    /// </summary>
    void ToggleBrowserDownloadViaProcesses(bool pause)
    {
        try
        {
            foreach (var procName in new[] { "chrome", "msedge", "brave", "opera" })
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    try
                    {
                        // Solo el subproceso de red tiene pocos hilos (~10-18) y sin ventana principal
                        if (p.MainWindowHandle != IntPtr.Zero) continue;
                        if (IsChromiumNetworkUtilityProcess(p.Handle))
                        {
                            if (pause) NtSuspendProcess(p.Handle);
                            else NtResumeProcess(p.Handle);
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
        }
        catch { }
    }

    static bool IsChromiumNetworkUtilityProcess(IntPtr processHandle)
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            int status = NtQueryInformationProcess(processHandle, 60, IntPtr.Zero, 0, out int size);
            if (size <= 0 || size > 32768) return false;
            buf = Marshal.AllocHGlobal(size);
            if (NtQueryInformationProcess(processHandle, 60, buf, size, out _) != 0)
                return false;

            var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
            if (us.Buffer == IntPtr.Zero || us.Length == 0) return false;
            string cmd = Marshal.PtrToStringUni(us.Buffer, us.Length / 2) ?? "";
            return cmd.Contains("network.mojom.NetworkService", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, IntPtr processInformation, int processInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    static extern int NtResumeProcess(IntPtr processHandle);

    static long GetRealFileSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return fs.Length;
        }
        catch
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }
    }

    static long GetTotalNetworkBytesReceived()
    {
        long total = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                total += ni.GetIPStatistics().BytesReceived;
            }
        }
        catch { }
        return total;
    }

    static bool IsTemp(string path) =>
        TempExt.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    static bool IsIgnoredFile(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ||
               name.StartsWith("~") ||
               name.StartsWith(".") ||
               name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    static string CleanName(string path)
    {
        var n = Path.GetFileName(path);
        return IsTemp(path) ? Path.GetFileNameWithoutExtension(n) : n;
    }

    void OnCreated(object s, FileSystemEventArgs e)
    {
        if (IsIgnoredFile(e.FullPath)) return;

        if (IsTemp(e.FullPath))
        {
            Begin(e.FullPath);
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(450);
            if (!File.Exists(e.FullPath) || IsTemp(e.FullPath)) return;
            NotifyCompleted(e.FullPath);
        });
    }

    void Begin(string path)
    {
        lock (_lock)
        {
            _active[path] = 0;
            _lastName = CleanName(path);
            _browserPaused = false;
        }
        Poll();
    }

    void OnRenamed(object s, RenamedEventArgs e)
    {
        if (IsIgnoredFile(e.FullPath)) return;

        bool oldTemp = IsTemp(e.OldFullPath), newTemp = IsTemp(e.FullPath);

        if (!oldTemp && newTemp) { Begin(e.FullPath); return; }

        if (oldTemp && newTemp)
        {
            lock (_lock)
            {
                _active.Remove(e.OldFullPath);
                _active[e.FullPath] = 0;
                _lastName = CleanName(e.FullPath);
            }
            Poll();
            return;
        }

        if (oldTemp && !newTemp)
        {
            lock (_lock) _active.Remove(e.OldFullPath);
            NotifyCompleted(e.FullPath);
        }
    }

    void NotifyCompleted(string finalPath)
    {
        int left;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_recentCompleted.TryGetValue(finalPath, out var prev) && (now - prev).TotalSeconds < 2.5)
                return;
            _recentCompleted[finalPath] = now;
            left = _active.Count + _steamTrackers.Count;
        }

        long size = GetRealFileSize(finalPath);
        if (size == 0)
        {
            Thread.Sleep(150);
            size = GetRealFileSize(finalPath);
        }

        Changed?.Invoke(new DownloadUpdate(
            DownloadStatus.Completed,
            Path.GetFileName(finalPath),
            size,
            left,
            finalPath));
    }

    void OnDeleted(object s, FileSystemEventArgs e)
    {
        if (!IsTemp(e.FullPath)) return;
        int left;
        lock (_lock)
        {
            if (!_active.Remove(e.FullPath)) return;
            left = _active.Count + _steamTrackers.Count;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            lock (_lock)
            {
                foreach (var dt in _recentCompleted.Values)
                    if ((DateTime.UtcNow - dt).TotalMilliseconds < 600)
                        return;
            }
            Changed?.Invoke(new DownloadUpdate(DownloadStatus.Cancelled, CleanName(e.FullPath), 0, left, null));
        });
    }

    void Poll()
    {
        bool hasActive;
        lock (_lock)
        {
            hasActive = _active.Count > 0 || _steamTrackers.Count > 0;
        }

        if (!hasActive)
        {
            _idleTickCounter++;
            if (_idleTickCounter % 4 != 0) return;
        }
        else
        {
            _idleTickCounter = 0;
        }

        var now = DateTime.UtcNow;
        double elapsedSec = Math.Max(0.15, (now - _lastPollTime).TotalSeconds);
        _lastPollTime = now;

        long currentNetBytes = GetTotalNetworkBytesReceived();
        long netDelta = _lastNetBytes > 0 && currentNetBytes >= _lastNetBytes
            ? currentNetBytes - _lastNetBytes
            : 0;
        _lastNetBytes = currentNetBytes;

        PollSteam(netDelta);

        string[] keys;
        string name;
        int steamTotalCount;
        SteamLiveTracker? currentSteam = null;

        lock (_lock)
        {
            keys = _active.Count > 0 ? _active.Keys.ToArray() : Array.Empty<string>();
            name = _lastName;
            steamTotalCount = _steamTrackers.Count;
            if (steamTotalCount > 0)
            {
                currentSteam = _steamTrackers.Values.FirstOrDefault(t => !t.IsPaused)
                               ?? _steamTrackers.Values.First();
            }
        }

        if (keys.Length > 0)
        {
            long total = 0;
            int validKeys = 0;
            foreach (var k in keys)
            {
                if (!File.Exists(k))
                {
                    lock (_lock) _active.Remove(k);
                    continue;
                }
                validKeys++;
                total += GetRealFileSize(k);
            }

            if (validKeys > 0)
            {
                long fileDelta = _lastBrowserBytes > 0 && total >= _lastBrowserBytes ? total - _lastBrowserBytes : 0;
                if (fileDelta > 0) _browserPaused = false;
                long effectiveDelta = _browserPaused ? 0 : (fileDelta > 0 ? fileDelta : netDelta);
                long instantSpeed = (long)(effectiveDelta / elapsedSec);
                _smoothedSpeed = _browserPaused ? 0 : (_smoothedSpeed == 0 ? instantSpeed : (long)(_smoothedSpeed * 0.55 + instantSpeed * 0.45));
                _lastBrowserBytes = total;

                Changed?.Invoke(new DownloadUpdate(
                    _browserPaused ? DownloadStatus.Paused : DownloadStatus.Active,
                    name,
                    total,
                    validKeys + steamTotalCount,
                    null,
                    TotalBytes: 0,
                    SpeedBytesPerSec: _smoothedSpeed));
                return;
            }
        }

        _lastBrowserBytes = 0;

        if (currentSteam != null)
        {
            if (currentSteam.IsPaused)
            {
                _smoothedSpeed = 0;
                Changed?.Invoke(new DownloadUpdate(
                    DownloadStatus.Paused,
                    currentSteam.Name,
                    currentSteam.EstimatedBytes,
                    steamTotalCount,
                    currentSteam.InstallDir,
                    TotalBytes: currentSteam.TotalBytes,
                    SpeedBytesPerSec: 0,
                    SourceApp: "Steam",
                    AppId: currentSteam.AppId));
            }
            else
            {
                long instantSpeed = (long)(netDelta / elapsedSec);
                _smoothedSpeed = _smoothedSpeed == 0 ? instantSpeed : (long)(_smoothedSpeed * 0.6 + instantSpeed * 0.4);

                Changed?.Invoke(new DownloadUpdate(
                    DownloadStatus.Active,
                    currentSteam.Name,
                    currentSteam.EstimatedBytes,
                    steamTotalCount,
                    currentSteam.InstallDir,
                    TotalBytes: currentSteam.TotalBytes,
                    SpeedBytesPerSec: _smoothedSpeed,
                    SourceApp: "Steam",
                    AppId: currentSteam.AppId));
            }
        }
        else
        {
            _smoothedSpeed = 0;
        }
    }

    // ---------- Soporte en tiempo real para Steam ----------
    void PollSteam(long netDeltaBytes)
    {
        try
        {
            EnsureSteamDirectoriesCached();
            if (_cachedSteamRoot == null || _cachedSteamDirs == null || _cachedSteamDirs.Count == 0) return;

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<SteamLiveTracker>? completedList = null;
            List<SteamLiveTracker>? cancelledList = null;

            foreach (var dir in _cachedSteamDirs)
            {
                string[] manifests;
                try { manifests = Directory.GetFiles(dir, "appmanifest_*.acf"); }
                catch { continue; }

                foreach (var mf in manifests)
                {
                    string content;
                    try
                    {
                        using var fs = new FileStream(mf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var sr = new StreamReader(fs, Encoding.UTF8);
                        content = sr.ReadToEnd();
                    }
                    catch { continue; }

                    long.TryParse(ExtractVdfValue(content, "StateFlags"), out long flags);
                    long.TryParse(ExtractVdfValue(content, "BytesToDownload"), out long toDownload);

                    // Bit 1024 = StateUpdateRunning, Bit 2 = StateUpdateRequired con descarga iniciada
                    bool hasActiveDownload = ((flags & 1024) != 0 || (flags & 512) != 0) && toDownload > 0;
                    if (!hasActiveDownload) continue;

                    string appId = ExtractVdfValue(content, "appid");
                    string gameName = ExtractVdfValue(content, "name");
                    if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(gameName) || appId == "228980") continue;

                    var (pausedInLog, uninstalledInLog) = GetAppStatesFromSteamLog(_cachedSteamRoot);
                    if (uninstalledInLog.Contains(appId)) continue;

                    long.TryParse(ExtractVdfValue(content, "BytesDownloaded"), out long downloaded);

                    seenIds.Add(appId);
                    bool isSuspendedInLog = (flags & 512) != 0 || pausedInLog.Contains(appId);
                    string installDirName = ExtractVdfValue(content, "installdir");
                    string fullInstallDir = Path.Combine(dir, "common", installDirName);

                    lock (_lock)
                    {
                        if (!_steamTrackers.TryGetValue(appId, out var tracker))
                        {
                            tracker = new SteamLiveTracker
                            {
                                AppId = appId,
                                Name = gameName,
                                AcfBaselineBytes = downloaded,
                                EstimatedBytes = downloaded,
                                TotalBytes = toDownload,
                                InstallDir = Directory.Exists(fullInstallDir) ? fullInstallDir : dir,
                                IsPaused = isSuspendedInLog
                            };
                            _steamTrackers[appId] = tracker;
                        }
                        else
                        {
                            tracker.Name = gameName;
                            tracker.TotalBytes = toDownload;

                            // Si el usuario acaba de pulsar Pausar/Continuar desde la isla, respetamos su acción unos segundos
                            // mientras Steam termina de escribir el cambio en content_log.txt
                            if (tracker.ManualOverrideUntilUtc == null || DateTime.UtcNow > tracker.ManualOverrideUntilUtc)
                            {
                                tracker.IsPaused = isSuspendedInLog;
                                tracker.ManualOverrideUntilUtc = null;
                            }

                            if (downloaded != tracker.AcfBaselineBytes)
                            {
                                tracker.AcfBaselineBytes = downloaded;
                                if (downloaded > tracker.EstimatedBytes)
                                    tracker.EstimatedBytes = downloaded;
                            }
                        }

                        if (!tracker.IsPaused && netDeltaBytes > 0)
                        {
                            tracker.EstimatedBytes = Math.Min(tracker.TotalBytes, tracker.EstimatedBytes + netDeltaBytes);
                        }
                    }
                }
            }

            lock (_lock)
            {
                if (_steamTrackers.Count > 0)
                {
                    List<string>? toRemove = null;
                    foreach (var k in _steamTrackers.Keys)
                    {
                        if (!seenIds.Contains(k))
                        {
                            toRemove ??= new();
                            toRemove.Add(k);
                        }
                    }

                    if (toRemove != null)
                    {
                        foreach (var appId in toRemove)
                        {
                            var done = _steamTrackers[appId];
                            _steamTrackers.Remove(appId);
                            if (!done.IsPaused && done.TotalBytes > 0 && done.EstimatedBytes >= done.TotalBytes * 0.95)
                            {
                                completedList ??= new();
                                completedList.Add(done);
                            }
                            else
                            {
                                cancelledList ??= new();
                                cancelledList.Add(done);
                            }
                        }
                    }
                }
            }

            if (completedList != null)
            {
                foreach (var done in completedList)
                {
                    int left;
                    lock (_lock) left = _active.Count + _steamTrackers.Count;
                    Changed?.Invoke(new DownloadUpdate(
                        DownloadStatus.Completed,
                        done.Name,
                        done.TotalBytes,
                        left,
                        done.InstallDir,
                        TotalBytes: done.TotalBytes,
                        SourceApp: "Steam",
                        AppId: done.AppId));
                }
            }

            if (cancelledList != null)
            {
                foreach (var canc in cancelledList)
                {
                    int left;
                    lock (_lock) left = _active.Count + _steamTrackers.Count;
                    Changed?.Invoke(new DownloadUpdate(
                        DownloadStatus.Cancelled,
                        canc.Name,
                        0,
                        left,
                        null,
                        SourceApp: "Steam",
                        AppId: canc.AppId));
                }
            }
        }
        catch { }
    }

    (HashSet<string> Suspended, HashSet<string> Uninstalled) GetAppStatesFromSteamLog(string steamRoot)
    {
        var logPath = Path.Combine(steamRoot, "logs", "content_log.txt");
        if (!File.Exists(logPath)) return (_cachedSuspendedApps, _cachedUninstalledApps);

        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length == _lastSteamLogLength)
                return (_cachedSuspendedApps, _cachedUninstalledApps);

            _lastSteamLogLength = fs.Length;
            long startPos = Math.Max(0, fs.Length - 16384);
            fs.Seek(startPos, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string tail = sr.ReadToEnd();

            var suspended = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uninstalled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in tail.Split('\n'))
            {
                var m = s_steamLogRegex.Match(line);
                if (!m.Success) continue;

                string appId = m.Groups[1].Value;
                string status = (m.Groups[2].Value + " " + m.Groups[3].Value).Trim();

                if (status.Contains("Uninstalling", StringComparison.OrdinalIgnoreCase) ||
                    status.Contains("Uninstalled", StringComparison.OrdinalIgnoreCase))
                {
                    uninstalled.Add(appId);
                    suspended.Remove(appId);
                }
                else if (status.Contains("Suspended", StringComparison.OrdinalIgnoreCase) ||
                         status.Contains("Stopping", StringComparison.OrdinalIgnoreCase) ||
                         status.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    suspended.Add(appId);
                }
                else if (status.Contains("Downloading", StringComparison.OrdinalIgnoreCase) ||
                         status.Contains("Update Running", StringComparison.OrdinalIgnoreCase))
                {
                    suspended.Remove(appId);
                    uninstalled.Remove(appId);
                }
            }
            _cachedSuspendedApps = suspended;
            _cachedUninstalledApps = uninstalled;
        }
        catch { }

        return (_cachedSuspendedApps, _cachedUninstalledApps);
    }

    void EnsureSteamDirectoriesCached()
    {
        var now = DateTime.UtcNow;
        if (_cachedSteamDirs != null && (now - _lastSteamDirCacheUtc).TotalSeconds < 60)
            return;

        _lastSteamDirCacheUtc = now;
        _cachedSteamRoot ??= GetSteamRootPath();
        if (string.IsNullOrEmpty(_cachedSteamRoot)) return;

        var result = new List<string>();
        var mainSteamApps = Path.Combine(_cachedSteamRoot, "steamapps");
        if (Directory.Exists(mainSteamApps))
            result.Add(mainSteamApps);

        var vdf = Path.Combine(mainSteamApps, "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                var text = File.ReadAllText(vdf);
                foreach (Match m in s_vdfPathRegex.Matches(text))
                {
                    var libPath = m.Groups[1].Value.Replace("\\\\", "\\");
                    var sa = Path.Combine(libPath, "steamapps");
                    if (Directory.Exists(sa) && !result.Contains(sa, StringComparer.OrdinalIgnoreCase))
                        result.Add(sa);
                }
            }
            catch { }
        }

        _cachedSteamDirs = result;
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

    public static string FormatBytes(long b)
    {
        if (b < 1024 * 1024) return $"{b / 1024.0:0} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / 1024.0 / 1024.0:0.0} MB";
        return $"{b / 1024.0 / 1024.0 / 1024.0:0.00} GB";
    }

    public void Dispose()
    {
        if (_browserPaused) ToggleBrowserDownloadViaProcesses(false);
        _timer?.Dispose();
        _watcher?.Dispose();
    }
}
