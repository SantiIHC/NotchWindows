using System.IO;
using System.Diagnostics;
using System.Media;
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
    enum IslandTab { Summary, Music, Downloads, Games, Timer }

    static readonly SolidColorBrush s_whiteBrush = CreateFrozenBrush(0xF5, 0xF5, 0xF7);
    static readonly SolidColorBrush s_purpleBrush = CreateFrozenBrush(0xA7, 0x8B, 0xFA);
    static readonly SolidColorBrush s_riotRedBrush = CreateFrozenBrush(0xFF, 0x46, 0x55);
    static readonly SolidColorBrush s_epicBlueBrush = CreateFrozenBrush(0x38, 0xBD, 0xF8);
    static readonly SolidColorBrush s_activeTabBg = CreateFrozenBrush(0x28, 0x28, 0x2F);
    static readonly SolidColorBrush s_idleTabBg = CreateFrozenBrush(0x14, 0x14, 0x17);
    static readonly SolidColorBrush s_idleTabFg = CreateFrozenBrush(0x8E, 0x8E, 0x93);
    static readonly SolidColorBrush s_greenBrush = CreateFrozenBrush(0x30, 0xD1, 0x58);
    static readonly SolidColorBrush s_amberBrush = CreateFrozenBrush(0xFF, 0x9F, 0x0A);
    static readonly SolidColorBrush s_amberDarkBgBrush = CreateFrozenBrush(0x2A, 0x1C, 0x08);
    static readonly SolidColorBrush s_amberBorderBrush = CreateFrozenBrush(0x4D, 0x33, 0x10);
    static readonly SolidColorBrush s_defaultBorderBrush = CreateFrozenBrush(0x26, 0x26, 0x2A);
    static readonly SolidColorBrush s_presetIdleBg = CreateFrozenBrush(0x17, 0x17, 0x1C);
    static readonly SolidColorBrush s_presetIdleBorder = CreateFrozenBrush(0x29, 0x29, 0x32);
    static readonly SolidColorBrush s_redBrush = CreateFrozenBrush(0xFF, 0x45, 0x3A);
    static readonly SolidColorBrush s_grayBrush = CreateFrozenBrush(0x8E, 0x8E, 0x93);

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
    readonly GameLauncherService _games = new();
    readonly StringBuilder _classNameBuf = new(64);

    readonly DispatcherTimer _mainTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _autoCollapseTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _completedTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    Storyboard? _eqStoryboard;
    Storyboard? _expEqStoryboard;
    bool _expEqRunning;
    bool _eqRunning;
    int _trimTicks;
    int _batteryTicks;
    (bool HasBattery, int Percent, bool Charging, bool Plugged, int RemainMin)? _lastBattery;
    System.Windows.Forms.NotifyIcon? _tray;
    System.Windows.Forms.ToolStripMenuItem? _screensTrayMenu;

    string? _selectedDeviceName;
    bool _hiddenByFullscreen;

    string _lastTitle = "";
    string? _lastFinalPath;
    int _activeDownloads;
    DateTime _lastDlUpdateUtc = DateTime.UtcNow;
    bool _showCompleted;
    bool _isPlaying;
    bool _expanded;
    double _currentDlFraction;
    IslandTab _selectedTab = IslandTab.Music;
    IReadOnlyList<GameEntry> _recentGames = Array.Empty<GameEntry>();
    string? _activeSteamAppId;
    double _activeSteamFraction;
    bool _activeSteamPaused;

    // ---------- Estado del Temporizador (Fase 8) ----------
    int _timerTotalSeconds = 15 * 60;
    double _timerRemainingSeconds = 15 * 60;
    DateTime _timerEndUtc;
    bool _timerRunning;
    bool _timerPaused;
    bool _timerAlarming;
    int _alarmFlashTick;

    bool HasTimerActivity => _timerRunning || _timerPaused || _timerAlarming;

    // Tamaños de la isla (base 1.0x para pantalla de laptop ~1280 DIPs; escala suavemente en monitores grandes)
    const double BaseWinW = 550, BaseWinH = 250;
    const double BaseCompactW = 252, ActiveTimerCompactW = 292, CompactH = 36;
    const double ExpandedW = 512, MusicH = 190, DownloadH = 132, SummaryH = 132, TimerH = 178;
    double GamesH => _recentGames.Count > 1 ? 196 : 146;
    double CurrentCompactW => HasTimerActivity ? ActiveTimerCompactW : BaseCompactW;

    bool HasDownloadActivity => _activeDownloads > 0 || _showCompleted;
    double ExpandedH => _selectedTab switch
    {
        IslandTab.Downloads => DownloadH,
        IslandTab.Summary => SummaryH,
        IslandTab.Games => GamesH,
        IslandTab.Timer => TimerH,
        _ => MusicH
    };

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
        UpdateBattery();
        UpdateTimerVisuals();
        RefreshTabVisuals(animate: false);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _countdownTimer.Tick += OnCountdownTick;

        _mainTimer.Tick += (_, _) =>
        {
            UpdateClock();
            CheckFullscreen();
            CheckStaleDownload();
            if (_expanded && _selectedTab == IslandTab.Music && !_hiddenByFullscreen)
                UpdateProgress();

            if (++_batteryTicks >= 20) // Cada ~10 s (además de los eventos de energía)
            {
                _batteryTicks = 0;
                UpdateBattery();
                _games.CheckRunningGames();
            }

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
            if (!Island.IsMouseOver && !_timerAlarming) Animate(false);
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
            if (!Island.IsMouseOver && !_timerAlarming) Animate(false);
        };

        _downloads.Changed += u => Dispatcher.Invoke(() => OnDownload(u));
        _downloads.Start();

        _games.Changed += list => Dispatcher.Invoke(() => ApplyGames(list));
        _ = _games.RefreshAsync(4);

        _media.Changed += info => Dispatcher.Invoke(() => Apply(info));
        try { await _media.InitAsync(); }
        catch { TitleText.Text = "Multimedia no disponible"; }

        TrimWorkingSet();
    }

    // Red de seguridad: mientras hay una descarga activa el servicio informa cada ~0,5 s. Si pasan >4 s sin
    // ningún aviso (y no se está mostrando un "completa"), la descarga ya terminó sin que llegara el aviso
    void CheckStaleDownload()
    {
        if (_activeDownloads <= 0 || _showCompleted) return;
        if ((DateTime.UtcNow - _lastDlUpdateUtc).TotalSeconds < 4) return;

        _activeDownloads = 0;
        _activeSteamAppId = null;
        UpdateFeaturedGameStatus();
        SetRingProgress(0);
        CompactDlRing.Stroke = s_greenBrush;
        CompactDlIcon.Foreground = s_greenBrush;
        CompactDlIcon.Text = "⬇";
        ExpandedDlRing.Stroke = s_greenBrush;
        DlIcon.Foreground = s_greenBrush;
        DlIcon.Text = "⬇";
        DlTitle.Text = "Sin descargas activas";
        DlSub.Text = "Las descargas de navegador y Steam aparecerán aquí";
        DlPauseResumeBtn.Visibility = Visibility.Collapsed;
        DlProgressTrack.Visibility = Visibility.Collapsed;
        OpenBtn.Visibility = Visibility.Collapsed;
        RefreshCompactIndicators();

        if (_selectedTab == IslandTab.Downloads)
            SelectTab(_isPlaying ? IslandTab.Music : IslandTab.Summary, animate: _expanded);
        if (!Island.IsMouseOver && !_timerAlarming) Animate(false);
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
            ExpandedClockText.Text = DateTime.Now.ToString("HH:mm · ddd d MMM");
        }
    }

    void BuildEqualizerStoryboard()
    {
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(sb, 60);
        AddBarAnim(sb, EqBar1, 4, 13, 320, 0);
        AddBarAnim(sb, EqBar2, 12, 4, 280, 90);
        AddBarAnim(sb, EqBar3, 5, 14, 360, 40);
        AddBarAnim(sb, EqBar4, 11, 5, 300, 130);
        _eqStoryboard = sb;

        var xs = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(xs, 60);
        AddBarAnim(xs, XEqBar1, 5, 16, 320, 0);
        AddBarAnim(xs, XEqBar2, 15, 5, 280, 90);
        AddBarAnim(xs, XEqBar3, 6, 17, 360, 40);
        AddBarAnim(xs, XEqBar4, 14, 6, 300, 130);
        _expEqStoryboard = xs;
    }

    void UpdateExpandedEq()
    {
        if (_expEqStoryboard == null) return;
        bool should = _isPlaying && _expanded && _selectedTab == IslandTab.Music && !_hiddenByFullscreen;
        if (should == _expEqRunning) return;
        _expEqRunning = should;
        if (should) _expEqStoryboard.Begin(this, true);
        else _expEqStoryboard.Stop(this);
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

        const double compactTotal = 23.04;
        const double expandedTotal = 43.53;

        double cDash = compactTotal * _currentDlFraction;
        CompactDlRing.StrokeDashArray = new DoubleCollection { cDash, 100 };

        double eDash = expandedTotal * _currentDlFraction;
        ExpandedDlRing.StrokeDashArray = new DoubleCollection { eDash, 100 };
    }

    static DoubleAnimation CreateFluidAnim(double to, double ms, IEasingFunction? ease = null, double beginMs = 0)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
            BeginTime = TimeSpan.FromMilliseconds(beginMs)
        };
        Timeline.SetDesiredFrameRate(anim, 120);
        return anim;
    }

    static DoubleAnimation CreateFluidAnimFromTo(double from, double to, double ms, IEasingFunction? ease = null)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease
        };
        Timeline.SetDesiredFrameRate(anim, 120);
        return anim;
    }

    void RefreshCompactIndicators(bool animateCompactWidth = true)
    {
        bool hasDl = HasDownloadActivity;
        bool hasTimer = HasTimerActivity;
        bool bothLeft = hasDl && hasTimer;

        CompactDlIndicator.Visibility = hasDl ? Visibility.Visible : Visibility.Collapsed;
        CompactTimerIndicator.Visibility = hasTimer ? Visibility.Visible : Visibility.Collapsed;

        // se oculta el icono
        CompactTimerIcon.Visibility = bothLeft ? Visibility.Collapsed : Visibility.Visible;
        CompactTimerRingHost.Visibility = bothLeft ? Visibility.Collapsed : Visibility.Visible;

        // Columnas izquierda y derecha idénticas para que la hora central jamás se mueva del centro
        double sideColW = hasTimer ? 110 : 94;
        CompactColLeft.Width = new GridLength(sideColW);
        CompactColRight.Width = new GridLength(sideColW);

        SetEqualizerRunning(_isPlaying);
        UpdateExpandedEq();

        if (!_expanded && animateCompactWidth && Math.Abs(Island.ActualWidth - CurrentCompactW) > 0.5)
        {
            var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
            Island.BeginAnimation(WidthProperty, CreateFluidAnim(CurrentCompactW, 220, ease));
        }
    }

    // ---------- Selección de pestaña (Click en iconos o pestañas) ----------
    void SelectTab(IslandTab tab, bool animate)
    {
        bool changed = _selectedTab != tab;
        _selectedTab = tab;
        if (tab == IslandTab.Games)
        {
            ApplyGames(_games.GetRecentGames(4));
            _ = _games.RefreshAsync(4);
        }
        RefreshTabVisuals(animate && changed);
    }

    void RefreshTabVisuals(bool animate)
    {
        bool isSummary = _selectedTab == IslandTab.Summary;
        bool isMusic = _selectedTab == IslandTab.Music;
        bool isDl = _selectedTab == IslandTab.Downloads;
        bool isGames = _selectedTab == IslandTab.Games;
        bool isTimer = _selectedTab == IslandTab.Timer;

        SummaryPanel.Visibility = isSummary ? Visibility.Visible : Visibility.Collapsed;
        MusicPanel.Visibility = isMusic ? Visibility.Visible : Visibility.Collapsed;
        DownloadPanel.Visibility = isDl ? Visibility.Visible : Visibility.Collapsed;
        GamesPanel.Visibility = isGames ? Visibility.Visible : Visibility.Collapsed;
        TimerPanel.Visibility = isTimer ? Visibility.Visible : Visibility.Collapsed;

        TabSummaryBtn.Background = isSummary ? s_activeTabBg : s_idleTabBg;
        TabSummaryBtn.Foreground = isSummary ? Brushes.White : s_idleTabFg;
        TabMusicBtn.Background = isMusic ? s_activeTabBg : s_idleTabBg;
        TabMusicBtn.Foreground = isMusic ? Brushes.White : s_idleTabFg;
        TabDlBtn.Background = isDl ? s_activeTabBg : s_idleTabBg;
        TabDlBtn.Foreground = isDl ? Brushes.White : s_idleTabFg;
        TabGamesBtn.Background = isGames ? s_activeTabBg : s_idleTabBg;
        TabGamesBtn.Foreground = isGames ? Brushes.White : s_idleTabFg;

        TabTimerBtn.Background = isTimer ? s_amberDarkBgBrush : s_idleTabBg;
        TabTimerBtn.BorderBrush = isTimer ? s_amberBrush : s_defaultBorderBrush;
        TabTimerBtn.Foreground = isTimer ? Brushes.White : s_idleTabFg;

        UpdateExpandedEq();

        if (animate)
        {
            var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
            ContentHost.BeginAnimation(OpacityProperty, CreateFluidAnimFromTo(0.15, 1.0, 190, ease));
            ContentSlide.BeginAnimation(TranslateTransform.YProperty, CreateFluidAnimFromTo(4.5, 0.0, 210, ease));
        }

        if (_expanded) Animate(true);
    }

    void CompactDl_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SelectTab(IslandTab.Downloads, animate: _expanded);
        Animate(true);
    }

    void CompactTimer_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SelectTab(IslandTab.Timer, animate: _expanded);
        Animate(true);
    }

    void CompactMusic_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SelectTab(IslandTab.Music, animate: _expanded);
        Animate(true);
    }

    void Clock_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_timerAlarming || (HasTimerActivity && !_isPlaying && !HasDownloadActivity))
            SelectTab(IslandTab.Timer, animate: _expanded);
        else if (HasDownloadActivity && !_isPlaying)
            SelectTab(IslandTab.Downloads, animate: _expanded);
        else if (_isPlaying)
            SelectTab(IslandTab.Music, animate: _expanded);
        else
            SelectTab(IslandTab.Summary, animate: _expanded);
        Animate(true);
    }

    void CompactBattery_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SelectTab(IslandTab.Summary, animate: _expanded);
        Animate(true);
    }

    void TabSummary_Click(object sender, RoutedEventArgs e) => SelectTab(IslandTab.Summary, animate: true);
    void TabMusic_Click(object sender, RoutedEventArgs e) => SelectTab(IslandTab.Music, animate: true);
    void TabDl_Click(object sender, RoutedEventArgs e) => SelectTab(IslandTab.Downloads, animate: true);
    void TabGames_Click(object sender, RoutedEventArgs e) => SelectTab(IslandTab.Games, animate: true);
    void TabTimer_Click(object sender, RoutedEventArgs e) => SelectTab(IslandTab.Timer, animate: true);

    // ---------- Temporizador inmersivo (Fase 8) ----------
    void OnCountdownTick(object? sender, EventArgs e)
    {
        if (_timerAlarming)
        {
            _alarmFlashTick++;
            bool flash = (_alarmFlashTick % 2) == 0;
            IslandBorderStop0.Color = flash ? Color.FromRgb(0xFF, 0x9F, 0x0A) : Color.FromRgb(0x1F, 0x1F, 0x24);
            IslandBorderStop1.Color = flash ? Color.FromRgb(0xFF, 0x6B, 0x00) : Color.FromRgb(0x2E, 0x2E, 0x36);
            ExpandedTimerRing.Stroke = flash ? s_amberBrush : s_redBrush;

            if (_alarmFlashTick % 10 == 0)
            {
                try { SystemSounds.Exclamation.Play(); } catch { }
            }
            return;
        }

        if (!_timerRunning) return;

        double rem = (_timerEndUtc - DateTime.UtcNow).TotalSeconds;
        if (rem <= 0)
        {
            _timerRemainingSeconds = 0;
            _timerRunning = false;
            _timerPaused = false;
            _timerAlarming = true;
            _alarmFlashTick = 0;
            CompactTimerScale.ScaleX = 1.0;
            CompactTimerScale.ScaleY = 1.0;

            UpdateTimerVisuals();
            RefreshCompactIndicators();

            try { SystemSounds.Exclamation.Play(); } catch { }

            SelectTab(IslandTab.Timer, animate: false);
            _autoCollapseTimer.Stop();
            Animate(true);
            return;
        }

        _timerRemainingSeconds = rem;
        UpdateTimerVisuals();

        // Latido suave en reposo cuando quedan ≤ 10 segundos
        if (rem <= 10 && !_expanded)
        {
            double pulse = 1.0 + 0.05 * Math.Abs(Math.Sin(rem * Math.PI));
            CompactTimerScale.ScaleX = pulse;
            CompactTimerScale.ScaleY = pulse;
        }
        else
        {
            CompactTimerScale.ScaleX = 1.0;
            CompactTimerScale.ScaleY = 1.0;
        }
    }

    void UpdateTimerVisuals()
    {
        int dispSec = (int)Math.Ceiling(Math.Max(0, _timerRemainingSeconds));
        int mins = dispSec / 60;
        int secs = dispSec % 60;
        string formatted = $"{mins:00}:{secs:00}";

        TimerBigMinText.Text = mins.ToString("00");
        TimerBigSecText.Text = secs.ToString("00");
        CompactTimerText.Text = formatted;
        TimerRingTimeText.Text = formatted;

        double frac = _timerTotalSeconds > 0
            ? Math.Clamp(_timerRemainingSeconds / _timerTotalSeconds, 0.0, 1.0)
            : 0.0;

        // Circunferencia en unidades de StrokeThickness:
        // CompactTimerRing: (14 - 2) * pi / 2 ≈ 18.85
        // ExpandedTimerRing: (80 - 5) * pi / 5 ≈ 47.12
        const double compactCirc = 18.85;
        const double expandedCirc = 47.12;

        CompactTimerRing.StrokeDashArray = new DoubleCollection { compactCirc * frac, 100 };
        ExpandedTimerRing.StrokeDashArray = new DoubleCollection { expandedCirc * (_timerAlarming ? 1.0 : frac), 100 };

        if (_timerAlarming)
        {
            TimerRingStateText.Text = "¡TERMINADO!";
            TimerRingStateText.Foreground = s_redBrush;
            TimerPrimaryBtnText.Text = "↻ Repetir";
            TimerResetBtn.Visibility = Visibility.Visible;
        }
        else if (_timerRunning)
        {
            TimerRingStateText.Text = "ACTIVO";
            TimerRingStateText.Foreground = s_amberBrush;
            ExpandedTimerRing.Stroke = s_amberBrush;
            TimerPrimaryBtnText.Text = "⏸ Pausar";
            TimerResetBtn.Visibility = Visibility.Visible;
        }
        else if (_timerPaused)
        {
            TimerRingStateText.Text = "EN PAUSA";
            TimerRingStateText.Foreground = s_grayBrush;
            ExpandedTimerRing.Stroke = s_amberBrush;
            TimerPrimaryBtnText.Text = "▶ Reanudar";
            TimerResetBtn.Visibility = Visibility.Visible;
        }
        else
        {
            TimerRingStateText.Text = "LISTO";
            TimerRingStateText.Foreground = s_amberBrush;
            ExpandedTimerRing.Stroke = s_amberBrush;
            TimerPrimaryBtnText.Text = "▶ Iniciar";
            TimerResetBtn.Visibility = Visibility.Collapsed;
        }

        HighlightMatchingPreset();
    }

    void HighlightMatchingPreset()
    {
        bool idle = !_timerRunning && !_timerPaused && !_timerAlarming;
        int activePresetMin = idle && (_timerTotalSeconds % 60 == 0) ? _timerTotalSeconds / 60 : -1;

        var buttons = new (Button Btn, int Min)[]
        {
            (Preset3Btn, 3),
            (Preset5Btn, 5),
            (Preset10Btn, 10),
            (Preset15Btn, 15),
            (Preset30Btn, 30)
        };

        foreach (var (btn, m) in buttons)
        {
            bool sel = m == activePresetMin;
            btn.Background = sel ? s_amberDarkBgBrush : s_presetIdleBg;
            btn.BorderBrush = sel ? s_amberBrush : s_presetIdleBorder;
            btn.Foreground = sel ? Brushes.White : s_whiteBrush;
        }
    }

    void StopAlarmGlow()
    {
        _timerAlarming = false;
        CompactTimerScale.ScaleX = 1.0;
        CompactTimerScale.ScaleY = 1.0;
        IslandBorderStop0.Color = Color.FromRgb(0x1F, 0x1F, 0x24);
        IslandBorderStop1.Color = Color.FromRgb(0x2E, 0x2E, 0x36);
    }

    void AdjustTimerSeconds(int deltaSeconds)
    {
        StopAlarmGlow();
        int baseSec = (_timerRunning || _timerPaused)
            ? (int)Math.Ceiling(_timerRemainingSeconds)
            : _timerTotalSeconds;

        int next = Math.Clamp(baseSec + deltaSeconds, 15, 99 * 60 + 45);
        _timerTotalSeconds = next;
        _timerRemainingSeconds = next;

        if (_timerRunning)
            _timerEndUtc = DateTime.UtcNow.AddSeconds(next);

        UpdateTimerVisuals();
        RefreshCompactIndicators();
    }

    void TimerMin_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        AdjustTimerSeconds(e.Delta > 0 ? 60 : -60);
    }

    void TimerSec_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        AdjustTimerSeconds(e.Delta > 0 ? 15 : -15);
    }

    void TimerPanel_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        AdjustTimerSeconds(e.Delta > 0 ? 60 : -60);
    }

    void TimerMinUp_Click(object sender, RoutedEventArgs e) => AdjustTimerSeconds(60);
    void TimerMinDown_Click(object sender, RoutedEventArgs e) => AdjustTimerSeconds(-60);
    void TimerSecUp_Click(object sender, RoutedEventArgs e) => AdjustTimerSeconds(15);
    void TimerSecDown_Click(object sender, RoutedEventArgs e) => AdjustTimerSeconds(-15);

    void TimerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !int.TryParse(btn.Tag?.ToString(), out int mins)) return;

        StopAlarmGlow();
        _timerRunning = false;
        _timerPaused = false;
        _countdownTimer.Stop();

        _timerTotalSeconds = mins * 60;
        _timerRemainingSeconds = _timerTotalSeconds;
        UpdateTimerVisuals();
        RefreshCompactIndicators();
    }

    void TimerPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_timerAlarming)
        {
            StopAlarmGlow();
            _timerRemainingSeconds = _timerTotalSeconds;
            _timerEndUtc = DateTime.UtcNow.AddSeconds(_timerRemainingSeconds);
            _timerRunning = true;
            _timerPaused = false;
            _countdownTimer.Start();
            UpdateTimerVisuals();
            RefreshCompactIndicators();
            return;
        }

        if (_timerRunning)
        {
            // Pausar
            _timerRemainingSeconds = Math.Max(0, (_timerEndUtc - DateTime.UtcNow).TotalSeconds);
            _timerRunning = false;
            _timerPaused = true;
            _countdownTimer.Stop();
        }
        else
        {
            // Iniciar o Reanudar
            if (_timerRemainingSeconds <= 0)
                _timerRemainingSeconds = _timerTotalSeconds;
            _timerEndUtc = DateTime.UtcNow.AddSeconds(_timerRemainingSeconds);
            _timerRunning = true;
            _timerPaused = false;
            _countdownTimer.Start();
        }

        UpdateTimerVisuals();
        RefreshCompactIndicators();
    }

    void TimerReset_Click(object sender, RoutedEventArgs e)
    {
        StopAlarmGlow();
        _timerRunning = false;
        _timerPaused = false;
        _countdownTimer.Stop();
        _timerRemainingSeconds = _timerTotalSeconds;
        UpdateTimerVisuals();
        RefreshCompactIndicators();
    }

    // ---------- Juegos recientes (Fase 7) ----------
    void ApplyGames(IReadOnlyList<GameEntry> games)
    {
        bool countChanged = (_recentGames.Count > 1) != (games.Count > 1);
        _recentGames = games;

        if (games.Count == 0)
        {
            FeaturedGameCover.Source = null;
            FeaturedGameBadge.Text = "JUEGOS";
            FeaturedGameBadge.Foreground = s_purpleBrush;
            FeaturedGameTitle.Text = "Sin juegos detectados";
            FeaturedGameSub.Text = "Tus juegos de Steam, Epic y Riot aparecerán aquí";
            FeaturedGameDlTrack.Visibility = Visibility.Collapsed;
            FeaturedPlayBtn.Visibility = Visibility.Collapsed;
            RecentGamesRow.Visibility = Visibility.Collapsed;
            if (countChanged && _expanded && _selectedTab == IslandTab.Games) Animate(true);
            return;
        }

        var feat = games[0];
        FeaturedGameCover.Source = feat.Cover;
        FeaturedGameTitle.Text = feat.Name;
        FeaturedPlayBtn.Visibility = Visibility.Visible;
        UpdateFeaturedGameStatus();

        RecentGamesRow.Visibility = games.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        BindRecentSlot(games, 1, RecentGameBtn1, RecentGameImg1, RecentGameName1);
        BindRecentSlot(games, 2, RecentGameBtn2, RecentGameImg2, RecentGameName2);
        BindRecentSlot(games, 3, RecentGameBtn3, RecentGameImg3, RecentGameName3);

        if (countChanged && _expanded && _selectedTab == IslandTab.Games) Animate(true);
    }

    static void BindRecentSlot(IReadOnlyList<GameEntry> games, int idx, Button btn, Image img, TextBlock title)
    {
        if (idx >= games.Count)
        {
            btn.Visibility = Visibility.Collapsed;
            return;
        }
        var g = games[idx];
        btn.Visibility = Visibility.Visible;
        btn.ToolTip = $"Jugar {g.Name} ({GameLauncherService.FormatRelativeTime(g.LastPlayedUtc, g.HasRealLastPlayed, g.Platform)})";
        img.Source = g.Cover;
        title.Text = g.Name;
    }

    SolidColorBrush GetPlatformBrush(string platform) => platform switch
    {
        "Riot" => s_riotRedBrush,
        "Epic" => s_epicBlueBrush,
        _ => s_purpleBrush
    };

    void UpdateFeaturedGameStatus()
    {
        if (_recentGames.Count == 0) return;
        var feat = _recentGames[0];
        bool isUpdatingThisGame = !string.IsNullOrEmpty(_activeSteamAppId) &&
                                  string.Equals(_activeSteamAppId, feat.Id, StringComparison.OrdinalIgnoreCase);

        if (isUpdatingThisGame)
        {
            var accent = _activeSteamPaused ? s_amberBrush : s_greenBrush;
            int pct = (int)Math.Round(_activeSteamFraction * 100);
            string platUpper = feat.Platform.ToUpperInvariant();
            FeaturedGameBadge.Text = _activeSteamPaused ? $"EN PAUSA · {platUpper}" : $"ACTUALIZANDO EN {platUpper}";
            FeaturedGameBadge.Foreground = accent;
            FeaturedGameSub.Text = $"Descarga en curso · {pct}%";
            FeaturedGameDlTrack.Visibility = Visibility.Visible;
            FeaturedGameDlFill.Background = accent;
            double trackW = FeaturedGameDlTrack.ActualWidth > 0 ? FeaturedGameDlTrack.ActualWidth : 190;
            FeaturedGameDlFill.Width = trackW * Math.Clamp(_activeSteamFraction, 0.04, 1.0);
            FeaturedPlayBtn.Background = accent;
            FeaturedPlayBtnText.Text = "Ver ⬇";
            FeaturedPlayBtn.ToolTip = "Ver progreso de la actualización";
        }
        else
        {
            var platBrush = GetPlatformBrush(feat.Platform);
            FeaturedGameBadge.Text = $"ÚLTIMO JUGADO · {feat.Platform.ToUpperInvariant()}";
            FeaturedGameBadge.Foreground = platBrush;
            FeaturedGameSub.Text = GameLauncherService.FormatRelativeTime(feat.LastPlayedUtc, feat.HasRealLastPlayed, feat.Platform);
            FeaturedGameDlTrack.Visibility = Visibility.Collapsed;
            FeaturedPlayBtn.Background = platBrush;
            FeaturedPlayBtnText.Text = "Jugar ▶";
            FeaturedPlayBtn.ToolTip = $"Ejecutar {feat.Name} ({feat.Platform})";
        }
    }

    void FeaturedGamePlay_Click(object sender, RoutedEventArgs e)
    {
        if (_recentGames.Count == 0) return;
        var feat = _recentGames[0];
        if (!string.IsNullOrEmpty(_activeSteamAppId) &&
            string.Equals(_activeSteamAppId, feat.Id, StringComparison.OrdinalIgnoreCase))
        {
            SelectTab(IslandTab.Downloads, animate: true);
            return;
        }
        _games.Launch(feat);
        Animate(false);
    }

    void RecentGame1_Click(object sender, RoutedEventArgs e) => LaunchRecentSlot(1);
    void RecentGame2_Click(object sender, RoutedEventArgs e) => LaunchRecentSlot(2);
    void RecentGame3_Click(object sender, RoutedEventArgs e) => LaunchRecentSlot(3);

    void LaunchRecentSlot(int idx)
    {
        if (idx >= _recentGames.Count) return;
        _games.Launch(_recentGames[idx]);
        Animate(false);
    }

    // ---------- Batería ----------
    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateBattery);

    void UpdateBattery()
    {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        var status = ps.BatteryChargeStatus;
        bool hasBattery = (status & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0;

        int pct = 0, remainMin = -1;
        bool charging = false, plugged = false;
        if (hasBattery)
        {
            pct = (int)Math.Round(Math.Clamp(ps.BatteryLifePercent, 0f, 1f) * 100);
            charging = (status & System.Windows.Forms.BatteryChargeStatus.Charging) != 0;
            plugged = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            int secs = ps.BatteryLifeRemaining;
            remainMin = secs > 0 && !plugged ? secs / 60 : -1;
        }

        var key = (hasBattery, pct, charging, plugged, remainMin);
        if (_lastBattery == key) return;
        _lastBattery = key;

        CompactBatteryIndicator.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;

        if (!hasBattery)
        {
            BatteryRingText.Text = "--";
            BatteryRing.StrokeDashArray = new DoubleCollection { 0, 100 };
            BatteryTitle.Text = "Sin batería";
            BatterySub.Text = "Este equipo funciona solo con corriente";
            SetBatteryBar(0, s_grayBrush);
            return;
        }

        var brush = charging ? s_greenBrush
                  : pct <= 10 ? s_redBrush
                  : pct <= 20 ? s_amberBrush
                  : s_whiteBrush;
        double frac = pct / 100.0;

        CompactBatteryText.Text = pct + "%";
        CompactBatteryText.Foreground = brush;
        CompactBatteryFill.Background = brush;
        CompactBatteryFill.Width = Math.Max(1.6, 15.6 * frac);
        CompactBatteryIndicator.ToolTip = $"Batería {pct}%" + (charging ? " · cargando" : "");

        BatteryRingText.Text = pct.ToString();
        BatteryRing.Stroke = brush == s_whiteBrush ? s_greenBrush : brush;
        BatteryRing.StrokeDashArray = new DoubleCollection { 43.53 * frac, 100 };
        SetBatteryBar(frac, brush == s_whiteBrush ? s_greenBrush : brush);

        string state;
        if (charging) state = "Cargando";
        else if (plugged) state = pct >= 100 ? "Cargada" : "Conectada";
        else state = "En batería";
        BatteryTitle.Text = $"{pct}% · {state}";

        if (plugged)
            BatterySub.Text = charging ? "Conectada a la corriente" : (pct >= 100 ? "Batería completa" : "Conectada, sin cargar");
        else if (remainMin >= 0)
            BatterySub.Text = "Tiempo restante: " + (remainMin >= 60 ? $"{remainMin / 60} h {remainMin % 60} min" : $"{remainMin} min");
        else
            BatterySub.Text = "Tiempo restante no disponible";
    }

    void SetBatteryBar(double fraction, Brush brush)
    {
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        BatteryBarCol0.Width = new GridLength(fraction, GridUnitType.Star);
        BatteryBarCol1.Width = new GridLength(1.0 - fraction, GridUnitType.Star);
        BatteryBarFill.Background = brush;
    }

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

        double logicalScreenW = bounds.Width / scaleX;
        double uiScale = Math.Clamp(1.0 + Math.Max(0, (logicalScreenW - 1320.0) / 1320.0) * 0.45, 1.0, 1.38);

        IslandScale.ScaleX = uiScale;
        IslandScale.ScaleY = uiScale;
        Width = Math.Ceiling(BaseWinW * uiScale);
        Height = Math.Ceiling(BaseWinH * uiScale);

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
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _countdownTimer.Stop();
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
        CoverStateDot.Fill = _isPlaying ? s_greenBrush : s_grayBrush;

        RefreshCompactIndicators();

        if (_isPlaying && info.Title != _lastTitle && !HasDownloadActivity && !_timerAlarming)
        {
            SelectTab(IslandTab.Music, animate: false);
            ShowTemporarily(3);
        }

        _lastTitle = info.Title;
    }

    void UpdateProgress()
    {
        var (pos, total, ok) = _media.GetTimes();
        ProgressRow.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        if (!ok) return;
        ProgressFill.Width = ProgressTrack.ActualWidth * (pos / total);
        ElapsedText.Text = FormatTime(pos);
        TotalText.Text = FormatTime(total);
    }

    static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
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
                _activeSteamAppId = u.AppId;
                _activeSteamFraction = frac;
                _activeSteamPaused = paused;
                UpdateFeaturedGameStatus();
                CompactDlIcon.Text = paused ? "⏸" : "⬇";
                DlIcon.Text = paused ? "⏸" : "⬇";
                OpenBtn.Visibility = Visibility.Collapsed;
                _showCompleted = false;
                _completedTimer.Stop();
                _lastDlUpdateUtc = DateTime.UtcNow;
                RefreshCompactIndicators();

                if (wasIdle && !paused && !_timerAlarming)
                {
                    SelectTab(IslandTab.Downloads, animate: false);
                    ShowTemporarily(2.8);
                }
                break;

            case DownloadStatus.Completed:
                _activeSteamAppId = null;
                UpdateFeaturedGameStatus();
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
                _activeSteamAppId = null;
                UpdateFeaturedGameStatus();
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

    DispatcherTimer? _cancelArmTimer;
    bool _cancelArmed;

    void DlCancel_Click(object s, RoutedEventArgs e)
    {
        if (_cancelArmed)
        {
            DisarmCancel();
            _downloads.CancelCurrent();
            return;
        }

        _cancelArmed = true;
        DlCancelPath.Stroke = s_redBrush;
        DlCancelBtn.ToolTip = "Pulsa otra vez para cancelar";

        _cancelArmTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _cancelArmTimer.Tick -= CancelArmTimeout;
        _cancelArmTimer.Tick += CancelArmTimeout;
        _cancelArmTimer.Stop();
        _cancelArmTimer.Start();
    }

    void CancelArmTimeout(object? s, EventArgs e) => DisarmCancel();

    void DisarmCancel()
    {
        _cancelArmed = false;
        _cancelArmTimer?.Stop();
        DlCancelPath.Stroke = s_grayBrush;
        DlCancelBtn.ToolTip = "Cancelar descarga";
    }

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

        Point pt = e.GetPosition(Island);
        if (_timerAlarming)
        {
            SelectTab(IslandTab.Timer, animate: false);
        }
        else if (pt.X < Island.ActualWidth * 0.36)
        {
            if (HasTimerActivity && !HasDownloadActivity)
                SelectTab(IslandTab.Timer, animate: false);
            else if (HasDownloadActivity)
                SelectTab(IslandTab.Downloads, animate: false);
        }
        else if (pt.X > Island.ActualWidth * 0.64 && _isPlaying)
        {
            SelectTab(IslandTab.Music, animate: false);
        }
        else if (!_isPlaying && !HasDownloadActivity && !HasTimerActivity && _selectedTab == IslandTab.Music)
        {
            SelectTab(IslandTab.Summary, animate: false);
        }

        Animate(true);
    }

    void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_showCompleted && !_timerAlarming) Animate(false);
    }

    void Animate(bool expand)
    {
        if (expand && _hiddenByFullscreen) return;
        bool stateChanged = _expanded != expand;
        _expanded = expand;
        RefreshCompactIndicators(animateCompactWidth: false);
        if (expand && _selectedTab == IslandTab.Music) UpdateProgress();

        // Si ya estaba expandida y solo cambió de pestaña (altura), deslizar tamaño suavemente sin reiniciar opacidades
        if (!stateChanged && expand)
        {
            var tabResizeEase = new QuarticEase { EasingMode = EasingMode.EaseOut };
            Island.BeginAnimation(WidthProperty, CreateFluidAnim(ExpandedW, 220, tabResizeEase));
            Island.BeginAnimation(HeightProperty, CreateFluidAnim(ExpandedH, 230, tabResizeEase));
            return;
        }

        var sizeEase = expand
            ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.11 }
            : new QuarticEase { EasingMode = EasingMode.EaseOut };
        double durMs = expand ? 310 : 240;

        Island.BeginAnimation(WidthProperty, CreateFluidAnim(expand ? ExpandedW : CurrentCompactW, durMs, sizeEase));
        Island.BeginAnimation(HeightProperty, CreateFluidAnim(expand ? ExpandedH : CompactH, durMs, sizeEase));

        ExpandedPanel.IsHitTestVisible = expand;
        CompactBar.IsHitTestVisible = !expand;

        var fadeEase = new QuarticEase { EasingMode = EasingMode.EaseOut };
        ExpandedPanel.BeginAnimation(OpacityProperty,
            CreateFluidAnim(expand ? 1 : 0, expand ? 200 : 95, fadeEase, expand ? 45 : 0));
        ExpandedSlide.BeginAnimation(TranslateTransform.YProperty,
            CreateFluidAnim(expand ? 0 : -5, expand ? 240 : 120, fadeEase, expand ? 35 : 0));

        CompactBar.BeginAnimation(OpacityProperty,
            CreateFluidAnim(expand ? 0 : 1, expand ? 85 : 190, fadeEase, expand ? 0 : 60));
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