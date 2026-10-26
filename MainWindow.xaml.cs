using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace IslaDinamica;

public partial class MainWindow : Window
{
    enum IslandTab { Music, Downloads }

    static readonly SolidColorBrush s_activeTabBg = CreateFrozenBrush(0x28, 0x28, 0x2F);
    static readonly SolidColorBrush s_idleTabBg = CreateFrozenBrush(0x14, 0x14, 0x17);
    static readonly SolidColorBrush s_idleTabFg = CreateFrozenBrush(0x8E, 0x8E, 0x93);
    static readonly SolidColorBrush s_greenBrush = CreateFrozenBrush(0x30, 0xD1, 0x58);
    static readonly SolidColorBrush s_amberBrush = CreateFrozenBrush(0xFF, 0x9F, 0x0A);

    static readonly Geometry s_playGeometry = CreateFrozenGeometry("M 2,0 L 11,6 L 2,12 Z");
    static readonly Geometry s_pauseGeometry = CreateFrozenGeometry("M 1,0 L 4,0 L 4,11 L 1,11 Z M 7,0 L 10,0 L 10,11 L 7,11 Z");

    static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    static Geometry CreateFrozenGeometry(string pathData)
    {
        var g = Geometry.Parse(pathData);
        g.Freeze();
        return g;
    }

    readonly MediaService _media = new();
    readonly DownloadService _downloads = new();
    readonly StringBuilder _classNameBuf = new(64);

    readonly DispatcherTimer _mainTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _autoCollapseTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _completedTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    Storyboard? _eqStoryboard;
    bool _eqRunning;
    int _trimTicks;
    System.Windows.Forms.NotifyIcon? _tray;
    System.Windows.Forms.ToolStripMenuItem? _screensTrayMenu;

    string? _selectedDeviceName;
    bool _hiddenByFullscreen;

    string _lastTitle = "";
    string? _lastFinalPath;
    int _activeDownloads;
    bool _showCompleted;
    bool _isPlaying;
    bool _expanded;
    double _currentDlFraction;
    IslandTab _selectedTab = IslandTab.Music;

    // Tamaños de la isla
    const double CompactW = 196, CompactH = 36;
    const double ExpandedW = 398, MusicH = 132, DownloadH = 132;

    bool HasDownloadActivity => _activeDownloads > 0 || _showCompleted;
    double ExpandedH => _selectedTab == IslandTab.Downloads ? DownloadH : MusicH;

