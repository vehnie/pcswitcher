using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace MonitorAutoSwitch
{
    class DdcException : Exception
    {
        public DdcException(string msg) : base(msg) { }
    }

    /// A physical monitor attached to the Windows desktop, identified by its EDID.
    /// Owns a physical-monitor handle: dispose it (or use DisposeAll) when done.
    /// Never cache these across switch attempts; monitors come and go during hot-plug.
    class DisplayMonitor : IDisposable
    {
        public string Manufacturer = "";      // 3-letter PnP ID from EDID, e.g. "AOC"
        public string ProductName = "";       // EDID monitor name descriptor, e.g. "C27G4Z"
        public int ProductId;                 // EDID product code (same number macOS reports)
        public string Serial = "";            // EDID numeric serial
        public string AlphanumericSerial = ""; // EDID serial string descriptor
        public string GdiDevice = "";         // \\.\DISPLAY1
        public string InstanceId = "";        // DISPLAY\AOC2401\5&...
        public IntPtr Handle;

        public string DisplayName
        {
            get { return (Manufacturer + " " + ProductName).Trim() + (Serial.Length == 0 || Serial == "0" ? "" : " #" + Serial); }
        }

        /// Get VCP feature. Returns current value; `max` gets the maximum.
        public uint Read(byte vcp, out uint max)
        {
            int err = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) Thread.Sleep(50);
                uint cur, mx;
                if (Native.GetVCPFeatureAndVCPFeatureReply(Handle, vcp, IntPtr.Zero, out cur, out mx)) { max = mx; return cur; }
                err = Marshal.GetLastWin32Error();
            }
            throw new DdcException(string.Format("DDC read of VCP 0x{0:X2} failed on {1}: {2}", vcp, DisplayName, Win32Message(err)));
        }

        /// Set VCP feature.
        public void Write(byte vcp, uint value)
        {
            if (!Native.SetVCPFeature(Handle, vcp, value))
                throw new DdcException(string.Format("DDC write of VCP 0x{0:X2} failed on {1}: {2}", vcp, DisplayName, Win32Message(Marshal.GetLastWin32Error())));
        }

        static string Win32Message(int err)
        {
            return string.Format("0x{0:X8} {1}", err, new Win32Exception(err).Message);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { Native.DestroyPhysicalMonitor(Handle); Handle = IntPtr.Zero; }
        }

        public static void DisposeAll(IEnumerable<DisplayMonitor> list)
        {
            foreach (var d in list) d.Dispose();
        }

        // ---- Discovery ----

        /// Enumerates desktop monitors -> physical monitor handles, and pairs each with its EDID via
        /// EnumDisplayDevices (device interface path -> registry Enum\DISPLAY\...\Device Parameters\EDID).
        public static List<DisplayMonitor> Discover()
        {
            var result = new List<DisplayMonitor>();
            Native.MonitorEnumProc proc = (hMon, hdc, rect, data) =>
            {
                var info = new Native.MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX));
                if (!Native.GetMonitorInfo(hMon, ref info)) return true;

                uint count;
                if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) || count == 0) return true;
                var physical = new Native.PHYSICAL_MONITOR[count];
                if (!Native.GetPhysicalMonitorsFromHMONITOR(hMon, count, physical)) return true;

                // The active monitor devices of this GDI display, in the same order as the physical monitors.
                var instanceIds = new List<string>();
                var dd = new Native.DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
                for (uint i = 0; Native.EnumDisplayDevices(info.szDevice, i, ref dd, Native.EDD_GET_DEVICE_INTERFACE_NAME); i++)
                {
                    if ((dd.StateFlags & Native.DISPLAY_DEVICE_ACTIVE) != 0) instanceIds.Add(InstanceIdFromInterface(dd.DeviceID));
                    dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
                }

                for (int i = 0; i < physical.Length; i++)
                {
                    var d = new DisplayMonitor { Handle = physical[i].hPhysicalMonitor, GdiDevice = info.szDevice };
                    d.InstanceId = i < instanceIds.Count ? instanceIds[i] : "";
                    d.FillFromEdid();
                    if (d.ProductName.Length == 0) d.ProductName = physical[i].szPhysicalMonitorDescription;
                    result.Add(d);
                }
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            GC.KeepAlive(proc);
            return result;
        }

        /// \\?\DISPLAY#AOC2401#5&7a7f12&0&UID8448#{e6f07b5f-...} -> DISPLAY\AOC2401\5&7a7f12&0&UID8448
        static string InstanceIdFromInterface(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var parts = path.Split('#');
            if (parts.Length < 3) return "";
            return "DISPLAY\\" + parts[1] + "\\" + parts[2];
        }

        void FillFromEdid()
        {
            if (InstanceId.Length == 0) return;
            var parts = InstanceId.Split('\\');
            // Fallback from the PnP hardware ID (e.g. AOC2401): manufacturer + hex product code.
            if (parts.Length >= 2 && parts[1].Length == 7)
            {
                Manufacturer = parts[1].Substring(0, 3);
                int pid;
                if (int.TryParse(parts[1].Substring(3), System.Globalization.NumberStyles.HexNumber, null, out pid)) ProductId = pid;
            }
            byte[] edid = null;
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + InstanceId + @"\Device Parameters"))
                {
                    if (key != null) edid = key.GetValue("EDID") as byte[];
                }
            }
            catch { }
            if (edid == null || edid.Length < 128) return;

            int m = (edid[8] << 8) | edid[9];
            Manufacturer = new string(new[] { (char)('A' - 1 + ((m >> 10) & 31)), (char)('A' - 1 + ((m >> 5) & 31)), (char)('A' - 1 + (m & 31)) });
            ProductId = edid[10] | (edid[11] << 8);
            uint serial = (uint)(edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24));
            Serial = serial == 0 ? "" : serial.ToString();
            for (int off = 54; off <= 108; off += 18)
            {
                if (edid[off] != 0 || edid[off + 1] != 0 || edid[off + 2] != 0) continue;
                string text = DescriptorText(edid, off + 5);
                if (edid[off + 3] == 0xFC) ProductName = text;
                if (edid[off + 3] == 0xFF) AlphanumericSerial = text;
            }
        }

        static string DescriptorText(byte[] edid, int start)
        {
            var sb = new StringBuilder();
            for (int i = start; i < start + 13; i++)
            {
                if (edid[i] == 0x0A || edid[i] == 0) break;
                sb.Append((char)edid[i]);
            }
            return sb.ToString().Trim();
        }
    }
}
