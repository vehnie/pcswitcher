using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MonitorAutoSwitch
{
    /// Glue between the USB watcher and the switcher: debounce + switching rules + pause. UI-thread only
    /// (needs a message loop for the watcher window and the debounce timer).
    class Agent : IDisposable
    {
        public Config Config { get; private set; }
        public readonly Switcher Switcher;
        UsbWatcher watcher;
        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
        bool? settledPresent;
        bool paused;
        SynchronizationContext ui;
        public string LastResult = "none yet";
        public event Action StateChanged;

        public Agent(Config config)
        {
            Config = config;
            Switcher = new Switcher(config);
            debounce.Tick += (s, e) => Settle();
        }

        public bool Paused
        {
            get { return paused; }
            set
            {
                paused = value;
                Log.Info(paused ? "Automatic switching PAUSED" : "Automatic switching RESUMED");
                RaiseStateChanged();
            }
        }

        public void Start()
        {
            ui = SynchronizationContext.Current;
            if (Config.Sentinels.Count == 0)
                Log.Warn("No sentinels configured - automatic switching is inactive. Run `mas list-usb` and set `sentinels` in " + Config.FilePath);
            Log.Info(string.Format("Config: {0} monitor(s), debounce {1} ms, retries {2}, on_connect={3}, on_disconnect={4}",
                Config.Monitors.Count, Config.DebounceMs, Config.Retries, Lower(Config.OnConnect), Lower(Config.OnDisconnect)));

            watcher = new UsbWatcher();
            watcher.DeviceChanged += OnUsb;
            var present = Usb.PresentSentinels(Config);
            settledPresent = present.Count > 0;   // no switch at startup; only on transitions
            Log.Info("USB watcher started; sentinel " + (present.Count > 0
                ? "PRESENT: " + string.Join(", ", present.Select(d => d.VidPid + " " + d.Name))
                : "ABSENT"));

            SystemEvents.PowerModeChanged += OnPower;
            RaiseStateChanged();
        }

        static string Lower(bool b) { return b ? "true" : "false"; }

        void OnPower(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) Log.Info("System going to sleep");
            if (e.Mode == PowerModes.Resume) Log.Info("System woke; sentinel " + (Usb.PresentSentinels(Config).Count > 0 ? "present" : "absent"));
        }

        public void ReloadConfig()
        {
            try
            {
                var c = Config.Load();
                Stop();
                Config = c;
                Switcher.Config = c;
                Log.Info("Config reloaded");
                Start();
            }
            catch (Exception e)
            {
                Log.Error("Config reload failed, keeping previous config: " + e.Message);
            }
        }

        public void ManualSwitch(Target target)
        {
            Log.Info("Manual switch requested: to " + Switcher.Label(target));
            Switcher.SwitchAll(target, "manual", Finished);
        }

        void OnUsb(bool arrived, int vid, int pid, string path)
        {
            if (!Config.IsSentinel(vid, pid)) return;
            Log.Info(string.Format("USB {0}: {1:x4}:{2:x4} (sentinel); debouncing {3} ms", arrived ? "connected" : "disconnected", vid, pid, Config.DebounceMs));
            debounce.Stop();
            debounce.Interval = Math.Max(1, Config.DebounceMs);
            debounce.Start();
            RaiseStateChanged();
        }

        void Settle()
        {
            debounce.Stop();
            bool present = Usb.PresentSentinels(Config).Count > 0;
            try
            {
                if (settledPresent == present)
                {
                    Log.Info("Settled sentinel state unchanged (" + (present ? "present" : "absent") + "); no action");
                    return;
                }
                settledPresent = present;
                if (paused) { Log.Info("Sentinel now " + (present ? "present" : "absent") + " but switching is paused; no action"); return; }
                if (present)
                {
                    if (!Config.OnConnect) { Log.Info("Sentinel connected; on_connect rule disabled, no action"); return; }
                    Switcher.SwitchAll(Target.Pc, "sentinel connected to this PC", Finished);
                }
                else
                {
                    if (!Config.OnDisconnect) { Log.Info("Sentinel disconnected; on_disconnect rule disabled, no action"); return; }
                    Switcher.SwitchAll(Target.Mac, "sentinel disconnected from this PC", Finished);
                }
            }
            finally { RaiseStateChanged(); }
        }

        void Finished(string summary)
        {
            LastResult = summary + " at " + DateTime.Now.ToString("HH:mm:ss");
            RaiseStateChanged();
        }

        void RaiseStateChanged()
        {
            var h = StateChanged;
            if (h == null) return;
            if (ui != null) ui.Post(_ => h(), null); else h();
        }

        void Stop()
        {
            debounce.Stop();
            SystemEvents.PowerModeChanged -= OnPower;
            if (watcher != null) { watcher.Dispose(); watcher = null; }
        }

        public void Dispose() { Stop(); debounce.Dispose(); }
    }

    // ---- Tray UI ----

    class Tray : IDisposable
    {
        readonly Agent agent;
        readonly NotifyIcon icon = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly Icon activeIcon = MakeIcon(Color.FromArgb(46, 160, 67));
        readonly Icon pausedIcon = MakeIcon(Color.Gray);

        public Tray(Agent agent)
        {
            this.agent = agent;
            menu.Opening += (s, e) => { Rebuild(); e.Cancel = false; };
            menu.Items.Add("...");   // must be non-empty for Opening to fire
            icon.ContextMenuStrip = menu;
            icon.Visible = true;
            agent.StateChanged += Refresh;
            Refresh();
        }

        void Refresh()
        {
            icon.Icon = agent.Paused ? pausedIcon : activeIcon;
            string tip = "Monitor Auto-Switch" + (agent.Paused ? " (paused)" : "") + "\nLast: " + agent.LastResult;
            icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;   // NotifyIcon limit
        }

        void Rebuild()
        {
            menu.Items.Clear();
            string sentinel;
            if (agent.Config.Sentinels.Count == 0) sentinel = "Sentinel: not configured";
            else sentinel = "Sentinel: " + (Usb.PresentSentinels(agent.Config).Count > 0 ? "connected to this PC" : "not connected");
            Disabled(sentinel);
            Disabled("Last switch: " + agent.LastResult);
            if (agent.Paused) Disabled("Automatic switching is paused");
            menu.Items.Add(new ToolStripSeparator());
            Item("Switch monitors to Mac now", () => agent.ManualSwitch(Target.Mac));
            Item("Switch monitors to PC now", () => agent.ManualSwitch(Target.Pc));
            Item("Pause automatic switching", () => agent.Paused = !agent.Paused).Checked = agent.Paused;
            menu.Items.Add(new ToolStripSeparator());
            Item("Open config", () => Open(Config.FilePath));
            Item("Reload config", agent.ReloadConfig);
            Item("Open log", () => Open(Log.FilePath));
            Item("Start at login", ToggleLogin).Checked = Autostart.IsInstalled;
            menu.Items.Add(new ToolStripSeparator());
            Item("Quit", () => { Log.Info("Quit from tray"); Application.Exit(); });
        }

        void Disabled(string text) { menu.Items.Add(new ToolStripMenuItem(text) { Enabled = false }); }

        ToolStripMenuItem Item(string text, Action action)
        {
            var i = new ToolStripMenuItem(text);
            i.Click += (s, e) => action();
            menu.Items.Add(i);
            return i;
        }

        static void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"")); }
            catch (Exception e) { Log.Error("Could not open " + path + ": " + e.Message); }
        }

        void ToggleLogin()
        {
            try { if (Autostart.IsInstalled) Autostart.Uninstall(false); else Autostart.Install(false); }
            catch (Exception e) { Log.Error("Start-at-login toggle failed: " + e.Message); }
        }

        /// Two little monitors on a coloured tile.
        static Icon MakeIcon(Color color)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var fill = new SolidBrush(color))
                using (var white = new SolidBrush(Color.White))
                {
                    g.Clear(Color.Transparent);
                    g.FillRectangle(fill, 0, 1, 16, 14);
                    g.FillRectangle(white, 2, 4, 5, 5);
                    g.FillRectangle(white, 9, 4, 5, 5);
                    g.FillRectangle(white, 4, 10, 1, 2);
                    g.FillRectangle(white, 11, 10, 1, 2);
                    g.FillRectangle(white, 2, 12, 5, 1);
                    g.FillRectangle(white, 9, 12, 5, 1);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
        }
    }

    // ---- Start at login: Task Scheduler (preferred), HKCU Run key as fallback ----

    static class Autostart
    {
        const string TaskName = "MonitorAutoSwitch";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// The windowless build next to this exe (MonitorAutoSwitch.exe); falls back to this exe.
        public static string AgentExe
        {
            get
            {
                string self = Process.GetCurrentProcess().MainModule.FileName;
                string sibling = Path.Combine(Path.GetDirectoryName(self), "MonitorAutoSwitch.exe");
                return File.Exists(sibling) ? sibling : self;
            }
        }

        static bool TaskExists { get { return Schtasks("/Query /TN " + TaskName) == 0; } }

        static bool RunKeyExists
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(TaskName) != null;
            }
        }

        public static bool IsInstalled { get { return TaskExists || RunKeyExists; } }

        public static string Install(bool startNow)
        {
            string exe = AgentExe;
            string user = WindowsIdentity.GetCurrent().Name;
            string xml = @"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Monitor Auto-Switch agent: switches monitor inputs when the USB switch moves.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + @"</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>" + SecurityElement.Escape(user) + @"</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RestartOnFailure><Interval>PT1M</Interval><Count>10</Count></RestartOnFailure>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author""><Exec><Command>" + SecurityElement.Escape(exe) + @"</Command><Arguments>agent</Arguments></Exec></Actions>
</Task>";
            string tmp = Path.Combine(Path.GetTempPath(), "MonitorAutoSwitch-task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int rc = Schtasks("/Create /TN " + TaskName + " /XML \"" + tmp + "\" /F");
            try { File.Delete(tmp); } catch { }

            string how;
            if (rc == 0)
            {
                how = "Task Scheduler task '" + TaskName + "'";
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) { if (k != null) k.DeleteValue(TaskName, false); }
                if (startNow) Schtasks("/Run /TN " + TaskName);
            }
            else
            {
                Log.Warn("schtasks /Create failed (" + rc + "); using the HKCU Run key instead");
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.SetValue(TaskName, "\"" + exe + "\" agent");
                how = @"HKCU\" + RunKey + @"\" + TaskName;
                if (startNow) Process.Start(exe, "agent");
            }
            Log.Info("Start at login installed via " + how + " -> " + exe);
            return how;
        }

        public static void Uninstall(bool stopRunning)
        {
            if (TaskExists)
            {
                if (stopRunning) Schtasks("/End /TN " + TaskName);
                Schtasks("/Delete /TN " + TaskName + " /F");
            }
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) { if (k != null) k.DeleteValue(TaskName, false); }
            if (stopRunning)
            {
                int self = Process.GetCurrentProcess().Id;
                foreach (var p in Process.GetProcessesByName("MonitorAutoSwitch").Where(p => p.Id != self))
                {
                    try { p.Kill(); } catch { }
                }
            }
            Log.Info("Start at login removed");
        }

        static int Schtasks(string args)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                // /Query runs every time the tray menu opens; only log the changes.
                if (!args.StartsWith("/Query")) Log.Debug("schtasks " + args + " -> " + p.ExitCode + " " + output.Trim());
                return p.ExitCode;
            }
        }
    }
}
