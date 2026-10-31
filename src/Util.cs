using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;

namespace HwMon
{
    internal static class Log
    {
        static readonly object Gate = new object();
        public static string FilePath;
        public static void Error(string message, Exception ex)
        {
            try
            {
                lock (Gate)
                {
                    if (FilePath == null) return;
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message +
                        (ex == null ? "" : (" :: " + ex)) + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    internal static class Fmt
    {
        static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        public static string Bytes(ulong bytes)
        {
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
            if (i == 0) return ((long)v).ToString() + " " + Units[i];
            return v.ToString(v >= 100 ? "0" : (v >= 10 ? "0.0" : "0.00"), CultureInfo.InvariantCulture) + " " + Units[i];
        }

        public static string Rate(double bytesPerSecond)
        {
            if (bytesPerSecond < 0) return "--";
            double v = bytesPerSecond;
            int i = 0;
            while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
            if (i == 0) return ((long)v).ToString() + " B/s";
            return v.ToString(v >= 100 ? "0" : (v >= 10 ? "0.0" : "0.00"), CultureInfo.InvariantCulture) + " " + Units[i] + "/s";
        }

        /// <summary>紧凑字节数：10.6G / 125G / 1.2M</summary>
        public static string BytesShort(ulong bytes)
        {
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
            if (i == 0) return ((long)v).ToString();
            return v.ToString(v >= 100 ? "0" : (v >= 10 ? "0.#" : "0.##"), CultureInfo.InvariantCulture) + Units[i].Substring(0, 1);
        }

        public static string Duration(long seconds)
        {
            if (seconds < 0) seconds = 0;
            long d = seconds / 86400;
            long h = (seconds % 86400) / 3600;
            long m = (seconds % 3600) / 60;
            long s = seconds % 60;
            if (d > 0) return d + "d " + h.ToString("00") + ":" + m.ToString("00");
            if (h > 0) return h + "h " + m.ToString("00") + "m";
            if (m > 0) return m + "m " + s.ToString("00") + "s";
            return s + "s";
        }

        public static string Percent(double v)
        {
            if (v < 0) return "--";
            if (v >= 99.5) return "100%";
            return v.ToString(v >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%";
        }
    }

    /// <summary>字体缓存：按 (family, px, style) 复用，避免每帧创建 GDI 对象。</summary>
    internal sealed class FontCache : IDisposable
    {
        readonly Dictionary<string, Font> _map = new Dictionary<string, Font>();
        public string Family = "Segoe UI";

        public static string PickFamily()
        {
            string[] prefer = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Tahoma", "Arial" };
            try
            {
                using (InstalledFontCollection col = new InstalledFontCollection())
                {
                    FontFamily[] fams = col.Families;
                    for (int p = 0; p < prefer.Length; p++)
                    {
                        for (int i = 0; i < fams.Length; i++)
                        {
                            if (string.Equals(fams[i].Name, prefer[p], StringComparison.OrdinalIgnoreCase))
                                return fams[i].Name;
                        }
                    }
                }
            }
            catch { }
            return "Segoe UI";
        }

        public Font Get(float px, FontStyle style)
        {
            if (px < 5) px = 5;
            float key = (float)Math.Round(px * 2) / 2f;
            string k = familyKey + "|" + key.ToString(CultureInfo.InvariantCulture) + "|" + (int)style;
            Font f;
            if (_map.TryGetValue(k, out f)) return f;
            f = new Font(Family, key, style, GraphicsUnit.Pixel);
            if (_map.Count > 96) Dispose();
            _map[k] = f;
            return f;
        }

        string familyKey { get { return Family; } }

        public void Dispose()
        {
            foreach (KeyValuePair<string, Font> kv in _map)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _map.Clear();
        }
    }
}
