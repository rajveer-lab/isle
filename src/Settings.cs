using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Isle
{
    sealed class Settings
    {
        public string Style = "notch";      // notch | island
        public string Idle = "clock";       // clock | minimal
        public int HoverDelayMs = 500;
        public int CollapseDelayMs = 300;
        public bool HideInFullscreen = true;
        public bool ScrollVolume = true;
        public bool TrackPeek = true;
        public bool CompactTitle = false;
        public bool PhoneEnabled = true;
        public string PhoneName = "";
        public bool StartKdeConnect = true;
        public bool UseKdeConnect = true;
        public bool IsleLink = true;
        public bool OwnVolumePopup = true;
        public bool OpenOnEdge = true;
        public bool FadeOnHover = true;
        public bool Glass = true;
        public string BudsColor = "black";
        public readonly List<KeyValuePair<string, string>> Apps = new List<KeyValuePair<string, string>>();

        public bool IsNotch => !Style.Equals("island", StringComparison.OrdinalIgnoreCase);
        public bool MinimalIdle => Idle.Equals("minimal", StringComparison.OrdinalIgnoreCase);

        public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Isle");
        public static string FilePath => Path.Combine(Dir, "settings.ini");

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                EnsureFile();
                string section = "";
                bool hasVersion = false;
                int version = 1;
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                    if (section == "apps") { s.Apps.Add(new KeyValuePair<string, string>(key, val)); continue; }
                    switch (key.ToLowerInvariant())
                    {
                        case "style": s.Style = val; break;
                        case "idle": s.Idle = val; break;
                        case "hover_delay_ms": s.HoverDelayMs = Int(val, s.HoverDelayMs, 0, 3000); break;
                        case "collapse_delay_ms": s.CollapseDelayMs = Int(val, s.CollapseDelayMs, 0, 10000); break;
                        case "hide_in_fullscreen": s.HideInFullscreen = Bool(val, s.HideInFullscreen); break;
                        case "scroll_volume": s.ScrollVolume = Bool(val, s.ScrollVolume); break;
                        case "track_peek": s.TrackPeek = Bool(val, s.TrackPeek); break;
                        case "compact_title": s.CompactTitle = Bool(val, s.CompactTitle); break;
                        case "phone": s.PhoneEnabled = !val.Equals("off", StringComparison.OrdinalIgnoreCase); break;
                        case "phone_name": s.PhoneName = val; break;
                        case "start_kdeconnect": s.StartKdeConnect = Bool(val, s.StartKdeConnect); break;
                        case "use_kdeconnect": s.UseKdeConnect = Bool(val, s.UseKdeConnect); break;
                        case "isle_link": s.IsleLink = Bool(val, s.IsleLink); break;
                        case "own_volume_popup": s.OwnVolumePopup = Bool(val, s.OwnVolumePopup); break;
                        case "open_trigger": s.OpenOnEdge = !val.Equals("hover", StringComparison.OrdinalIgnoreCase); break;
                        case "fade_on_hover": s.FadeOnHover = Bool(val, s.FadeOnHover); break;
                        case "glass": s.Glass = Bool(val, s.Glass); break;
                        case "buds_color": s.BudsColor = val; break;
                        case "settings_version": hasVersion = true; version = Int(val, 1, 1, 99); break;
                    }
                }
                if (!hasVersion)
                {
                    // v2: the pill became click-through, so it opens on a longer rest instead of a quick hover
                    Write("hover_delay_ms", "500");
                    Write("collapse_delay_ms", "300");
                    s.HoverDelayMs = 500;
                    s.CollapseDelayMs = 300;
                    version = 2;
                }
                if (version < 3)
                {
                    // v3: phone integration (KDE Connect / Bluetooth / Phone Link)
                    Write("phone", "auto");
                    Write("start_kdeconnect", "true");
                    version = 3;
                }
                if (version < 4)
                {
                    // v4: phone can work without KDE Connect (Phone Link + Quick Share + Bluetooth + Find My Device)
                    Write("use_kdeconnect", "true");
                    Write("settings_version", "4");
                }
            }
            catch (Exception ex) { App.Log(ex); }
            return s;
        }

        /// <summary>Rewrites a single "key = value" line in the general section, keeping comments and apps intact.</summary>
        public static void Write(string key, string value)
        {
            try
            {
                EnsureFile();
                var lines = new List<string>(File.ReadAllLines(FilePath));
                int insertAt = 0;
                for (int i = 0; i < lines.Count; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[")) { insertAt = i; break; }
                    insertAt = i + 1;
                    int eq = t.IndexOf('=');
                    if (eq > 0 && t[0] != '#' && t.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = key + " = " + value;
                        File.WriteAllLines(FilePath, lines, new UTF8Encoding(false));
                        return;
                    }
                }
                lines.Insert(insertAt, key + " = " + value);
                File.WriteAllLines(FilePath, lines, new UTF8Encoding(false));
            }
            catch (Exception ex) { App.Log(ex); }
        }

        /// <summary>Replaces the [apps] section (keeps everything above it and the comment lines under the header).</summary>
        public static void WriteApps(IEnumerable<KeyValuePair<string, string>> apps)
        {
            try
            {
                EnsureFile();
                var lines = new List<string>(File.ReadAllLines(FilePath));
                int header = lines.FindIndex(l => l.Trim().Equals("[apps]", StringComparison.OrdinalIgnoreCase));
                var result = new List<string>();
                if (header < 0) { result.AddRange(lines); result.Add(""); result.Add("[apps]"); }
                else
                {
                    result.AddRange(lines.Take(header + 1));
                    for (int i = header + 1; i < lines.Count && lines[i].TrimStart().StartsWith("#"); i++) result.Add(lines[i]);
                }
                foreach (var a in apps) result.Add(a.Key.Replace("=", "-") + " = " + a.Value);
                File.WriteAllLines(FilePath, result, new UTF8Encoding(false));
            }
            catch (Exception ex) { App.Log(ex); }
        }

        static void EnsureFile()
        {
            if (File.Exists(FilePath)) return;
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, DefaultText, new UTF8Encoding(false));
        }

        static int Int(string v, int fallback, int min, int max) =>
            int.TryParse(v, out int n) ? Math.Max(min, Math.Min(max, n)) : fallback;

        static bool Bool(string v, bool fallback)
        {
            switch (v.Trim().ToLowerInvariant())
            {
                case "true": case "yes": case "on": case "1": return true;
                case "false": case "no": case "off": case "0": return false;
                default: return fallback;
            }
        }

        const string DefaultText =
