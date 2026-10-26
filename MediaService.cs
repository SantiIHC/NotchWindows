using System.IO;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace IslaDinamica;

public record MediaInfo(string Title, string Artist, bool IsPlaying, BitmapImage? Art)
{
    public static readonly MediaInfo Empty = new("", "", false, null);
}

/// <summary>Lee lo que suena en Windows (Spotify, YouTube, etc.) vía SMTC con caché de portada para ahorrar RAM.</summary>
public class MediaService
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;

    string _lastTrackKey = "";
    BitmapImage? _cachedArt;

    public event Action<MediaInfo>? Changed;

    public async Task InitAsync()
    {
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _mgr.CurrentSessionChanged += (_, _) => Attach();
        Attach();
    }

    void Attach()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
        }
        _session = _mgr?.GetCurrentSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
        }
        _ = Refresh(forceArt: true);
    }

    void OnPropertiesChanged(object? s, object e) => _ = Refresh(forceArt: true);
    void OnPlaybackChanged(object? s, object e) => _ = Refresh(forceArt: false);

    async Task Refresh(bool forceArt)
    {
        try
        {
            if (_session == null)
            {
                _lastTrackKey = "";
                _cachedArt = null;
                Changed?.Invoke(MediaInfo.Empty);
                return;
            }

            var props = await _session.TryGetMediaPropertiesAsync();
            var title = props.Title ?? "";
            var artist = props.Artist ?? "";
            var playing = _session.GetPlaybackInfo().PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            string trackKey = $"{title}|{artist}";
            if (forceArt || trackKey != _lastTrackKey)
            {
                _lastTrackKey = trackKey;
                _cachedArt = null;

                if (props.Thumbnail != null)
                {
                    using var ras = await props.Thumbnail.OpenReadAsync();
                    using var s = ras.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms);
                    ms.Position = 0;

                    var art = new BitmapImage();
                    art.BeginInit();
                    art.CacheOption = BitmapCacheOption.OnLoad;
                    // Redimensionar a 136px en decodificación ahorra ~95% de RAM frente a portadas 1000x1000
                    art.DecodePixelWidth = 136;
                    art.StreamSource = ms;
                    art.EndInit();
                    art.Freeze();
                    _cachedArt = art;
                }
            }

            Changed?.Invoke(new MediaInfo(title, artist, playing, _cachedArt));
        }
        catch { /* sesión cerrada mientras se leía: se ignora */ }
    }

    /// <summary>Progreso de la canción (0 a 1). ok=false si la app no reporta duración.</summary>
    public (double Fraction, bool Ok) GetProgress()
    {
        if (_session == null) return (0, false);
        try
        {
            var t = _session.GetTimelineProperties();
            var total = (t.EndTime - t.StartTime).TotalSeconds;
            if (total <= 0) return (0, false);

            var pos = (t.Position - t.StartTime).TotalSeconds;
            var playing = _session.GetPlaybackInfo().PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing) pos += (DateTimeOffset.Now - t.LastUpdatedTime).TotalSeconds;

            return (Math.Clamp(pos / total, 0, 1), true);
        }
        catch { return (0, false); }
    }

    public async Task PlayPause() { if (_session != null) await _session.TryTogglePlayPauseAsync(); }
    public async Task Next()      { if (_session != null) await _session.TrySkipNextAsync(); }
    public async Task Previous()  { if (_session != null) await _session.TrySkipPreviousAsync(); }
}
