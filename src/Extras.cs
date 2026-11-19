using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Windows.Threading;

namespace Isle
{
    /// <summary>Laptop screen brightness changes (Fn keys / slider), straight from the display driver via WMI events.</summary>
    sealed class BrightnessWatcher : IDisposable
    {
        ManagementEventWatcher _watcher;
        public event Action<int> Changed;   // UI thread, 0..100

        public void Start(Dispatcher ui)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var w = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                    w.EventArrived += (s, e) =>
                    {
                        try
                        {
                            int level = Convert.ToInt32(e.NewEvent["Brightness"]);
                            ui.BeginInvoke(new Action(() => Changed?.Invoke(level)));
                        }
                        catch { }
                    };
                    w.Start();
                    _watcher = w;
                }
                catch (Exception ex) { App.Log(ex); }   // desktops without a built-in panel: no brightness HUD
            });
        }

        public void Dispose()
        {
            try { _watcher?.Stop(); _watcher?.Dispose(); } catch { }
        }
    }

    /// <summary>Battery level Windows reads from Bluetooth earbuds/headsets (the same number Settings › Bluetooth shows).</summary>
    static class BluetoothBattery
    {
        const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        const string BluetoothClass = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";

        /// <summary>-1 when Windows doesn't know (not connected yet, or the device doesn't report it).</summary>
        public static async System.Threading.Tasks.Task<int> ForDeviceAsync(string name)
        {
            try
            {
                string key = name;
                int paren = key.IndexOf(" (", StringComparison.Ordinal);
                if (paren > 4) key = key.Substring(0, paren);   // "Galaxy Buds Live (356D)" -> "Galaxy Buds Live"
                var nodes = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                    "System.Devices.ClassGuid:=\"" + BluetoothClass + "\"", new[] { BatteryKey },
                    Windows.Devices.Enumeration.DeviceInformationKind.Device);
                int best = -1;
                foreach (var n in nodes)
                {
                    if (n.Name == null || n.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (n.Properties.TryGetValue(BatteryKey, out object v) && v != null)
                    {
                        int pct = Convert.ToInt32(v);
                        if (pct >= 0 && pct <= 100) best = Math.Max(best, pct);
                    }
                }
                return best;
            }
            catch (Exception ex) { App.Log(ex); return -1; }
        }
    }

    /// <summary>File shelf: drop files on the island to park them, drag them back out later (references, not copies).</summary>
    sealed class Shelf
    {
        public readonly List<string> Items = new List<string>();
        public bool Persist = true;
        static string FilePath => Path.Combine(Settings.Dir, "shelf.txt");

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    string p = line.Trim();
                    if (p.Length > 0 && Exists(p) && !Contains(p)) Items.Add(p);
                }
            }
            catch (Exception ex) { App.Log(ex); }
        }

        public void Add(IEnumerable<string> paths)
        {
            foreach (var p in paths) if (Exists(p) && !Contains(p)) Items.Add(p);
            while (Items.Count > 30) Items.RemoveAt(0);
            Save();
        }

        public void Remove(string path) { Items.RemoveAll(i => i.Equals(path, StringComparison.OrdinalIgnoreCase)); Save(); }
        public void Clear() { Items.Clear(); Save(); }
        public void Prune() { if (Items.RemoveAll(p => !Exists(p)) > 0) Save(); }

        bool Contains(string p) => Items.Any(i => i.Equals(p, StringComparison.OrdinalIgnoreCase));
        static bool Exists(string p) { try { return File.Exists(p) || Directory.Exists(p); } catch { return false; } }

        void Save()
        {
            if (!Persist) return;
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                File.WriteAllLines(FilePath, Items);
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }
}
