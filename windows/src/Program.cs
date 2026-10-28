using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MonitorAutoSwitch
{
    static class Program
    {
        static readonly string Usage = @"mas - Monitor Auto-Switch (Windows agent)

Usage:
  mas agent                 run the tray agent (MonitorAutoSwitch.exe does this with no arguments)
  mas watch                 run in the foreground with verbose logging (no tray icon)
  mas list-usb              list connected USB devices with VID:PID
  mas list-monitors         list monitors, EDID info, DDC reachability, current input
  mas get-input <mon>       read the current input (VCP 0x60 or the monitor's vcp_code)
  mas set-input <mon> <v>   write an input value, e.g. 0x11
  mas get-vcp <mon> <code>  read any VCP code, e.g. 0x10 (brightness)
  mas to-mac | to-pc        switch all configured monitors now
  mas init-config [--force] write a starter config with the connected monitors filled in
  mas install-agent         start at login (Task Scheduler) and start it now
  mas uninstall-agent       remove start at login and stop the agent

<mon> is a config name, a number from `list-monitors`, or the product name.
Config: " + Config.FilePath + @"
Log:    " + Log.FilePath;

        [STAThread]
        static int Main(string[] args)
        {
#if AGENT_EXE
            string command = args.Length > 0 ? args[0] : "agent";
#else
            string command = args.Length > 0 ? args[0] : "help";
#endif
            try { return Run(command, args); }
            catch (Exception e)
            {
                Log.Error("Unhandled: " + e);
                Console.Error.WriteLine("error: " + e.Message);
                return 1;
            }
        }

        static int Fail(string msg)
        {
            Console.Error.WriteLine("error: " + msg);
            return 1;
        }

        static Config LoadConfig(bool createIfMissing)
        {
            if (!File.Exists(Config.FilePath))
            {
                if (!createIfMissing) return new Config();
                Directory.CreateDirectory(Config.ConfigDir);
                var displays = DisplayMonitor.Discover();
                File.WriteAllText(Config.FilePath, Config.Template(displays), new UTF8Encoding(false));
                DisplayMonitor.DisposeAll(displays);
                Log.Warn("No config found; wrote a starter config to " + Config.FilePath);
            }
            return Config.Load();
        }

        static string Hex(uint v) { return string.Format("0x{0:X2}", v); }

        static string InputName(uint v)
        {
            switch (v & 0xFF)
            {
                case 0x0F: return "DisplayPort 1";
                case 0x10: return "DisplayPort 2";
                case 0x11: return "HDMI 1";
                case 0x12: return "HDMI 2";
                case 0x1B: return "USB-C";
                default: return "?";
            }
        }

        /// Resolve <mon> to a display (from `all`) plus its monitor config, if any.
        static DisplayMonitor Resolve(string arg, Config config, List<DisplayMonitor> all, out MonitorConfig mc)
        {
            mc = config.Monitors.FirstOrDefault(m => string.Equals(m.Name, arg, StringComparison.OrdinalIgnoreCase));
            if (mc != null)
            {
                var named = mc;
                var d = all.FirstOrDefault(named.Matches);
                if (d == null) throw new DdcException("monitor '" + mc.Name + "' is configured but not connected");
                return d;
            }
            int n;
            DisplayMonitor found = null;
            if (int.TryParse(arg, out n) && n >= 1 && n <= all.Count) found = all[n - 1];
            else found = all.FirstOrDefault(d => string.Equals(d.ProductName, arg, StringComparison.OrdinalIgnoreCase));
            if (found == null) throw new DdcException("no monitor '" + arg + "'. Try `mas list-monitors`.");
            mc = config.Monitors.FirstOrDefault(m => m.Matches(found));
            return found;
        }

        static int Run(string command, string[] args)
        {
            switch (command)
            {
                case "agent":
                {
                    bool created;
                    using (var mutex = new Mutex(true, @"Local\MonitorAutoSwitchAgent", out created))
                    {
                        if (!created) { Log.Info("Agent already running; exiting"); return 0; }
                        var config = LoadConfig(true);
                        Log.Info("=== mas agent starting (pid " + System.Diagnostics.Process.GetCurrentProcess().Id + ") ===");
                        Application.EnableVisualStyles();
                        using (var ctx = new ApplicationContext())
                        {
                            // A Control must exist before Start() so the agent captures the WinForms SynchronizationContext.
                            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                            using (var agent = new Agent(config))
                            using (var tray = new Tray(agent))
                            {
                                agent.Start();
                                Application.Run(ctx);
                            }
                        }
                        Log.Info("Agent stopped");
                    }
                    return 0;
                }

                case "watch":
                {
                    Log.Verbose = true;
                    var config = LoadConfig(true);
                    Log.Info("=== mas watch (Ctrl-C to stop) ===");
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    using (var agent = new Agent(config))
                    {
                        agent.Start();
                        Console.CancelKeyPress += (s, e) => { e.Cancel = true; Application.Exit(); };
                        Application.Run();
                    }
                    return 0;
                }

                case "list-usb":
                {
                    var config = LoadConfig(false);
                    Console.WriteLine("VID:PID    Name");
                    foreach (var d in Usb.All().OrderBy(d => d.VidPid))
                        Console.WriteLine(d.VidPid + "  " + d.Name + (config.IsSentinel(d.Vid, d.Pid) ? "  <- sentinel" : ""));
                    return 0;
                }

                case "list-monitors":
                {
                    var config = LoadConfig(false);
                    var all = DisplayMonitor.Discover();
                    try
                    {
                        if (all.Count == 0) Console.WriteLine("No monitors found.");
                        for (int i = 0; i < all.Count; i++)
                        {
                            var d = all[i];
                            var mc = config.Monitors.FirstOrDefault(m => m.Matches(d));
                            Console.WriteLine(string.Format("[{0}] {1} {2}  productID={3}  serial={4}{5}  ({6})", i + 1, d.Manufacturer, d.ProductName, d.ProductId,
                                d.Serial, d.AlphanumericSerial.Length > 0 ? " / " + d.AlphanumericSerial : "", d.GdiDevice));
                            Console.WriteLine("    device:  " + d.InstanceId);
                            Console.WriteLine("    config:  " + (mc == null ? "no matching [[monitors]] entry"
                                : string.Format("\"{0}\" (mac {1}, pc {2}, vcp 0x{3:X2})", mc.Name, Hex(mc.MacInput), Hex(mc.PcInput), mc.VcpCode)));
                            byte vcp = mc == null ? (byte)0x60 : mc.VcpCode;
                            try
                            {
                                uint max;
                                uint cur = d.Read(vcp, out max);
                                Console.WriteLine(string.Format("    DDC:     reachable, VCP 0x{0:X2} = {1} ({2})", vcp, Hex(cur), InputName(cur)));
                            }
                            catch (DdcException e) { Console.WriteLine("    DDC:     NOT reachable - " + e.Message); }
                        }
                    }
                    finally { DisplayMonitor.DisposeAll(all); }
                    return 0;
                }

                case "get-input":
                case "get-vcp":
                {
                    int need = command == "get-vcp" ? 3 : 2;
                    if (args.Length < need) return Fail("usage: mas " + command + " <mon>" + (command == "get-vcp" ? " <code>" : ""));
                    var config = LoadConfig(false);
                    var all = DisplayMonitor.Discover();
                    try
                    {
                        MonitorConfig mc;
                        var d = Resolve(args[1], config, all, out mc);
                        byte vcp;
                        if (command == "get-vcp")
                        {
                            long c;
                            if (!Config.TryParseInt(args[2], out c) || c < 0 || c > 0xFF) return Fail("bad VCP code '" + args[2] + "'");
                            vcp = (byte)c;
                        }
                        else vcp = mc == null ? (byte)0x60 : mc.VcpCode;
                        uint max;
                        uint cur = d.Read(vcp, out max);
                        Console.WriteLine(string.Format("{0}: VCP 0x{1:X2} = {2} ({3})  max={4}{5}", d.DisplayName, vcp, Hex(cur), cur, max,
                            command == "get-input" ? "  " + InputName(cur) : ""));
                    }
                    catch (DdcException e) { return Fail(e.Message); }
                    finally { DisplayMonitor.DisposeAll(all); }
                    return 0;
                }

                case "set-input":
                {
                    long v;
                    if (args.Length < 3 || !Config.TryParseInt(args[2], out v) || v < 0 || v > 0xFFFF) return Fail("usage: mas set-input <mon> <value>  (e.g. 0x11)");
                    var config = LoadConfig(false);
                    var all = DisplayMonitor.Discover();
                    try
                    {
                        MonitorConfig mc;
                        var d = Resolve(args[1], config, all, out mc);
                        byte vcp = mc == null ? (byte)0x60 : mc.VcpCode;
                        d.Write(vcp, (uint)v);
                        Log.Info(string.Format("CLI set-input: {0} VCP 0x{1:X2} = {2}", d.DisplayName, vcp, Hex((uint)v)));
                        Console.WriteLine(string.Format("{0}: wrote VCP 0x{1:X2} = {2}", d.DisplayName, vcp, Hex((uint)v)));
                    }
                    catch (DdcException e) { return Fail(e.Message); }
                    finally { DisplayMonitor.DisposeAll(all); }
                    return 0;
                }

                case "to-mac":
                case "to-pc":
                {
                    Log.Verbose = true;
                    var config = LoadConfig(false);
                    Console.WriteLine(new Switcher(config).SwitchAllSync(command == "to-mac" ? Target.Mac : Target.Pc, "CLI"));
                    return 0;
                }

                case "init-config":
                {
                    if (File.Exists(Config.FilePath) && !args.Contains("--force")) return Fail(Config.FilePath + " exists (use --force to overwrite)");
                    Directory.CreateDirectory(Config.ConfigDir);
                    var displays = DisplayMonitor.Discover();
                    File.WriteAllText(Config.FilePath, Config.Template(displays), new UTF8Encoding(false));
                    DisplayMonitor.DisposeAll(displays);
                    Console.WriteLine("Wrote " + Config.FilePath);
                    return 0;
                }

                case "install-agent":
                {
                    LoadConfig(true);
                    Console.WriteLine("Start at login installed via " + Autostart.Install(true) + "; agent started.");
                    return 0;
                }

                case "uninstall-agent":
                    Autostart.Uninstall(true);
                    Console.WriteLine("Start at login removed; agent stopped.");
                    return 0;

                case "help":
                case "-h":
                case "--help":
                case "/?":
                    Console.WriteLine(Usage);
                    return 0;

                default:
                    Console.WriteLine(Usage);
                    return 2;
            }
        }
    }
}
