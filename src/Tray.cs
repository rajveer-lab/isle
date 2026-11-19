using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace Isle
{
    sealed class Tray : IDisposable
    {
        readonly WF.NotifyIcon _icon;

        public Tray(IslandWindow island)
        {
            var menu = new WF.ContextMenuStrip();
            var header = new WF.ToolStripMenuItem("Open Isle") { Font = new System.Drawing.Font(WF.SystemInformation.MenuFont, System.Drawing.FontStyle.Bold) };
            header.Click += (s, e) => island.OpenHub();
            var showIsland = new WF.ToolStripMenuItem("Show the island\tCtrl+Alt+I");
            showIsland.Click += (s, e) => island.ToggleExpanded();
            var notch = new WF.ToolStripMenuItem("Notch style (attached to top)");
            var floating = new WF.ToolStripMenuItem("Floating island style");
            var minimal = new WF.ToolStripMenuItem("Minimal when idle");
            var startup = new WF.ToolStripMenuItem("Start with Windows");
            var edit = new WF.ToolStripMenuItem("Edit settings && quick apps...");
            var reload = new WF.ToolStripMenuItem("Reload settings");
            var exit = new WF.ToolStripMenuItem("Exit");

            menu.Items.AddRange(new WF.ToolStripItem[]
            {
                header, showIsland, new WF.ToolStripSeparator(), notch, floating, minimal, new WF.ToolStripSeparator(),
                startup, edit, reload, new WF.ToolStripSeparator(), exit,
            });

            menu.Opening += (s, e) =>
            {
                notch.Checked = island.Settings.IsNotch;
                floating.Checked = !island.Settings.IsNotch;
                minimal.Checked = island.Settings.MinimalIdle;
                startup.Checked = StartupEnabled;
            };
            notch.Click += (s, e) => island.ChangeSetting("style", "notch");
            floating.Click += (s, e) => island.ChangeSetting("style", "island");
            minimal.Click += (s, e) => island.ChangeSetting("idle", island.Settings.MinimalIdle ? "clock" : "minimal");
            startup.Click += (s, e) => SetStartup(!StartupEnabled);
            edit.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Settings.FilePath + "\"") { UseShellExecute = true }); }
                catch (Exception ex) { App.Log(ex); }
            };
            reload.Click += (s, e) => island.ReloadSettings();
            exit.Click += (s, e) => App.Quit();

            _icon = new WF.NotifyIcon
            {
                Text = "Isle",
                Icon = LoadIcon(),
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) island.ToggleExpanded(); };
        }

        static System.Drawing.Icon LoadIcon()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Isle.isle.ico"))
                    return new System.Drawing.Icon(s, WF.SystemInformation.SmallIconSize);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                return System.Drawing.SystemIcons.Application;
            }
        }

        static bool StartupEnabled => AutoStart.Enabled;
        static void SetStartup(bool on) => AutoStart.Set(on);

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }

    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue("Isle") is string;
            }
        }

        public static void Set(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("Isle", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else k.DeleteValue("Isle", false);
                }
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }
}
