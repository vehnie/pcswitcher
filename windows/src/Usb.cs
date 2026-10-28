using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MonitorAutoSwitch
{
    class UsbDevice
    {
        public int Vid, Pid;
        public string Name = "";
        public string Path = "";
        public string VidPid { get { return string.Format("{0:x4}:{1:x4}", Vid, Pid); } }
    }

    static class Usb
    {
        public static Guid UsbDeviceInterface = new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED");
        static readonly Regex VidPidRx = new Regex(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);

        public static bool TryParseVidPid(string path, out int vid, out int pid)
        {
            vid = pid = 0;
            var m = VidPidRx.Match(path ?? "");
            if (!m.Success) return false;
            vid = Convert.ToInt32(m.Groups[1].Value, 16);
            pid = Convert.ToInt32(m.Groups[2].Value, 16);
            return true;
        }

        /// All present USB devices (GUID_DEVINTERFACE_USB_DEVICE), with the product string the device reports.
        public static List<UsbDevice> All()
        {
            var list = new List<UsbDevice>();
            var guid = UsbDeviceInterface;
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var ifd = new Native.SP_DEVICE_INTERFACE_DATA();
                    ifd.cbSize = Marshal.SizeOf(ifd);
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifd)) break;

                    var dev = new Native.SP_DEVINFO_DATA();
                    dev.cbSize = Marshal.SizeOf(dev);
                    int required;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref ifd, IntPtr.Zero, 0, out required, ref dev);
                    if (required <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(required);
                    string path;
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);   // sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W)
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref ifd, buf, required, out required, ref dev)) continue;
                        path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(buf); }

                    int vid, pid;
                    if (!TryParseVidPid(path, out vid, out pid)) continue;
                    list.Add(new UsbDevice { Vid = vid, Pid = pid, Path = path, Name = DeviceName(set, ref dev) });
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        static string DeviceName(IntPtr set, ref Native.SP_DEVINFO_DATA dev)
        {
            var buf = new byte[1024];
            uint type, required;
            var key = Native.BusReportedDeviceDesc;
            if (Native.SetupDiGetDeviceProperty(set, ref dev, ref key, out type, buf, (uint)buf.Length, out required, 0))
                return Encoding.Unicode.GetString(buf, 0, (int)required).TrimEnd('\0');
            foreach (var prop in new[] { Native.SPDRP_FRIENDLYNAME, Native.SPDRP_DEVICEDESC })
            {
                if (Native.SetupDiGetDeviceRegistryProperty(set, ref dev, prop, out type, buf, (uint)buf.Length, out required))
                    return Encoding.Unicode.GetString(buf, 0, (int)required).TrimEnd('\0');
            }
            return "?";
        }

        public static List<UsbDevice> PresentSentinels(Config config)
        {
            return All().Where(d => config.IsSentinel(d.Vid, d.Pid)).ToList();
        }
    }

    /// Hidden window that receives WM_DEVICECHANGE for USB device arrivals/removals.
    /// Requires a message loop on the creating thread (Application.Run).
    class UsbWatcher : NativeWindow, IDisposable
    {
        IntPtr notification;

        /// (arrived, vid, pid, device path)
        public event Action<bool, int, int, string> DeviceChanged;

        public UsbWatcher()
        {
            CreateHandle(new CreateParams());
            var filter = new Native.DEV_BROADCAST_DEVICEINTERFACE();
            filter.dbcc_size = Marshal.SizeOf(filter);
            filter.dbcc_devicetype = Native.DBT_DEVTYP_DEVICEINTERFACE;
            filter.dbcc_classguid = Usb.UsbDeviceInterface;
            IntPtr buf = Marshal.AllocHGlobal(filter.dbcc_size);
            try
            {
                Marshal.StructureToPtr(filter, buf, false);
                notification = Native.RegisterDeviceNotification(Handle, buf, 0);
            }
            finally { Marshal.FreeHGlobal(buf); }
            if (notification == IntPtr.Zero) Log.Error("RegisterDeviceNotification failed: " + Marshal.GetLastWin32Error());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DEVICECHANGE && m.LParam != IntPtr.Zero)
            {
                int ev = m.WParam.ToInt32();
                if ((ev == Native.DBT_DEVICEARRIVAL || ev == Native.DBT_DEVICEREMOVECOMPLETE)
                    && Marshal.ReadInt32(m.LParam, 4) == Native.DBT_DEVTYP_DEVICEINTERFACE)
                {
                    // dbcc_name starts after size, devicetype, reserved (3 x int) and the class GUID.
                    string path = Marshal.PtrToStringUni(new IntPtr(m.LParam.ToInt64() + 28));
                    int vid, pid;
                    if (Usb.TryParseVidPid(path, out vid, out pid) && DeviceChanged != null)
                        DeviceChanged(ev == Native.DBT_DEVICEARRIVAL, vid, pid, path);
                }
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (notification != IntPtr.Zero) { Native.UnregisterDeviceNotification(notification); notification = IntPtr.Zero; }
            DestroyHandle();
        }
    }
}
