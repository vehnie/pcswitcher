using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MonitorAutoSwitch
{
    enum Target { Mac, Pc }

    class MonitorConfig
    {
        public string Name;
        public string Manufacturer = "";
        public string Model = "";
        public string Serial = "";
        public byte VcpCode = 0x60;
        public uint MacInput = 0x11;
        public uint PcInput = 0x0F;
        /// false for monitors whose VCP 0x60 readout lies (e.g. AOC 24G1WG4 keeps reporting HDMI while on DP):
        /// always write, never skip as "already there", don't verify.
        public bool ReliableReadback = true;

        public uint InputFor(Target t) { return t == Target.Mac ? MacInput : PcInput; }

        public bool Matches(DisplayMonitor d)
        {
            if (Manufacturer.Length > 0 && !string.Equals(Manufacturer, d.Manufacturer, StringComparison.OrdinalIgnoreCase)) return false;
            if (Model.Length > 0)
            {
                bool byName = string.Equals(Model, d.ProductName, StringComparison.OrdinalIgnoreCase);
                long id;
                bool byId = Config.TryParseInt(Model, out id) && id == d.ProductId;
                if (!byName && !byId) return false;
            }
            if (Serial.Length > 0 && Serial != d.Serial && !string.Equals(Serial, d.AlphanumericSerial, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
    }

    class Sentinel
    {
        public int Vid, Pid;
        public override string ToString() { return string.Format("{0:x4}:{1:x4}", Vid, Pid); }
    }

    class ConfigException : Exception
    {
        public ConfigException(string msg) : base(msg) { }
    }

    /// Same schema as the macOS agent (macos/Sources/mas/Config.swift). Keys only the Mac uses
    /// (ddc_backend, external_command, i2c_address) are accepted and ignored here.
    class Config
    {
        public List<Sentinel> Sentinels = new List<Sentinel>();
        public int DebounceMs = 500;
        public int Retries = 3;
        public List<int> RetryDelaysMs = new List<int> { 300, 600, 1200 };
        public bool VerifyReadback = true;
        public bool OnConnect = true;
        public bool OnDisconnect = true;
        public List<MonitorConfig> Monitors = new List<MonitorConfig>();

        public static string ConfigDir
        {
            get
            {
                // MAS_CONFIG_DIR overrides the location (used for testing against a throwaway config).
                string over = Environment.GetEnvironmentVariable("MAS_CONFIG_DIR");
                if (!string.IsNullOrEmpty(over)) return over;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorAutoSwitch");
            }
        }
        public static string FilePath { get { return Path.Combine(ConfigDir, "config.toml"); } }

        public bool IsSentinel(int vid, int pid) { return Sentinels.Any(s => s.Vid == vid && s.Pid == pid); }

        public static Config Load() { return Parse(File.ReadAllText(FilePath, Encoding.UTF8)); }

        public static Config Parse(string text)
        {
            var doc = Toml.Parse(text);
            var c = new Config();
            object v;
            if (doc.Root.TryGetValue("sentinels", out v))
            {
                var list = v as List<object>;
                if (list == null) throw new ConfigException("sentinels must be an array");
                foreach (var o in list)
                {
                    var s = o as string;
                    var parts = s == null ? new string[0] : s.Split(':');
                    int vid, pid;
                    if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.HexNumber, null, out vid) || !int.TryParse(parts[1], NumberStyles.HexNumber, null, out pid))
                        throw new ConfigException("bad sentinel '" + o + "', expected VID:PID in hex");
                    c.Sentinels.Add(new Sentinel { Vid = vid, Pid = pid });
                }
            }
            if (doc.Root.TryGetValue("debounce_ms", out v) && v is long) c.DebounceMs = (int)(long)v;
            if (doc.Root.TryGetValue("retries", out v) && v is long) c.Retries = Math.Max(1, (int)(long)v);
            if (doc.Root.TryGetValue("retry_delays_ms", out v) && v is List<object>)
                c.RetryDelaysMs = ((List<object>)v).OfType<long>().Select(x => (int)x).ToList();
            if (doc.Root.TryGetValue("verify_readback", out v) && v is bool) c.VerifyReadback = (bool)v;
            if (doc.Root.TryGetValue("on_connect", out v) && v is bool) c.OnConnect = (bool)v;
            if (doc.Root.TryGetValue("on_disconnect", out v) && v is bool) c.OnDisconnect = (bool)v;

            List<Dictionary<string, object>> tables;
            if (doc.Arrays.TryGetValue("monitors", out tables))
            {
                for (int i = 0; i < tables.Count; i++)
                {
                    var t = tables[i];
                    var m = new MonitorConfig { Name = t.ContainsKey("name") ? Convert.ToString(t["name"]) : "Monitor " + (i + 1) };
                    if (t.TryGetValue("match", out v) && v is Dictionary<string, object>)
                    {
                        var match = (Dictionary<string, object>)v;
                        if (match.ContainsKey("manufacturer")) m.Manufacturer = Convert.ToString(match["manufacturer"]);
                        if (match.ContainsKey("model")) m.Model = Convert.ToString(match["model"]);
                        if (match.ContainsKey("serial")) m.Serial = Convert.ToString(match["serial"]);
                    }
                    if (t.TryGetValue("vcp_code", out v) && v is long) m.VcpCode = (byte)(long)v;
                    if (t.TryGetValue("mac_input", out v) && v is long) m.MacInput = (uint)(long)v;
                    if (t.TryGetValue("pc_input", out v) && v is long) m.PcInput = (uint)(long)v;
                    if (t.TryGetValue("reliable_readback", out v) && v is bool) m.ReliableReadback = (bool)v;
                    c.Monitors.Add(m);
                }
            }
            return c;
        }

        /// Starter config pre-filled with the EDID data of the currently connected monitors.
        public static string Template(List<DisplayMonitor> displays)
        {
            var sb = new StringBuilder();
            sb.Append(@"# MonitorAutoSwitch config (Windows agent). Same schema as the macOS agent.

# Sentinel USB device(s) behind the USB switch (VID:PID, hex). Find them with `mas list-usb`.
# The group counts as ""present"" if ANY of them is connected.
sentinels = []

debounce_ms     = 500
retries         = 3
retry_delays_ms = [300, 600, 1200]
verify_readback = true

# Which rules this agent applies
on_connect    = true    # sentinel appears on this PC   -> monitors to pc_input
on_disconnect = true    # sentinel leaves this PC       -> monitors to mac_input

# Common VCP 0x60 values: 0x0F DP1, 0x10 DP2, 0x11 HDMI1, 0x12 HDMI2, 0x1B USB-C
# Confirm yours with `mas get-input <monitor>` while each computer is shown.
");
            if (displays.Count == 0) displays = new List<DisplayMonitor> { null };
            for (int i = 0; i < displays.Count; i++)
            {
                var d = displays[i];
                sb.AppendFormat(@"
[[monitors]]
name      = ""{0}""
match     = {{ manufacturer = ""{1}"", model = ""{2}"", serial = ""{3}"" }}
vcp_code  = 0x60
mac_input = 0x11    # HDMI 1
pc_input  = 0x0F    # DisplayPort 1
",
                    d == null ? "Monitor " + (i + 1) : d.ProductName,
                    d == null ? "" : d.Manufacturer,
                    d == null ? "" : d.ProductName,
                    d == null ? "" : d.Serial);
            }
            return sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        public static bool TryParseInt(string s, out long value)
        {
            var t = s.Trim().Replace("_", "");
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            return long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    /// Minimal TOML subset (same as the macOS agent): key = value, [[array.tables]], [tables],
    /// strings, ints (dec/hex), bools, arrays (may span lines), inline tables, # comments.
    static class Toml
    {
        public class Document
        {
            public Dictionary<string, object> Root = new Dictionary<string, object>();
            public Dictionary<string, List<Dictionary<string, object>>> Arrays = new Dictionary<string, List<Dictionary<string, object>>>();
        }

        public static Document Parse(string text)
        {
            var doc = new Document();
            string currentArray = null, currentTable = null;
            string pending = "";
            int lineNo = 0;
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                lineNo++;
                var line = StripComment(raw).Trim();
                if (pending.Length == 0 && line.Length == 0) continue;
                if (pending.Length == 0 && line.StartsWith("[["))
                {
                    if (!line.EndsWith("]]")) throw new ConfigException("line " + lineNo + ": bad table header");
                    var name = line.Substring(2, line.Length - 4).Trim();
                    if (!doc.Arrays.ContainsKey(name)) doc.Arrays[name] = new List<Dictionary<string, object>>();
                    doc.Arrays[name].Add(new Dictionary<string, object>());
                    currentArray = name; currentTable = null;
                    continue;
                }
                if (pending.Length == 0 && line.StartsWith("["))
                {
                    currentTable = line.Substring(1, line.Length - 2).Trim();
                    currentArray = null;
                    continue;
                }
                pending += (pending.Length == 0 ? "" : " ") + line;
                if (BracketDepth(pending) > 0) continue;   // multi-line array / inline table
                var stmt = pending;
                pending = "";

                int eq = stmt.IndexOf('=');
                if (eq < 0) throw new ConfigException("line " + lineNo + ": expected key = value");
                var key = stmt.Substring(0, eq).Trim().Trim('"');
                object value;
                try { var sc = new Scanner(stmt.Substring(eq + 1)); value = sc.Value(); }
                catch (ConfigException e) { throw new ConfigException("line " + lineNo + ": " + e.Message); }

                if (currentArray != null)
                {
                    var list = doc.Arrays[currentArray];
                    list[list.Count - 1][key] = value;
                }
                else if (currentTable != null)
                {
                    object t;
                    if (!doc.Root.TryGetValue(currentTable, out t)) { t = new Dictionary<string, object>(); doc.Root[currentTable] = t; }
                    ((Dictionary<string, object>)t)[key] = value;
                }
                else doc.Root[key] = value;
            }
            if (pending.Length > 0) throw new ConfigException("unterminated array or inline table");
            return doc;
        }

        static string StripComment(string s)
        {
            bool inString = false;
            var sb = new StringBuilder();
            foreach (var ch in s)
            {
                if (ch == '"') inString = !inString;
                if (ch == '#' && !inString) break;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        static int BracketDepth(string s)
        {
            int depth = 0; bool inString = false;
            foreach (var ch in s)
            {
                if (ch == '"') inString = !inString;
                if (inString) continue;
                if (ch == '[' || ch == '{') depth++;
                if (ch == ']' || ch == '}') depth--;
            }
            return depth;
        }

        class Scanner
        {
            readonly string s;
            int i;
            public Scanner(string s) { this.s = s; }

            void SkipWS() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

            public object Value()
            {
                SkipWS();
                if (i >= s.Length) throw new ConfigException("missing value");
                char c = s[i];
                if (c == '"')
                {
                    i++;
                    var sb = new StringBuilder();
                    while (i < s.Length && s[i] != '"')
                    {
                        if (s[i] == '\\' && i + 1 < s.Length) i++;
                        sb.Append(s[i]); i++;
                    }
                    if (i >= s.Length) throw new ConfigException("unterminated string");
                    i++;
                    return sb.ToString();
                }
                if (c == '[')
                {
                    i++;
                    var arr = new List<object>();
                    while (true)
                    {
                        SkipWS();
                        if (i >= s.Length) throw new ConfigException("unterminated array");
                        if (s[i] == ']') { i++; return arr; }
                        arr.Add(Value());
                        SkipWS();
                        if (i < s.Length && s[i] == ',') i++;
                    }
                }
                if (c == '{')
                {
                    i++;
                    var t = new Dictionary<string, object>();
                    while (true)
                    {
                        SkipWS();
                        if (i >= s.Length) throw new ConfigException("unterminated inline table");
                        if (s[i] == '}') { i++; return t; }
                        int eq = s.IndexOf('=', i);
                        if (eq < 0) throw new ConfigException("expected = in inline table");
                        var key = s.Substring(i, eq - i).Trim().Trim('"');
                        i = eq + 1;
                        t[key] = Value();
                        SkipWS();
                        if (i < s.Length && s[i] == ',') i++;
                    }
                }
                var tok = new StringBuilder();
                while (i < s.Length && ",]}".IndexOf(s[i]) < 0 && !char.IsWhiteSpace(s[i])) { tok.Append(s[i]); i++; }
                var str = tok.ToString();
                if (str == "true") return true;
                if (str == "false") return false;
                long n;
                if (Config.TryParseInt(str, out n)) return n;
                throw new ConfigException("cannot parse value '" + str + "'");
            }
        }
    }
}
