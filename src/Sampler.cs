using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace HwMon
{
    internal sealed class DiskInfo
    {
        public string Name = "";
        public ulong Total;
        public ulong Free;
        public ulong Used { get { return Total > Free ? Total - Free : 0; } }
        public double Percent { get { return Total > 0 ? 100.0 * Used / Total : 0; } }
    }

    /// <summary>某一时刻的整机快照（发布后不可变，可安全跨线程读取）。</summary>
    internal sealed class Sample
    {
        public long Seq;
        public double Cpu;
        public double[] Cores = new double[0];
        public double MemPercent;
        public ulong MemUsed, MemTotal, MemAvail;
        public double Gpu = -1;
        public double DiskActivity = -1;
        public double Procs = -1;
        public double Threads = -1;
        public double NetDown, NetUp;
        public ulong NetDownTotal, NetUpTotal;
        public bool HasBattery;
        public int BatteryPercent = -1;
        public bool AcOnline = true;
        public long UptimeSec;
        public DiskInfo[] Disks = new DiskInfo[0];
    }

    internal static class MachineInfo
    {
        public static int LogicalCores = 1;
        public static int PhysicalCores = 1;
        public static double NominalMhz;
        public static string CpuName = "";

        public static void Probe()
        {
            try
            {
                LogicalCores = Environment.ProcessorCount;
                if (LogicalCores < 1) LogicalCores = 1;
            }
            catch { LogicalCores = 1; }
            PhysicalCores = LogicalCores;
            try
            {
                uint len = 0;
                Native.GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
                if (len > 0)
                {
                    IntPtr buf = Marshal.AllocHGlobal((int)len);
                    try
                    {
                        if (Native.GetLogicalProcessorInformationEx(0, buf, ref len))
                        {
                            int off = 0, cores = 0;
                            while (off + 8 <= (int)len)
                            {
                                int rel = Marshal.ReadInt32(buf, off);
                                int size = Marshal.ReadInt32(buf, off + 4);
                                if (size <= 0) break;
                                if (rel == 0) cores++;
                                off += size;
                            }
                            if (cores > 0) PhysicalCores = cores;
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            catch { }
            try
            {
                Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (k != null)
                {
                    object name = k.GetValue("ProcessorNameString");
                    if (name != null) CpuName = Convert.ToString(name).Trim();
                    object mhz = k.GetValue("~MHz");
                    if (mhz != null) NominalMhz = Convert.ToDouble(mhz);
                    k.Close();
                }
            }
            catch { }
            if (CpuName.Length == 0) CpuName = "CPU";
        }
    }

    /// <summary>后台采样线程：所有开销较大的读取都在这里完成，UI 只读取快照。</summary>
    internal sealed class Sampler : IDisposable
    {
        volatile int _interval = 1000;
        volatile bool _stop;
        /// <summary>关闭后不再查询 GPU 性能计数器（进一步降低占用）。</summary>
        public volatile bool GpuEnabled = true;
        Thread _thread;
        readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);
        Sample _current;
        long _seq;

        public Sample Current { get { return _current; } }
        public bool GpuSupported { get; private set; }
        public int CoresSeen { get; private set; }

        public int IntervalMs
        {
            get { return _interval; }
            set { if (value < 200) value = 200; if (value > 60000) value = 60000; _interval = value; _wake.Set(); }
        }

        public void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "HwMon.Sampler";
            _thread.Start();
        }

        void Loop()
        {
            int n = MachineInfo.LogicalCores;
            IntPtr cpuBuf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.ProcessorPerfInfo)) * n);
            long[] prevIdle = new long[n];
            long[] prevBusy = new long[n];
            bool havePrev = false;
            long prevDown = 0, prevUp = 0;
            bool haveNet = false;
            long prevTick = (long)Native.GetTickCount64();

            List<NetworkInterface> nics = new List<NetworkInterface>();
            List<DriveInfo2> drives = new List<DriveInfo2>();
            long lastRefresh = 0;

            Pdh pdh = new Pdh();
            pdh.Open();
            GpuSupported = pdh.GpuOk;

            try
            {
                while (!_stop)
                {
                    long nowTick = (long)Native.GetTickCount64();
                    if (!havePrev || nowTick - lastRefresh > 60000)
                    {
                        RefreshHardware(nics, drives);
                        lastRefresh = nowTick;
                    }

                    Sample s = new Sample();
                    s.Seq = ++_seq;
                    s.UptimeSec = (long)(nowTick / 1000);

                    // ---- CPU ----
                    double[] cores = new double[n];
                    double totalPct = 0;
                    int got = 0;
                    int retLen;
                    int st = Native.NtQuerySystemInformation(Native.SystemProcessorPerformanceInformation,
                        cpuBuf, Marshal.SizeOf(typeof(Native.ProcessorPerfInfo)) * n, out retLen);
                    if (st == 0)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            int off = i * Marshal.SizeOf(typeof(Native.ProcessorPerfInfo));
                            long idle = Marshal.ReadInt64(cpuBuf, off);
                            long kern = Marshal.ReadInt64(cpuBuf, off + 8);
                            long user = Marshal.ReadInt64(cpuBuf, off + 16);
                            long busy = (kern - idle) + user;
                            if (havePrev)
                            {
                                double dIdle = idle - prevIdle[i];
                                double db = busy - prevBusy[i];
                                double denom = dIdle + db;
                                double pct = denom > 0 ? 100.0 * db / denom : 0;
                                if (pct < 0) pct = 0;
                                if (pct > 100) pct = 100;
                                cores[i] = pct;
                                if (denom > 0) { totalPct += pct; got++; }
                            }
                            prevIdle[i] = idle;
                            prevBusy[i] = busy;
                        }
                    }
                    if (got > 0) s.Cpu = totalPct / got;
                    else if (havePrev) s.Cpu = 0;
                    s.Cores = cores;

                    // ---- 内存 ----
                    Native.MemoryStatusEx mem = new Native.MemoryStatusEx();
                    mem.Length = (uint)Marshal.SizeOf(typeof(Native.MemoryStatusEx));
                    if (Native.GlobalMemoryStatusEx(ref mem))
                    {
                        s.MemTotal = mem.TotalPhys;
                        s.MemAvail = mem.AvailPhys;
                        s.MemUsed = mem.TotalPhys > mem.AvailPhys ? mem.TotalPhys - mem.AvailPhys : 0;
                        s.MemPercent = mem.TotalPhys > 0 ? 100.0 * s.MemUsed / mem.TotalPhys : 0;
                    }

                    // ---- 电池 ----
                    Native.PowerStatus ps;
                    if (Native.GetSystemPowerStatus(out ps))
                    {
                        if (ps.BatteryLifePercent != 255 && ps.BatteryFlag != 128)
                        {
                            s.HasBattery = true;
                            s.BatteryPercent = ps.BatteryLifePercent;
                            s.AcOnline = ps.ACLineStatus == 1;
                        }
                    }

                    // ---- 磁盘剩余空间 ----
                    DiskInfo[] di = new DiskInfo[drives.Count];
                    for (int i = 0; i < drives.Count; i++)
                    {
                        DiskInfo d2 = new DiskInfo();
                        d2.Name = drives[i].Name;
                        ulong free, total, totalFree;
                        if (Native.GetDiskFreeSpaceEx(d2.Name, out free, out total, out totalFree))
                        {
                            d2.Total = total;
                            d2.Free = free;
                        }
                        di[i] = d2;
                    }
                    s.Disks = di;

                    // ---- 网络 ----
                    ulong downTotal = 0, upTotal = 0;
                    for (int i = 0; i < nics.Count; i++)
                    {
                        try
                        {
                            IPv4InterfaceStatistics st2 = nics[i].GetIPv4Statistics();
                            downTotal += (ulong)st2.BytesReceived;
                            upTotal += (ulong)st2.BytesSent;
                        }
                        catch { }
                    }
                    s.NetDownTotal = downTotal;
                    s.NetUpTotal = upTotal;
                    if (haveNet)
                    {
                        double dt = (nowTick - prevTick) / 1000.0;
                        if (dt <= 0) dt = 1;
                        if (downTotal >= (ulong)prevDown) s.NetDown = (downTotal - (ulong)prevDown) / dt;
                        if (upTotal >= (ulong)prevUp) s.NetUp = (upTotal - (ulong)prevUp) / dt;
                    }
                    prevDown = (long)downTotal; prevUp = (long)upTotal;

                    // ---- PDH：GPU / 磁盘活动 / 进程 / 线程 ----
                    pdh.Collect();
                    s.Gpu = GpuEnabled ? pdh.Gpu() : -1;
                    s.DiskActivity = pdh.DiskActivity();
                    s.Procs = pdh.Processes();
                    s.Threads = pdh.Threads();

                    GpuSupported = pdh.GpuOk;

                    havePrev = true;
                    haveNet = true;
                    prevTick = nowTick;
                    _current = s;

                    int ms = _interval;
                    _wake.Wait(ms);
                    _wake.Reset();
                }
            }
            catch (Exception ex)
            {
                Log.Error("采样线程异常", ex);
            }
            finally
            {
                Marshal.FreeHGlobal(cpuBuf);
                pdh.Dispose();
            }
        }

        struct DriveInfo2 { public string Name; }

        static void RefreshHardware(List<NetworkInterface> nics, List<DriveInfo2> drives)
        {
            nics.Clear();
            try
            {
                NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < all.Length; i++)
                {
                    try
                    {
                        if (all[i].NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        if (all[i].OperationalStatus != OperationalStatus.Up) continue;
                        if (!all[i].Supports(NetworkInterfaceComponent.IPv4)) continue;
                        nics.Add(all[i]);
                    }
                    catch { }
                }
            }
            catch { }

            drives.Clear();
            try
            {
                DriveInfo[] ds = DriveInfo.GetDrives();
                for (int i = 0; i < ds.Length; i++)
                {
                    try
                    {
                        if (ds[i].DriveType != DriveType.Fixed) continue;
                        DriveInfo2 d;
                        d.Name = ds[i].Name.TrimEnd('\\');
                        drives.Add(d);
                    }
                    catch { }
                }
            }
            catch { }
            drives.Sort(delegate (DriveInfo2 a, DriveInfo2 b) { return string.Compare(a.Name, b.Name); });
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            Thread t = _thread;
            if (t != null) { try { t.Join(1500); } catch { } }
            _thread = null;
        }
    }

    /// <summary>历史采样环形缓冲（仅在 UI 线程访问）。</summary>
    internal sealed class History
    {
        public const int Cap = 240;
        readonly double[] _cpu = new double[Cap];
        readonly double[] _mem = new double[Cap];
        readonly double[] _gpu = new double[Cap];
        readonly double[] _disk = new double[Cap];
        int _head, _count;

        public int Count { get { return _count; } }

        public void Add(Sample s)
        {
            _cpu[_head] = s.Cpu;
            _mem[_head] = s.MemPercent;
            _gpu[_head] = s.Gpu;
            _disk[_head] = s.DiskActivity;
            _head = (_head + 1) % Cap;
            if (_count < Cap) _count++;
        }

        public void Clear() { _head = 0; _count = 0; }

        /// <summary>按时间顺序（旧 -> 新）取出，返回实际条数。</summary>
        public int Copy(double[] cpu, double[] mem, double[] gpu, double[] disk)
        {
            int n = _count;
            int start = (_head - n + Cap) % Cap;
            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % Cap;
                cpu[i] = _cpu[idx];
                mem[i] = _mem[idx];
                if (gpu != null) gpu[i] = _gpu[idx];
                if (disk != null) disk[i] = _disk[idx];
            }
            return n;
        }
    }
}
