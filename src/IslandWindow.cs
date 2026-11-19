using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Path = System.Windows.Shapes.Path;

namespace Isle
{
    /// <summary>
    /// The island: one transparent, never-focused, topmost window. While it is small it is fully click-through
    /// (clicks land on the Chrome tab underneath); it opens when the cursor rests on it. The black shape is a Path
    /// rebuilt from three springs (width, height, corner radius) only while it moves, so an idle island costs nothing.
    /// </summary>
    sealed class IslandWindow : Window
    {
        const double ExpandedWidth = 520, Ear = 10, ExpandedRadius = 34;
        static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58), Orange = Color.FromRgb(0xFF, 0x9F, 0x0A), Red = Color.FromRgb(0xFF, 0x5A, 0x5A);
        const int HotkeyId = 0x1515;

        Settings _cfg;
        readonly MediaService _media;
        readonly AudioService _audio;
        readonly CallService _calls;
        readonly Shelf _shelf;
        readonly BrightnessWatcher _brightness;
        readonly PhoneService _phone;
        IntPtr _hwnd;
        string _dropTarget = "keep";
        DispatcherTimer _phoneFlash;
        static readonly Color Gray = Color.FromRgb(0x8E, 0x8E, 0x93);

        enum Mode { Compact, Expanded }
        Mode _mode = Mode.Compact;
        bool _hasMedia, _hudActive, _peekActive, _dragging, _volDragging, _hiddenForFullscreen, _animating;
        bool _nudge, _dropActive, _forcedOpen, _cursorVisited, _suppressDwell, _dwellWithButton, _shelfDragged;
        DateTime? _dwellStart;
        DateTime _openedAt, _cursorLeftAt = DateTime.MinValue;
        double _hudWidth = 300, _expandedH = 220;
        string _compactView = "", _hudKind = "", _expandedKind = "";
        bool? _lastOnAc;

        readonly Spring _w = new Spring(), _h = new Spring(), _r = new Spring();
        TimeSpan _lastTick;

        readonly SolidColorBrush _accent = new SolidColorBrush(MediaService.DefaultAccent);
        // the unplayed part of the seek / volume bars: the same album colour, faded
        readonly SolidColorBrush _accentTrack = new SolidColorBrush(TrackTint(MediaService.DefaultAccent));
        static Color TrackTint(Color c) => Color.FromArgb(0x47, c.R, c.G, c.B);
        Color _accentTarget = MediaService.DefaultAccent;
        readonly List<ScaleTransform> _compactBarScales = new List<ScaleTransform>(), _expBarScales = new List<ScaleTransform>();
        readonly double[] _levels = new double[4];
        static readonly double[] BarFreq = { 7.1, 9.7, 6.3, 11.3 }, BarPhase = { 0.0, 1.7, 3.1, 4.6 };
        double _meterT;
        readonly string _timeFmt;
        readonly BlurEffect _compactBlur = new BlurEffect { RenderingBias = RenderingBias.Performance };
        readonly BlurEffect _expandedBlur = new BlurEffect { RenderingBias = RenderingBias.Performance };
        readonly TranslateTransform _dotShift = new TranslateTransform();
        readonly ScaleTransform _acceptPulse = new ScaleTransform(1, 1);

        DispatcherTimer _hudTimer, _peekTimer, _progressTimer, _meterTimer, _clockTimer, _fsTimer, _cursorTimer, _callTimer, _dropLeaveTimer;

        // XAML parts
        FrameworkElement _root;
        Grid _islandRoot, _body, _compact, _compactMedia, _compactCall, _hud, _hudBar, _expMedia, _expIdle, _expIncoming, _expCall, _expDrop,
             _shelfArea, _progressRow, _seekArea, _volArea;
        Path _shape, _outline, _playIcon, _pauseIcon;
        Ellipse _privacyDot;
        Image _ambient, _incomingAppIcon, _callIcon;
        // frosted glass behind the open island
        Image _glassImage;
        UIElement _glassHost, _glassTint, _glassSheen;
        GlassBackdrop _glass;
        double _glassW;
        StackPanel _expanded, _appsRow, _compactBars, _expBars, _batteryBlock, _shelfRow;
        TextBlock _idleClock, _compactTitle, _hudIcon, _hudLabel, _hudValue, _title, _artist, _sourceText, _sourceSwitch,
                  _elapsed, _remaining, _bigClock, _dateText, _batteryPct, _batteryBolt, _batteryState, _callCompactIcon, _callCompactTimer,
                  _incomingInitials, _incomingName, _incomingSub, _callIconGlyph, _callTitle, _callStatus, _micChipText, _muteLabel;
        Border _compactArt, _artLarge, _artPlaceholder, _seekTrack, _seekFill, _volTrack, _volFill, _appsDivider, _battFill, _camChip;
        ColumnDefinition _seekDone, _seekLeft, _volDone, _volLeft, _battDone, _battLeft, _hudDone, _hudLeft;
        Button _playButton, _prevButton, _nextButton, _volButton, _sourceChip, _artButton, _openAppButton,
               _acceptButton, _declineButton, _muteButton, _callOpenButton, _endButton, _shelfClear;
        ScaleTransform _expScale;
        Border _phoneRow;
        TextBlock _phoneName, _phoneStatus, _dropSendText;
        Ellipse _phoneDot;
        StackPanel _phoneInfo;
        Grid _dropSend;
        Rectangle _dropKeepBox, _dropSendBox;
        ColumnDefinition _dropSendCol;
        Button _phoneRingButton, _phoneClipButton, _phoneSendButton, _phoneSmsButton, _phoneLinkButton, _shelfSend;
        Grid _expBuds, _budsRingBox;
        TextBlock _budsName, _budsSub, _budsPct;
        Path _budsRing;
        Border _budsIconBox;
        bool _budsActive;
        DispatcherTimer _budsTimer;
        // earbuds status while connected
        Border _budsRow, _budsRowArt;
        TextBlock _budsRowName, _budsRowStatus, _budsRowPct;
        Path _budsRowRing;
        string _budsConnectedName = "";
        int _budsBattery = -1;
        bool _budsLowWarned;
        DispatcherTimer _budsPoll;
        Native.LowLevelKeyboardProc _keyProc;
        IntPtr _keyHook;

        public Settings Settings => _cfg;
        bool IsNotch => _cfg.IsNotch;
        double CompactH => IsNotch ? 32 : 34;
        double IslandTop => IsNotch ? 0 : 7;
        CallInfo Call => _calls.Call;
        bool InCall => Call != null && Call.Phase == CallPhase.Active;
        bool Ringing => Call != null && Call.Phase == CallPhase.Incoming;

