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
    volatile bool _browserPaused;     // pausada por nosotros (proceso de red suspendido)
    volatile bool _browserStalled;    // sin crecimiento: pausada o detenida desde el propio navegador
    DateTime _lastBrowserGrowthUtc = DateTime.UtcNow;

    // Puente con la extensión del navegador (pausa real, nombre y total reales)
    readonly BrowserBridge _bridge = new();
    // Suspender el proceso de red congela TODO el internet del navegador y la descarga acaba en "conexión perdida".
    // Por eso está desactivado: la pausa de navegador se hace con la extensión.
    static readonly bool AllowNetworkSuspendFallback = false;
    DateTime _lastPollTime = DateTime.UtcNow;

    // PID de los procesos de red del navegador que suspendimos nosotros (se reanudan siempre)
    static readonly string[] BrowserProcessNames = { "chrome", "msedge", "brave", "opera" };
    static readonly string SuspendedStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IslaDinamica", "suspended.txt");
    readonly object _pauseLock = new();
    readonly HashSet<int> _suspendedPids = new();

    // Exclusión de Poll() y vigilancia de Steam (evita releer todos los .acf en reposo)
    readonly object _pollLock = new();
    readonly object _steamCacheLock = new();
    readonly Dictionary<string, FileSystemWatcher> _steamWatchers = new(StringComparer.OrdinalIgnoreCase);
    volatile bool _steamDirty = true;   // true = hay que hacer un primer escaneo
    DateTime _lastSteamScanUtc = DateTime.MinValue;

    // Caché para evitar lecturas innecesarias de disco/registro en reposo
    string? _cachedSteamRoot;
    List<string>? _cachedSteamDirs;
    DateTime _lastSteamDirCacheUtc = DateTime.MinValue;
    long _lastSteamLogLength = -1;
    HashSet<string> _cachedSuspendedApps = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> _cachedUninstalledApps = new(StringComparer.OrdinalIgnoreCase);

    class SteamLiveTracker
    {
        public string AppId = "";
        public string Name = "";
        public string SourceApp = "Steam";
        public long AcfBaselineBytes;
        public long EstimatedBytes;
        public long ExtraSinceAcf;   // bytes extrapolados por red desde la última lectura real del .acf
        public long TotalBytes;
        public string InstallDir = "";
        public bool IsPaused;
        public DateTime LastGrowthUtc = DateTime.UtcNow;
        public DateTime? ManualOverrideUntilUtc;
        public DateTime? MissingSinceUtc;   // desde cuándo no aparece como descarga activa en el manifiesto
    }

    public event Action<DownloadUpdate>? Changed;

    public void Start()
    {
        RecoverOrphanedSuspension();

        _bridge.Reported += Poll;
        _bridge.Finished += OnExtFinished;
        _bridge.Start();

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
    /// <summary>
    /// Cancela la descarga que se está mostrando. Navegador (con la extensión): cancelación real dentro
    /// del navegador. Steam: abre su diálogo de desinstalar (Steam pide su propia confirmación).
    /// </summary>
    public void CancelCurrent()
    {
        var ext = _bridge.GetActive();
        if (ext.Count > 0)
        {
            var primary = ext.FirstOrDefault(d => !d.Paused) ?? ext[0];   // la misma que muestra la isla
            _bridge.Enqueue("cancel", primary.Id);
            Log($"Cancelar (extensión): descarga {primary.Id}");
            return;
        }

        SteamLiveTracker? steam;
        bool hasBrowserFile;
        lock (_lock)
        {
            hasBrowserFile = _active.Count > 0;
            steam = _steamTrackers.Values.FirstOrDefault(t => !t.IsPaused) ?? _steamTrackers.Values.FirstOrDefault();
        }

        if (steam != null && !hasBrowserFile && steam.AppId.All(char.IsDigit))
        {
            try { Process.Start(new ProcessStartInfo($"steam://uninstall/{steam.AppId}") { UseShellExecute = true }); }
            catch { }
            return;
        }

        Log("Cancelar descarga de navegador sin extensión: no disponible (instala IslaExtension en Chrome/Edge).");
    }

    /// <summary>La extensión avisa de que una descarga terminó o se interrumpió (cancelada o fallida).</summary>
    void OnExtFinished(ExtFinished f)
    {
        if (f.State == "complete")
        {
            if (!string.IsNullOrEmpty(f.Path)) NotifyCompleted(f.Path);   // se desduplica con el aviso del vigilante
            return;
        }

        int left;
        lock (_lock) left = _steamTrackers.Count;
        left += _bridge.GetActive().Count(d => d.Id != f.Id);

        Changed?.Invoke(new DownloadUpdate(DownloadStatus.Cancelled, f.Name, 0, left, null));
    }

    public void TogglePauseResume()
    {
        // Navegador con la extensión conectada: pausa/reanuda de verdad dentro de Chrome/Edge
        var extDownloads = _bridge.GetActive();
        if (extDownloads.Count > 0)
        {
            bool anyRunning = extDownloads.Any(d => !d.Paused);
            foreach (var d in extDownloads)
                _bridge.Enqueue(anyRunning ? "pause" : "resume", d.Id);
            return;
        }

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
            // Si el usuario pausó la descarga en el propio Chrome/Edge, desde aquí no se puede reanudar
            if (_browserStalled && !_browserPaused) return;

            if (!AllowNetworkSuspendFallback)
            {
                Log("Pausa de navegador sin extensión: no disponible (instala IslaExtension en Chrome/Edge).");
                return;
            }

            SetBrowserPaused(!_browserPaused);
            Poll();
        }
    }

    const uint PROCESS_SUSPEND_RESUME = 0x0800;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    /// <summary>Registro de diagnóstico en %LocalAppData%\IslaDinamica\debug.log (solo al pausar/reanudar).</summary>
    internal static void Log(string msg)
    {
        try
        {
            var dir = Path.GetDirectoryName(SuspendedStatePath)!;
            Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, "debug.log");
            if (File.Exists(f) && new FileInfo(f).Length > 100_000) File.Delete(f);
            File.AppendAllText(f, $"{DateTime.Now:HH:mm:ss}  {msg}\n");
        }
        catch { }
    }

    /// <summary>
    /// Pausa o reanuda el proceso de red de los navegadores Chromium. Guarda los PID que suspendió
    /// para reanudar exactamente esos (NtSuspendProcess es un contador) y poder recuperarse si la isla
    /// se cierra a la fuerza. Abre cada proceso solo con los permisos mínimos (suspender + consultar),
    /// porque pedir acceso total puede ser denegado en procesos con sandbox.
    /// Si no encuentra ningún proceso que suspender (p. ej. Firefox), no se marca como pausado.
    /// </summary>
    void SetBrowserPaused(bool pause)
    {
        lock (_pauseLock)
        {
            if (pause)
            {
                int total = 0, noAccess = 0, noCmd = 0, network = 0, suspended = 0;
                foreach (var procName in BrowserProcessNames)
                {
                    Process[] procs;
                    try { procs = Process.GetProcessesByName(procName); }
                    catch { continue; }

                    foreach (var p in procs)
                    {
                        int pid = p.Id;
                        bool hasWindow = false;
                        try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { }
                        p.Dispose();

                        total++;
                        if (hasWindow || _suspendedPids.Contains(pid)) continue;

                        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                        if (h == IntPtr.Zero) { noAccess++; continue; }
                        try
                        {
                            var cmd = GetProcessCommandLine(h);
                            if (cmd == null) { noCmd++; continue; }
                            if (!cmd.Contains("NetworkService", StringComparison.OrdinalIgnoreCase)) continue;

                            network++;
                            int st = NtSuspendProcess(h);
                            if (st == 0) { _suspendedPids.Add(pid); suspended++; }
                            else Log($"NtSuspendProcess({pid}) devolvió 0x{st:X8}");
                        }
                        finally { CloseHandle(h); }
                    }
                }
                _browserPaused = _suspendedPids.Count > 0;
                Log($"Pausar navegador: procesos={total}, sin acceso={noAccess}, sin línea de comandos={noCmd}, " +
                    $"proceso de red encontrado={network}, suspendidos={suspended}");
            }
            else
            {
                foreach (var pid in _suspendedPids.ToArray())
                {
                    IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
                    if (h == IntPtr.Zero) continue;
                    try { NtResumeProcess(h); }
                    finally { CloseHandle(h); }
                }
                if (_suspendedPids.Count > 0) Log($"Reanudado: {_suspendedPids.Count} proceso(s)");
                _suspendedPids.Clear();
                _browserPaused = false;
                _lastBrowserGrowthUtc = DateTime.UtcNow;   // evita marcar "detenida" justo al reanudar
            }
            PersistSuspendedPids();
        }
    }

    bool AnySuspendedProcessAlive()
    {
        lock (_pauseLock)
        {
            foreach (var pid in _suspendedPids)
            {
                try { using var p = Process.GetProcessById(pid); if (!p.HasExited) return true; }
                catch { }
            }
            return false;
        }
    }

    void PersistSuspendedPids()
    {
        try
        {
            if (_suspendedPids.Count == 0)
            {
                if (File.Exists(SuspendedStatePath)) File.Delete(SuspendedStatePath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(SuspendedStatePath)!);
            File.WriteAllLines(SuspendedStatePath, _suspendedPids.Select(i => i.ToString()));
        }
        catch { }
    }

    /// <summary>Si la isla se cerró a la fuerza con el navegador en pausa, lo reanuda al arrancar.</summary>
    void RecoverOrphanedSuspension()
    {
        try
        {
            if (!File.Exists(SuspendedStatePath)) return;
            foreach (var line in File.ReadAllLines(SuspendedStatePath))
            {
                if (!int.TryParse(line, out int pid)) continue;
                IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    if (IsChromiumNetworkUtilityProcess(h))   // evita tocar un PID reutilizado
                    {
                        NtResumeProcess(h);
                        Log($"Recuperado al arrancar: proceso {pid} reanudado");
                    }
                }
                finally { CloseHandle(h); }
            }
            File.Delete(SuspendedStatePath);
        }
        catch { }
    }

    static bool IsChromiumNetworkUtilityProcess(IntPtr processHandle) =>
        GetProcessCommandLine(processHandle)?.Contains("NetworkService", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Línea de comandos del proceso (ProcessCommandLineInformation = 60), o null si no se puede leer.</summary>
    static string? GetProcessCommandLine(IntPtr processHandle)
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            NtQueryInformationProcess(processHandle, 60, IntPtr.Zero, 0, out int size);
            if (size <= 0 || size > 65536) return null;
            buf = Marshal.AllocHGlobal(size);
            if (NtQueryInformationProcess(processHandle, 60, buf, size, out _) != 0) return null;

            var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
            if (us.Buffer == IntPtr.Zero || us.Length == 0) return null;
            return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
        }
        catch { return null; }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
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

    /// <summary>
    /// Se llama desde el temporizador, el vigilante de archivos y la interfaz. Si ya hay un Poll en curso
    /// se omite (el siguiente tick de 500 ms lo cubre). No se usa lock bloqueante porque PollCore invoca
    /// Changed -> Dispatcher.Invoke y podría haber un interbloqueo con el hilo de la interfaz.
    /// </summary>
    void Poll()
    {
        if (!Monitor.TryEnter(_pollLock)) return;
        try { PollCore(); }
        finally { Monitor.Exit(_pollLock); }
    }

    void PollCore()
    {
        bool hasActive;
        lock (_lock)
        {
            hasActive = _active.Count > 0 || _steamTrackers.Count > 0 || _bridge.GetActive().Count > 0;
        }

        if (!hasActive)
        {
            // Reposo real: sin leer la red y sin releer los .acf salvo que Steam haya tocado alguno
            // (o cada 30 s como respaldo, por si aparece una biblioteca nueva).
            if (_browserPaused) SetBrowserPaused(false);   // nunca dejar el navegador congelado

            var t = DateTime.UtcNow;
            _lastNetBytes = 0;          // se vuelve a medir al empezar una descarga
            _lastPollTime = t;
            _smoothedSpeed = 0;

            if (!_steamDirty && (t - _lastSteamScanUtc).TotalSeconds < 30) return;
            _steamDirty = false;
            _lastSteamScanUtc = t;
            PollSteam(0);
            PollEpicAndRiot(0);
            return;
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
        PollEpicAndRiot(netDelta);

        // Con la extensión conectada el navegador informa de verdad: nombre, total, porcentaje y pausa real
        var ext = _bridge.GetActive();
        if (ext.Count > 0)
        {
            var primary = ext.FirstOrDefault(d => !d.Paused) ?? ext[0];
            int steamCount;
            lock (_lock) steamCount = _steamTrackers.Count;

            _lastBrowserBytes = 0;
            _browserStalled = false;
            _smoothedSpeed = primary.Paused ? 0 : primary.SpeedBytesPerSec;

            Changed?.Invoke(new DownloadUpdate(
                primary.Paused ? DownloadStatus.Paused : DownloadStatus.Active,
                string.IsNullOrEmpty(primary.Name) ? "Descargando archivo" : primary.Name,
                primary.Bytes,
                ext.Count + steamCount,
                null,
                TotalBytes: primary.Total,
                SpeedBytesPerSec: _smoothedSpeed));
            return;
        }

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
            // Si el proceso de red que suspendimos ya no existe (el navegador se reinició), limpiar el estado
            if (_browserPaused && !AnySuspendedProcessAlive()) SetBrowserPaused(false);

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

                // Lo único fiable para un navegador es el crecimiento del archivo (la red incluye todo lo demás).
                // Al pausar, Chrome aún vuelca a disco lo que tenía en búfer: eso NO significa que se reanudó.
                if (_lastBrowserBytes == 0 || fileDelta > 0) _lastBrowserGrowthUtc = now;

                bool stalled = !_browserPaused && (now - _lastBrowserGrowthUtc).TotalSeconds >= 4;
                _browserStalled = stalled;
                bool shownPaused = _browserPaused || stalled;

                if (shownPaused)
                {
                    _smoothedSpeed = 0;
                }
                else if (fileDelta > 0)
                {
                    long instantSpeed = (long)(fileDelta / elapsedSec);
                    _smoothedSpeed = _smoothedSpeed == 0 ? instantSpeed : (long)(_smoothedSpeed * 0.55 + instantSpeed * 0.45);
                }
                else
                {
                    _smoothedSpeed = (long)(_smoothedSpeed * 0.8);   // el navegador escribe a ráfagas: decae suave
                }
                _lastBrowserBytes = total;

                Changed?.Invoke(new DownloadUpdate(
                    shownPaused ? DownloadStatus.Paused : DownloadStatus.Active,
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
        _browserStalled = false;

        if (_browserPaused)
        {
            bool anyBrowserDownload;
            lock (_lock) anyBrowserDownload = _active.Count > 0;
            if (!anyBrowserDownload) SetBrowserPaused(false);
        }

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
                    SourceApp: currentSteam.SourceApp,
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
                    SourceApp: currentSteam.SourceApp,
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
            var uninstalledNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var completedNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    if (!hasActiveDownload)
                    {
                        // StateFlags = 4 -> totalmente instalado: la descarga que seguíamos terminó
                        string doneId = ExtractVdfValue(content, "appid");
                        if (flags == 4 && doneId.Length > 0) completedNow.Add(doneId);
                        continue;
                    }

                    string appId = ExtractVdfValue(content, "appid");
                    string gameName = ExtractVdfValue(content, "name");
                    if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(gameName) || appId == "228980") continue;

                    var (pausedInLog, uninstalledInLog) = GetAppStatesFromSteamLog(_cachedSteamRoot);
                    if (uninstalledInLog.Contains(appId)) { uninstalledNow.Add(appId); continue; }

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
                                // Steam escribió un valor real: es la verdad; se descarta lo extrapolado
                                // (antes el error de la red —cabeceras, otras apps— se acumulaba sin corregirse)
                                tracker.AcfBaselineBytes = downloaded;
                                tracker.ExtraSinceAcf = 0;
                                tracker.EstimatedBytes = downloaded;
                            }
                        }

                        if (!tracker.IsPaused && netDeltaBytes > 0)
                        {
                            tracker.ExtraSinceAcf += netDeltaBytes;
                            tracker.EstimatedBytes = Math.Min(tracker.TotalBytes, tracker.AcfBaselineBytes + tracker.ExtraSinceAcf);
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
                        var tr = _steamTrackers[k];
                        if (tr.SourceApp != "Steam") continue;
                        if (seenIds.Contains(k)) { tr.MissingSinceUtc = null; continue; }

                        bool looksComplete = !tr.IsPaused && tr.TotalBytes > 0 && tr.EstimatedBytes >= tr.TotalBytes * 0.95;
                        bool immediate = uninstalledNow.Contains(k) || completedNow.Contains(k) || looksComplete;
                        if (!immediate)
                        {
                            // Al pausar/reanudar, Steam reescribe el manifiesto y por un momento no parece "descargando".
                            // No se da por cancelada hasta que lleve ~10 s sin aparecer (y no haya una pausa/reanudación en curso).
                            var nowUtc = DateTime.UtcNow;
                            tr.MissingSinceUtc ??= nowUtc;
                            bool overrideActive = tr.ManualOverrideUntilUtc != null && nowUtc <= tr.ManualOverrideUntilUtc;
                            if (overrideActive || (nowUtc - tr.MissingSinceUtc.Value).TotalSeconds < 10) continue;
                        }

                        toRemove ??= new();
                        toRemove.Add(k);
                    }

                    if (toRemove != null)
                    {
                        foreach (var appId in toRemove)
                        {
                            var done = _steamTrackers[appId];
                            _steamTrackers.Remove(appId);
                            if (completedNow.Contains(appId) || (!done.IsPaused && done.TotalBytes > 0 && done.EstimatedBytes >= done.TotalBytes * 0.95))
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

    // ---------- Soporte en tiempo real para Epic Games y Riot Games ----------
    void PollEpicAndRiot(long netDeltaBytes)
    {
        EnsureExternalLauncherWatchers();
        PollEpicGames(netDeltaBytes);
        PollRiotGames(netDeltaBytes);
    }

    bool _externalWatchersInitialized;

    void EnsureExternalLauncherWatchers()
    {
        if (_externalWatchersInitialized) return;
        _externalWatchersInitialized = true;

        string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string epicManifests = Path.Combine(progData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        string riotMeta = Path.Combine(progData, "Riot Games", "Metadata");

        foreach (var (dir, filter, subdirs) in new[]
        {
            (epicManifests, "*.item", true),
            (riotMeta, "*.*", true)
        })
        {
            if (!Directory.Exists(dir) || _steamWatchers.ContainsKey(dir)) continue;
            try
            {
                var w = new FileSystemWatcher(dir, filter)
                {
                    IncludeSubdirectories = subdirs,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                w.Changed += (_, _) => _steamDirty = true;
                w.Created += (_, _) => _steamDirty = true;
                w.Deleted += (_, _) => _steamDirty = true;
                w.Renamed += (_, _) => _steamDirty = true;
                w.EnableRaisingEvents = true;
                _steamWatchers[dir] = w;
            }
            catch { }
        }
    }

    void PollEpicGames(long netDeltaBytes)
    {
        try
        {
            string manifestsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(manifestsDir)) return;

            bool epicRunning = Process.GetProcessesByName("EpicGamesLauncher").Length > 0;
            var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var completedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(manifestsDir, "*.item", SearchOption.AllDirectories))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string appName = root.TryGetProperty("AppName", out var an) ? (an.GetString() ?? "") : "";
                    string displayName = root.TryGetProperty("DisplayName", out var dn) ? (dn.GetString() ?? "") : "";
                    if (string.IsNullOrEmpty(appName) || string.IsNullOrEmpty(displayName)) continue;

                    string key = $"epic:{appName}";
                    bool incomplete = root.TryGetProperty("bIsIncompleteInstall", out var inc) &&
                                      inc.ValueKind == System.Text.Json.JsonValueKind.True;

                    // Si el archivo está dentro de la subcarpeta Pending, también es una descarga en curso
                    if (!incomplete && file.Contains("\\Pending\\", StringComparison.OrdinalIgnoreCase))
                        incomplete = true;

                    if (!incomplete)
                    {
                        completedIds.Add(key);
                        continue;
                    }

                    if (!epicRunning) continue;

                    long totalSize = root.TryGetProperty("InstallSize", out var isz) && isz.TryGetInt64(out long sz) ? sz : 0;
                    string installLoc = root.TryGetProperty("InstallLocation", out var il) ? (il.GetString() ?? "") : "";

                    activeIds.Add(key);
                    lock (_lock)
                    {
                        var now = DateTime.UtcNow;
                        if (!_steamTrackers.TryGetValue(key, out var tr))
                        {
                            tr = new SteamLiveTracker
                            {
                                AppId = key,
                                Name = displayName,
                                SourceApp = "Epic",
                                TotalBytes = totalSize,
                                InstallDir = installLoc,
                                LastGrowthUtc = now
                            };
                            _steamTrackers[key] = tr;
                        }
                        else
                        {
                            tr.Name = displayName;
                            if (totalSize > 0) tr.TotalBytes = totalSize;
                            if (!string.IsNullOrEmpty(installLoc)) tr.InstallDir = installLoc;
                        }

                        if (netDeltaBytes > 4096)
                        {
                            tr.LastGrowthUtc = now;
                            tr.IsPaused = false;
                            tr.EstimatedBytes = tr.TotalBytes > 0
                                ? Math.Min(tr.TotalBytes, tr.EstimatedBytes + netDeltaBytes)
                                : tr.EstimatedBytes + netDeltaBytes;
                        }
                        else if ((now - tr.LastGrowthUtc).TotalSeconds >= 6)
                        {
                            tr.IsPaused = true;
                        }
                    }
                }
                catch { }
            }

            CleanupLauncherTrackers("Epic", activeIds, completedIds);
        }
        catch { }
    }

    void PollRiotGames(long netDeltaBytes)
    {
        try
        {
            string metaRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Riot Games", "Metadata");
            if (!Directory.Exists(metaRoot)) return;

            bool riotRunning = Process.GetProcessesByName("RiotClientServices").Length > 0;
            var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var completedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var products = new (string Key, string Name, string DefaultDir)[]
            {
                ("valorant", "VALORANT", @"C:\Riot Games\VALORANT\live"),
                ("league_of_legends", "League of Legends", @"C:\Riot Games\League of Legends"),
                ("bacon", "Legends of Runeterra", @"C:\Riot Games\LoR\live"),
                ("lion", "2XKO", @"C:\Riot Games\2XKO\live")
            };

            foreach (var prod in products)
            {
                string prodDir = Path.Combine(metaRoot, $"{prod.Key}.live");
                if (!Directory.Exists(prodDir)) continue;

                string id = $"riot:{prod.Key}";
                string okFile = Path.Combine(prodDir, $"{prod.Key}.live.ok");
                string lockFile = Path.Combine(prodDir, $"{prod.Key}.live.lock");
                bool hasOk = File.Exists(okFile);
                bool hasLock = File.Exists(lockFile);

                if (hasOk && !hasLock)
                {
                    completedIds.Add(id);
                    continue;
                }

                if (!riotRunning) continue;

                // Está actualizando si existe .lock o falta .ok mientras RiotClientServices está activo
                activeIds.Add(id);
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    if (!_steamTrackers.TryGetValue(id, out var tr))
                    {
                        tr = new SteamLiveTracker
                        {
                            AppId = id,
                            Name = prod.Name,
                            SourceApp = "Riot",
                            TotalBytes = 0,
                            InstallDir = prod.DefaultDir,
                            LastGrowthUtc = now
                        };
                        _steamTrackers[id] = tr;
                    }

                    if (netDeltaBytes > 4096)
                    {
                        tr.LastGrowthUtc = now;
                        tr.IsPaused = false;
                        tr.EstimatedBytes += netDeltaBytes;
                    }
                    else if ((now - tr.LastGrowthUtc).TotalSeconds >= 6)
                    {
                        tr.IsPaused = true;
                    }
                }
            }

            CleanupLauncherTrackers("Riot", activeIds, completedIds);
        }
        catch { }
    }

    void CleanupLauncherTrackers(string sourceApp, HashSet<string> activeIds, HashSet<string> completedIds)
    {
        List<SteamLiveTracker>? doneList = null;
        List<SteamLiveTracker>? cancList = null;

        lock (_lock)
        {
            List<string>? toRemove = null;
            foreach (var kv in _steamTrackers)
            {
                var tr = kv.Value;
                if (tr.SourceApp != sourceApp) continue;
                if (activeIds.Contains(kv.Key)) { tr.MissingSinceUtc = null; continue; }

                if (!completedIds.Contains(kv.Key))
                {
                    tr.MissingSinceUtc ??= DateTime.UtcNow;
                    if ((DateTime.UtcNow - tr.MissingSinceUtc.Value).TotalSeconds < 8) continue;
                }

                toRemove ??= new();
                toRemove.Add(kv.Key);
            }

            if (toRemove != null)
            {
                foreach (var k in toRemove)
                {
                    var tr = _steamTrackers[k];
                    _steamTrackers.Remove(k);
                    if (completedIds.Contains(k))
                    {
                        doneList ??= new();
                        doneList.Add(tr);
                    }
                    else
                    {
                        cancList ??= new();
                        cancList.Add(tr);
                    }
                }
            }
        }

        if (doneList != null)
        {
            foreach (var d in doneList)
            {
                int left;
                lock (_lock) left = _active.Count + _steamTrackers.Count;
                Changed?.Invoke(new DownloadUpdate(
                    DownloadStatus.Completed,
                    d.Name,
                    d.TotalBytes > 0 ? d.TotalBytes : d.EstimatedBytes,
                    left,
                    Directory.Exists(d.InstallDir) ? d.InstallDir : null,
                    TotalBytes: d.TotalBytes,
                    SourceApp: d.SourceApp,
                    AppId: d.AppId));
            }
        }

        if (cancList != null)
        {
            foreach (var c in cancList)
            {
                int left;
                lock (_lock) left = _active.Count + _steamTrackers.Count;
                Changed?.Invoke(new DownloadUpdate(
                    DownloadStatus.Cancelled,
                    c.Name,
                    0,
                    left,
                    null,
                    SourceApp: c.SourceApp,
                    AppId: c.AppId));
            }
        }
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
        lock (_steamCacheLock) EnsureSteamDirectoriesCachedCore();
    }

    void EnsureSteamDirectoriesCachedCore()
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
        SyncSteamWatchers(result);
    }

    /// <summary>Un vigilante por biblioteca: marca _steamDirty cuando Steam crea/modifica/borra un appmanifest.</summary>
    void SyncSteamWatchers(List<string> dirs)
    {
        foreach (var d in dirs)
        {
            if (_steamWatchers.ContainsKey(d)) continue;
            try
            {
                var w = new FileSystemWatcher(d, "appmanifest_*.acf")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                w.Changed += (_, _) => _steamDirty = true;
                w.Created += (_, _) => _steamDirty = true;
                w.Deleted += (_, _) => _steamDirty = true;
                w.Renamed += (_, _) => _steamDirty = true;
                w.EnableRaisingEvents = true;
                _steamWatchers[d] = w;
            }
            catch { }
        }
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
        _timer?.Dispose();
        _bridge.Dispose();
        SetBrowserPaused(false);   // siempre deja el navegador reanudado
        _watcher?.Dispose();
        foreach (var w in _steamWatchers.Values) w.Dispose();
        _steamWatchers.Clear();
    }
}
