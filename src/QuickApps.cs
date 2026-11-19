using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace Isle
{
    sealed class QuickApp
    {
        public string Name, Target;
        public BitmapSource Icon;
    }

    static class QuickApps
    {
        /// <summary>Turns the [apps] entries into launchable items, skipping apps that are not installed.</summary>
        public static List<QuickApp> Resolve(IEnumerable<KeyValuePair<string, string>> entries)
        {
            var list = new List<QuickApp>();
            foreach (var kv in entries)
            {
                string target = Environment.ExpandEnvironmentVariables(kv.Value.Trim().Trim('"'));
                if (target.Length == 0) continue;
                BitmapSource icon = null;
                if (!IsUrl(target))
                {
                    try { icon = Native.ShellIcon(IsPath(target) ? target : "shell:AppsFolder\\" + target, 64); }
                    catch (Exception ex) { App.Log(ex); }
                    if (icon == null && !IsPath(target)) continue;
                }
                list.Add(new QuickApp { Name = kv.Key, Target = target, Icon = icon });
            }
            return list;
        }

        public static void Launch(string target)
        {
            try
            {
                if (IsUrl(target) || IsPath(target))
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + target) { UseShellExecute = true });
            }
            catch (Exception ex) { App.Log(ex); }
        }

        static bool IsUrl(string t) =>
            t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        static bool IsPath(string t)
        {
            try { return File.Exists(t) || Directory.Exists(t); }
            catch { return false; }
        }
    }
}