@"settings_version = 4
# Isle - your own dynamic island
# Edit, save, then right-click the tray icon and choose ""Reload settings"".

# notch  = attached to the top edge of the screen
# island = floating pill a few pixels below the edge
style = notch

# What the island shows when nothing is playing: clock | minimal
idle = clock

# The small pill never blocks clicks (Chrome tabs under it keep working).
# It opens when the cursor rests on it this long without clicking; Ctrl+Alt+I also opens it.
hover_delay_ms = 500
collapse_delay_ms = 300

# Hide while a fullscreen game / video / presentation is in front
hide_in_fullscreen = true

# edge  = the island opens when you push the cursor against the very top edge of the screen (Chrome-friendly)
# hover = it opens when the cursor rests anywhere on the pill
open_trigger = edge
# the pill fades almost invisible while the cursor is over it, so the browser tab underneath shows
fade_on_hover = true
# the open island is frosted glass (what's behind it shows through, blurred). false = solid black
glass = true

# colour of the earbuds drawn when they connect: black | white | bronze | blue | red | silver
buds_color = black

# Volume keys show Isle's pop-up instead of Windows' own one
own_volume_popup = true

# Mouse wheel over the island changes the master volume
scroll_volume = true

# Briefly widen the island to show the new song when the track changes
track_peek = true

# false = iPhone style pill (album art + bars only), true = also show the song title in the pill
compact_title = false

# Your phone (uses KDE Connect, Bluetooth and Phone Link). auto | off
phone = auto
# leave empty to use the phone paired in KDE Connect / Bluetooth
phone_name =
# Isle Link: Isle's own Bluetooth connection to the Isle for Android app (clipboard, battery, ring)
isle_link = true
# use KDE Connect (adds phone clipboard, ring over Wi-Fi, direct file send). false = Microsoft/Google only:
# Phone Link (calls, messages, photos), Quick Share (files), Google Find My Device (ring), Bluetooth (status)
use_kdeconnect = true
# start KDE Connect in the background together with Isle
start_kdeconnect = true

[apps]
# Name = AppID (run Get-StartApps in PowerShell to list them), a full path to an .exe/.lnk, or a https:// URL
Chrome = Chrome
VS Code = Microsoft.VisualStudioCode
Terminal = Microsoft.WindowsTerminal_8wekyb3d8bbwe!App
File Explorer = Microsoft.Windows.Explorer
WhatsApp = 5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App
Telegram = TelegramMessengerLLP.TelegramDesktop_t4vj0pshhgkwm!Telegram.TelegramDesktop.Store
Discord = com.squirrel.Discord.Discord
Settings = windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel
";
    }
}
