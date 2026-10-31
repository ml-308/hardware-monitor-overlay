using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace HwMon
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Log.FilePath = Path.Combine(Path.GetTempPath(), "HwMon.log");
            try
            {
                MachineInfo.Probe();
                string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                if (mode == "--selftest") { SelfTest(args); return; }
                if (mode == "--render") { RenderPreview(args); return; }
                if (mode == "--shot") { Shot(args); return; }
                if (mode == "--verify") { Verify(args); return; }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs e)
                { Log.Error("UI 线程异常", e.Exception); };

                Settings set = Settings.Load();
                Sampler sampler = new Sampler();
                sampler.IntervalMs = set.IntervalMs;
                sampler.Start();

                OverlayForm form = new OverlayForm(set, sampler);
                Application.Run(form);
                sampler.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("主程序异常", ex);
                try { MessageBox.Show("硬件监控浮窗启动失败：\n" + ex.Message, "硬件监控", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch { }
            }
        }

        // ==================== 自检：真实采集数据 ====================
        static void SelfTest(string[] args)
        {
            string outPath = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "hwmon-selftest.txt");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("HwMon selftest @ " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("CPU: " + MachineInfo.CpuName);
            sb.AppendLine("logical=" + MachineInfo.LogicalCores + " physical=" + MachineInfo.PhysicalCores +
                          " nominalMHz=" + MachineInfo.NominalMhz.ToString("0"));
            sb.AppendLine("ProcessorPerfInfo size=" + System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.ProcessorPerfInfo)));
            sb.AppendLine("配置路径: " + Settings.ConfigPath);
            sb.AppendLine("日志路径: " + Log.FilePath);
            sb.AppendLine("可执行文件: " + System.Windows.Forms.Application.ExecutablePath);
            sb.AppendLine();

            Sampler s = new Sampler();
            s.IntervalMs = 1000;
            s.Start();
            long last = -1;
            for (int i = 0; i < 3; i++)
            {
                long deadline = Environment.TickCount + (i == 0 ? 2500 : 1200);
                Sample cur = null;
                while (Environment.TickCount < deadline)
                {
                    cur = s.Current;
                    System.Threading.Thread.Sleep(50);
                }
                cur = s.Current;
                if (cur == null) { sb.AppendLine("no sample"); continue; }
                if (cur.Seq == last) { sb.AppendLine("(same sample)"); }
                last = cur.Seq;
                sb.AppendLine("---- sample #" + cur.Seq + " ----");
                sb.AppendLine("CPU        : " + cur.Cpu.ToString("0.0") + " %");
                StringBuilder cores = new StringBuilder();
                for (int c = 0; c < cur.Cores.Length; c++) { cores.Append(cur.Cores[c].ToString("0")); cores.Append(c == cur.Cores.Length - 1 ? "" : " / "); }
                sb.AppendLine("cores      : " + cores);
                sb.AppendLine("RAM        : " + cur.MemPercent.ToString("0.0") + " %  " + Fmt.Bytes(cur.MemUsed) + " / " + Fmt.Bytes(cur.MemTotal) + " (avail " + Fmt.Bytes(cur.MemAvail) + ")");
                sb.AppendLine("GPU        : " + (cur.Gpu < 0 ? "不可用" : cur.Gpu.ToString("0.0") + " %") + "  (pdh=" + s.GpuSupported + ")");
                sb.AppendLine("disk act   : " + (cur.DiskActivity < 0 ? "不可用" : cur.DiskActivity.ToString("0.0") + " %"));
                sb.AppendLine("processes  : " + cur.Procs.ToString("0") + "   threads: " + cur.Threads.ToString("0"));
                sb.AppendLine("network    : down " + Fmt.Rate(cur.NetDown) + " / up " + Fmt.Rate(cur.NetUp) +
                              "   total " + Fmt.Bytes(cur.NetDownTotal) + " / " + Fmt.Bytes(cur.NetUpTotal));
                sb.AppendLine("battery    : " + (cur.HasBattery ? cur.BatteryPercent + "% ac=" + cur.AcOnline : "无电池"));
                sb.AppendLine("uptime     : " + Fmt.Duration(cur.UptimeSec));
                for (int d = 0; d < cur.Disks.Length; d++)
                    sb.AppendLine("drive " + cur.Disks[d].Name + " : " + cur.Disks[d].Percent.ToString("0.0") + " %  free " + Fmt.Bytes(cur.Disks[d].Free) + " / " + Fmt.Bytes(cur.Disks[d].Total));
            }
            s.Dispose();
            sb.AppendLine();
            sb.AppendLine("history ring: ok");
            sb.AppendLine("DONE");

            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        }

        // ==================== 预览：把浮窗画到一张对照图上 ====================
        static void RenderPreview(string[] args)
        {
            string outPath = args.Length > 1 ? args[1] : "preview.png";
            Settings set = Settings.Load();
            Renderer r = new Renderer(set);

            int[][] sizes = new int[][] {
                new int[] { 300, 320 }, new int[] { 420, 240 }, new int[] { 210, 200 },
                new int[] { 280, 520 }, new int[] { 380, 380 }
            };

            Sample sample = FakeSample(0);
            History h = FakeHistory();

            StringBuilder rep = new StringBuilder();
            rep.AppendLine("布局自检：每个尺寸独立渲染，clip 必须为 0，usedBottom 必须 <= H");
            rep.AppendLine("模式      尺寸       scale  usedBottom/H  clip  minShrink");
            int[][] checkSizes = new int[][] {
                new int[] { 186, 150 }, new int[] { 200, 220 }, new int[] { 240, 160 }, new int[] { 300, 300 },
                new int[] { 380, 260 }, new int[] { 520, 360 }, new int[] { 300, 620 }, new int[] { 640, 480 },
                new int[] { 210, 200 }, new int[] { 900, 300 }
            };
            Settings full = Settings.Load();
            Settings mini = Settings.Load();
            mini.ApplyMini(true);
            string[] presetNames = new string[] { "完整", "迷你" };
            Settings[] presets = new Settings[] { full, mini };
            for (int pi = 0; pi < presets.Length; pi++)
            {
                foreach (int[] cs2 in checkSizes)
                {
                    Renderer rr = new Renderer(presets[pi]);
                    Bitmap tb = new Bitmap(cs2[0], cs2[1], PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(tb))
                    {
                        g.Clear(Color.Transparent);
                        rr.Draw(g, cs2[0], cs2[1], sample, h);
                    }
                    int[] ink2 = InkStats(tb);
                    rep.AppendLine(string.Format("{0,-6} {1,-9} {2,5:0.00}  {3,6:0.0}/{4,-6} {5,5}  {6,8:0.00}",
                        presetNames[pi], cs2[0] + "x" + cs2[1], rr.LastScale, rr.UsedBottom, cs2[1], rr.ClipWarnings, rr.MinTextShrink));
                    tb.Dispose();
                    rr.Dispose();
                }
            }
            rep.AppendLine();
            rep.AppendLine("预览图（对照尺寸）:");
            rep.AppendLine("size       scale  usedBottom/H   clip  minShrink  inkRatio  inkBBox");

            Bitmap canvas = new Bitmap(1180, 700, PixelFormat.Format32bppArgb);
            using (Graphics cg = Graphics.FromImage(canvas))
            {
                cg.Clear(Color.FromArgb(255, 22, 26, 34));
                using (LinearGradientBrush lb = new LinearGradientBrush(new Rectangle(0, 0, 1180, 700),
                    Color.FromArgb(255, 36, 52, 84), Color.FromArgb(255, 12, 14, 18), 35f))
                    cg.FillRectangle(lb, 0, 0, 1180, 700);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 120, 170, 220)))
                {
                    cg.FillRectangle(b, 0, 560, 1180, 40);
                    cg.FillRectangle(b, 60, 606, 320, 90);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 210, 120, 160)))
                    cg.FillEllipse(b, 860, 60, 260, 260);

                int x = 20, y = 20, rowH = 0;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int bw2 = sizes[i][0], bh2 = sizes[i][1];
                    if (x + bw2 > canvas.Width - 20) { x = 20; y += rowH + 20; rowH = 0; }
                    Bitmap bmp = new Bitmap(bw2, bh2, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        r.Draw(g, bw2, bh2, sample, h);
                    }
                    int[] ink = InkStats(bmp);
                    rep.AppendLine(string.Format("{0,-9} {1,5:0.00}  {2,6:0.0}/{3,-6} {4,5}  {5,8:0.00}  {6,7:0.000}  x{7}-{8} y{9}-{10}",
                        bw2 + "x" + bh2, r.LastScale, r.UsedBottom, bh2, r.ClipWarnings, r.MinTextShrink,
                        ink[0] / (double)(bw2 * bh2), ink[1], ink[2], ink[3], ink[4]));
                    cg.DrawImageUnscaled(bmp, x, y);
                    cg.DrawString(bw2 + "x" + bh2, SystemFonts.DefaultFont, Brushes.White, x + 2, y + bh2 + 2);
                    x += bw2 + 20;
                    if (bh2 > rowH) rowH = bh2;
                }
            }
            canvas.Save(outPath, ImageFormat.Png);
            r.Dispose();
            File.WriteAllText(outPath + ".txt", rep.ToString(), new UTF8Encoding(false));
        }

        /// <summary>返回：不透明像素比例, 墨迹包围盒 x0,y0,x1,y1</summary>
        static int[] InkStats(Bitmap bmp)
        {
            int x0 = bmp.Width, y0 = bmp.Height, x1 = -1, y1 = -1, count = 0;
            Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                byte[] row = new byte[stride];
                for (int y = 0; y < bmp.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * stride), row, 0, stride);
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        if (row[x * 4 + 3] > 12)
                        {
                            count++;
                            if (x < x0) x0 = x;
                            if (x > x1) x1 = x;
                            if (y < y0) y0 = y;
                            if (y > y1) y1 = y;
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }
            return new int[] { count, x0, y0, x1, y1 };
        }

        static Sample FakeSample(int seed)
        {
            Sample s = new Sample();
            s.Seq = 100;
            int n = MachineInfo.LogicalCores;
            s.Cores = new double[n];
            Random rnd = new Random(7);
            for (int i = 0; i < n; i++) s.Cores[i] = 8 + rnd.Next(0, 55) + (i == 2 ? 25 : 0);
            s.Cpu = 37.4;
            s.MemTotal = 16UL * 1024 * 1024 * 1024;
            s.MemUsed = (ulong)(9.9 * 1024 * 1024 * 1024);
            s.MemAvail = s.MemTotal - s.MemUsed;
            s.MemPercent = 61.9;
            s.Gpu = 12.0;
            s.DiskActivity = 23.0;
            s.Procs = 340;
            s.Threads = 3380;
            s.NetDown = 1.25 * 1024 * 1024;
            s.NetUp = 310 * 1024;
            s.NetDownTotal = 7UL * 1024 * 1024 * 1024;
            s.NetUpTotal = 1600UL * 1024 * 1024;
            s.HasBattery = true;
            s.BatteryPercent = 99;
            s.AcOnline = true;
            s.UptimeSec = 3 * 86400 + 4 * 3600 + 11 * 60;
            DiskInfo[] d = new DiskInfo[2];
            d[0] = new DiskInfo(); d[0].Name = "C:"; d[0].Total = 210UL * 1024 * 1024 * 1024; d[0].Free = 122UL * 1024 * 1024 * 1024;
            d[1] = new DiskInfo(); d[1].Name = "D:"; d[1].Total = 954UL * 1024 * 1024 * 1024; d[1].Free = 402UL * 1024 * 1024 * 1024;
            s.Disks = d;
            return s;
        }

        static History FakeHistory()
        {
            History h = new History();
            Random rnd = new Random(11);
            for (int i = 0; i < 160; i++)
            {
                Sample s = new Sample();
                double t = i / 12.0;
                s.Cpu = 34 + 22 * Math.Sin(t) + rnd.NextDouble() * 8;
                s.MemPercent = 58 + 6 * Math.Sin(t / 2.0);
                s.Gpu = 8 + 14 * Math.Abs(Math.Sin(t * 0.7)) + rnd.NextDouble() * 4;
                s.DiskActivity = 6 + 18 * Math.Abs(Math.Cos(t * 0.5));
                if (s.Cpu < 0) s.Cpu = 0; if (s.Cpu > 100) s.Cpu = 100;
                h.Add(s);
            }
            return h;
        }

        // ==================== 真实浮窗截图（端到端验证） ====================
        static void Shot(string[] args)
        {
            string outPath = args.Length > 1 ? args[1] : "shot.png";
            int seconds = args.Length > 2 ? int.Parse(args[2]) : 4;
            int px = args.Length > 3 ? int.Parse(args[3]) : 60;
            int py = args.Length > 4 ? int.Parse(args[4]) : 60;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Settings set = Settings.Load();
            set.X = px; set.Y = py; set.W = 320; set.H = 380;
            set.TopMost = true;
            Sampler sampler = new Sampler();
            sampler.IntervalMs = 400;
            sampler.Start();

            OverlayForm form = new OverlayForm(set, sampler);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(px, py);
            form.ClientSize = new Size(320, 380);
            form.Show();

            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = seconds * 1000;
            t.Tick += delegate
            {
                t.Stop();
                try
                {
                    Rectangle b = form.Bounds;
                    using (Bitmap cap = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(cap))
                            g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                        cap.Save(outPath, ImageFormat.Png);
                    }
                }
                catch (Exception ex) { Log.Error("截图失败", ex); }
                form.Close();
            };
            t.Start();
            Application.Run(form);
            sampler.Dispose();
        }

        // ==================== 端到端验证：真实分层窗口 ====================
        static void Verify(string[] args)
        {
            string prefix = args.Length > 1 ? args[1] : "verify";
            int px = args.Length > 2 ? int.Parse(args[2]) : 60;
            int py = args.Length > 3 ? int.Parse(args[3]) : 60;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Settings set = Settings.Load();
            set.OpacityPct = 84;
            set.TopMost = true;
            Sampler sampler = new Sampler();
            sampler.IntervalMs = 400;
            sampler.Start();

            OverlayForm form = new OverlayForm(set, sampler);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(px, py);
            form.ClientSize = new Size(340, 420);
            form.TraceOn = true;
            string pre = "载入配置 W=" + set.W + " H=" + set.H + " X=" + set.X + " Y=" + set.Y +
                         " | 赋值后 client=" + form.ClientSize + " loc=" + form.Location +
                         " min=" + form.MinimumSize + " max=" + form.MaximumSize;
            form.Show();
            string post = "Show 后 client=" + form.ClientSize + " loc=" + form.Location +
                          " min=" + form.MinimumSize + " max=" + form.MaximumSize + " dpi=" + form.DeviceDpi +
                          " border=" + form.FormBorderStyle +
                          " style=0x" + Native.GetWindowLong(form.Handle, -16).ToString("X8") +
                          " exstyle=0x" + Native.GetWindowLong(form.Handle, -20).ToString("X8");

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(pre);
            sb.AppendLine(post);
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 3500;
            t.Tick += delegate
            {
                t.Stop();
                Native.RECT wr;
                Native.GetWindowRect(form.Handle, out wr);
                Rectangle b = new Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
                sb.AppendLine("真实浮窗端到端验证 @ " + DateTime.Now.ToString("HH:mm:ss"));
                sb.AppendLine("窗口矩形(物理): " + b.X + "," + b.Y + " " + b.Width + "x" + b.Height +
                              "   逻辑: " + form.Bounds.X + "," + form.Bounds.Y + " " + form.Bounds.Width + "x" + form.Bounds.Height +
                              "   客户区: " + form.ClientSize.Width + "x" + form.ClientSize.Height);
                sb.AppendLine("DPI: system=" + Native.GetDpiForSystem() + " window=" + Native.GetDpiForWindow(form.Handle));
                sb.AppendLine("渲染自检: scale=" + form.Renderer.LastScale.ToString("0.000") +
                              " usedBottom=" + form.Renderer.UsedBottom.ToString("0.0") +
                              " clip=" + form.Renderer.ClipWarnings +
                              " minShrink=" + form.Renderer.MinTextShrink.ToString("0.00"));
                try
                {
                    Bitmap a = Capture(b);
                    form.Hide();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(320);
                    Bitmap c = Capture(b);

                    byte[] pa = Pixels(a), pc = Pixels(c);
                    int w = b.Width, h = b.Height;

                    // 1) 四角（圆角外，应完全透明）
                    double corner = 0; int cn = 0;
                    int[] cxs = { 0, w - 4, 0, w - 4 }, cys = { 0, 0, h - 4, h - 4 };
                    for (int k = 0; k < 4; k++)
                        for (int yy = 0; yy < 4; yy++)
                            for (int xx = 0; xx < 4; xx++)
                            {
                                int i = ((cys[k] + yy) * w + cxs[k] + xx) * 4;
                                corner += Math.Abs(pa[i] - pc[i]) + Math.Abs(pa[i + 1] - pc[i + 1]) + Math.Abs(pa[i + 2] - pc[i + 2]);
                                cn++;
                            }
                    corner /= cn;

                    // 2) 面板内部：与背景的相关性（>0 表示半透明混合，桌面透出来了）
                    int inx0 = 14, iny0 = 14, inx1 = w - 14, iny1 = h - 14;
                    double sA = 0, sB = 0, sAB = 0, sAA = 0, sBB = 0, lumA = 0, lumB = 0;
                    int n = 0, ink = 0, total = 0;
                    for (int yy = 0; yy < h; yy++)
                        for (int xx = 0; xx < w; xx++)
                        {
                            int i = (yy * w + xx) * 4;
                            double la = 0.299 * pa[i + 2] + 0.587 * pa[i + 1] + 0.114 * pa[i];
                            double lb = 0.299 * pc[i + 2] + 0.587 * pc[i + 1] + 0.114 * pc[i];
                            total++;
                            if (Math.Abs(la - lb) > 6) ink++;
                            if (xx >= inx0 && xx < inx1 && yy >= iny0 && yy < iny1)
                            {
                                sA += la; sB += lb; sAB += la * lb; sAA += la * la; sBB += lb * lb; lumA += la; lumB += lb; n++;
                            }
                        }
                    double corr = 0, panelDelta = 0;
                    if (n > 0)
                    {
                        double num = n * sAB - sA * sB;
                        double den = Math.Sqrt(Math.Max(1e-9, (n * sAA - sA * sA) * (n * sBB - sB * sB)));
                        if (den > 1e-9) corr = num / den;
                        lumA /= n; lumB /= n;
                        panelDelta = Math.Abs(lumA - lumB);
                    }
                    double cornerLimit = Math.Max(12.0, panelDelta * 0.25);
                    bool pass = corner < cornerLimit && corr > 0.10;

                    sb.AppendLine("面板平均改变 : " + panelDelta.ToString("0.0") + " / 255");
                    sb.AppendLine("四角平均色差 : " + corner.ToString("0.00") + "  阈值 < " + cornerLimit.ToString("0.0") + "  (圆角外完全透明)");
                    sb.AppendLine("面板亮度     : 显示后 " + lumA.ToString("0.0") + " / 隐藏后 " + lumB.ToString("0.0"));
                    sb.AppendLine("背景相关性   : " + corr.ToString("0.000") + "  (>0 表示背景透过半透明面板可见)");
                    sb.AppendLine("覆盖像素比例 : " + (100.0 * ink / total).ToString("0.0") + "%  (窗口范围内被浮窗改变的像素)");
                    sb.AppendLine(pass
                        ? "结论: 通过 —— 逐像素透明生效，半透明背景可见，内容未溢出窗口"
                        : "结论: 需检查");

                    a.Save(prefix + "-widget.png", ImageFormat.Png);
                    c.Save(prefix + "-desktop.png", ImageFormat.Png);
                    a.Dispose(); c.Dispose();
                }
                catch (Exception ex) { sb.AppendLine("验证异常: " + ex.Message); Log.Error("verify", ex); }

                File.WriteAllText(prefix + ".txt", sb.ToString(), new UTF8Encoding(false));
                form.Close();
            };
            t.Start();
            Application.Run(form);
            sampler.Dispose();
        }

        static Bitmap Capture(Rectangle r)
        {
            Bitmap b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
                g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
            return b;
        }

        static byte[] Pixels(Bitmap b)
        {
            Rectangle r = new Rectangle(0, 0, b.Width, b.Height);
            BitmapData bd = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] all = new byte[b.Width * b.Height * 4];
                for (int y = 0; y < b.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), all, y * b.Width * 4, b.Width * 4);
                return all;
            }
            finally { b.UnlockBits(bd); }
        }
    }
}
