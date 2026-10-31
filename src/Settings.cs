using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace HwMon
{
    internal sealed class Settings
    {
        public int X = int.MinValue, Y = int.MinValue;
        public int W = 300, H = 300;
        public int OpacityPct = 84;
        public bool TopMost = true;
        public bool ShowCpu = true;
        public bool ShowRam = true;
        public bool ShowGpu = true;
        public bool ShowDisk = true;
        public bool ShowNet = true;
        public bool ShowCores = true;
        public bool ShowGraph = true;
        public bool ShowFooter = true;
        public bool Mini = false;
        public int IntervalMs = 1000;
        public int GpuMemMB = 2048;

        /// <summary>首选配置路径：%APPDATA%\HwMon\config.ini</summary>
        public static string ConfigPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HwMon");
                return Path.Combine(dir, "config.ini");
            }
        }

        /// <summary>备选配置路径：与 exe 同目录（%APPDATA% 不可写时使用，例如绿色/U 盘运行）</summary>
        public static string PortableConfigPath
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
                    if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, "HwMon.ini");
                }
                catch { }
                return "HwMon.ini";
            }
        }

        static string ExistingConfigPath()
        {
            try
            {
                if (File.Exists(ConfigPath)) return ConfigPath;
                if (File.Exists(PortableConfigPath)) return PortableConfigPath;
            }
            catch { }
            return null;
        }

        static string Bool(bool b) { return b ? "1" : "0"; }
        static bool ParseBool(string s, bool dflt)
        {
            if (s == null) return dflt;
            s = s.Trim();
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return dflt;
        }
        static int ParseInt(string s, int dflt)
        {
            int v;
            if (s != null && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return dflt;
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                double dpiScale = 1.0;
                try
                {
                    uint dpi = Native.GetDpiForSystem();
                    if (dpi >= 72 && dpi <= 480) dpiScale = dpi / 96.0;
                }
                catch { }
                if (dpiScale > 1.001)
                {
                    s.W = (int)Math.Round(300 * dpiScale);
                    s.H = (int)Math.Round(300 * dpiScale);
                }
                string cfg = ExistingConfigPath();
                if (cfg != null)
                {
                    foreach (string raw in File.ReadAllLines(cfg))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = line.Substring(eq + 1).Trim();
                        switch (k)
                        {
                            case "x": s.X = ParseInt(v, s.X); break;
                            case "y": s.Y = ParseInt(v, s.Y); break;
                            case "w": s.W = ParseInt(v, s.W); break;
                            case "h": s.H = ParseInt(v, s.H); break;
                            case "opacity": s.OpacityPct = ParseInt(v, s.OpacityPct); break;
                            case "topmost": s.TopMost = ParseBool(v, s.TopMost); break;
                            case "cpu": s.ShowCpu = ParseBool(v, s.ShowCpu); break;
                            case "ram": s.ShowRam = ParseBool(v, s.ShowRam); break;
                            case "gpu": s.ShowGpu = ParseBool(v, s.ShowGpu); break;
                            case "disk": s.ShowDisk = ParseBool(v, s.ShowDisk); break;
                            case "net": s.ShowNet = ParseBool(v, s.ShowNet); break;
                            case "cores": s.ShowCores = ParseBool(v, s.ShowCores); break;
                            case "graph": s.ShowGraph = ParseBool(v, s.ShowGraph); break;
                            case "footer": s.ShowFooter = ParseBool(v, s.ShowFooter); break;
                            case "mini": s.Mini = ParseBool(v, s.Mini); break;
                            case "interval": s.IntervalMs = ParseInt(v, s.IntervalMs); break;
                        }
                    }
                }
            }
            catch { }
            if (s.W < 150) s.W = 150;
            if (s.H < 100) s.H = 100;
            if (s.OpacityPct < 20) s.OpacityPct = 20;
            if (s.OpacityPct > 100) s.OpacityPct = 100;
            if (s.IntervalMs < 200) s.IntervalMs = 200;
            if (s.IntervalMs > 60000) s.IntervalMs = 60000;
            return s;
        }

        public void ApplyMini(bool mini)
        {
            Mini = mini;
            if (mini)
            {
                ShowCores = false;
                ShowGraph = false;
                ShowDisk = false;
                ShowFooter = true;
            }
            else
            {
                ShowCores = true;
                ShowGraph = true;
                ShowDisk = true;
                ShowFooter = true;
            }
        }

        public void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# 硬件监控浮窗配置");
                lines.Add("x=" + X);
                lines.Add("y=" + Y);
                lines.Add("w=" + W);
                lines.Add("h=" + H);
                lines.Add("opacity=" + OpacityPct);
                lines.Add("topmost=" + Bool(TopMost));
                lines.Add("cpu=" + Bool(ShowCpu));
                lines.Add("ram=" + Bool(ShowRam));
                lines.Add("gpu=" + Bool(ShowGpu));
                lines.Add("disk=" + Bool(ShowDisk));
                lines.Add("net=" + Bool(ShowNet));
                lines.Add("cores=" + Bool(ShowCores));
                lines.Add("graph=" + Bool(ShowGraph));
                lines.Add("footer=" + Bool(ShowFooter));
                lines.Add("mini=" + Bool(Mini));
                lines.Add("interval=" + IntervalMs);
                if (!TryWrite(ConfigPath, lines)) TryWrite(PortableConfigPath, lines);
            }
            catch { }
        }

        static bool TryWrite(string path, List<string> lines)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(path, lines.ToArray());
                return true;
            }
            catch { return false; }
        }

        // ---------- 开机自启 ----------
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "HwMonOverlay";

        public static bool AutoStartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(RunName);
                    return v != null;
                }
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunName, "\"" + System.Windows.Forms.Application.ExecutablePath + "\" \"\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }
    }
}
