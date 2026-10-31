using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace HwMon
{
    /// <summary>纯绘制层：可在无窗口环境下渲染到任意 Bitmap（用于 --render 预览）。</summary>
    internal sealed class Renderer : IDisposable
    {
        public Settings Set;
        public Rectangle CloseRect = Rectangle.Empty;
        public bool HoverClose;

        readonly FontCache _fonts = new FontCache();
        readonly double[] _hCpu = new double[History.Cap];
        readonly double[] _hMem = new double[History.Cap];
        readonly double[] _hGpu = new double[History.Cap];
        readonly double[] _hDisk = new double[History.Cap];

        double scale = 1;
        double netMax = 128 * 1024;

        // ---- 布局自检（--render / --verify 使用）----
        public int ClipWarnings;
        public double MinTextShrink = 1;
        public double UsedBottom;
        int _W, _H;

        // ---------- 配色 ----------
        static readonly Color CText = Color.FromArgb(255, 230, 237, 243);
        static readonly Color CTextDim = Color.FromArgb(200, 145, 158, 171);
        static readonly Color CCpu = Color.FromArgb(235, 78, 201, 176);
        static readonly Color CMem = Color.FromArgb(235, 86, 156, 214);
        static readonly Color CGpu = Color.FromArgb(235, 206, 145, 120);
        static readonly Color CDisk = Color.FromArgb(235, 220, 220, 170);
        static readonly Color CNetD = Color.FromArgb(235, 106, 153, 85);
        static readonly Color CNetU = Color.FromArgb(235, 197, 134, 192);
        static readonly Color CTrack = Color.FromArgb(48, 255, 255, 255);
        static readonly Color CPanel = Color.FromArgb(8, 255, 255, 255);

        static readonly StringFormat FNear = MakeFormat(StringAlignment.Near);
        static readonly StringFormat FFar = MakeFormat(StringAlignment.Far);
        static readonly StringFormat FCenter = MakeFormat(StringAlignment.Center);

        static StringFormat MakeFormat(StringAlignment a)
        {
            StringFormat f = new StringFormat(StringFormat.GenericTypographic);
            f.Alignment = a;
            f.LineAlignment = StringAlignment.Center;
            f.FormatFlags |= StringFormatFlags.NoWrap;
            f.Trimming = StringTrimming.None;
            return f;
        }

        public Renderer(Settings set)
        {
            Set = set;
            _fonts.Family = FontCache.PickFamily();
        }

        float Px(double u) { return (float)(u * scale); }

        public double LastScale { get { return scale; } }

        public void Dispose() { _fonts.Dispose(); }

        // =================== 主入口 ===================
        public void Draw(Graphics g, int W, int H, Sample s, History hist)
        {
            if (W < 8 || H < 8) return;
            _W = W; _H = H; ClipWarnings = 0; MinTextShrink = 1;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.Bilinear;

            if (s == null) s = new Sample();

            // ---------- 1. 计算缩放与固定高度 ----------
            const double padU = 8, headerU = 20, gapU = 5, rowU = 19, coreCapU = 13, coreRowU = 12, footU = 15, graphMinU = 40;
            const double labelU = 48, valueU = 108, barGapU = 5;
            const double baseW = 300;

            bool gpuOk = s.Gpu >= 0;
            bool showGpu = Set.ShowGpu && gpuOk;
            bool showCpu = Set.ShowCpu;
            bool showRam = Set.ShowRam;
            int nDisk = (Set.ShowDisk && s.Disks != null) ? Math.Min(s.Disks.Length, 2) : 0;
            int netRows = Set.ShowNet ? 2 : 0;
            int rows = (showCpu ? 1 : 0) + (showRam ? 1 : 0) + (showGpu ? 1 : 0) + nDisk + netRows;

            int nCores = (s.Cores != null && s.Cores.Length > 0) ? s.Cores.Length : MachineInfo.LogicalCores;

            double widthScale = W / baseW;
            if (widthScale < 0.55) widthScale = 0.55;
            if (widthScale > 2.6) widthScale = 2.6;

            double fixedU = padU * 2 + headerU + gapU + rows * rowU;
            if (Set.ShowCores)
            {
                double cellU = 54;
                double cw = Math.Max(baseW, W / widthScale) - padU * 2;
                int cols = (int)Math.Floor(cw / cellU);
                if (cols < 1) cols = 1;
                int rws = (nCores + cols - 1) / cols;
                fixedU += gapU + coreCapU + rws * coreRowU;
            }
            if (Set.ShowFooter) fixedU += gapU + footU;
            double graphMin = Set.ShowGraph ? graphMinU : 0;
            double minUnits = fixedU + graphMin;

            double maxScale = H / minUnits;
            double sc = widthScale;
            if (sc > maxScale) sc = maxScale;
            if (sc < 0.5) sc = 0.5;
            scale = sc;

            float contentW = W - Px(padU) * 2f;

            // ---------- 2. 面板背景 ----------
            int alpha = (int)Math.Round(Set.OpacityPct * 255.0 / 100.0);
            if (alpha < 12) alpha = 12;
            if (alpha > 255) alpha = 255;
            Color bg = Color.FromArgb(alpha, 13, 16, 21);
            Color border = Color.FromArgb(Math.Min(255, alpha + 46), 74, 92, 118);

            float radius = Px(10);
            using (GraphicsPath panel = RoundRect(0.5f, 0.5f, W - 1f, H - 1f, radius))
            {
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, panel);
                using (Pen p = new Pen(border, 1f)) g.DrawPath(p, panel);
            }

            // ---------- 3. 头部 ----------
            float y = Px(padU);
            float hx = Px(padU);
            float hh = Px(headerU);
            float closeSize = Px(17);
            CloseRect = new Rectangle((int)(W - Px(padU) - closeSize), (int)(y + (hh - closeSize) / 2f),
                (int)closeSize, (int)closeSize);

            float titleRight = CloseRect.Left - Px(4);
            float textPx = Px(12);
            float dimPx = Px(9.5);

            string machine = "";
            if (MachineInfo.PhysicalCores > 0 && MachineInfo.LogicalCores > 0)
                machine = MachineInfo.PhysicalCores + "C/" + MachineInfo.LogicalCores + "T";
            if (MachineInfo.NominalMhz >= 100)
                machine += (machine.Length > 0 ? " " : "") + (MachineInfo.NominalMhz / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "GHz";
            string intervalTxt = IntervalText(Set.IntervalMs);
            float machineW = 0, intervalW = 0;
            {
                Font fm = _fonts.Get(dimPx, FontStyle.Regular);
                machineW = g.MeasureString(machine, fm, new SizeF(10000, 1000), FFar).Width;
                intervalW = g.MeasureString(intervalTxt, fm, new SizeF(10000, 1000), FFar).Width;
            }
            float rightEdge = titleRight - Px(2);
            float intervalLeft = rightEdge - intervalW - Px(2);
            float machineRight = intervalLeft - Px(4);
            float machineLeft = machineRight - machineW;

            RectangleF titleRect = new RectangleF(hx, y, Math.Max(Px(10), machineLeft - Px(3) - hx), hh);
            Text(g, "硬件监控", textPx, FontStyle.Bold, CText, titleRect, StringAlignment.Near);
            if (machine.Length > 0 && machineLeft > hx + Px(60))
            {
                RectangleF mr = new RectangleF(machineLeft, y, machineRight - machineLeft, hh);
                Text(g, machine, dimPx, FontStyle.Regular, CTextDim, mr, StringAlignment.Far);
            }
            RectangleF ir = new RectangleF(intervalLeft, y, intervalW + Px(1), hh);
            Text(g, intervalTxt, dimPx, FontStyle.Regular, CTextDim, ir, StringAlignment.Far);

            // 关闭按钮
            if (HoverClose)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 248, 81, 73)))
                using (GraphicsPath p = RoundRect(CloseRect.X, CloseRect.Y, CloseRect.Width, CloseRect.Height, Px(5)))
                    g.FillPath(b, p);
            }
            Text(g, "✕", Px(10), FontStyle.Regular, HoverClose ? Color.FromArgb(255, 248, 81, 73) : CTextDim,
                new RectangleF(CloseRect.X, CloseRect.Y, CloseRect.Width, CloseRect.Height), StringAlignment.Center);

            y += hh + Px(gapU - 3);

            // ---------- 4. 指标行 ----------
            float rowH = Px(rowU);
            float labelW = Px(labelU);
            float valueW = Px(valueU);
            float barX = hx + labelW + Px(barGapU);
            float barW = contentW - labelW - valueW - Px(barGapU * 2);
            if (barW < Px(20)) barW = Px(20);
            float valueX = hx + contentW - valueW;
            float barH = Px(9);
            float fsRow = Px(10.2);
            float fsVal = Px(9.6);

            // 网络速率动态量程（缓慢回落到至少 128KB/s）
            double peak = Math.Max(s.NetDown, s.NetUp);
            if (peak > netMax) netMax = peak;
            else netMax = Math.Max(netMax * 0.985, 128 * 1024);

            if (showCpu)
            {
                Row(g, y, rowH, hx, labelW, valueX, valueW, barX, barW, barH,
                    "CPU", s.Cpu / 100.0, CCpu, Fmt.Percent(s.Cpu), fsRow, fsVal);
                y += rowH;
            }
            if (showRam)
            {
                string v = Fmt.Percent(s.MemPercent);
                if (s.MemTotal > 0) v += " · " + Fmt.BytesShort(s.MemUsed) + "/" + Fmt.BytesShort(s.MemTotal);
                Row(g, y, rowH, hx, labelW, valueX, valueW, barX, barW, barH,
                    "内存", s.MemPercent / 100.0, CMem, v, fsRow, fsVal);
                y += rowH;
            }
            if (showGpu)
            {
                Row(g, y, rowH, hx, labelW, valueX, valueW, barX, barW, barH,
                    "GPU", s.Gpu / 100.0, CGpu, Fmt.Percent(s.Gpu), fsRow, fsVal);
                y += rowH;
            }
            for (int i = 0; i < nDisk; i++)
            {
                DiskInfo d = s.Disks[i];
                string v = Fmt.Percent(d.Percent) + " · 余" + Fmt.BytesShort(d.Free);
                Row(g, y, rowH, hx, labelW, valueX, valueW, barX, barW, barH,
                    d.Name + ":", d.Percent / 100.0, CDisk, v, fsRow, fsVal);
                y += rowH;
            }
            if (netRows > 0)
            {
                Row(g, y, rowH, hx, labelW, valueX, valueW, barX, barW, barH,
                    "网络↓", s.NetDown / netMax, CNetD, Fmt.Rate(s.NetDown), fsRow, fsVal);
                y += rowH;
                Row(g, y, rowH, hx, labelW, valueX, valueW, barX, barW, barH,
                    "网络↑", s.NetUp / netMax, CNetU, Fmt.Rate(s.NetUp), fsRow, fsVal);
                y += rowH;
            }

            // ---------- 5. 每核心 ----------
            if (Set.ShowCores && nCores > 0)
            {
                y += Px(gapU - 3);
                float capH = Px(coreCapU);
                Text(g, "每核心 (" + nCores + ")", Px(9), FontStyle.Regular, CTextDim,
                    new RectangleF(hx, y - Px(2), contentW, capH), StringAlignment.Near);
                y += capH * 0.7f;

                double cellU = 54;
                double logicalContentW = contentW / scale;
                int cols = (int)Math.Floor(logicalContentW / cellU);
                if (cols < 1) cols = 1;
                float cellW = contentW / cols;
                int rws = (nCores + cols - 1) / cols;
                float cellH = Px(coreRowU);
                float bH = Px(6);
                for (int i = 0; i < nCores; i++)
                {
                    int col = i % cols;
                    int rw = i / cols;
                    float cx = hx + col * cellW;
                    float cy = y + rw * cellH;
                    double pct = (s.Cores != null && i < s.Cores.Length) ? s.Cores[i] : 0;
                    Text(g, (i + 1).ToString(), Px(8.5), FontStyle.Regular, CTextDim,
                        new RectangleF(cx, cy, Px(11), cellH), StringAlignment.Near);
                    float bx = cx + Px(12);
                    float bw = cellW - Px(12) - Px(24);
                    if (bw < Px(6)) bw = Px(6);
                    Bar(g, bx, cy + (cellH - bH) / 2f, bw, bH, pct / 100.0, Heat(pct));
                    Text(g, Fmt.Percent(pct), Px(8.5), FontStyle.Regular, CTextDim,
                        new RectangleF(cx + cellW - Px(23), cy, Px(23), cellH), StringAlignment.Far);
                }
                y += rws * cellH;
            }

            // ---------- 6. 历史曲线 ----------
            float bottomLimit = H - Px(padU) - (Set.ShowFooter ? Px(footU) + Px(gapU) : 0);
            if (Set.ShowGraph)
            {
                float gh = bottomLimit - Px(gapU) - y;
                if (gh < Px(18)) gh = Px(18);
                DrawGraph(g, hx, y, contentW, gh, s, hist);
                y += gh + Px(gapU);
            }

            // ---------- 7. 页脚 ----------
            if (Set.ShowFooter)
            {
                float fy = H - Px(padU) - Px(footU);
                if (fy < y - Px(gapU)) fy = y - Px(gapU) + 1;
                RectangleF fr = new RectangleF(hx, fy, contentW, Px(footU));
                DrawFooter(g, fr, s);
            }

            UsedBottom = Math.Max(y, H - Px(padU));
        }

        string IntervalText(int ms)
        {
            if (ms % 1000 == 0) return (ms / 1000) + "s";
            return (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        static Color Heat(double pct)
        {
            double t = pct / 100.0;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            Color a = Color.FromArgb(255, 63, 185, 80);
            Color b = Color.FromArgb(255, 210, 153, 34);
            Color c = Color.FromArgb(255, 248, 81, 73);
            Color r;
            if (t < 0.6) r = Lerp(a, b, t / 0.6);
            else r = Lerp(b, c, (t - 0.6) / 0.4);
            return Color.FromArgb(235, r.R, r.G, r.B);
        }

        static Color Lerp(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        void Row(Graphics g, float y, float rowH, float hx, float labelW, float valueX, float valueW,
            float barX, float barW, float barH, string label, double frac, Color color, string value, float fs, float fsVal)
        {
            float mid = y + rowH / 2f;
            Text(g, label, fs, FontStyle.Regular, CText, new RectangleF(hx, y, labelW - Px(4), rowH), StringAlignment.Near);
            Bar(g, barX, mid - barH / 2f, barW, barH, frac, color);
            Text(g, value, fsVal, FontStyle.Regular, CText, new RectangleF(valueX - Px(1), y, valueW, rowH), StringAlignment.Far);
        }

        void Bar(Graphics g, float x, float y, float w, float h, double frac, Color color)
        {
            if (w <= 0 || h <= 0) return;
            float r = h / 2f;
            using (GraphicsPath track = RoundRect(x, y, w, h, r))
            using (SolidBrush tb = new SolidBrush(CTrack))
                g.FillPath(tb, track);
            if (frac <= 0) return;
            if (frac > 1) frac = 1;
            float fw = (float)(w * frac);
            float minW = h * 1.2f;
            if (fw < minW) fw = (float)Math.Min(minW, w);
            using (GraphicsPath fill = RoundRect(x, y, fw, h, r))
            using (SolidBrush fb = new SolidBrush(color))
                g.FillPath(fb, fill);
        }

        void DrawGraph(Graphics g, float x, float y, float w, float h, Sample s, History hist)
        {
            if (w < Px(30) || h < Px(12)) return;
            using (GraphicsPath p = RoundRect(x, y, w, h, Px(6)))
            using (SolidBrush b = new SolidBrush(CPanel))
                g.FillPath(b, p);

            float inner = Px(3);
            float gx = x + inner, gy = y + inner, gw = w - inner * 2, gh = h - inner * 2;

            using (Pen gp = new Pen(Color.FromArgb(26, 255, 255, 255), 1f))
            {
                for (int i = 1; i <= 3; i++)
                {
                    float ly = gy + gh * i / 4f;
                    g.DrawLine(gp, gx, ly, gx + gw, ly);
                }
            }

            int n = 0;
            if (hist != null)
            {
                n = hist.Copy(_hCpu, _hMem, _hGpu, _hDisk);
            }
            int maxPts = (int)(gw / Math.Max(1.0f, Px(1.6)));
            if (maxPts < 2) maxPts = 2;
            int use = n < maxPts ? n : maxPts;

            if (use >= 2)
            {
                int start = n - use;
                DrawSeries(g, gx, gy, gw, gh, _hCpu, start, use, CCpu);
                if (Set.ShowRam) DrawSeries(g, gx, gy, gw, gh, _hMem, start, use, CMem);
                if (Set.ShowGpu && s.Gpu >= 0) DrawSeries(g, gx, gy, gw, gh, _hGpu, start, use, CGpu);
                if (Set.ShowDisk) DrawSeries(g, gx, gy, gw, gh, _hDisk, start, use, CDisk);
            }
            else
            {
                Text(g, "正在采集…", Px(9), FontStyle.Regular, CTextDim,
                    new RectangleF(gx, gy, gw, gh), StringAlignment.Center);
            }

            // 图例
            if (h > Px(34) && w > Px(150))
            {
                float lx = gx + Px(4);
                float ly = gy + Px(1);
                lx = Legend(g, lx, ly, "CPU", CCpu);
                if (Set.ShowRam) lx = Legend(g, lx, ly, "内存", CMem);
                if (Set.ShowGpu && s.Gpu >= 0) lx = Legend(g, lx, ly, "GPU", CGpu);
                if (Set.ShowDisk && s.DiskActivity >= 0) Legend(g, lx, ly, "磁盘", CDisk);
            }
        }

        float Legend(Graphics g, float x, float y, string text, Color c)
        {
            float sq = Px(5);
            using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, x, y + sq, sq, sq);
            float fs = Px(8);
            Font f = _fonts.Get(fs, FontStyle.Regular);
            SizeF sz = g.MeasureString(text, f, new SizeF(10000, 1000), FNear);
            using (SolidBrush tb = new SolidBrush(CTextDim))
                g.DrawString(text, f, tb, new RectangleF(x + sq + Px(3), y, sz.Width + 2, sq * 2 + 2), FNear);
            return x + sq + Px(3) + sz.Width + Px(8);
        }

        void DrawSeries(Graphics g, float x, float y, float w, float h, double[] data, int start, int count, Color c)
        {
            if (count < 2) return;
            PointF[] pts = new PointF[count];
            for (int i = 0; i < count; i++)
            {
                double v = data[(start + i) % data.Length];
                if (v < 0) v = 0;
                if (v > 100) v = 100;
                float px = x + w * i / (count - 1);
                float py = y + h - (float)(v / 100.0 * h);
                pts[i] = new PointF(px, py);
            }
            using (Pen p = new Pen(c, Math.Max(1f, Px(1.2))))
            {
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, pts);
            }
            using (SolidBrush b = new SolidBrush(c))
            {
                float d = Math.Max(1.6f, Px(2.4));
                g.FillEllipse(b, pts[count - 1].X - d / 2f, pts[count - 1].Y - d / 2f, d, d);
            }
        }

        void DrawFooter(Graphics g, RectangleF r, Sample s)
        {
            float fs = Px(9);
            Font f = _fonts.Get(fs, FontStyle.Regular);
            string[] parts = new string[6];
            int n = 0;
            parts[n++] = "运行 " + Fmt.Duration(s.UptimeSec);
            if (s.Procs >= 0) parts[n++] = "进程 " + ((int)s.Procs).ToString();
            if (s.Threads >= 0) parts[n++] = "线程 " + ((int)s.Threads).ToString();
            if (s.HasBattery && s.BatteryPercent >= 0)
                parts[n++] = "电池 " + s.BatteryPercent + "%" + (s.AcOnline ? " ⚡" : "");
            parts[n++] = DateTime.Now.ToString("HH:mm:ss");

            // 从右往左排布，放不下就跳过（不裁剪）
            float x = r.Right;
            float sepW = g.MeasureString("  ·  ", f, new SizeF(10000, 1000), FFar).Width;
            for (int i = n - 1; i >= 0; i--)
            {
                string t = parts[i];
                SizeF sz = g.MeasureString(t, f, new SizeF(10000, 1000), FFar);
                float need = sz.Width + (i < n - 1 ? sepW : 0) + Px(2);
                if (x - need < r.Left) break;
                x -= sz.Width + Px(2);
                using (SolidBrush b = new SolidBrush(CTextDim))
                    g.DrawString(t, f, b, new RectangleF(x, r.Y, sz.Width + Px(1), r.Height), FFar);
                if (i < n - 1)
                {
                    x -= sepW;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(120, 145, 158, 171)))
                        g.DrawString("·", f, b, new RectangleF(x, r.Y, sepW, r.Height), FFar);
                }
            }
        }

        void Text(Graphics g, string text, float px, FontStyle style, Color color, RectangleF rect, StringAlignment align)
        {
            if (text == null || text.Length == 0 || rect.Width <= 1 || rect.Height <= 1) return;
            StringFormat fmt = align == StringAlignment.Far ? FFar : (align == StringAlignment.Center ? FCenter : FNear);
            Font f = _fonts.Get(px, style);
            SizeF sz = g.MeasureString(text, f, new SizeF(10000, 1000), fmt);
            if (sz.Width > rect.Width && rect.Width > 2)
            {
                float k = rect.Width / sz.Width;
                float px2 = px * k;
                float min = px * 0.62f;
                if (px2 < min) px2 = min;
                double shrink = px2 / px;
                if (shrink < MinTextShrink) MinTextShrink = shrink;
                f = _fonts.Get(px2, style);
                SizeF sz3 = g.MeasureString(text, f, new SizeF(10000, 1000), fmt);
                if (sz3.Width > rect.Width + 0.75f) ClipWarnings++;
            }
            if (rect.Right > _W + 0.5f || rect.Bottom > _H + 0.5f || rect.Left < -0.5f || rect.Top < -0.5f) ClipWarnings++;
            using (SolidBrush b = new SolidBrush(color))
                g.DrawString(text, f, b, rect, fmt);
        }

        static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            if (w <= 0 || h <= 0) { p.AddRectangle(new RectangleF(x, y, Math.Max(0.1f, w), Math.Max(0.1f, h))); return p; }
            if (r < 0) r = 0;
            float max = Math.Min(w, h) / 2f;
            if (r > max) r = max;
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>根据当前可见元素计算最小窗口尺寸（保证内容完整显示）。</summary>
        public Size MinSize(Sample s)
        {
            const double padU = 8, headerU = 20, gapU = 5, rowU = 19, coreCapU = 13, coreRowU = 12, footU = 15, graphMinU = 40;
            bool gpuOk = s != null && s.Gpu >= 0;
            int nDisk = (Set.ShowDisk && s != null && s.Disks != null) ? Math.Min(s.Disks.Length, 2) : 0;
            int netRows = Set.ShowNet ? 2 : 0;
            int rows = (Set.ShowCpu ? 1 : 0) + (Set.ShowRam ? 1 : 0) + ((Set.ShowGpu && gpuOk) ? 1 : 0) + nDisk + netRows;
            int nCores = (s != null && s.Cores != null && s.Cores.Length > 0) ? s.Cores.Length : MachineInfo.LogicalCores;

            double fixedU = padU * 2 + headerU + gapU + rows * rowU;
            if (Set.ShowCores)
            {
                int cols = 2;
                int rws = (nCores + cols - 1) / cols;
                fixedU += gapU + coreCapU + rws * coreRowU;
            }
            if (Set.ShowFooter) fixedU += gapU + footU;
            if (Set.ShowGraph) fixedU += gapU + graphMinU;

            double minScale = 0.62;
            int minW = (int)Math.Ceiling(300 * minScale);
            int minH = (int)Math.Ceiling(fixedU * minScale);
            if (minW < 150) minW = 150;
            if (minH < 90) minH = 90;
            return new Size(minW, minH);
        }
    }
}