        public IslandWindow(Settings cfg, MediaService media, AudioService audio, CallService calls, Shelf shelf, BrightnessWatcher brightness, PhoneService phone)
        {
            _cfg = cfg;
            _media = media;
            _audio = audio;
            _calls = calls;
            _shelf = shelf;
            _brightness = brightness;
            _phone = phone;

            Title = "Isle";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            AllowDrop = true;
            Width = ExpandedWidth + 2 * Ear + 60;
            Height = 430;

            _timeFmt = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H") ? "H:mm" : "h:mm";

            LoadParts();
            Content = _root;
            CreateTimers();
            WireEvents();

            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeOverlay(_hwnd);
                Native.SetClickThrough(_hwnd, true);
                HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
                Native.AddClipboardFormatListener(_hwnd);   // to notice text arriving from the phone
                // Ctrl + Alt + I opens / closes the island
                if (!Native.RegisterHotKey(_hwnd, HotkeyId, 0x1 | 0x2 | 0x4000, 0x49)) App.Log(new Exception("Ctrl+Alt+I is already taken by another app"));
            };
            Closed += (s, e) =>
            {
                if (_hwnd == IntPtr.Zero) return;
                Native.UnregisterHotKey(_hwnd, HotkeyId);
                Native.RemoveClipboardFormatListener(_hwnd);
            };
            Loaded += OnLoaded;
            PlaceOnScreen();
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && wParam.ToInt32() == HotkeyId) { ToggleExpanded(); handled = true; }
            else if (msg == 0x031D) _phone.OnClipboardUpdate();   // WM_CLIPBOARDUPDATE
            return IntPtr.Zero;
        }

        // =====================================================================================
        //  setup
        // =====================================================================================

        T Part<T>(string name) where T : class =>
            _root.FindName(name) as T ?? throw new InvalidOperationException("Missing XAML part: " + name);

        void LoadParts()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Isle.Island.xaml"))
                _root = (FrameworkElement)XamlReader.Load(s);

            _islandRoot = Part<Grid>("IslandRoot"); _body = Part<Grid>("Body"); _compact = Part<Grid>("Compact");
            _compactMedia = Part<Grid>("CompactMedia"); _compactCall = Part<Grid>("CompactCall"); _hud = Part<Grid>("Hud"); _hudBar = Part<Grid>("HudBar");
            _expMedia = Part<Grid>("ExpMedia"); _expIdle = Part<Grid>("ExpIdle"); _expIncoming = Part<Grid>("ExpIncoming");
            _expCall = Part<Grid>("ExpCall"); _expDrop = Part<Grid>("ExpDrop"); _shelfArea = Part<Grid>("ShelfArea");
            _progressRow = Part<Grid>("ProgressRow"); _seekArea = Part<Grid>("SeekArea"); _volArea = Part<Grid>("VolArea");
            _shape = Part<Path>("Shape"); _outline = Part<Path>("Outline"); _playIcon = Part<Path>("PlayIcon"); _pauseIcon = Part<Path>("PauseIcon");
            _privacyDot = Part<Ellipse>("PrivacyDot");
            _glassImage = Part<Image>("GlassImage"); _glassHost = Part<UIElement>("GlassHost");
            _glassTint = Part<UIElement>("GlassTint"); _glassSheen = Part<UIElement>("GlassSheen");
            _glass = new GlassBackdrop(this, frame => _glassImage.Source = frame);
            _ambient = Part<Image>("Ambient"); _incomingAppIcon = Part<Image>("IncomingAppIcon"); _callIcon = Part<Image>("CallIcon");
            _expanded = Part<StackPanel>("Expanded"); _appsRow = Part<StackPanel>("AppsRow"); _compactBars = Part<StackPanel>("CompactBars");
            _expBars = Part<StackPanel>("ExpBars"); _batteryBlock = Part<StackPanel>("BatteryBlock"); _shelfRow = Part<StackPanel>("ShelfRow");
            _idleClock = Part<TextBlock>("IdleClock"); _compactTitle = Part<TextBlock>("CompactTitle"); _hudIcon = Part<TextBlock>("HudIcon");
            _hudLabel = Part<TextBlock>("HudLabel"); _hudValue = Part<TextBlock>("HudValue"); _title = Part<TextBlock>("Title");
            _artist = Part<TextBlock>("Artist"); _sourceText = Part<TextBlock>("SourceText"); _sourceSwitch = Part<TextBlock>("SourceSwitch");
            _elapsed = Part<TextBlock>("Elapsed"); _remaining = Part<TextBlock>("Remaining"); _bigClock = Part<TextBlock>("BigClock");
            _dateText = Part<TextBlock>("DateText"); _batteryPct = Part<TextBlock>("BatteryPct"); _batteryBolt = Part<TextBlock>("BatteryBolt");
            _batteryState = Part<TextBlock>("BatteryState"); _callCompactIcon = Part<TextBlock>("CallCompactIcon");
            _callCompactTimer = Part<TextBlock>("CallCompactTimer"); _incomingInitials = Part<TextBlock>("IncomingInitials");
            _incomingName = Part<TextBlock>("IncomingName"); _incomingSub = Part<TextBlock>("IncomingSub"); _callIconGlyph = Part<TextBlock>("CallIconGlyph");
            _callTitle = Part<TextBlock>("CallTitle"); _callStatus = Part<TextBlock>("CallStatus"); _micChipText = Part<TextBlock>("MicChipText");
            _muteLabel = Part<TextBlock>("MuteLabel");
            _compactArt = Part<Border>("CompactArt"); _artLarge = Part<Border>("ArtLarge"); _artPlaceholder = Part<Border>("ArtPlaceholder");
            _seekTrack = Part<Border>("SeekTrack"); _seekFill = Part<Border>("SeekFill"); _volTrack = Part<Border>("VolTrack");
            _volFill = Part<Border>("VolFill"); _appsDivider = Part<Border>("AppsDivider"); _battFill = Part<Border>("BattFill"); _camChip = Part<Border>("CamChip");
            _seekDone = Part<ColumnDefinition>("SeekDone"); _seekLeft = Part<ColumnDefinition>("SeekLeft");
            _volDone = Part<ColumnDefinition>("VolDone"); _volLeft = Part<ColumnDefinition>("VolLeft");
            _battDone = Part<ColumnDefinition>("BattDone"); _battLeft = Part<ColumnDefinition>("BattLeft");
            _hudDone = Part<ColumnDefinition>("HudDone"); _hudLeft = Part<ColumnDefinition>("HudLeft");
            _playButton = Part<Button>("PlayButton"); _prevButton = Part<Button>("PrevButton"); _nextButton = Part<Button>("NextButton");
            _volButton = Part<Button>("VolButton"); _sourceChip = Part<Button>("SourceChip"); _artButton = Part<Button>("ArtButton");
            _openAppButton = Part<Button>("OpenAppButton"); _acceptButton = Part<Button>("AcceptButton"); _declineButton = Part<Button>("DeclineButton");
            _muteButton = Part<Button>("MuteButton"); _callOpenButton = Part<Button>("CallOpenButton"); _endButton = Part<Button>("EndButton");
            _shelfClear = Part<Button>("ShelfClear");
            _phoneRow = Part<Border>("PhoneRow"); _phoneName = Part<TextBlock>("PhoneName"); _phoneStatus = Part<TextBlock>("PhoneStatus");
            _dropSendText = Part<TextBlock>("DropSendText"); _phoneDot = Part<Ellipse>("PhoneDot"); _phoneInfo = Part<StackPanel>("PhoneInfo");
            _dropSend = Part<Grid>("DropSend"); _dropKeepBox = Part<Rectangle>("DropKeepBox"); _dropSendBox = Part<Rectangle>("DropSendBox");
            _dropSendCol = Part<ColumnDefinition>("DropSendCol");
            _phoneRingButton = Part<Button>("PhoneRingButton"); _phoneClipButton = Part<Button>("PhoneClipButton");
            _phoneSendButton = Part<Button>("PhoneSendButton"); _phoneSmsButton = Part<Button>("PhoneSmsButton");
            _phoneLinkButton = Part<Button>("PhoneLinkButton"); _shelfSend = Part<Button>("ShelfSend");
            Part<Button>("PhoneMirrorButton").Click += (s, e) => StartMirror();
            _expBuds = Part<Grid>("ExpBuds"); _budsRingBox = Part<Grid>("BudsRingBox"); _budsName = Part<TextBlock>("BudsName");
            _budsSub = Part<TextBlock>("BudsSub"); _budsPct = Part<TextBlock>("BudsPct"); _budsRing = Part<Path>("BudsRing");
            _budsIconBox = Part<Border>("BudsIconBox");
            _budsRow = Part<Border>("BudsRow"); _budsRowArt = Part<Border>("BudsRowArt"); _budsRowName = Part<TextBlock>("BudsRowName");
            _budsRowStatus = Part<TextBlock>("BudsRowStatus"); _budsRowPct = Part<TextBlock>("BudsRowPct"); _budsRowRing = Part<Path>("BudsRowRing");

            _expScale = new ScaleTransform(1, 1);
            _expanded.RenderTransform = _expScale;
            _expanded.RenderTransformOrigin = new Point(0.5, 0);
            _expanded.IsHitTestVisible = false;
            _privacyDot.RenderTransform = _dotShift;
            _acceptButton.RenderTransform = _acceptPulse;
            _acceptButton.RenderTransformOrigin = new Point(0.5, 0.5);

            _seekFill.Background = _accent;
            _seekTrack.Background = _accentTrack;
            _volFill.Background = _accent;
            _volTrack.Background = _accentTrack;
            _playButton.Background = _accent;
            MakeBars(_compactBars, _compactBarScales, 14, 3, 2.5);
            MakeBars(_expBars, _expBarScales, 16, 3, 3);
        }

        void MakeBars(Panel host, List<ScaleTransform> scales, double height, double width, double gap)
        {
            for (int i = 0; i < 4; i++)
            {
                var sc = new ScaleTransform(1, 0.22);
                host.Children.Add(new Rectangle
                {
                    Width = width, Height = height, RadiusX = width / 2, RadiusY = width / 2, Fill = _accent,
                    Margin = new Thickness(i == 0 ? 0 : gap, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    RenderTransform = sc, RenderTransformOrigin = new Point(0.5, 0.5),
                });
                scales.Add(sc);
            }
        }

        DispatcherTimer Timer(int ms, EventHandler tick, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            var t = new DispatcherTimer(priority) { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += tick;
            return t;
        }

        void CreateTimers()
        {
            _hudTimer = Timer(1600, (s, e) => { _hudTimer.Stop(); _hudActive = false; UpdateCompactContent(); Tune(460, 0.86); Retarget(); });
            _peekTimer = Timer(2800, (s, e) => { _peekTimer.Stop(); _peekActive = false; UpdateCompactTitle(); Tune(420, 0.86); Retarget(); });
            _progressTimer = Timer(500, (s, e) => UpdateProgress(), DispatcherPriority.Background);
            _meterTimer = Timer(33, OnMeterTick, DispatcherPriority.Render);
            _clockTimer = Timer(60000, (s, e) => UpdateClock(), DispatcherPriority.Background);
            _fsTimer = Timer(1000, (s, e) => CheckFullscreen(), DispatcherPriority.Background);
            _cursorTimer = Timer(250, (s, e) => OnCursorTick(), DispatcherPriority.Input);
            _callTimer = Timer(1000, (s, e) => UpdateCallTimer(), DispatcherPriority.Background);
            _dropLeaveTimer = Timer(250, (s, e) => { _dropLeaveTimer.Stop(); _dropActive = false; RefreshExpandedKind(); });
            _phoneFlash = Timer(3200, (s, e) => { _phoneFlash.Stop(); UpdatePhoneUi(); });
            _budsPoll = Timer(60000, async (s, e) => await RefreshBudsBattery(), DispatcherPriority.Background);
            _budsTimer = Timer(5000, (s, e) =>
            {
                _budsTimer.Stop();
                _budsActive = false;
                RefreshExpandedKind();
                if (_mode == Mode.Expanded && !CursorOverIsland()) Collapse();
            });
        }

        void WireEvents()
        {
            _islandRoot.MouseWheel += OnWheel;

            _playButton.Click += (s, e) => _media.TogglePlayPause();
            _prevButton.Click += (s, e) => _media.Previous();
            _nextButton.Click += (s, e) => _media.Next();
            _sourceChip.Click += (s, e) =>
            {
                if (_media.State.Count > 1) _media.CycleSession();
                else { _media.OpenSourceApp(); Collapse(); }
            };
            _artButton.Click += (s, e) => { _media.OpenSourceApp(); Collapse(); };
            _openAppButton.Click += (s, e) => { _media.OpenSourceApp(); Collapse(); };
            _volButton.Click += (s, e) => _audio.ToggleMute();

            _acceptButton.Click += (s, e) => _calls.Accept();
            _declineButton.Click += (s, e) => _calls.Decline();
            _muteButton.Click += (s, e) => _audio.ToggleMicMute();
            _callOpenButton.Click += (s, e) => { _calls.OpenApp(); Collapse(); };
            _endButton.Click += (s, e) => _calls.End();
            _shelfClear.Click += (s, e) => { _shelf.Clear(); BuildShelf(); RefreshExpandedKind(); };
            _shelfSend.Click += (s, e) => SendToPhone(_shelf.Items.ToList(), false);

            // phone
            _phoneRingButton.Click += async (s, e) =>
            {
                if (!_phone.CanAct) { _phone.OpenFindMyDevice(); Collapse(); return; }
                PhoneFeedback("Ringing " + _phone.Name + "…");
                if (await _phone.Ring()) { PhoneFeedback("Ringing " + _phone.Name + " — tap it on the phone to stop"); return; }
                // not on Wi-Fi with KDE Connect: Google's Find My Device can still ring it
                PhoneFeedback(_phone.Name + " isn't on KDE Connect — opening Find My Device");
                _phone.OpenFindMyDevice();
            };
            _phoneClipButton.Click += async (s, e) =>
            {
                PhoneFeedback(await _phone.SendClipboard() ? "Clipboard sent to " + _phone.Name : _phone.Name + " isn't reachable right now");
            };
            _phoneSendButton.Click += (s, e) =>
            {
                var dlg = new OpenFileDialog { Multiselect = true, Title = "Send to " + _phone.Name };
                if (dlg.ShowDialog() == true) SendToPhone(dlg.FileNames, false);
            };
            _phoneSmsButton.Click += (s, e) => { _phone.OpenMessages(); Collapse(); };
            _phoneLinkButton.Click += (s, e) => { _phone.OpenPhoneLink(); Collapse(); };
            _phoneInfo.MouseLeftButtonUp += (s, e) =>
            {
                if (_phone.CopyLastClipAgain()) PhoneFeedback("Copied again — press Ctrl+V to paste");
                else _phone.Refresh(true);
            };

            WireDrag(_seekArea, SeekPreview, SeekCommit, on => { });
            WireDrag(_volArea, f => _audio.SetVolume((float)f), f => _audio.SetVolume((float)f), on => _volDragging = on);
            ThickenOnHover(_seekArea, _seekTrack, _seekFill);
            ThickenOnHover(_volArea, _volTrack, _volFill);

            // file shelf drop target
            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            DragLeave += (s, e) => { _dropLeaveTimer.Stop(); _dropLeaveTimer.Start(); };
            Drop += OnDrop;
        }

        static double Frac(MouseEventArgs e, FrameworkElement area) =>
            Clamp01(e.GetPosition(area).X / Math.Max(1, area.ActualWidth));

        void WireDrag(Grid area, Action<double> move, Action<double> commit, Action<bool> state)
        {
            bool active = false;
            area.MouseLeftButtonDown += (s, e) =>
            {
                active = true;
                _dragging = true;
                state(true);
                area.CaptureMouse();
                move(Frac(e, area));
                e.Handled = true;
            };
            area.MouseMove += (s, e) => { if (active) move(Frac(e, area)); };
            area.MouseLeftButtonUp += (s, e) =>
            {
                if (!active) return;
                double f = Frac(e, area);
                active = false;
                _dragging = false;
                state(false);
                area.ReleaseMouseCapture();
                commit(f);
                UpdateVolumeUi();
                e.Handled = true;
            };
            area.LostMouseCapture += (s, e) =>
            {
                if (!active) return;
                active = false;
                _dragging = false;
                state(false);
                UpdateVolumeUi();
            };
        }

        static void ThickenOnHover(Grid area, Border track, Border fill)
        {
            void Set(double t)
            {
                track.Height = fill.Height = t;
                track.CornerRadius = fill.CornerRadius = new CornerRadius(t / 2);
            }
            area.MouseEnter += (s, e) => Set(6);
            area.MouseLeave += (s, e) => { if (!area.IsMouseCaptured) Set(4); };
            area.LostMouseCapture += (s, e) => { if (!area.IsMouseOver) Set(4); };
        }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // drop-in intro: start as a sliver and spring into the pill
            _w.Snap(60); _h.Snap(4); _r.Snap(2);
            ApplyStyle();
            UpdateClock();
            UpdateVolumeUi();
            UpdateMicUi();
            _lastOnAc = Native.GetPower().OnAc;
            _fsTimer.Start();
            _cursorTimer.Start();
            InstallVolumeKeys();
            Closed += (s, a) => RemoveVolumeKeys();
            if (App.SnapshotDir == null) SetBudsConnected(IsHeadphones(_audio.DeviceName) ? _audio.DeviceName : "");

            _audio.VolumeChanged += OnVolumeChanged;
            _audio.MicChanged += OnMicChanged;
            _audio.OutputDeviceSwitched += OnOutputSwitched;
            if (!App.NoMedia) _media.Changed += OnMediaChanged;   // --no-media: demo snapshots without what's really playing
            _calls.Changed += OnCallChanged;
            _brightness.Changed += OnBrightness;
            _phone.OnlineChanged += OnPhoneOnline;
            _phone.StatusChanged += () => { UpdatePhoneUi(); RefreshExpandedKind(); };
            _phone.ClipboardFromPhone += OnPhoneClipboard;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Closed += (s, a) =>
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            };

            await Dispatcher.InvokeAsync(() => { BuildApps(); BuildShelf(); }, DispatcherPriority.Background);
            _calls.Start();
            _brightness.Start(Dispatcher);
            if (App.SnapshotDir == null) { _phone.Start(_cfg); HookMirror(); }
            try { await _media.StartAsync(); }
            catch (Exception ex) { App.Log(ex); }

            if (App.PinExpanded) Expand();
            if (App.Welcome) OpenHub();
            if (App.SnapshotDir != null) RunSnapshots();
        }

        void OnDisplayChanged(object sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(PlaceOnScreen));

        void PlaceOnScreen()
        {
            var wa = SystemParameters.WorkArea;
            Left = Math.Round(wa.Left + (wa.Width - Width) / 2);
            Top = wa.Top;
        }

        void ApplyStyle()
        {
            _islandRoot.Margin = new Thickness(0, IslandTop, 0, 0);
            UpdateCompactTitle();
            UpdateCompactContent(true);
            Tune(420, 0.8);
            Retarget();
            ApplyFrame();
        }

        void BuildApps()
        {
            _appsRow.Children.Clear();
            var style = (Style)_root.FindResource("AppButton");
            foreach (var app in QuickApps.Resolve(_cfg.Apps).Take(10))
            {
                UIElement content;
                if (app.Icon != null)
                {
                    var img = new Image { Source = app.Icon, Width = 26, Height = 26 };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    content = img;
                }
                else content = Letter(app.Name, 26);
                var button = new Button { Style = style, Content = content, ToolTip = app.Name, Margin = new Thickness(2, 0, 2, 0) };
                string target = app.Target;
                button.Click += (s, e) => { QuickApps.Launch(target); Collapse(); };
                _appsRow.Children.Add(button);
            }
            _expandedKind = "";
            RefreshExpandedKind();
        }

        static UIElement Letter(string name, double size) => new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.27), Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)),
            Child = new TextBlock
            {
                Text = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?", FontSize = size * 0.5, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        // =====================================================================================
        //  public API (tray / hotkey)
        // =====================================================================================

        /// <summary>Isle was launched again (Start menu / shortcut) or just installed: open the Isle app window.</summary>
        public void ShowFromLaunch() => OpenHub();

        HubWindow _hub;

        public void OpenHub()
        {
            if (_hub == null) _hub = new HubWindow(this, _phone);
            _hub.ShowAndActivate();
        }

        public void ToggleExpanded()
        {
            if (_mode == Mode.Expanded) Collapse();
            else Expand();
        }

        public void ReloadSettings()
        {
            _cfg = Settings.Load();
            if (_cfg.OwnVolumePopup) InstallVolumeKeys(); else RemoveVolumeKeys();
            BuildApps();
            if (!_cfg.HideInFullscreen && _hiddenForFullscreen) SetFullscreenHidden(false);
            ApplyStyle();
        }

        public void ChangeSetting(string key, string value)
        {
            Settings.Write(key, value);
            ReloadSettings();
        }

        // =====================================================================================
        //  hover: the pill is click-through, so hovering is read from the cursor position
        // =====================================================================================

        void OnCursorTick()
        {
            if (_hwnd == IntPtr.Zero || App.SnapshotDir != null) return;
            if (_hiddenForFullscreen) { ResetDwell(); return; }
            if (!Native.GetCursorPos(out var sp)) return;
            Point p;
            try { p = PointFromScreen(new Point(sp.X, sp.Y)); }
            catch { return; }

            bool near = p.Y > -6 && p.Y < _h.Value + 90 && p.X > -60 && p.X < ActualWidth + 60;
            var interval = TimeSpan.FromMilliseconds(_mode == Mode.Expanded || near ? 40 : 250);
            if (_cursorTimer.Interval != interval) _cursorTimer.Interval = interval;

            if (_mode == Mode.Expanded)
            {
                if (OverIsland(p, 10)) { _cursorVisited = true; _cursorLeftAt = DateTime.MinValue; return; }
                if (_dragging || _dropActive || _forcedOpen || App.PinExpanded) return;
                if (!_cursorVisited && (DateTime.Now - _openedAt).TotalSeconds < 6) return;   // opened from hotkey / tray
                if (_cursorLeftAt == DateTime.MinValue) _cursorLeftAt = DateTime.Now;
                if ((DateTime.Now - _cursorLeftAt).TotalMilliseconds >= _cfg.CollapseDelayMs) Collapse();
                return;
            }

            bool over = OverIsland(p, 3);
            bool button = Native.MouseButtonDown();
            // pushed all the way against the top edge of the screen (MacBook-notch style trigger)
            bool atEdge = over && p.Y <= 2;
            bool dragging = _dwellStart != null ? _dwellWithButton : button;
            // see-through: while the cursor is over the pill (but not asking it to open), fade it so the tab underneath shows
            SetSeeThrough(_cfg.FadeOnHover && over && !atEdge && !dragging);
            bool trigger = dragging || !_cfg.OpenOnEdge ? over : atEdge;
            if (!trigger)
            {
                if (_dwellStart != null || _nudge || _suppressDwell) ResetDwell();
                return;
            }
            if (_dwellStart == null)
            {
                _dwellStart = DateTime.Now;
                _dwellWithButton = button;     // already holding something (dragging a file in)
                _suppressDwell = false;
            }
            if (_suppressDwell) return;
            if (button && !_dwellWithButton)
            {
                // you clicked whatever is under the pill (a Chrome tab): don't open until the cursor leaves
                _suppressDwell = true;
                SetNudge(false);
                return;
            }
            double ms = (DateTime.Now - _dwellStart.Value).TotalMilliseconds;
            double need = _dwellWithButton ? 900 : _cfg.OpenOnEdge ? Math.Min(_cfg.HoverDelayMs, 300) : _cfg.HoverDelayMs;
            if (ms > Math.Min(180, need * 0.4)) SetNudge(true);
            if (ms >= need)
            {
                ResetDwell();
                Expand();
                _cursorVisited = true;
            }
        }

        bool _seeThrough;

        void SetSeeThrough(bool on)
        {
            if (_mode != Mode.Compact) on = false;
            if (on == _seeThrough) return;
            _seeThrough = on;
            _islandRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0.08 : 1, TimeSpan.FromMilliseconds(on ? 140 : 220)));
        }

        bool OverIsland(Point p, double slack)
        {
            double w = _w.Value, h = Math.Max(_h.Value, 12);
            double left = (ActualWidth - w) / 2, top = IslandTop;
            return p.X >= left - slack && p.X <= left + w + slack && p.Y >= top - 8 - slack && p.Y <= top + h + slack;
        }

        bool CursorOverIsland()
        {
            if (!Native.GetCursorPos(out var sp)) return false;
            try { return OverIsland(PointFromScreen(new Point(sp.X, sp.Y)), 10); }
            catch { return false; }
        }

        void ResetDwell()
        {
            _dwellStart = null;
            _suppressDwell = false;
            SetNudge(false);
        }

        void SetNudge(bool on)
        {
            if (_nudge == on) return;
            _nudge = on;
            if (_mode == Mode.Compact) { Tune(520, 0.6); Retarget(); }
        }

        // =====================================================================================
        //  morph
        // =====================================================================================

        void Trace(string what)
        {
            if (App.SnapshotDir == null && !App.PinExpanded) return;
            App.Log(new Exception(string.Format("{0}: mode={1} kind={2} w={3:0} h={4:0} -> {5:0}x{6:0}",
                what, _mode, _expandedKind, _w.Value, _h.Value, _w.Target, _h.Target)));
        }

        public void Expand()
        {
            Trace("Expand");
            if (_mode == Mode.Expanded || (_hiddenForFullscreen && !Ringing)) return;
            _mode = Mode.Expanded;
            if (_seeThrough) { _seeThrough = false; _islandRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150))); }
            _openedAt = DateTime.Now;
            _cursorVisited = false;
            _cursorLeftAt = DateTime.MinValue;
            _nudge = false;
            _hudActive = false; _peekActive = false;
            _hudTimer.Stop(); _peekTimer.Stop();
            if (_hwnd != IntPtr.Zero) Native.SetClickThrough(_hwnd, false);
            UpdateCompactTitle();
            UpdateCompactContent();
            RefreshExpandedKind(false);
            UpdateProgress();
            RefreshBattery();
            UpdateClock();
            UpdateVolumeUi();
            UpdateMicUi();
            UpdateCallTimer();
            UpdatePhoneUi();
            _phone.RefreshIfStale();
            _compact.IsHitTestVisible = false;
            _expanded.IsHitTestVisible = true;
            Tune(300, 0.7);
            Retarget();
            if (_hasMedia && _media.State.Playing) _progressTimer.Start();
            UpdateMeterTimer();
        }

        public void Collapse()
        {
            Trace("Collapse");
            if (_mode == Mode.Compact || _forcedOpen) return;
            _mode = Mode.Compact;
            _dropActive = false;
            _progressTimer.Stop();
            if (_hwnd != IntPtr.Zero) Native.SetClickThrough(_hwnd, true);
            _compact.IsHitTestVisible = true;
            _expanded.IsHitTestVisible = false;
            Tune(380, 0.82);
            Retarget();
            UpdateMeterTimer();
            UpdatePrivacyDot();
        }

        void Peek()
        {
            _peekActive = true;
            _peekTimer.Stop();
            _peekTimer.Start();
            UpdateCompactTitle();
            Tune(360, 0.66);
            Retarget();
        }

        void ShowHud(double width)
        {
            _hudActive = true;
            _hudWidth = width;
            UpdateCompactContent();
            _hudTimer.Stop();
            _hudTimer.Interval = TimeSpan.FromMilliseconds(1600);
            _hudTimer.Start();
            Tune(540, 0.7);
            Retarget();
        }

        (double W, double H, double R) CompactTarget()
        {
            double h = CompactH + (_nudge ? 3 : 0), grow = _nudge ? 16 : 0;
            if (_hudActive) return (_hudWidth, CompactH, CompactH / 2);
            if (InCall) return (190 + grow, h, h / 2);
            if (_hasMedia) return ((_peekActive ? 340 : _cfg.CompactTitle ? 250 : 200) + grow, h, h / 2);
            if (_cfg.MinimalIdle) return IsNotch ? (130 + grow, 7 + (_nudge ? 5 : 0), 3.5) : (110 + grow, 6 + (_nudge ? 5 : 0), 3);
            return ((_budsConnectedName.Length > 0 && _budsBattery >= 0 ? 178 : 112) + grow, h, h / 2);
        }

        void Tune(double stiffness, double damping)
        {
            foreach (var s in new[] { _w, _h, _r }) { s.Stiffness = stiffness; s.Damping = damping; }
            _r.Stiffness = stiffness * 1.3;
        }

        double MeasureExpanded()
        {
            _expanded.Measure(new Size(ExpandedWidth, double.PositiveInfinity));
            return Math.Ceiling(_expanded.DesiredSize.Height);
        }

        void Retarget()
        {
            _expandedH = MeasureExpanded();
            if (_mode == Mode.Expanded)
            {
                _w.Target = ExpandedWidth;
                _h.Target = _expandedH;
                _r.Target = ExpandedRadius;
                UpdateGlass();
            }
            else
            {
                var t = CompactTarget();
                _w.Target = t.W; _h.Target = t.H; _r.Target = t.R;
                _glass.Stop();
            }
            StartAnimating();
        }

        bool GlassOn => _cfg.Glass;

        /// <summary>Tells the glass which strip of screen sits behind the open island and starts refreshing it.</summary>
        static BitmapSource SampleWallpaper()
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x6B), Color.FromRgb(0x6A, 0x1F, 0x5E), 0), null, new Rect(0, 0, 96, 60));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF2, 0x8C, 0x38)), null, new Point(20, 46), 22, 16);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2F, 0xB8, 0xD8)), null, new Point(78, 14), 24, 14);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xC0, 0xF4, 0xF4, 0xF6)), null, new Rect(30, 0, 40, 9));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE8, 0x4A, 0x8A)), null, new Point(60, 50), 12, 10);
            }
            var rtb = new RenderTargetBitmap(96, 60, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        void UpdateGlass()
        {
            if (!GlassOn || _hwnd == IntPtr.Zero || !IsVisible) { _glass.Stop(); return; }
            if (App.SnapshotDir != null)
            {
                // snapshots never read the real screen: glass over a sample wallpaper instead
                const double pad = 28;
                _glassImage.Width = _glassW = _w.Target + 2 * pad;
                _glassImage.Height = _h.Target + pad;
                if (_glassImage.Source == null) _glassImage.Source = SampleWallpaper();
                return;
            }
            try
            {
                const double pad = 28;   // a little extra all round so the blur has no dark edges inside the island
                double w = _w.Target + 2 * pad, h = _h.Target + pad;
                // the island is centred in this window and hangs from the top of the body
                var top = _body.TranslatePoint(new Point(0, 0), this).Y;
                double left = ActualWidth / 2 - w / 2;
                var dpi = VisualTreeHelper.GetDpi(this);
                var screen = PointToScreen(new Point(left, top));
                _glassImage.Width = _glassW = w;
                _glassImage.Height = h;
                Canvas.SetTop(_glassImage, 0);
                _glass.SetRect(new Int32Rect((int)Math.Round(screen.X), (int)Math.Round(screen.Y),
                                             (int)Math.Round(w * dpi.DpiScaleX), (int)Math.Round(h * dpi.DpiScaleY)));
                _glass.Start();
            }
            catch (Exception ex) { App.Log(ex); }
        }

        void StartAnimating()
        {
            if (_animating) return;
            _animating = true;
            _lastTick = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }

        void OnRendering(object sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == _lastTick) return;
            double dt = _lastTick == TimeSpan.Zero ? 1.0 / 60 : (now - _lastTick).TotalSeconds;
            _lastTick = now;
            dt = Math.Min(dt, 0.05);
            bool a = _w.Step(dt), b = _h.Step(dt), c = _r.Step(dt);
            ApplyFrame();
            if (a && b && c)
            {
                CompositionTarget.Rendering -= OnRendering;
                _animating = false;
            }
        }

        void ApplyFrame()
        {
            double w = Math.Max(8, _w.Value), h = Math.Max(3, _h.Value);
            double r = Math.Max(0, Math.Min(_r.Value, Math.Min(w, h) / 2));
            bool notch = IsNotch;

            _body.Width = w;
            _body.Height = h;
            _body.Clip = BuildShape(w, h, r, 0, 0, notch, true);
            _shape.Width = _outline.Width = w + 2 * Ear;
            _shape.Height = h;
            _outline.Height = h + 1;
            _shape.Data = BuildShape(w, h, r, Ear, Ear, notch, true);
            // the notch "ears" belong to the black pill; as it turns to glass they shrink away with it
            _outline.Data = BuildShape(w, h, r, Ear, Ear * (1 - GlassAmount(h)), notch, !notch);
            _compact.Height = Math.Max(CompactH, Math.Min(h, CompactH + 5));

            double compactH = CompactH;
            double p = _expandedH > compactH + 1 ? Clamp01((h - compactH - 5) / (_expandedH - compactH - 5)) : 0;
            double compactOpacity = 1 - SmoothStep(0, 0.28, p);
            double expandedOpacity = SmoothStep(0.3, 0.92, p);
            _compact.Opacity = compactOpacity;
            _compact.Visibility = compactOpacity > 0.01 ? Visibility.Visible : Visibility.Hidden;
            _expanded.Opacity = expandedOpacity;
            _expanded.Visibility = expandedOpacity > 0.01 ? Visibility.Visible : Visibility.Hidden;
            _expScale.ScaleX = _expScale.ScaleY = 0.9 + 0.1 * p;
            // glass: the black pill turns into frosted glass as it opens (only once a backdrop frame exists)
            double g = GlassAmount(h);
            _glassHost.Opacity = _glassTint.Opacity = _glassSheen.Opacity = g;
            Canvas.SetLeft(_glassImage, (w - _glassW) / 2);
            _shape.Opacity = 1 - g;
            _outline.Opacity = 0.3 + 0.7 * g;
            // album colour: a soft wash over the glass, or the full glow on the solid black island
            _ambient.Opacity = (g > 0 ? 0.24 : 0.55) * SmoothStep(0.2, 1, p);

            // iOS-style content blur while the island morphs
            ApplyBlur(_compact, _compactBlur, compactOpacity > 0.01 ? 12 * SmoothStep(0, 0.28, p) : 0);
            ApplyBlur(_expanded, _expandedBlur, expandedOpacity > 0.01 ? 12 * (1 - SmoothStep(0.35, 0.9, p)) : 0);

            // privacy dot sits just right of the pill
            _dotShift.X = w / 2 + 13;
            _dotShift.Y = Math.Min(h, CompactH) / 2 - 3.5;
            _privacyDot.Opacity = compactOpacity;
        }

        /// <summary>0 = solid black pill, 1 = fully frosted glass; follows how far the island has opened.</summary>
        double GlassAmount(double h)
        {
            if (!GlassOn || _glassImage.Source == null) return 0;
            double compactH = CompactH;
            double p = _expandedH > compactH + 1 ? Clamp01((h - compactH - 5) / (_expandedH - compactH - 5)) : 0;
            return SmoothStep(0.04, 0.6, p);
        }

        static void ApplyBlur(UIElement el, BlurEffect fx, double radius)
        {
            if (radius < 0.4) { if (el.Effect != null) el.Effect = null; return; }
            fx.Radius = radius;
            if (el.Effect != fx) el.Effect = fx;
        }

        /// <summary>
        /// Notch: flat top with concave "ears" that melt into the screen edge. Island: rounded rectangle.
        /// Corners are "continuous" (squircle-like, as on Apple devices) rather than plain circular arcs.
        /// </summary>
        static Geometry BuildShape(double w, double h, double r, double ox, double earMax, bool notch, bool closed)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                if (notch)
                {
                    double p = Math.Min(Math.Min(r * 1.28, w / 2), h);
                    double k = CornerK(p, r);
                    double ear = Math.Min(earMax, Math.Max(0, h - p));
                    c.BeginFigure(new Point(ox - ear, 0), closed, closed);
                    if (ear > 0) c.QuadraticBezierTo(new Point(ox, 0), new Point(ox, ear), true, true);
                    c.LineTo(new Point(ox, h - p), true, true);
                    Corner(c, new Point(ox, h - p), new Point(ox, h), new Point(ox + p, h), k);
                    c.LineTo(new Point(ox + w - p, h), true, true);
                    Corner(c, new Point(ox + w - p, h), new Point(ox + w, h), new Point(ox + w, h - p), k);
                    c.LineTo(new Point(ox + w, ear), true, true);
                    if (ear > 0) c.QuadraticBezierTo(new Point(ox + w, 0), new Point(ox + w + ear, 0), true, true);
                }
                else
                {
                    double p = Math.Min(r * 1.28, Math.Min(w, h) / 2);
                    double k = CornerK(p, r);
                    c.BeginFigure(new Point(ox + p, 0), closed, true);
                    c.LineTo(new Point(ox + w - p, 0), true, true);
                    Corner(c, new Point(ox + w - p, 0), new Point(ox + w, 0), new Point(ox + w, p), k);
                    c.LineTo(new Point(ox + w, h - p), true, true);
                    Corner(c, new Point(ox + w, h - p), new Point(ox + w, h), new Point(ox + w - p, h), k);
                    c.LineTo(new Point(ox + p, h), true, true);
                    Corner(c, new Point(ox + p, h), new Point(ox, h), new Point(ox, h - p), k);
                    c.LineTo(new Point(ox, p), true, true);
                    Corner(c, new Point(ox, p), new Point(ox, 0), new Point(ox + p, 0), k);
                }
            }
            g.Freeze();
            return g;
        }

        // handle position along each edge: 0.5523 = circular arc, higher = smoother, Apple-like continuous corner
        static double CornerK(double p, double r) => r <= 0 ? 0.5523 : 0.5523 + (0.74 - 0.5523) * Clamp01((p / r - 1) / 0.28);

        static void Corner(StreamGeometryContext c, Point a, Point corner, Point b, double k)
        {
            c.BezierTo(new Point(a.X + (corner.X - a.X) * k, a.Y + (corner.Y - a.Y) * k),
                       new Point(b.X + (corner.X - b.X) * k, b.Y + (corner.Y - b.Y) * k), b, true, true);
        }

        // =====================================================================================
        //  content
        // =====================================================================================

        void UpdateCompactContent(bool force = false)
        {
            string view = _hudActive ? "hud" : InCall ? "call" : _hasMedia ? "media" : "idle";
            if (view == _compactView && !force) return;
            _compactView = view;
            SetShown(_idleClock, view == "idle" && !_cfg.MinimalIdle);
            SetShown(_compactMedia, view == "media");
            SetShown(_compactCall, view == "call");
            SetShown(_hud, view == "hud");
        }

        void UpdateCompactTitle()
        {
            bool show = _peekActive || _cfg.CompactTitle;
            if (show && _compactTitle.Visibility != Visibility.Visible)
            {
                _compactTitle.Visibility = Visibility.Visible;
                _compactTitle.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(90) });
            }
            else if (!show) _compactTitle.Visibility = Visibility.Collapsed;
        }

        static void SetShown(UIElement el, bool on)
        {
            if (!on)
            {
                el.BeginAnimation(OpacityProperty, null);
                el.Visibility = Visibility.Collapsed;
                return;
            }
            if (el.Visibility == Visibility.Visible) return;
            el.Visibility = Visibility.Visible;
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase() });
        }

        string ExpandedKind()
        {
            if (_dropActive) return "drop";
            if (Ringing) return "incoming";
            if (_budsActive) return "buds";
            if (InCall) return "call";
            return _hasMedia ? "media" : "idle";
        }

        void RefreshExpandedKind(bool retarget = true)
        {
            string kind = ExpandedKind();
            bool shelf = _shelf.Items.Count > 0 && kind != "incoming" && kind != "drop" && kind != "buds";
            bool apps = _appsRow.Children.Count > 0 && kind != "incoming" && kind != "buds";
            bool phone = _phone.Configured && (kind == "media" || kind == "idle" || kind == "call");
            bool buds = _budsConnectedName.Length > 0 && (kind == "media" || kind == "idle" || kind == "call");
            bool same = kind == _expandedKind && (_shelfArea.Visibility == Visibility.Visible) == shelf
                        && (_appsRow.Visibility == Visibility.Visible) == apps && (_phoneRow.Visibility == Visibility.Visible) == phone
                        && (_budsRow.Visibility == Visibility.Visible) == buds;
            if (same) return;
            _expandedKind = kind;
            _budsRow.Visibility = buds ? Visibility.Visible : Visibility.Collapsed;
            _phoneRow.Visibility = phone ? Visibility.Visible : Visibility.Collapsed;
            _expIncoming.Visibility = kind == "incoming" ? Visibility.Visible : Visibility.Collapsed;
            _expBuds.Visibility = kind == "buds" ? Visibility.Visible : Visibility.Collapsed;
            _expCall.Visibility = kind == "call" ? Visibility.Visible : Visibility.Collapsed;
            _expDrop.Visibility = kind == "drop" ? Visibility.Visible : Visibility.Collapsed;
            _expMedia.Visibility = kind == "media" ? Visibility.Visible : Visibility.Collapsed;
            _expIdle.Visibility = kind == "idle" ? Visibility.Visible : Visibility.Collapsed;
            _shelfArea.Visibility = shelf ? Visibility.Visible : Visibility.Collapsed;
            _appsRow.Visibility = _appsDivider.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
            _ambient.Source = kind == "media" ? _media.State.Ambient : null;

            if (kind == "incoming")
            {
                var pulse = new DoubleAnimation(1, 1.09, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
                _acceptPulse.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                _acceptPulse.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            }
            else
            {
                _acceptPulse.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _acceptPulse.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            if (retarget && _mode == Mode.Expanded) Retarget();
        }

        // ---------- media ----------

        void OnMediaChanged(MediaChange change)
        {
            var st = _media.State;
            bool had = _hasMedia;
            _hasMedia = st.HasSession;

            string title = st.Title.Length > 0 ? st.Title : st.Source;
            _compactTitle.Text = st.Title.Length > 0 && st.Artist.Length > 0 ? st.Title + "  ·  " + st.Artist : title;
            _title.Text = title;
            _title.ToolTip = title;
            _artist.Text = st.Artist.Length > 0 ? st.Artist : st.Album.Length > 0 ? st.Album : st.Source;

            Brush art = null;
            if (st.Art != null)
            {
                var ib = new ImageBrush(st.Art) { Stretch = Stretch.UniformToFill };
                ib.Freeze();
                art = ib;
            }
            _compactArt.Background = art ?? new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
            _artLarge.Background = art;
            _artPlaceholder.Visibility = art == null ? Visibility.Visible : Visibility.Hidden;
            if (_expandedKind == "media") _ambient.Source = st.Ambient;

            // the playing app's own icon (no pill); its name only if there's no icon
            var appIcon = SourceAppIcon(st.SourceId, st.Source);
            var sourceIcon = (Image)_root.FindName("SourceIcon");
            sourceIcon.Source = appIcon;
            sourceIcon.Visibility = appIcon != null ? Visibility.Visible : Visibility.Collapsed;
            _sourceText.Text = appIcon == null ? st.Source : "";
            _sourceText.Visibility = appIcon == null ? Visibility.Visible : Visibility.Collapsed;
            _sourceSwitch.Text = (st.Index + 1) + "/" + st.Count;
            _sourceSwitch.Visibility = st.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _sourceChip.ToolTip = st.Count > 1 ? st.Source + "  ·  click to switch player" : st.Source + "  ·  click to open";

            _playIcon.Visibility = st.Playing ? Visibility.Collapsed : Visibility.Visible;
            _pauseIcon.Visibility = st.Playing ? Visibility.Visible : Visibility.Collapsed;
            _playButton.IsEnabled = st.CanToggle;
            _prevButton.IsEnabled = st.CanPrev;
            _nextButton.IsEnabled = st.CanNext;

            SetAccent(st.Accent);
            UpdateProgress();
            UpdateCompactContent();
            RefreshExpandedKind();

            if (_mode == Mode.Expanded)
            {
                if (_hasMedia && st.Playing) _progressTimer.Start();
                else _progressTimer.Stop();
            }

            if (change == MediaChange.Track && _hasMedia && st.Title.Length > 0 && _cfg.TrackPeek &&
                _mode == Mode.Compact && !_hudActive && !_hiddenForFullscreen && !InCall)
                Peek();
            else if (had != _hasMedia)
                Retarget();

            UpdateMeterTimer();
        }

        readonly Dictionary<string, BitmapSource> _sourceIcons = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Icon of the app that's playing ("Chrome", "Spotify.exe", "SpotifyAB...!Spotify", ...), cached.</summary>
        BitmapSource SourceAppIcon(string id, string pretty)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_sourceIcons.TryGetValue(id, out var cached)) return cached;
            BitmapSource icon = null;
            try
            {
                // registered app id (Chrome, MSEdge, Store apps)
                icon = Native.ShellIcon("shell:AppsFolder\\" + id, 48);
                // plain exe name: use the running process's file
                if (icon == null)
                {
                    string exe = System.IO.Path.GetFileNameWithoutExtension(id.Contains("!") ? "" : id);
                    foreach (var name in new[] { exe, pretty })
                    {
                        if (string.IsNullOrEmpty(name) || icon != null) continue;
                        foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                        {
                            try { icon = Native.ShellIcon(p.MainModule.FileName, 48); } catch { }
                            if (icon != null) break;
                        }
                    }
                }
            }
            catch (Exception ex) { App.Log(ex); }
            _sourceIcons[id] = icon;
            return icon;
        }

        void SetAccent(Color c)
        {
            if (c == _accentTarget) return;
            _accentTarget = c;
            _accent.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(c, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase() });
            _accentTrack.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(TrackTint(c), TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase() });
        }

        void UpdateProgress()
        {
            var st = _media.State;
            var dur = st.Duration;
            bool show = st.HasSession && dur > TimeSpan.FromSeconds(1);
            var vis = show ? Visibility.Visible : Visibility.Collapsed;
            if (_progressRow.Visibility != vis)
            {
                _progressRow.Visibility = vis;
                if (_mode == Mode.Expanded) Retarget();
            }
            _seekArea.IsHitTestVisible = st.CanSeek;
            if (!show || _seekArea.IsMouseCaptured) return;
            var pos = st.PositionNow - st.Start;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            if (pos > dur) pos = dur;
            SetRatio(_seekDone, _seekLeft, pos.TotalSeconds / dur.TotalSeconds);
            _elapsed.Text = Fmt(pos);
            _remaining.Text = "-" + Fmt(dur - pos);
        }

        void SeekPreview(double f)
        {
            var dur = _media.State.Duration;
            if (dur <= TimeSpan.Zero) return;
            SetRatio(_seekDone, _seekLeft, f);
            var pos = TimeSpan.FromTicks((long)(dur.Ticks * f));
            _elapsed.Text = Fmt(pos);
            _remaining.Text = "-" + Fmt(dur - pos);
        }

        void SeekCommit(double f)
        {
            var st = _media.State;
            if (st.Duration <= TimeSpan.Zero) return;
            _media.Seek(st.Start + TimeSpan.FromTicks((long)(st.Duration.Ticks * f)));
            UpdateProgress();
        }

        static string Fmt(TimeSpan t) =>
            t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                              : ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");

        static void SetRatio(ColumnDefinition done, ColumnDefinition left, double f)
        {
            f = Clamp01(f);
            done.Width = new GridLength(f, GridUnitType.Star);
            left.Width = new GridLength(1 - f, GridUnitType.Star);
        }

        // ---------- visualizer: real output peak level, shaped into 4 lively bars ----------

        void UpdateMeterTimer()
        {
            bool want = _hasMedia && !_hiddenForFullscreen && !(InCall && _mode == Mode.Compact) && (_media.State.Playing || _levels.Any(l => l > 0.01));
            if (want) { if (!_meterTimer.IsEnabled) _meterTimer.Start(); }
            else if (_meterTimer.IsEnabled) _meterTimer.Stop();
        }

        void OnMeterTick(object sender, EventArgs e)
        {
            var st = _media.State;
            double peak = st.Playing && !_audio.Muted ? _audio.Peak() : 0;
            _meterT += 0.033;
            bool flat = true;
            var scales = _mode == Mode.Expanded ? _expBarScales : _compactBarScales;
            for (int i = 0; i < 4; i++)
            {
                double wobble = 0.5 + 0.5 * Math.Abs(Math.Sin(_meterT * BarFreq[i] + BarPhase[i]));
                double target = Math.Min(1, Math.Sqrt(peak) * 1.2 * (0.4 + 0.6 * wobble));
                double cur = _levels[i];
                cur += (target - cur) * (target > cur ? 0.55 : 0.16);
                _levels[i] = cur;
                if (cur > 0.01) flat = false;
                scales[i].ScaleY = 0.22 + 0.78 * cur;
            }
            if (!st.Playing && flat) _meterTimer.Stop();
        }

        // ---------- calls ----------

        void OnCallChanged()
        {
            var c = Call;
            UpdateCallUi();
            UpdateCompactContent();
            if (Ringing)
            {
                _forcedOpen = true;
                if (_hiddenForFullscreen) SetFullscreenHidden(false);
                if (_mode != Mode.Expanded) Expand();
                else RefreshExpandedKind();
            }
            else
            {
                bool wasForced = _forcedOpen;
                _forcedOpen = false;
                RefreshExpandedKind();
                if (wasForced && _mode == Mode.Expanded && !CursorOverIsland()) Collapse();
            }
            if (InCall) _callTimer.Start(); else _callTimer.Stop();
            Retarget();
            UpdatePrivacyDot();
            UpdateMeterTimer();
        }

        void UpdateCallUi()
        {
            var c = Call;
            if (c == null) return;
            // calls that come through Phone Link are your phone's calls: show them as "Pixel 8"
            string app = c.App == "Phone Link" && _phone.Configured ? _phone.Name : c.App;
            string who = c.Caller.Length > 0 ? c.Caller : app;
            _incomingName.Text = c.Caller.Length > 0 ? c.Caller : "Incoming call";
            _incomingSub.Text = (app.Length > 0 && app != "Call" ? app + "  ·  " : "") + (c.Video ? "Incoming video call" : "Incoming call");
            _incomingInitials.Text = Initials(who);
            _incomingAppIcon.Source = c.Icon;
            _callTitle.Text = who.Length > 0 ? who : "Call";
            _callIcon.Source = c.Icon;
            _callIconGlyph.Visibility = c.Icon == null ? Visibility.Visible : Visibility.Collapsed;
            _callCompactIcon.Text = c.Video ? "" : "";
            _camChip.Visibility = c.Video ? Visibility.Visible : Visibility.Collapsed;
            UpdateCallTimer();
            UpdateMicUi();
        }

        void UpdateCallTimer()
        {
            var c = Call;
            if (c == null || c.Phase != CallPhase.Active) return;
            var d = DateTime.Now - c.Started;
            if (d < TimeSpan.Zero) d = TimeSpan.Zero;
            string t = Fmt(d);
            _callCompactTimer.Text = t;
            string kind = c.Video ? "Video call" : "Call";
            string app = c.App == "Phone Link" && _phone.Configured ? _phone.Name : c.App;
            _callStatus.Text = kind + (c.Caller.Length > 0 && app.Length > 0 ? "  ·  " + app : "") + "  ·  " + t;
        }

        static string Initials(string name)
        {
            var parts = (name ?? "").Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Where(p => char.IsLetterOrDigit(p[0])).ToArray();
            if (parts.Length == 0) return "?";
            string s = parts[0].Substring(0, 1);
            if (parts.Length > 1) s += parts[parts.Length - 1].Substring(0, 1);
            return s.ToUpperInvariant();
        }

        void UpdateMicUi()
        {
            bool m = _audio.MicMuted;
            _muteButton.Content = m ? "" : "";
            _muteButton.Background = new SolidColorBrush(m ? Colors.White : Color.FromArgb(0x2E, 255, 255, 255));
            _muteButton.Foreground = new SolidColorBrush(m ? Colors.Black : Colors.White);
            _muteLabel.Text = m ? "Unmute" : "Mute";
            _micChipText.Text = m ? "Muted" : "Mic";
            _muteButton.IsEnabled = _audio.MicAvailable;
        }

        void OnMicChanged(bool external)
        {
            UpdateMicUi();
            if (external && _mode == Mode.Compact && !_hiddenForFullscreen)
            {
                bool m = _audio.MicMuted;
                ShowLabelHud("mic", m ? "" : "", m ? "Microphone off" : "Microphone on", "", m ? Orange : Colors.White, 250);
            }
        }

        void UpdatePrivacyDot()
        {
            bool show = (_calls.MicInUse || _calls.CameraInUse) && !InCall && !_hiddenForFullscreen;
            _privacyDot.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _privacyDot.Fill = new SolidColorBrush(_calls.CameraInUse ? Green : Orange);
            _privacyDot.ToolTip = _calls.MicApp;
        }

        // ---------- file shelf ----------

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (_dragging || !e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; return; }
            e.Effects = DragDropEffects.Copy;
            _dropLeaveTimer.Stop();

            // which half of the drop zone is the cursor over?
            bool overSend = false;
            if (_dropSend.IsVisible)
            {
                var pt = e.GetPosition(_dropSend);
                overSend = pt.X >= 0 && pt.Y >= -10 && pt.X <= _dropSend.ActualWidth + 10 && pt.Y <= _dropSend.ActualHeight + 10;
            }
            _dropTarget = overSend ? "send" : "keep";
            _dropKeepBox.Fill = new SolidColorBrush(overSend ? Color.FromArgb(0x0D, 255, 255, 255) : Color.FromArgb(0x2A, 255, 255, 255));
            _dropSendBox.Fill = new SolidColorBrush(overSend ? Color.FromArgb(0x38, 0x30, 0xD1, 0x58) : Color.FromArgb(0x0D, 0x30, 0xD1, 0x58));

            if (_dropActive) return;
            _dropActive = true;
            if (_mode != Mode.Expanded) Expand();
            RefreshExpandedKind();
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            _dropLeaveTimer.Stop();
            if (!_dragging && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                if (_dropTarget == "send" && _phone.Configured) SendToPhone(files, true);
                else
                {
                    _shelf.Add(files);
                    BuildShelf();
                }
            }
            _dropActive = false;
            _cursorVisited = true;
            RefreshExpandedKind();
        }

        // ---------- phone ----------

        void UpdatePhoneUi()
        {
            string name = _phone.Configured ? _phone.Name : "Phone";
            _phoneName.Text = name;
            if (!_phoneFlash.IsEnabled) _phoneStatus.Text = _phone.StatusText;
            _phoneDot.Fill = new SolidColorBrush(_phone.Online ? Green : Gray);
            bool kde = _phone.HasKdeConnect;
            _phoneRingButton.ToolTip = _phone.CanAct ? "Find my phone (rings it)" : "Find my phone (Google Find My Device)";
            _phoneClipButton.Visibility = _phone.CanAct ? Visibility.Visible : Visibility.Collapsed;
            _phoneSendButton.ToolTip = kde ? "Send files to " + name : "Send files with Quick Share";
            _phoneSmsButton.ToolTip = kde ? "Messages" : "Messages (Phone Link)";
            _dropSendText.Text = "Send to " + name;
            bool showSend = _phone.Configured;
            _dropSend.Visibility = showSend ? Visibility.Visible : Visibility.Collapsed;
            _dropSendCol.Width = showSend ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            _shelfSend.Visibility = showSend ? Visibility.Visible : Visibility.Collapsed;
            _shelfSend.Content = "Send to " + name;
        }

        // ---------- phone screen mirroring ----------

        MirrorWindow _mirror;
        bool _mirrorHooked;

        /// <summary>Asks the phone (over Bluetooth) to share its screen; the window opens when the phone answers.</summary>
        public void StartMirror()
        {
            HookMirror();
            if (_mirror != null) { _mirror.Activate(); return; }
            if (!_phone.RequestMirror())
            {
                PhoneFeedback("Phone not connected — open Isle on the phone (Bluetooth on)");
                return;
            }
            PhoneFeedback("Tap “Show your screen” on " + _phone.Name + ", then Start now");
        }

        void HookMirror()
        {
            if (_mirrorHooked || _phone.Link == null) return;
            _mirrorHooked = true;
            _phone.Link.MirrorOffered += (ip, port, token, w, h) =>
            {
                if (_mirror != null) return;
                Collapse();
                _mirror = new MirrorWindow(_phone.Configured ? _phone.Name : "Phone", ip, port, token, w, h);
                _mirror.Closed += (s, e) => { _mirror = null; _phone.Link?.Send("MIRRORSTOP"); };
                _mirror.Show();
                _mirror.Activate();
            };
            _phone.Link.MirrorFailed += why => PhoneFeedback(why);
        }

        /// <summary>Live "Sending x — 42%": a progress bar HUD in the pill, or the phone row when open.</summary>
        void PhoneProgress(string text)
        {
            int pct = 0;
            int i = text.IndexOf('%');
            if (i > 0) int.TryParse(text.Substring(0, i), out pct);
            if (_mode == Mode.Expanded) { _phoneStatus.Text = text; _phoneFlash.Stop(); _phoneFlash.Start(); return; }
            if (_hiddenForFullscreen) return;
            _hudKind = "send";
            _hudIcon.Text = "";
            _hudIcon.Foreground = new SolidColorBrush(Green);
            _hudLabel.Visibility = Visibility.Collapsed;
            _hudBar.Visibility = Visibility.Visible;
            SetRatio(_hudDone, _hudLeft, pct / 100.0);
            _hudValue.Text = pct + "%";
            if (!_hudActive) ShowHud(300);
            _hudTimer.Stop();
            _hudTimer.Interval = TimeSpan.FromSeconds(4);
            _hudTimer.Start();
        }

        /// <summary>Feedback for a phone action: in the phone row when open, as a HUD when it is small.</summary>
        void PhoneFeedback(string text)
        {
            if (_mode == Mode.Expanded)
            {
                _phoneStatus.Text = text;
                _phoneFlash.Stop();
                _phoneFlash.Start();
            }
            else if (!_hiddenForFullscreen) ShowLabelHud("phone", "", text, "", Colors.White, 380);
        }

        /// <summary>Files handed to Isle from Explorer's "Send to" menu (or the command line).</summary>
        public void SendFromExplorer(IList<string> files) => SendToPhone(files, false);

        async void SendToPhone(IList<string> files, bool keepOnFailure)
        {
            App.Log(new Exception("send to phone: " + files.Count + " file(s); link connected=" + _phone.LinkConnected
                                  + " kde reachable=" + (_phone.HasKdeConnect && _phone.Reachable)));
            if (files.Count == 0) return;
            if (!_phone.LinkConnected && !(_phone.HasKdeConnect && _phone.Reachable))
            {
                // no direct link right now: Quick Share it (and keep it on the shelf)
                if (keepOnFailure) { _shelf.Add(files); BuildShelf(); RefreshExpandedKind(); }
                PhoneFeedback("Phone not connected — opening Quick Share");
                _phone.QuickShareFallback(files);
                return;
            }
            PhoneFeedback("Sending " + files.Count + (files.Count == 1 ? " file" : " files") + " to " + _phone.Name + "…");
            int sent = await _phone.SendFiles(files, PhoneProgress);
            if (sent > 0) PhoneFeedback("Sent " + sent + (sent == 1 ? " file" : " files") + " to " + _phone.Name + " — in Downloads/Isle");
            else
            {
                PhoneFeedback("Couldn't send — opening Quick Share");
                if (keepOnFailure) { _shelf.Add(files); BuildShelf(); RefreshExpandedKind(); }
                _phone.QuickShareFallback(files);
            }
        }

        void OnPhoneOnline(bool online)
        {
            UpdatePhoneUi();
            if (_mode != Mode.Compact || _hiddenForFullscreen) return;
            ShowLabelHud("phone", "", _phone.Name + (online ? " connected" : " disconnected"), "", online ? Green : Gray, 300);
        }

        void OnPhoneClipboard(string text)
        {
            UpdatePhoneUi();
            if (_mode == Mode.Expanded) { PhoneFeedback("Copied from phone — press Ctrl+V to paste"); return; }
            if (_hiddenForFullscreen) return;
            ShowLabelHud("clip", "", "From " + _phone.Name + ":  “" + PhoneService.Preview(text, 30) + "”", "Ctrl+V", Green, 410);
            _hudTimer.Stop();
            _hudTimer.Interval = TimeSpan.FromMilliseconds(2800);
            _hudTimer.Start();
        }

        void BuildShelf()
        {
            _shelfRow.Children.Clear();
            var style = (Style)_root.FindResource("AppButton");
            var items = _shelf.Items.AsEnumerable().Reverse().Take(7).ToList();
            foreach (var path in items)
            {
                BitmapSource icon = null;
                try { icon = Native.ShellIcon(path, 64); } catch { }
                var stack = new StackPanel { Width = 62 };
                stack.Children.Add(icon != null
                    ? new Image { Source = icon, Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Center }
                    : Letter(System.IO.Path.GetFileName(path), 30));
                string name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
                stack.Children.Add(new TextBlock
                {
                    Text = name.Length > 0 ? name : path, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)),
                    TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Margin = new Thickness(2, 4, 2, 0),
                });
                var b = new Button
                {
                    Style = style, Width = 66, Height = 64, Content = stack, Margin = new Thickness(0, 0, 3, 0),
                    ToolTip = path + "\nClick to open  ·  drag out to use it  ·  right-click to remove",
                };
                string p = path;
                Point? down = null;
                b.PreviewMouseLeftButtonDown += (s, e) => { down = e.GetPosition(b); _shelfDragged = false; };
                b.PreviewMouseMove += (s, e) =>
                {
                    if (down == null || e.LeftButton != MouseButtonState.Pressed) return;
                    var pos = e.GetPosition(b);
                    if (Math.Abs(pos.X - down.Value.X) + Math.Abs(pos.Y - down.Value.Y) < 6) return;
                    down = null;
                    _shelfDragged = true;
                    _dragging = true;
                    try { DragDrop.DoDragDrop(b, new DataObject(DataFormats.FileDrop, new[] { p }), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
                    catch (Exception ex) { App.Log(ex); }
                    _dragging = false;
                    _shelf.Prune();
                    BuildShelf();
                    RefreshExpandedKind();
                };
                b.Click += (s, e) => { if (!_shelfDragged) { QuickApps.Launch(p); Collapse(); } };
                b.MouseRightButtonUp += (s, e) => { _shelf.Remove(p); BuildShelf(); RefreshExpandedKind(); e.Handled = true; };
                _shelfRow.Children.Add(b);
            }
            if (_shelf.Items.Count > items.Count)
                _shelfRow.Children.Add(new TextBlock
                {
                    Text = "+" + (_shelf.Items.Count - items.Count), FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
                });
        }

        // ---------- volume / brightness / device HUDs ----------

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_cfg.ScrollVolume || !_audio.Available) return;
            _audio.Nudge(e.Delta / 120.0 * 0.02);
            e.Handled = true;
        }

        void OnVolumeChanged(bool external)
        {
            UpdateVolumeUi();
            if ((external && _mode == Mode.Compact && !_hiddenForFullscreen) || (_hudActive && _hudKind == "volume")) ShowVolumeHud();
        }

        void UpdateVolumeUi()
        {
            float v = _audio.Volume;
            bool m = _audio.Muted;
            if (!_volDragging) SetRatio(_volDone, _volLeft, m ? 0 : v);
            _volButton.Content = VolumeGlyph(v, m);
            _volButton.ToolTip = m ? "Unmute" : "Mute";
            _volButton.IsEnabled = _audio.Available;
            _volArea.IsEnabled = _audio.Available;
        }

        static string VolumeGlyph(float v, bool muted) =>
            muted || v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";

        void ShowVolumeHud()
        {
            float v = _audio.Volume;
            bool m = _audio.Muted;
            ShowLevelHud("volume", VolumeGlyph(v, m), m ? 0 : v, m ? "Muted" : ((int)Math.Round(v * 100)).ToString());
        }

        void OnBrightness(int level)
        {
            if (_mode != Mode.Compact || _hiddenForFullscreen) return;
            ShowLevelHud("brightness", "", level / 100.0, level.ToString());
        }

        static bool IsHeadphones(string name) =>
            !string.IsNullOrEmpty(name) && System.Text.RegularExpressions.Regex.IsMatch(name, "airpods|buds|head|ear|wh-|wf-|beats|bluetooth|jbl|sony|bose", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // ---------- earbuds status while connected: row in the open island + battery next to the clock ----------

        void SetBudsConnected(string name)
        {
            if (name == _budsConnectedName) return;
            _budsConnectedName = name;
            _budsBattery = -1;
            _budsLowWarned = false;
            if (name.Length > 0)
            {
                _budsRowArt.Child = new Viewbox { Width = 28, Height = 28, Child = EarbudsArt.Create(name, _cfg.BudsColor) };
                _budsPoll.Start();
                _ = RefreshBudsBattery();
            }
            else _budsPoll.Stop();
            UpdateBudsRow();
            RefreshExpandedKind();
            Retarget();
        }

        async Task RefreshBudsBattery()
        {
            string name = _budsConnectedName;
            if (name.Length == 0) return;
            int pct = await BluetoothBattery.ForDeviceAsync(name);
            if (name != _budsConnectedName || pct == _budsBattery) return;
            SetBudsBattery(pct);
        }

        void SetBudsBattery(int pct)
        {
            _budsBattery = pct;
            UpdateBudsRow();
            if (_mode == Mode.Compact) Retarget();
            if (pct >= 0 && pct <= 15 && !_budsLowWarned && _mode == Mode.Compact && !_hiddenForFullscreen)
            {
                _budsLowWarned = true;
                ShowLabelHud("buds", "", _budsConnectedName + " battery low", pct + "%", Red, 340);
            }
        }

        void UpdateBudsRow()
        {
            bool on = _budsConnectedName.Length > 0;
            if (on)
            {
                int pct = _budsBattery;
                _budsRowName.Text = _budsConnectedName;
                _budsRowStatus.Text = pct >= 0 ? "Connected  ·  " + pct + "% battery" : "Connected  ·  battery not reported yet";
                _budsRowRing.Stroke = new SolidColorBrush(pct >= 0 && pct <= 20 ? Red : Green);
                _budsRowRing.Data = pct >= 0 ? RingGeometry(pct / 100.0, 18, 16.25) : null;
                _budsRowPct.Text = pct >= 0 ? pct.ToString() : "–";
            }
            UpdateClock();
        }

        void OnOutputSwitched(string name)
        {
            SetBudsConnected(IsHeadphones(name) ? name : "");
            if (_hiddenForFullscreen) return;
            bool headphones = IsHeadphones(name);
            if (headphones) { ShowBudsCard(name); return; }
            if (_mode != Mode.Compact) return;
            ShowLabelHud("device", "", name, "Connected", Colors.White, 330);
        }

        // ---------- earbuds connected: AirPods-style card with battery ring ----------

        async void ShowBudsCard(string name, int demoBattery = -2)
        {
            if (Ringing) return;
            _budsName.Text = name;
            _budsSub.Text = "Connected";
            _budsPct.Text = "";
            _budsRing.Data = null;
            _budsRingBox.Opacity = 0.35;
            _budsActive = true;
            if (_mode != Mode.Expanded) Expand(); else RefreshExpandedKind();
            RefreshExpandedKind();

            // a drawing of the actual earbuds swings in (Galaxy Buds Live beans, AirPods stems, round buds, or headphones)
            _budsIconBox.Child = new Viewbox { Width = 74, Height = 74, Child = EarbudsArt.Create(name, _cfg.BudsColor) };
            _budsIconBox.Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255));

            _budsTimer.Stop();
            _budsTimer.Start();

            int pct = demoBattery != -2 ? demoBattery : await BluetoothBattery.ForDeviceAsync(name);
            if (pct < 0 && demoBattery == -2)
            {
                // the buds report their battery a few seconds after connecting
                await Task.Delay(3500);
                pct = await BluetoothBattery.ForDeviceAsync(name);
            }
            if (pct >= 0 && name == _budsConnectedName) SetBudsBattery(pct);
            if (pct >= 0 && _budsActive) AnimateRing(pct);
            else if (_budsActive) _budsSub.Text = "Connected  ·  battery not reported yet";
        }

        void AnimateRing(int pct)
        {
            var color = pct <= 20 ? Red : Green;
            _budsRing.Stroke = new SolidColorBrush(color);
            _budsRingBox.Opacity = 1;
            _budsSub.Text = "Connected  ·  " + pct + "% battery";
            DateTime start = DateTime.Now;
            EventHandler tick = null;
            tick = (s, e) =>
            {
                double t = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / 900.0);
                double eased = 1 - Math.Pow(1 - t, 3);
                double value = pct / 100.0 * eased;
                _budsRing.Data = RingGeometry(value, 29, 26.5);
                _budsPct.Text = ((int)Math.Round(pct * eased)) + "%";
                if (t >= 1) CompositionTarget.Rendering -= tick;
            };
            CompositionTarget.Rendering += tick;
        }

        static Geometry RingGeometry(double fraction, double center, double radius)
        {
            if (fraction <= 0.001) return null;
            if (fraction >= 0.999) return new EllipseGeometry(new Point(center, center), radius, radius);
            double angle = fraction * 2 * Math.PI - Math.PI / 2;
            var start = new Point(center, center - radius);
            var end = new Point(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle));
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(start, false, false);
                c.ArcTo(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        // ---------- volume keys: Isle's pop-up instead of Windows' ----------

        void InstallVolumeKeys()
        {
            if (!_cfg.OwnVolumePopup || _keyHook != IntPtr.Zero || App.SnapshotDir != null) return;
            _keyProc = VolumeKeyHook;
            _keyHook = Native.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, _keyProc, Native.GetModuleHandle(null), 0);
            if (_keyHook == IntPtr.Zero) App.Log(new Exception("Couldn't take over the volume keys"));
        }

        void RemoveVolumeKeys()
        {
            if (_keyHook == IntPtr.Zero) return;
            Native.UnhookWindowsHookEx(_keyHook);
            _keyHook = IntPtr.Zero;
        }

        IntPtr VolumeKeyHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && _audio.Available)
            {
                int vk = System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
                if (vk == 0xAD || vk == 0xAE || vk == 0xAF)   // mute, down, up
                {
                    int msg = wParam.ToInt32();
                    if (msg == 0x100 || msg == 0x104) Dispatcher.BeginInvoke(new Action(() => OnVolumeKey(vk)));
                    return new IntPtr(1);   // swallow it: Windows never shows its own volume pop-up
                }
            }
            return Native.CallNextHookEx(_keyHook, code, wParam, lParam);
        }

        void OnVolumeKey(int vk)
        {
            if (vk == 0xAD) _audio.ToggleMute();
            else _audio.Nudge(vk == 0xAF ? 0.02 : -0.02);
            if (_mode == Mode.Compact && !_hiddenForFullscreen) ShowVolumeHud();
        }

        void ShowLevelHud(string kind, string glyph, double fraction, string value)
        {
            _hudKind = kind;
            _hudIcon.Text = glyph;
            _hudIcon.Foreground = Brushes.White;
            _hudLabel.Visibility = Visibility.Collapsed;
            _hudBar.Visibility = Visibility.Visible;
            SetRatio(_hudDone, _hudLeft, fraction);
            _hudValue.Text = value;
            _hudValue.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255));
            ShowHud(300);
        }

        void ShowLabelHud(string kind, string glyph, string label, string value, Color iconColor, double width)
        {
            _hudKind = kind;
            _hudIcon.Text = glyph;
            _hudIcon.Foreground = new SolidColorBrush(iconColor);
            _hudLabel.Text = label;
            _hudLabel.Visibility = Visibility.Visible;
            _hudBar.Visibility = Visibility.Collapsed;
            _hudValue.Text = value;
            _hudValue.Foreground = new SolidColorBrush(kind == "power" ? iconColor : Color.FromArgb(0x99, 255, 255, 255));
            ShowHud(width);
        }

        // ---------- battery / clock ----------

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.StatusChange) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var p = Native.GetPower();
                RefreshBattery();
                if (!p.HasBattery) return;
                bool changed = _lastOnAc.HasValue && _lastOnAc.Value != p.OnAc;
                _lastOnAc = p.OnAc;
                if (!changed || _mode != Mode.Compact || _hiddenForFullscreen) return;
                ShowLabelHud("power", "", p.OnAc ? "Charging" : "On battery", p.Percent + "%", p.OnAc ? Green : Colors.White, 250);
            }));
        }

        void RefreshBattery()
        {
            var p = Native.GetPower();
            _batteryBlock.Visibility = p.HasBattery ? Visibility.Visible : Visibility.Collapsed;
            if (!p.HasBattery) return;
            _batteryPct.Text = p.Percent + "%";
            SetRatio(_battDone, _battLeft, p.Percent / 100.0);
            _batteryBolt.Visibility = p.OnAc ? Visibility.Visible : Visibility.Collapsed;
            _battFill.Background = new SolidColorBrush(p.OnAc ? Green : p.Percent <= 20 ? Red : Colors.White);
            _batteryState.Text = p.OnAc ? (p.Percent >= 100 ? "Fully charged" : "Charging") : "On battery";
        }

        void UpdateClock()
        {
            var now = DateTime.Now;
            string t = now.ToString(_timeFmt, CultureInfo.CurrentCulture);
            // clock pill: "9:41   🎧 76%" while earbuds are connected
            _idleClock.Inlines.Clear();
            _idleClock.Inlines.Add(new System.Windows.Documents.Run(t));
            if (_budsConnectedName.Length > 0 && _budsBattery >= 0)
            {
                _idleClock.Inlines.Add(new System.Windows.Documents.Run("    "));
                _idleClock.Inlines.Add(new System.Windows.Documents.Run(" ") { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11.5, FontWeight = FontWeights.Normal });
                _idleClock.Inlines.Add(new System.Windows.Documents.Run(_budsBattery + "%")
                {
                    Foreground = new SolidColorBrush(_budsBattery <= 20 ? Red : Green),
                });
            }
            _bigClock.Text = t;
            _dateText.Text = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
            _clockTimer.Interval = TimeSpan.FromSeconds(60.05 - now.Second - now.Millisecond / 1000.0);
            _clockTimer.Start();
        }

        // ---------- fullscreen ----------

        void CheckFullscreen()
        {
            if (_hwnd == IntPtr.Zero || App.SnapshotDir != null) return;
            bool fs = _cfg.HideInFullscreen && !Ringing && Native.IsFullscreenAppOnMonitorOf(_hwnd);
            if (fs != _hiddenForFullscreen) SetFullscreenHidden(fs);
        }

        void SetFullscreenHidden(bool hide)
        {
            _hiddenForFullscreen = hide;
            if (hide && _mode == Mode.Expanded) Collapse();
            _islandRoot.Visibility = hide ? Visibility.Hidden : Visibility.Visible;
            if (!hide) Native.ReassertTopmost(_hwnd);
            UpdateMeterTimer();
            UpdatePrivacyDot();
        }

        // ---------- math ----------

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        static double SmoothStep(double a, double b, double x)
        {
            double t = Clamp01((x - a) / (b - a));
            return t * t * (3 - 2 * t);
        }

        // =====================================================================================
        //  debug snapshots: --snapshot=<folder> renders every state of this window to PNGs, then exits
        // =====================================================================================

        async void RunSnapshots()
        {
            try
            {
                Directory.CreateDirectory(App.SnapshotDir);
                await Task.Delay(2500);
                SaveSnapshot("01-compact.png");
                Expand();
                await Task.Delay(140);
                SaveSnapshot("02-morph-midway.png");
                await Task.Delay(1300);
                SaveSnapshot("03-expanded-media.png");
                Collapse();
                await Task.Delay(1000);
                ShowVolumeHud();
                await Task.Delay(800);
                SaveSnapshot("04-volume-hud.png");
                await Task.Delay(1400);

                var wa = Native.ShellIcon("shell:AppsFolder\\5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", 64);
                _calls.Demo(new CallInfo { Phase = CallPhase.Incoming, App = "WhatsApp", Caller = "Rajveer Chopra", Icon = wa }, false, false);
                await Task.Delay(1400);
                SaveSnapshot("05-incoming-call.png");
                _calls.Demo(new CallInfo { Phase = CallPhase.Active, App = "WhatsApp", Caller = "Rajveer Chopra", Icon = wa, Started = DateTime.Now.AddSeconds(-151) }, true, false);
                await Task.Delay(1200);
                SaveSnapshot("06-call-compact.png");
                Expand();
                await Task.Delay(1300);
                SaveSnapshot("07-call-expanded.png");
                Collapse();
                _calls.Demo(null, true, false);
                await Task.Delay(1200);
                SaveSnapshot("08-privacy-dot.png");

                _shelf.Persist = false;
                string root = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
                _shelf.Add(new[] { System.IO.Path.Combine(root, "README.md"), System.IO.Path.Combine(root, "build.ps1"), System.IO.Path.Combine(root, "src"), Assembly.GetExecutingAssembly().Location });
                BuildShelf();
                _phone.Demo("Pixel 8", null);
                Expand();
                await Task.Delay(1300);
                SaveSnapshot("09-shelf-and-phone.png");
                _dropActive = true;
                RefreshExpandedKind();
                _dropTarget = "send";
                _dropSendBox.Fill = new SolidColorBrush(Color.FromArgb(0x38, 0x30, 0xD1, 0x58));
                await Task.Delay(900);
                SaveSnapshot("10-drop-zone.png");
                _dropActive = false;
                _forcedOpen = false;
                Collapse();
                await Task.Delay(1200);
                _phone.Demo("Pixel 8", "Meet at 7 near the station gate, I'll be in a blue jacket");
                OnPhoneClipboard(_phone.LastClip);
                await Task.Delay(900);
                SaveSnapshot("11-phone-clipboard.png");
                await Task.Delay(3200);
                OnPhoneOnline(true);
                await Task.Delay(900);
                SaveSnapshot("12-phone-connected.png");
                await Task.Delay(1800);
                ShowBudsCard("Galaxy Buds Live (356D)", 76);
                await Task.Delay(1500);
                SaveSnapshot("13-buds-connected.png");
                ShowBudsCard("AirPods Pro", 64);
                await Task.Delay(1500);
                SaveSnapshot("13b-airpods.png");
                _budsActive = false;
                RefreshExpandedKind();
                SetBudsConnected("Galaxy Buds Live (356D)");
                await Task.Delay(1500);
                SetBudsBattery(76);
                Expand();
                await Task.Delay(1500);
                SaveSnapshot("13c-buds-row.png");

                OpenHub();
                await Task.Delay(1200);
                var hubRoot = (FrameworkElement)_hub.Content;
                double hs = VisualTreeHelper.GetDpi(_hub).DpiScaleX;
                var rtb = new RenderTargetBitmap((int)(hubRoot.ActualWidth * hs), (int)(hubRoot.ActualHeight * hs), 96 * hs, 96 * hs, PixelFormats.Pbgra32);
                var bg = new DrawingVisual();
                using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x3A, 0x48)), null, new Rect(0, 0, hubRoot.ActualWidth, hubRoot.ActualHeight));
                rtb.Render(bg);
                rtb.Render(hubRoot);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var f = File.Create(System.IO.Path.Combine(App.SnapshotDir, "14-isle-app-window.png"))) enc.Save(f);
            }
            catch (Exception ex) { App.Log(ex); }
            Application.Current.Shutdown();
        }

        void SaveSnapshot(string name)
        {
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * s), (int)Math.Ceiling(ActualHeight * s), 96 * s, 96 * s, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x7A, 0x8C)), null, new Rect(0, 0, ActualWidth, ActualHeight));
            rtb.Render(bg);
            rtb.Render(_root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var f = File.Create(System.IO.Path.Combine(App.SnapshotDir, name))) enc.Save(f);
            Trace("snapshot " + name);
        }
    }
}
