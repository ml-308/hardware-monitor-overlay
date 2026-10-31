using System;
using System.Runtime.InteropServices;

namespace HwMon
{
    /// <summary>所有 Win32 / NT 原生调用的集中定义。</summary>
    internal static class Native
    {
        // ================= CPU =================
        [StructLayout(LayoutKind.Sequential)]
        public struct FileTime { public uint Low; public uint High; }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessorPerfInfo
        {
            public long IdleTime;
            public long KernelTime;
            public long UserTime;
            public long DpcTime;
            public long InterruptTime;
            public uint InterruptCount;
        }

        public const int SystemProcessorPerformanceInformation = 8;

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

        // ================= 内存 =================
        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        // ================= 电源 / 电池 =================
        [StructLayout(LayoutKind.Sequential)]
        public struct PowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemPowerStatus(out PowerStatus status);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        // ================= 磁盘 =================
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetDiskFreeSpaceEx(string directory,
            out ulong freeToCaller, out ulong total, out ulong totalFree);

        // ================= 处理器拓扑 =================
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

        // ================= 分层窗口 =================
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
            public POINT(int x_, int y_) { x = x_; y = y_; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int w, int h) { cx = w; cy = h; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        public static extern void CopyMemory(IntPtr destination, IntPtr source, IntPtr length);

        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const int ULW_ALPHA = 0x00000002;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const uint BI_RGB = 0;
        public const uint DIB_RGB_COLORS = 0;
    }

    /// <summary>PDH 性能计数器封装（GPU 使用率 / 磁盘活动 / 进程 / 线程）。</summary>
    internal sealed class Pdh : IDisposable
    {
        const uint PDH_FMT_DOUBLE = 0x00000200;
        const uint PDH_FMT_NOCAP100 = 0x00008000;
        const uint PDH_FMT = PDH_FMT_DOUBLE | PDH_FMT_NOCAP100;

        [StructLayout(LayoutKind.Sequential)]
        struct FmtValue
        {
            public uint Status;
            public double Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FmtItem
        {
            public IntPtr Name;
            public uint Status;
            public double Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
        static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
        static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
        static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterValue")]
        static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FmtValue value);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
        static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, out uint size, out uint count, IntPtr buffer);

        [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
        static extern uint PdhCloseQuery(IntPtr query);

        IntPtr _query = IntPtr.Zero;
        IntPtr _gpu, _disk, _proc, _thread;
        bool _gpuOk, _diskOk, _procOk, _threadOk;
        IntPtr _arrBuf = IntPtr.Zero;
        int _arrCap;

        public bool GpuOk { get { return _gpuOk; } }
        public bool DiskOk { get { return _diskOk; } }
        public bool SysOk { get { return _procOk || _threadOk; } }

        public bool Open()
        {
            try
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return false; }
                _gpuOk = PdhAddEnglishCounter(_query, "\\GPU Engine(*)\\Utilization Percentage", IntPtr.Zero, out _gpu) == 0;
                _diskOk = PdhAddEnglishCounter(_query, "\\PhysicalDisk(_Total)\\% Disk Time", IntPtr.Zero, out _disk) == 0;
                _procOk = PdhAddEnglishCounter(_query, "\\System\\Processes", IntPtr.Zero, out _proc) == 0;
                _threadOk = PdhAddEnglishCounter(_query, "\\System\\Threads", IntPtr.Zero, out _thread) == 0;
                Collect();
                Collect();
                return true;
            }
            catch { return false; }
        }

        public void Collect()
        {
            if (_query != IntPtr.Zero) { try { PdhCollectQueryData(_query); } catch { } }
        }

        static double Scalar(IntPtr counter, bool ok)
        {
            if (!ok) return -1;
            try
            {
                uint type; FmtValue v;
                uint rc = PdhGetFormattedCounterValue(counter, PDH_FMT, out type, out v);
                if (rc != 0 || (v.Status != 0 && v.Status != 1)) return -1;
                return v.Value;
            }
            catch { return -1; }
        }

        public double DiskActivity() { return Scalar(_disk, _diskOk); }
        public double Processes() { return Scalar(_proc, _procOk); }
        public double Threads() { return Scalar(_thread, _threadOk); }

        /// <summary>3D / 计算 / 拷贝 / 编解码引擎的利用率之和（每个进程实例相加）。</summary>
        public double Gpu()
        {
            if (!_gpuOk) return -1;
            try
            {
                uint size, count;
                uint rc = PdhGetFormattedCounterArray(_gpu, PDH_FMT, out size, out count, IntPtr.Zero);
                if (rc != 0x800007D2 && rc != 0) return -1;  // PDH_MORE_DATA or OK
                if (size == 0 || count == 0) return -1;
                if ((int)size > _arrCap)
                {
                    if (_arrBuf != IntPtr.Zero) Marshal.FreeHGlobal(_arrBuf);
                    _arrCap = (int)size;
                    _arrBuf = Marshal.AllocHGlobal(_arrCap);
                }
                rc = PdhGetFormattedCounterArray(_gpu, PDH_FMT, out size, out count, _arrBuf);
                if (rc != 0) return -1;

                int stride = Marshal.SizeOf(typeof(FmtItem)); // x64 = 24
                double sum = 0;
                int seen = 0;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(_arrBuf.ToInt64() + (long)i * stride);
                    uint st = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                    if (st != 0 && st != 1) continue;
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    if (name == null) continue;
                    if (name.IndexOf("engtype_") < 0) continue;
                    if (name.IndexOf("engtype_3D") < 0 &&
                        name.IndexOf("engtype_Compute") < 0 &&
                        name.IndexOf("engtype_Copy") < 0 &&
                        name.IndexOf("engtype_VideoDecode") < 0 &&
                        name.IndexOf("engtype_VideoEncode") < 0 &&
                        name.IndexOf("engtype_VideoProcessing") < 0) continue;
                    double v = BitConverter.ToDouble(BitConverter.GetBytes(Marshal.ReadInt64(item, IntPtr.Size + 8)), 0);
                    if (v < 0) continue;
                    sum += v;
                    seen++;
                }
                if (seen == 0) return -1;
                if (sum > 100) sum = 100;
                return sum;
            }
            catch { return -1; }
        }

        public void Dispose()
        {
            try { if (_query != IntPtr.Zero) PdhCloseQuery(_query); } catch { }
            _query = IntPtr.Zero;
            if (_arrBuf != IntPtr.Zero) { try { Marshal.FreeHGlobal(_arrBuf); } catch { } _arrBuf = IntPtr.Zero; }
        }
    }
}