    public MainWindow() => InitializeComponent();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // WS_EX_TOOLWINDOW oculta la ventana del selector Alt+Tab y Win+Tab
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
    }

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionOnScreen();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        SetupTray();
        BuildEqualizerStoryboard();
        UpdateClock();
        RefreshTabVisuals(animate: false);

        _mainTimer.Tick += (_, _) =>
        {
            UpdateClock();
            CheckFullscreen();
            if (_expanded && _selectedTab == IslandTab.Music && !_hiddenByFullscreen)
                UpdateProgress();

            if (++_trimTicks >= 120) // Cada ~60s en reposo compacta el Working Set
            {
                _trimTicks = 0;
                if (!_expanded) TrimWorkingSet();
            }
        };
        _mainTimer.Start();

        _autoCollapseTimer.Tick += (_, _) =>
        {
            _autoCollapseTimer.Stop();
            if (!Island.IsMouseOver) Animate(false);
        };

        _completedTimer.Tick += (_, _) =>
        {
            _completedTimer.Stop();
            _showCompleted = false;
            if (_activeDownloads == 0)
            {
                DlIcon.Text = "⬇";
                DlTitle.Text = "Sin descargas activas";
                DlSub.Text = "Las descargas de navegador y Steam aparecerán aquí";
                OpenBtn.Visibility = Visibility.Collapsed;
            }
            RefreshCompactIndicators();
            if (_selectedTab == IslandTab.Downloads && _isPlaying)
                SelectTab(IslandTab.Music, animate: _expanded);
            if (!Island.IsMouseOver) Animate(false);
        };

        _downloads.Changed += u => Dispatcher.Invoke(() => OnDownload(u));
        _downloads.Start();

        _media.Changed += info => Dispatcher.Invoke(() => Apply(info));
        try { await _media.InitAsync(); }
        catch { TitleText.Text = "Multimedia no disponible"; }

        TrimWorkingSet();
    }

    static void TrimWorkingSet()
    {
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
    }

    // ---------- Reloj central y ecualizador animado ----------
    void UpdateClock()
    {
        string now = DateTime.Now.ToString("HH:mm");
        if (ClockText.Text != now)
        {
            ClockText.Text = now;
            ExpandedClockText.Text = DateTime.Now.ToString("HH:mm  ·  ddd d MMM");
        }
    }

    void BuildEqualizerStoryboard()
    {
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        AddBarAnim(sb, EqBar1, 4, 13, 320, 0);
        AddBarAnim(sb, EqBar2, 12, 4, 280, 90);
        AddBarAnim(sb, EqBar3, 5, 14, 360, 40);
        AddBarAnim(sb, EqBar4, 11, 5, 300, 130);
        _eqStoryboard = sb;
    }

    static void AddBarAnim(Storyboard sb, FrameworkElement bar, double from, double to, int ms, int delayMs)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(anim, bar);
        Storyboard.SetTargetProperty(anim, new PropertyPath(HeightProperty));
        sb.Children.Add(anim);
    }

    void SetEqualizerRunning(bool showIcon)
    {
        if (_eqStoryboard == null) return;
        CompactMusicIndicator.Visibility = showIcon ? Visibility.Visible : Visibility.Collapsed;

        // Solo animar las barras cuando la isla está en modo compacto y visible
        bool shouldAnimate = showIcon && !_expanded && !_hiddenByFullscreen;
        if (shouldAnimate == _eqRunning) return;
        _eqRunning = shouldAnimate;

        if (shouldAnimate) _eqStoryboard.Begin(this, true);
        else _eqStoryboard.Stop(this);
    }

    // ---------- Círculo verde de progreso (compacto y expandido) ----------
    void SetRingProgress(double fraction)
    {
        _currentDlFraction = Math.Clamp(fraction, 0.0, 1.0);

        // Circunferencia en unidades de StrokeThickness:
        // CompactDlRing: (20 - 2.4) * pi / 2.4 ≈ 23.04
        // ExpandedDlRing: (52 - 3.5) * pi / 3.5 ≈ 43.53
        const double compactTotal = 23.04;
        const double expandedTotal = 43.53;

        double cDash = compactTotal * _currentDlFraction;
        CompactDlRing.StrokeDashArray = new DoubleCollection { cDash, 100 };

        double eDash = expandedTotal * _currentDlFraction;
        ExpandedDlRing.StrokeDashArray = new DoubleCollection { eDash, 100 };
    }

    void RefreshCompactIndicators()
    {
        CompactDlIndicator.Visibility = HasDownloadActivity ? Visibility.Visible : Visibility.Collapsed;
        SetEqualizerRunning(_isPlaying);
    }

    // ---------- Selección de pestaña (Click en iconos o pestañas) ----------
    void SelectTab(IslandTab tab, bool animate)
    {
        bool changed = _selectedTab != tab;
        _selectedTab = tab;
        RefreshTabVisuals(animate && changed);
    }

    void RefreshTabVisuals(bool animate)
    {
        bool isMusic = _selectedTab == IslandTab.Music;
        MusicPanel.Visibility = isMusic ? Visibility.Visible : Visibility.Collapsed;
        DownloadPanel.Visibility = isMusic ? Visibility.Collapsed : Visibility.Visible;

        TabMusicBtn.Background = isMusic ? s_activeTabBg : s_idleTabBg;
        TabMusicBtn.Foreground = isMusic ? Brushes.White : s_idleTabFg;
        TabDlBtn.Background = !isMusic ? s_activeTabBg : s_idleTabBg;
        TabDlBtn.Foreground = !isMusic ? Brushes.White : s_idleTabFg;

        if (animate)
        {
            ContentHost.Opacity = 0;
            ContentSlide.Y = 6;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ContentHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            ContentSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        }

        if (_expanded) Animate(true);
    }

    void CompactDl_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SelectTab(IslandTab.Downloads, animate: true);
        Animate(true);
    }

    void CompactMusic_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SelectTab(IslandTab.Music, animate: true);
        Animate(true);
    }

    void Clock_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (HasDownloadActivity && !_isPlaying)
            SelectTab(IslandTab.Downloads, animate: true);
        else
            SelectTab(IslandTab.Music, animate: true);
        Animate(true);
    }

    void TabMusic_Click(object sender, RoutedEventArgs e) => SelectTab(IslandTab.Music, animate: true);
    void TabDl_Click(object sender, RoutedEventArgs e) => SelectTab(IslandTab.Downloads, animate: true);

    // ---------- Multi-monitor y escalado DPI ----------
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        PositionOnScreen();
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            RefreshScreensMenu();
            PositionOnScreen();
        });
    }

    System.Windows.Forms.Screen GetCurrentScreen()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (!string.IsNullOrEmpty(_selectedDeviceName))
        {
            foreach (var s in screens)
                if (s.DeviceName == _selectedDeviceName)
                    return s;
        }
        return System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
    }

    void PositionOnScreen()
    {
        var screen = GetCurrentScreen();
        var bounds = screen.Bounds;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        var pt = new POINT { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + 1 };
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);

        double scaleX = 1.0;
        if (GetDpiForMonitor(hMonitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
            scaleX = dpiX / 96.0;
        else
            scaleX = VisualTreeHelper.GetDpi(this).DpiScaleX;

        int physW = (int)Math.Round(Width * scaleX);
        int physX = bounds.Left + (bounds.Width - physW) / 2;
        int physY = bounds.Top;

        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, IntPtr.Zero, physX, physY, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            var topLeft = source.CompositionTarget.TransformFromDevice.Transform(new Point(physX, physY));
            Left = topLeft.X;
            Top = topLeft.Y;
        }
        else
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
        }
    }

    // ---------- Detección de pantalla completa ----------
    void CheckFullscreen()
    {
        bool full = IsFullscreenOnCurrentMonitor();
        if (full == _hiddenByFullscreen) return;

        _hiddenByFullscreen = full;
        if (full)
        {
            _autoCollapseTimer.Stop();
            Animate(false);
            Visibility = Visibility.Hidden;
        }
        else
        {
            Visibility = Visibility.Visible;
            PositionOnScreen();
        }
    }

    bool IsFullscreenOnCurrentMonitor()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        IntPtr myHwnd = new WindowInteropHelper(this).Handle;
        if (fg == myHwnd) return _hiddenByFullscreen;

        IntPtr shellWnd = GetShellWindow();
        if (fg == GetDesktopWindow() || fg == shellWnd) return false;
        if (!IsWindowVisible(fg) || IsIconic(fg)) return false;

        if (shellWnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(shellWnd, out uint shellPid);
            GetWindowThreadProcessId(fg, out uint fgPid);
            if (fgPid == shellPid) return false;
        }

        _classNameBuf.Clear();
        GetClassName(fg, _classNameBuf, _classNameBuf.Capacity);
        string className = _classNameBuf.ToString();
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "XamlExplorerHostIslandWindow" or "MultitaskingViewFrame"
            or "TaskSwitcherWnd" or "TaskSwitcherOverlayWnd" or "ForegroundStaging")
            return false;

        var screen = GetCurrentScreen();
        var fgScreen = System.Windows.Forms.Screen.FromHandle(fg);
        if (fgScreen.DeviceName != screen.DeviceName)
            return false;

        if (!GetWindowRect(fg, out RECT r)) return false;

        var b = screen.Bounds;
        var w = screen.WorkingArea;

        bool coversScreen = r.Left <= b.Left && r.Top <= b.Top &&
                            r.Right >= b.Right && r.Bottom >= b.Bottom;
        if (!coversScreen) return false;

        if (b == w)
        {
            bool exactBounds = r.Left == b.Left && r.Top == b.Top &&
                               r.Right == b.Right && r.Bottom == b.Bottom;
            long style = GetWindowLong(fg, GWL_STYLE);
            bool isMaximizedWindow = (style & WS_MAXIMIZE) != 0 && (style & WS_CAPTION) != 0;
            if (isMaximizedWindow && !exactBounds)
                return false;
        }

        return true;
    }

    // ---------- Bandeja del sistema ----------
    void SetupTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        _screensTrayMenu = new System.Windows.Forms.ToolStripMenuItem("Pantalla");
        RefreshScreensMenu();

        var startup = new System.Windows.Forms.ToolStripMenuItem("Iniciar con Windows")
        {
            Checked = StartupManager.IsEnabled,
            CheckOnClick = true
        };
        startup.CheckedChanged += (_, _) => StartupManager.Set(startup.Checked);

        var exit = new System.Windows.Forms.ToolStripMenuItem("Salir");
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(_screensTrayMenu);
        menu.Items.Add(startup);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exit);

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Isla Dinámica",
            ContextMenuStrip = menu,
            Visible = true
        };
    }

    void RefreshScreensMenu()
    {
        if (_screensTrayMenu == null) return;
        _screensTrayMenu.DropDownItems.Clear();

        var screens = System.Windows.Forms.Screen.AllScreens;
        var current = GetCurrentScreen();

        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            string label = $"Pantalla {i + 1}" + (s.Primary ? " (Principal)" : "") + $" ({s.Bounds.Width}x{s.Bounds.Height})";
            var item = new System.Windows.Forms.ToolStripMenuItem(label)
            {
                Checked = s.DeviceName == current.DeviceName
            };
            string dev = s.DeviceName;
            item.Click += (_, _) =>
            {
                _selectedDeviceName = dev;
                RefreshScreensMenu();
                PositionOnScreen();
            };
            _screensTrayMenu.DropDownItems.Add(item);
        }
    }

    void ExitApp()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _downloads.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        Application.Current.Shutdown();
    }

    // ---------- Multimedia ----------
    void Apply(MediaInfo info)
    {
        TitleText.Text = string.IsNullOrEmpty(info.Title) ? "Sin reproducción" : info.Title;
        ArtistText.Text = string.IsNullOrEmpty(info.Artist) ? "Spotify o navegador" : info.Artist;
        Cover.Source = info.Art;
        PlayPauseIconPath.Data = info.IsPlaying ? s_pauseGeometry : s_playGeometry;
        PlayPauseIconPath.Margin = info.IsPlaying ? new Thickness(0) : new Thickness(1.5, 0, 0, 0);
        _isPlaying = info.IsPlaying && !string.IsNullOrEmpty(info.Title);

        RefreshCompactIndicators();

        // Al cambiar de canción, seleccionamos la pestaña de música y abrimos 3 s (si no hay descarga activa en pantalla)
        if (_isPlaying && info.Title != _lastTitle && !HasDownloadActivity)
        {
            SelectTab(IslandTab.Music, animate: false);
            ShowTemporarily(3);
        }

        _lastTitle = info.Title;
    }

    void UpdateProgress()
    {
        var (fraction, ok) = _media.GetProgress();
        ProgressTrack.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        if (ok) ProgressFill.Width = ProgressTrack.ActualWidth * fraction;
    }

    // ---------- Descargas ----------
    void OnDownload(DownloadUpdate u)
    {
        bool wasIdle = _activeDownloads == 0;
        _activeDownloads = u.ActiveCount;

        switch (u.Status)
        {
            case DownloadStatus.Active:
            case DownloadStatus.Paused:
                bool paused = u.Status == DownloadStatus.Paused;
                var accent = paused ? s_amberBrush : s_greenBrush;

                CompactDlRing.Stroke = accent;
                CompactDlIcon.Foreground = accent;
                ExpandedDlRing.Stroke = accent;
                DlIcon.Foreground = accent;
                DlProgressFill.Background = accent;

                DlPauseResumeBtn.Visibility = Visibility.Visible;
                DlPauseResumeBtn.ToolTip = paused ? "Continuar descarga" : "Pausar descarga";
                DlPauseResumePath.Data = paused ? s_playGeometry : s_pauseGeometry;
                DlPauseResumePath.Fill = accent;
                DlPauseResumePath.Margin = paused ? new Thickness(1.5, 0, 0, 0) : new Thickness(0);

                string speedStr = !paused && u.SpeedBytesPerSec > 10 * 1024
                    ? $" · {DownloadService.FormatBytes(u.SpeedBytesPerSec)}/s"
                    : "";

                double frac;
                if (u.TotalBytes > 0)
                {
                    double trueFrac = Math.Clamp((double)u.Bytes / u.TotalBytes, 0.0, 1.0);
                    frac = Math.Clamp(trueFrac, 0.03, 1.0);
                    int pct = (int)Math.Round(trueFrac * 100);
                    DlTitle.Text = u.SourceApp != null ? $"{u.SourceApp}: {u.Name}" : u.Name;
                    string prefix = paused ? "En pausa · " : "";
                    DlSub.Text = $"{prefix}{DownloadService.FormatBytes(u.Bytes)} / {DownloadService.FormatBytes(u.TotalBytes)} ({pct}%){speedStr}";
                    DlProgressTrack.Visibility = Visibility.Visible;
                    double trackW = DlProgressTrack.ActualWidth > 0 ? DlProgressTrack.ActualWidth : 240;
                    DlProgressFill.Width = trackW * frac;
                }
                else
                {
                    double mb = u.Bytes / (1024.0 * 1024.0);
                    frac = Math.Clamp(0.12 + 0.80 * (1.0 - Math.Exp(-mb / 45.0)), 0.12, 0.92);

                    bool tempName = string.IsNullOrEmpty(Path.GetExtension(u.Name)) &&
                                    System.Text.RegularExpressions.Regex.IsMatch(u.Name, @"\s\d+$");
                    DlTitle.Text = tempName ? "Descargando archivo" : u.Name;
                    string stateLabel = paused ? "En pausa" : "Descargando…";
                    DlSub.Text = $"{stateLabel} {DownloadService.FormatBytes(u.Bytes)}{speedStr}" +
                                 (u.ActiveCount > 1 ? $"  ·  {u.ActiveCount} archivos" : "");
                    DlProgressTrack.Visibility = Visibility.Collapsed;
                }

                SetRingProgress(frac);
                CompactDlIcon.Text = paused ? "⏸" : "⬇";
                DlIcon.Text = paused ? "⏸" : "⬇";
                OpenBtn.Visibility = Visibility.Collapsed;
                _showCompleted = false;
                _completedTimer.Stop();
                RefreshCompactIndicators();

                if (wasIdle && !paused)
                {
                    SelectTab(IslandTab.Downloads, animate: false);
                    ShowTemporarily(2.8);
                }
                break;

            case DownloadStatus.Completed:
                _lastFinalPath = u.FinalPath;
                CompactDlRing.Stroke = s_greenBrush;
                CompactDlIcon.Foreground = s_greenBrush;
                ExpandedDlRing.Stroke = s_greenBrush;
                DlIcon.Foreground = s_greenBrush;
                SetRingProgress(1.0);
                CompactDlIcon.Text = "✔";
                DlIcon.Text = "✔";
                DlTitle.Text = u.SourceApp != null ? $"{u.SourceApp}: {u.Name}" : u.Name;
                DlSub.Text = "Descarga completa · " + DownloadService.FormatBytes(u.Bytes);
                DlPauseResumeBtn.Visibility = Visibility.Collapsed;
                DlProgressTrack.Visibility = Visibility.Collapsed;
                OpenBtn.Visibility = _lastFinalPath != null ? Visibility.Visible : Visibility.Collapsed;
                _showCompleted = true;
                RefreshCompactIndicators();

                SelectTab(IslandTab.Downloads, animate: false);
                _autoCollapseTimer.Stop();
                Animate(true);
                _completedTimer.Interval = TimeSpan.FromSeconds(6);
                _completedTimer.Stop();
                _completedTimer.Start();
                break;

            case DownloadStatus.Cancelled:
                _showCompleted = false;
                if (_activeDownloads == 0)
                {
                    SetRingProgress(0);
                    CompactDlRing.Stroke = s_greenBrush;
                    CompactDlIcon.Foreground = s_greenBrush;
                    CompactDlIcon.Text = "⬇";
                    ExpandedDlRing.Stroke = s_greenBrush;
                    DlIcon.Foreground = s_idleTabFg;
                    DlIcon.Text = "✕";
                    DlTitle.Text = !string.IsNullOrEmpty(u.Name) ? $"Descarga eliminada: {u.Name}" : "Descarga eliminada";
                    DlSub.Text = "Ya no se está descargando nada";
                    DlPauseResumeBtn.Visibility = Visibility.Collapsed;
                    DlProgressTrack.Visibility = Visibility.Collapsed;
                    OpenBtn.Visibility = Visibility.Collapsed;

                    _completedTimer.Interval = TimeSpan.FromSeconds(3.5);
                    _completedTimer.Stop();
                    _completedTimer.Start();
                }
                RefreshCompactIndicators();
                break;
        }
    }

    void DlPauseResume_Click(object s, RoutedEventArgs e) => _downloads.TogglePauseResume();

    void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        if (_lastFinalPath == null) return;
        if (File.Exists(_lastFinalPath))
            Process.Start("explorer.exe", $"/select,\"{_lastFinalPath}\"");
        else if (Directory.Exists(_lastFinalPath))
            Process.Start("explorer.exe", $"\"{_lastFinalPath}\"");
    }

    void ShowTemporarily(double seconds)
    {
        Animate(true);
        _autoCollapseTimer.Stop();
        _autoCollapseTimer.Interval = TimeSpan.FromSeconds(seconds);
        _autoCollapseTimer.Start();
    }

    // ---------- Animación Premium de la Isla ----------
    void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _autoCollapseTimer.Stop();

        // Si el mouse entra por el lado izquierdo y hay descarga, muestra Descargas;
        // si entra por la derecha y hay música, muestra Música; si no, mantiene la pestaña elegida.
        Point pt = e.GetPosition(Island);
        if (pt.X < Island.ActualWidth * 0.36 && HasDownloadActivity)
            SelectTab(IslandTab.Downloads, animate: false);
        else if (pt.X > Island.ActualWidth * 0.64 && _isPlaying)
            SelectTab(IslandTab.Music, animate: false);

        Animate(true);
    }

    void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_showCompleted) Animate(false);
    }

    void Animate(bool expand)
    {
        if (expand && _hiddenByFullscreen) return;
        _expanded = expand;
        RefreshCompactIndicators();
        if (expand && _selectedTab == IslandTab.Music) UpdateProgress();

        var sizeEase = expand
            ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 }
            : new CubicEase { EasingMode = EasingMode.EaseInOut };
        var dur = TimeSpan.FromMilliseconds(expand ? 360 : 280);

        Island.BeginAnimation(WidthProperty,
            new DoubleAnimation(expand ? ExpandedW : CompactW, dur) { EasingFunction = sizeEase });
        Island.BeginAnimation(HeightProperty,
            new DoubleAnimation(expand ? ExpandedH : CompactH, dur) { EasingFunction = sizeEase });

        ExpandedPanel.IsHitTestVisible = expand;
        CompactBar.IsHitTestVisible = !expand;

        var fadeEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        ExpandedPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(expand ? 1 : 0, TimeSpan.FromMilliseconds(expand ? 240 : 110))
            {
                BeginTime = TimeSpan.FromMilliseconds(expand ? 90 : 0),
                EasingFunction = fadeEase
            });
        ExpandedSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(expand ? 0 : -6, TimeSpan.FromMilliseconds(expand ? 280 : 140))
            {
                BeginTime = TimeSpan.FromMilliseconds(expand ? 70 : 0),
                EasingFunction = fadeEase
            });

        CompactBar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(expand ? 0 : 1, TimeSpan.FromMilliseconds(expand ? 110 : 220))
            {
                BeginTime = TimeSpan.FromMilliseconds(expand ? 0 : 80),
                EasingFunction = fadeEase
            });
    }

    // ---------- Botones ----------
    async void Prev_Click(object s, RoutedEventArgs e) => await _media.Previous();
    async void Play_Click(object s, RoutedEventArgs e) => await _media.PlayPause();
    async void Next_Click(object s, RoutedEventArgs e) => await _media.Next();
    void Exit_Click(object s, RoutedEventArgs e) => ExitApp();

    // ---------- Win32 Interop (Multi-monitor DPI y pantalla completa) ----------
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const int GWL_STYLE = -16;
    const int GWL_EXSTYLE = -20;
    const long WS_MAXIMIZE = 0x01000000L;
    const long WS_CAPTION = 0x00C00000L;
    const long WS_EX_TOOLWINDOW = 0x00000080L;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    static extern long GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    static extern long SetWindowLong(IntPtr hWnd, int nIndex, long dwNewLong);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("psapi.dll")]
    static extern bool EmptyWorkingSet(IntPtr hProcess);
}
