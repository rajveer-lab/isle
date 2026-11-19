using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Isle
{
    enum MediaChange { Session, Track, Art, Playback, Timeline }

    sealed class MediaState
    {
        public bool HasSession;
        public string Title = "", Artist = "", Album = "", Source = "", SourceId = "";
        public BitmapSource Art, Ambient;
        public Color Accent = MediaService.DefaultAccent;
        public bool Playing, CanToggle, CanPrev, CanNext, CanSeek;
        public TimeSpan Start, End, Position;
        public DateTimeOffset Updated;
        public double Rate = 1;
        public int Index, Count;

        public TimeSpan Duration => End - Start;

        /// <summary>Position extrapolated from the last report, so the progress bar moves smoothly without polling the app.</summary>
        public TimeSpan PositionNow
        {
            get
            {
                var pos = Position;
                if (Playing && Updated.Year > 2000)
                {
                    var elapsed = DateTimeOffset.Now - Updated;
                    if (elapsed > TimeSpan.Zero) pos += TimeSpan.FromTicks((long)(elapsed.Ticks * (Rate > 0 ? Rate : 1)));
                }
                if (End > Start)
                {
                    if (pos > End) pos = End;
                    if (pos < Start) pos = Start;
                }
                return pos;
            }
        }
    }

    /// <summary>Wraps Windows' Global System Media Transport Controls: every app that shows up in the
    /// Windows media flyout (Spotify, Chrome/YouTube, Edge, Media Player, ...) shows up here.</summary>
    sealed class MediaService
    {
        public static readonly Color DefaultAccent = Color.FromRgb(0x7C, 0xF2, 0xC2);

        readonly Dispatcher _ui;
        GlobalSystemMediaTransportControlsSessionManager _mgr;
        readonly List<GlobalSystemMediaTransportControlsSession> _sessions = new List<GlobalSystemMediaTransportControlsSession>();
        readonly List<Action> _unsubscribe = new List<Action>();
        readonly Dictionary<object, bool> _wasPlaying = new Dictionary<object, bool>();
        GlobalSystemMediaTransportControlsSession _cur, _pinned;
        int _propsVersion;

        public MediaState State { get; private set; } = new MediaState();
        public event Action<MediaChange> Changed;

        public MediaService(Dispatcher ui) { _ui = ui; }

        public async Task StartAsync()
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.SessionsChanged += (s, e) => Post(RebuildSessions);
            _mgr.CurrentSessionChanged += (s, e) => Post(() => Pick(false));
            RebuildSessions();
        }

        void Post(Action a) => _ui.BeginInvoke(a);
        void Publish(MediaChange c) => Changed?.Invoke(c);

        void RebuildSessions()
        {
            foreach (var u in _unsubscribe) { try { u(); } catch { } }
            _unsubscribe.Clear();
            _sessions.Clear();
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> list = null;
            try { list = _mgr.GetSessions(); } catch (Exception ex) { App.Log(ex); }
            if (list != null) foreach (var s in list) Hook(s);
            foreach (var key in _wasPlaying.Keys.ToList())
                if (!_sessions.Any(s => ReferenceEquals(s, key))) _wasPlaying.Remove(key);
            Pick(false);
        }

        void Hook(GlobalSystemMediaTransportControlsSession s)
        {
            _sessions.Add(s);
            TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> props = (a, b) => Post(() => OnProps(s));
            TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> playback = (a, b) => Post(() => OnPlayback(s));
            TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> timeline = (a, b) => Post(() => OnTimeline(s));
            try
            {
                s.MediaPropertiesChanged += props;
                s.PlaybackInfoChanged += playback;
                s.TimelinePropertiesChanged += timeline;
            }
            catch (Exception ex) { App.Log(ex); }
            _unsubscribe.Add(() =>
            {
                s.MediaPropertiesChanged -= props;
                s.PlaybackInfoChanged -= playback;
                s.TimelinePropertiesChanged -= timeline;
            });
        }

        bool Has(GlobalSystemMediaTransportControlsSession s) => s != null && _sessions.Any(x => ReferenceEquals(x, s));

        static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }

        /// <summary>Choose which session to show: the one you picked, else whatever is playing, else the last one shown.</summary>
        void Pick(bool forceReload)
        {
            if (_pinned != null && !Has(_pinned)) _pinned = null;
            GlobalSystemMediaTransportControlsSession sys = null;
            try { sys = _mgr.GetCurrentSession(); } catch { }
            if (!Has(sys)) sys = _sessions.FirstOrDefault(x => sys != null && x.SourceAppUserModelId == sys.SourceAppUserModelId);

            var pick = _pinned;
            if (pick == null)
            {
                if (sys != null && IsPlaying(sys)) pick = sys;
                else if (Has(_cur) && IsPlaying(_cur)) pick = _cur;
                else pick = _sessions.FirstOrDefault(IsPlaying);
                if (pick == null) pick = Has(_cur) ? _cur : sys ?? _sessions.FirstOrDefault();
            }
            foreach (var s in _sessions) _wasPlaying[s] = IsPlaying(s);

            if (!ReferenceEquals(pick, _cur) || forceReload)
            {
                _cur = pick;
                LoadCurrent();
            }
            else
            {
                RefreshCounts();
                Publish(MediaChange.Session);
            }
        }

        void LoadCurrent()
        {
            var s = _cur;
            var st = new MediaState();
            State = st;
            if (s == null) { _propsVersion++; Publish(MediaChange.Session); return; }
            st.HasSession = true;
            try { st.SourceId = s.SourceAppUserModelId ?? ""; } catch { }
            st.Source = PrettyName(st.SourceId);
            RefreshCounts();
            ReadPlayback(s, st);
            ReadTimeline(s, st);
            Publish(MediaChange.Session);
            OnProps(s);
        }

        void RefreshCounts()
        {
            State.Count = _sessions.Count;
            State.Index = Math.Max(0, _sessions.FindIndex(x => ReferenceEquals(x, _cur)));
        }

        static void ReadPlayback(GlobalSystemMediaTransportControlsSession s, MediaState st)
        {
            try
            {
                var pi = s.GetPlaybackInfo();
                if (pi == null) return;
                st.Playing = pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var c = pi.Controls;
                st.CanToggle = c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled;
                st.CanPrev = c.IsPreviousEnabled;
                st.CanNext = c.IsNextEnabled;
                st.CanSeek = c.IsPlaybackPositionEnabled;
                st.Rate = pi.PlaybackRate ?? 1.0;
            }
            catch { }
        }

        static void ReadTimeline(GlobalSystemMediaTransportControlsSession s, MediaState st)
        {
            try
            {
                var t = s.GetTimelineProperties();
                if (t == null) return;
                st.Start = t.StartTime;
                st.End = t.EndTime;
                st.Position = t.Position;
                st.Updated = t.LastUpdatedTime;
            }
            catch { }
        }

        void OnPlayback(GlobalSystemMediaTransportControlsSession s)
        {
            bool playing = IsPlaying(s);
            _wasPlaying.TryGetValue(s, out bool was);
            _wasPlaying[s] = playing;
            if (!ReferenceEquals(s, _cur))
            {
                // another app just started playing -> follow it (like the phone island does)
                if (playing && !was) { _pinned = null; _cur = s; LoadCurrent(); }
                return;
            }
            ReadPlayback(s, State);
            ReadTimeline(s, State);
            Publish(MediaChange.Playback);
        }

        void OnTimeline(GlobalSystemMediaTransportControlsSession s)
        {
            if (!ReferenceEquals(s, _cur)) return;
            ReadTimeline(s, State);
            Publish(MediaChange.Timeline);
        }

        async void OnProps(GlobalSystemMediaTransportControlsSession s)
        {
            if (!ReferenceEquals(s, _cur)) return;
            int version = ++_propsVersion;
            GlobalSystemMediaTransportControlsSessionMediaProperties p = null;
            try { p = await s.TryGetMediaPropertiesAsync(); }
            catch (Exception ex) { App.Log(ex); }
            if (version != _propsVersion || !ReferenceEquals(s, _cur) || p == null) return;

            var st = State;
            string title = p.Title ?? "", artist = p.Artist ?? "", album = p.AlbumTitle ?? "";
            if (artist.Length == 0) artist = p.AlbumArtist ?? "";
            bool newTrack = title != st.Title || artist != st.Artist;
            st.Title = title;
            st.Artist = artist;
            st.Album = album;
            if (newTrack && p.Thumbnail == null) { st.Art = null; st.Ambient = null; st.Accent = DefaultAccent; }
            Publish(newTrack ? MediaChange.Track : MediaChange.Art);

            if (p.Thumbnail == null) return;
            try
            {
                var art = await ArtLoader.LoadAsync(p.Thumbnail);
                if (version != _propsVersion || !ReferenceEquals(s, _cur)) return;
                st.Art = art?.Image;
                st.Ambient = art?.Ambient;
                st.Accent = art?.Accent ?? DefaultAccent;
                Publish(MediaChange.Art);
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // ---------------- commands (optimistic so the UI reacts instantly) ----------------

        public async void TogglePlayPause()
        {
            var s = _cur;
            if (s == null) return;
            var st = State;
            st.Position = st.PositionNow;
            st.Updated = DateTimeOffset.Now;
            st.Playing = !st.Playing;
            Publish(MediaChange.Playback);
            try { await s.TryTogglePlayPauseAsync(); } catch (Exception ex) { App.Log(ex); }
            await Task.Delay(900);
            if (ReferenceEquals(s, _cur)) { ReadPlayback(s, State); ReadTimeline(s, State); Publish(MediaChange.Playback); }
        }

        public async void Next()
        {
            var s = _cur;
            if (s == null) return;
            try { await s.TrySkipNextAsync(); } catch (Exception ex) { App.Log(ex); }
        }

        public async void Previous()
        {
            var s = _cur;
            if (s == null) return;
            try { await s.TrySkipPreviousAsync(); } catch (Exception ex) { App.Log(ex); }
        }

        public async void Seek(TimeSpan position)
        {
            var s = _cur;
            if (s == null) return;
            State.Position = position;
            State.Updated = DateTimeOffset.Now;
            Publish(MediaChange.Timeline);
            try { await s.TryChangePlaybackPositionAsync(position.Ticks); } catch (Exception ex) { App.Log(ex); }
        }

        public void CycleSession()
        {
            if (_sessions.Count < 2) return;
            int i = _sessions.FindIndex(x => ReferenceEquals(x, _cur));
            _pinned = _sessions[(i + 1) % _sessions.Count];
            _cur = _pinned;
            LoadCurrent();
        }

        static readonly Dictionary<string, string> ProcessNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Chrome", "chrome" }, { "Edge", "msedge" }, { "Brave", "brave" }, { "Firefox", "firefox" }, { "Opera", "opera" },
            { "Spotify", "Spotify" }, { "VLC", "vlc" }, { "Discord", "Discord" }, { "Telegram", "Telegram" }, { "WhatsApp", "WhatsApp" },
            { "Media Player", "Microsoft.Media.Player" }, { "Apple Music", "AppleMusic" },
        };

        /// <summary>Brings the app that is playing to the front.</summary>
        public void OpenSourceApp()
        {
            string id = State.SourceId;
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                var candidates = new List<string>();
                string bare = Path.GetFileNameWithoutExtension(id.Contains("!") ? "" : id);
                if (bare.Length > 0) candidates.Add(bare);
                if (ProcessNames.TryGetValue(State.Source, out var pn)) candidates.Add(pn);
                foreach (var name in candidates)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        if (p.MainWindowHandle != IntPtr.Zero) { Native.BringToFront(p.MainWindowHandle); return; }
                    }
                }
                if (id.Contains("!")) Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + id) { UseShellExecute = true });
            }
            catch (Exception ex) { App.Log(ex); }
        }

        static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "chrome", "Chrome" }, { "msedge", "Edge" }, { "brave", "Brave" }, { "firefox", "Firefox" },
            { "308046B0AF4A39CB", "Firefox" }, { "opera", "Opera" }, { "spotify", "Spotify" }, { "vlc", "VLC" },
            { "Microsoft.ZuneMusic", "Media Player" }, { "Microsoft.ZuneVideo", "Movies & TV" },
            { "AppleInc.AppleMusicWin", "Apple Music" }, { "SpotifyAB.SpotifyMusic", "Spotify" },
            { "5319275A.WhatsAppDesktop", "WhatsApp" }, { "TelegramMessengerLLP.TelegramDesktop", "Telegram" },
            { "Discord", "Discord" },
        };

        public static string PrettyName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "Media";
            string s = id;
            int bang = s.IndexOf('!');
            if (bang >= 0)
            {
                string pkg = s.Substring(0, bang);
                int us = pkg.LastIndexOf('_');
                s = us > 0 ? pkg.Substring(0, us) : pkg;
            }
            int slash = s.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) s = s.Substring(slash + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            if (Known.TryGetValue(s, out var k)) return k;
            int dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1) s = s.Substring(dot + 1);
            if (Known.TryGetValue(s, out k)) return k;
            return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : "Media";
        }
    }

    sealed class ArtResult
    {
        public BitmapSource Image, Ambient;
        public Color Accent;
    }

    /// <summary>Decodes album art off the UI thread, trims YouTube-style letterbox bars and picks an accent color.</summary>
    static class ArtLoader
    {
        public static async Task<ArtResult> LoadAsync(IRandomAccessStreamReference reference)
        {
            byte[] bytes;
            using (var ras = await reference.OpenReadAsync())
            using (var stream = ras.AsStreamForRead())
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            if (bytes.Length == 0) return null;
            return await Task.Run(() => Decode(bytes));
        }

        static ArtResult Decode(byte[] bytes)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = new MemoryStream(bytes);
            bi.DecodePixelWidth = 240;
            bi.EndInit();
            bi.Freeze();

            BitmapSource img = bi;
            var crop = FindLetterbox(bi);
            if (crop.HasValue)
            {
                var cb = new CroppedBitmap(bi, crop.Value);
                cb.Freeze();
                img = cb;
            }

            var tiny = Pixels(img, 16, out int tw, out int th);
            var ambient = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Bgra32, null, tiny, tw * 4);
            ambient.Freeze();
            return new ArtResult { Image = img, Ambient = ambient, Accent = PickAccent(tiny) };
        }

        static byte[] Pixels(BitmapSource src, int size, out int w, out int h)
        {
            var scaled = new TransformedBitmap(src, new ScaleTransform((double)size / src.PixelWidth, (double)size / src.PixelHeight));
            var conv = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            w = conv.PixelWidth;
            h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            return px;
        }

        /// <summary>Detects black bars above/below the picture (common on YouTube thumbnails).</summary>
        static Int32Rect? FindLetterbox(BitmapSource src)
        {
            const int n = 64;
            var px = Pixels(src, n, out int w, out int h);
            bool DarkRow(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])) > 22) return false;
                }
                return true;
            }
            int top = 0, bottom = 0;
            while (top < h / 2 && DarkRow(top)) top++;
            while (bottom < h / 2 && DarkRow(h - 1 - bottom)) bottom++;
            if (top + bottom < 4 || top + bottom > h * 0.7) return null;
            int y0 = (int)Math.Ceiling((double)top / h * src.PixelHeight) + 1;
            int y1 = (int)Math.Floor((double)(h - bottom) / h * src.PixelHeight) - 1;
            if (y1 - y0 < 8) return null;
            return new Int32Rect(0, y0, src.PixelWidth, y1 - y0);
        }

        static Color PickAccent(byte[] px)
        {
            double sr = 0, sg = 0, sb = 0, sw = 0;
            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double sat = max <= 0 ? 0 : (max - min) / max;
                double weight = sat * sat * max + 0.002;
                sr += r * weight; sg += g * weight; sb += b * weight; sw += weight;
            }
            if (sw <= 0) return MediaService.DefaultAccent;
            ToHsv(sr / sw, sg / sw, sb / sw, out double hue, out double s, out double v);
            if (s < 0.14) return Color.FromRgb(0xE9, 0xE9, 0xEE);
            // keep the art's own hue and most of its richness (not a washed-out pastel), bright enough to read on black
            s = Math.Max(0.55, Math.Min(0.85, s * 1.15));
            v = Math.Max(0.88, v);
            return FromHsv(hue, s, v);
        }

        static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0) { h = 0; return; }
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        static Color FromHsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
    }
}
