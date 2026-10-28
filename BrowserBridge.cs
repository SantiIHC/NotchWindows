using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace IslaDinamica;

/// <param name="SpeedBytesPerSec">Velocidad suavizada calculada con los bytes que informa el navegador.</param>
public record ExtDownload(int Id, string Name, long Bytes, long Total, bool Paused, long SpeedBytesPerSec);

/// <param name="State">"complete" o "interrupted" (cancelada o fallida).</param>
public record ExtFinished(int Id, string State, string Path, string Name, long Bytes);

/// <summary>
/// Servidor local (solo 127.0.0.1) con el que habla la extensión del navegador. La extensión envía las
/// descargas reales (nombre, bytes, total, pausa) y recibe de vuelta los comandos pause/resume, que
/// ejecuta con chrome.downloads.pause/resume: una pausa de verdad, sin cortar el internet del navegador.
/// Solo acepta peticiones cuyo Origin sea de una extensión (una página web no puede falsificarlo).
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    public const int Port = 47831;

    sealed class DownloadDto
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public long Bytes { get; set; }
        public long Total { get; set; }
        public bool Paused { get; set; }
    }

    sealed class EventDto
    {
        public int Id { get; set; }
        public string? State { get; set; }
        public string? Path { get; set; }
        public string? Name { get; set; }
        public long Bytes { get; set; }
    }

    sealed class StateDto
    {
        public string? Version { get; set; }
        public List<DownloadDto>? Downloads { get; set; }
        public List<EventDto>? Events { get; set; }
    }

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    readonly object _lock = new();
    readonly ConcurrentQueue<(string Cmd, int Id)> _commands = new();
    readonly Dictionary<int, DateTime> _finishedIds = new();
    Dictionary<int, (long Bytes, DateTime T, long Speed)> _samples = new();
    IReadOnlyList<ExtDownload> _current = Array.Empty<ExtDownload>();
    DateTime _lastReportUtc = DateTime.MinValue;
    HttpListener? _listener;
    string? _extVersion;

    /// <summary>Se dispara cada vez que la extensión informa (para refrescar la isla al instante).</summary>
    public event Action? Reported;

    /// <summary>Una descarga terminó o se interrumpió/canceló (informado por la extensión).</summary>
    public event Action<ExtFinished>? Finished;

    public void Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = Task.Run(Loop);
        }
        catch
        {
            _listener = null;   // puerto ocupado o sin permiso: la isla sigue funcionando sin extensión
        }
    }

    /// <summary>Descargas en curso según la extensión (vacío si no está conectada o hace >3 s que no informa).</summary>
    public IReadOnlyList<ExtDownload> GetActive()
    {
        lock (_lock)
        {
            if ((DateTime.UtcNow - _lastReportUtc).TotalSeconds > 3) return Array.Empty<ExtDownload>();
            return _current;
        }
    }

    public void Enqueue(string cmd, int id) => _commands.Enqueue((cmd, id));

    async Task Loop()
    {
        while (_listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;

            string origin = req.Headers["Origin"] ?? "";
            bool fromExtension = origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                                 origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase);
            if (!fromExtension) { res.StatusCode = 403; res.Close(); return; }

            res.Headers["Access-Control-Allow-Origin"] = origin;
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            res.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";

            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }
            if (req.HttpMethod != "POST" || req.Url?.AbsolutePath != "/state") { res.StatusCode = 404; res.Close(); return; }

            StateDto? dto;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                dto = JsonSerializer.Deserialize<StateDto>(sr.ReadToEnd(), JsonOpts);

            if (dto?.Version != _extVersion)
            {
                _extVersion = dto?.Version;
                DownloadService.Log($"Extensión conectada: versión {_extVersion ?? "antigua (sin versión)"}");
            }

            var now = DateTime.UtcNow;
            var newSamples = new Dictionary<int, (long, DateTime, long)>();
            var list = new List<ExtDownload>();

            lock (_lock)
            {
                // Descargas rápidas: la lista "in_progress" puede haberse tomado justo antes de terminar, y el aviso
                // de "complete" llega en la misma petición. Se recuerdan los ids terminados para no mostrarlas
                // como activas después de completarse (si no, la isla se quedaba en "0 KB / X KB (0%)").
                foreach (var ev in dto?.Events ?? new List<EventDto>())
                    if (ev.State is "complete" or "interrupted") _finishedIds[ev.Id] = now;
                foreach (var k in _finishedIds.Where(kv => (now - kv.Value).TotalSeconds > 30).Select(kv => kv.Key).ToList())
                    _finishedIds.Remove(k);

                foreach (var d in dto?.Downloads ?? new List<DownloadDto>())
                {
                    if (_finishedIds.ContainsKey(d.Id)) continue;
                    long speed = 0;
                    if (!d.Paused && _samples.TryGetValue(d.Id, out var prev))
                    {
                        double dt = (now - prev.T).TotalSeconds;
                        if (dt > 0.05 && d.Bytes >= prev.Bytes)
                        {
                            long inst = (long)((d.Bytes - prev.Bytes) / dt);
                            speed = prev.Speed == 0 ? inst : (long)(prev.Speed * 0.6 + inst * 0.4);
                        }
                        else speed = prev.Speed;
                    }
                    newSamples[d.Id] = (d.Bytes, now, speed);
                    list.Add(new ExtDownload(d.Id, d.Name ?? "", d.Bytes, d.Total, d.Paused, speed));
                }
                _samples = newSamples;
                _current = list;
                _lastReportUtc = now;
            }

            var cmds = new List<object>();
            while (_commands.TryDequeue(out var c))
            {
                cmds.Add(new { cmd = c.Cmd, id = c.Id });
                DownloadService.Log($"Comando enviado a la extensión: {c.Cmd} (descarga {c.Id})");
            }

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { commands = cmds }));
            res.ContentType = "application/json";
            res.ContentLength64 = body.Length;
            res.OutputStream.Write(body, 0, body.Length);
            res.Close();

            foreach (var ev in dto?.Events ?? new List<EventDto>())
            {
                if (ev.State is "complete" or "interrupted")
                    Finished?.Invoke(new ExtFinished(ev.Id, ev.State, ev.Path ?? "", ev.Name ?? "", ev.Bytes));
            }

            Reported?.Invoke();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    public void Dispose()
    {
        try { _listener?.Close(); } catch { }
        _listener = null;
    }
}
