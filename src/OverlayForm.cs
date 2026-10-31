using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace HwMon
{
    /// <summary>分层（逐像素透明）浮窗：可拖动、可缩放、置顶、右键菜单、托盘常驻。</summary>
    internal sealed class OverlayForm : Form
    {
        const int HTCLIENT = 1;
        const int WM_NCHITTEST = 0x0084;

        readonly Settings _set;
        readonly Sampler _sampler;
        readonly History _hist = new History();
        readonly Renderer _render;
        System.Windows.Forms.Timer _timer;
        ContextMenuStrip _menu;
        NotifyIcon _tray;
        Icon _icon;

        Bitmap _bmp;
        IntPtr _dib = IntPtr.Zero, _bits = IntPtr.Zero, _memDc = IntPtr.Zero;
        int _dibW, _dibH;
        long _lastSeq = -1;

        bool _dragging, _resizing;
        int _zone;
        Point _dragCursor;
        Point _dragOrigin;
        Rectangle _dragBounds;
        bool _dragMoved;
        bool _hoverClose;
        Size _minSize = new Size(150, 90);

        public Renderer Renderer { get { return _render; } }

        public readonly System.Text.StringBuilder TraceLog = new System.Text.StringBuilder();
        public bool TraceOn;
        void Tr(string s)
        {
            if (!TraceOn || TraceLog.Length > 4000) return;
            TraceLog.AppendLine(s + " | client=" + ClientSize.Width + "x" + ClientSize.Height +
                " bounds=" + Bounds.Width + "x" + Bounds.Height + " dpi=" + DeviceDpi);
        }

        public OverlayForm(Settings set, Sampler sampler)
        {
            _set = set;
            _sampler = sampler;
            _render = new Renderer(set);

            AutoScaleMode = AutoScaleMode.None;   // 手动绘制：禁止 WinForms 自动缩放，像素尺寸即最终尺寸
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = set.TopMost;
            Text = "硬件监控";
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            int w = set.W, h = set.H;
            ClientSize = new Size(w, h);
            Location = ResolveStartLocation(w, h);

            _icon = CreateIcon();
            Icon = _icon;

            _sampler.GpuEnabled = set.ShowGpu;
            BuildMenu();
            SetupTray();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 100;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        // ================= 位置 / 尺寸 =================
        Point ResolveStartLocation(int w, int h)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            try { wa = Screen.FromPoint(Cursor.Position).WorkingArea; } catch { }
            if (_set.X == int.MinValue || _set.Y == int.MinValue)
                return new Point(wa.Right - w - 24, wa.Top + 24);
            Point p = new Point(_set.X, _set.Y);
            // 若完全落在屏幕外则复位
            bool visible = false;
            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                Rectangle r = Screen.AllScreens[i].WorkingArea;
                if (r.IntersectsWith(new Rectangle(p, new Size(Math.Max(40, w), Math.Max(20, h))))) { visible = true; break; }
            }
            if (!visible) return new Point(wa.Right - w - 24, wa.Top + 24);
            return p;
        }

        void SavePlace()
        {
            _set.X = Left;
            _set.Y = Top;
            _set.W = Width;
            _set.H = Height;
            _set.Save();
        }

        // ================= 渲染管线 =================
        void EnsureBuffers(int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            if (_bmp != null && _dibW == w && _dibH == h && _dib != IntPtr.Zero) return;
            ReleaseBuffers();

            _bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            Native.BitmapInfoHeader bmi = new Native.BitmapInfoHeader();
            bmi.biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.BitmapInfoHeader));
            bmi.biWidth = w;
            bmi.biHeight = -h;             // 自上而下
            bmi.biPlanes = 1;
            bmi.biBitCount = 32;
            bmi.biCompression = Native.BI_RGB;
            _dib = Native.CreateDIBSection(screenDc, ref bmi, Native.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            _memDc = Native.CreateCompatibleDC(screenDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            _dibW = w;
            _dibH = h;
        }

        void ReleaseBuffers()
        {
            if (_bmp != null) { try { _bmp.Dispose(); } catch { } _bmp = null; }
            if (_memDc != IntPtr.Zero) { try { Native.DeleteDC(_memDc); } catch { } _memDc = IntPtr.Zero; }
            if (_dib != IntPtr.Zero) { try { Native.DeleteObject(_dib); } catch { } _dib = IntPtr.Zero; }
            _bits = IntPtr.Zero;
            _dibW = _dibH = 0;
        }

        public void Render()
        {
            if (!IsHandleCreated) return;
            // 用真实窗口矩形驱动分层表面：UpdateLayeredWindow 会设置窗口尺寸，
            // 若传客户区尺寸会与 WinForms 的非客户区换算形成收缩回环。
            Native.RECT wr;
            if (!Native.GetWindowRect(Handle, out wr)) return;
            int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
            if (w <= 0 || h <= 0) return;
            EnsureBuffers(w, h);
            if (_bmp == null || _dib == IntPtr.Zero || _memDc == IntPtr.Zero) return;

            try
            {
                using (Graphics g = Graphics.FromImage(_bmp))
                {
                    g.Clear(Color.Transparent);
                    _render.HoverClose = _hoverClose;
                    Sample s = _sampler != null ? _sampler.Current : null;
                    _render.Draw(g, w, h, s, _hist);
                }
                PushLayered(w, h);
            }
            catch (Exception ex) { Log.Error("渲染失败", ex); }
        }

        void PushLayered(int w, int h)
        {
            Tr("ULW " + w + "x" + h);
            Rectangle rect = new Rectangle(0, 0, w, h);
            BitmapData bd = _bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int rowBytes = w * 4;
                long src = bd.Scan0.ToInt64();
                long dst = _bits.ToInt64();
                for (int y = 0; y < h; y++)
                    Native.CopyMemory(new IntPtr(dst + (long)y * rowBytes), new IntPtr(src + (long)y * bd.Stride), new IntPtr(rowBytes));
            }
            finally { _bmp.UnlockBits(bd); }

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr old = Native.SelectObject(_memDc, _dib);
            Native.POINT dstPt = new Native.POINT(Left, Top);
            Native.SIZE size = new Native.SIZE(w, h);
            Native.POINT srcPt = new Native.POINT(0, 0);
            Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
            bf.BlendOp = Native.AC_SRC_OVER;
            bf.BlendFlags = 0;
            bf.SourceConstantAlpha = 255;
            bf.AlphaFormat = Native.AC_SRC_ALPHA;
            Native.UpdateLayeredWindow(Handle, screenDc, ref dstPt, ref size, _memDc, ref srcPt, 0, ref bf, Native.ULW_ALPHA);
            Native.SelectObject(_memDc, old);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnHandleCreated(EventArgs e) { Tr("HandleCreated"); base.OnHandleCreated(e); Render(); }
        protected override void OnShown(EventArgs e) { Tr("Shown"); base.OnShown(e); Render(); }
        protected override void OnResize(EventArgs e) { Tr("Resize"); base.OnResize(e); Render(); }

        // ================= 采样刷新 =================
        void OnTick(object sender, EventArgs e)
        {
            Sample s = _sampler != null ? _sampler.Current : null;
            if (s == null || s.Seq == _lastSeq) return;
            _lastSeq = s.Seq;
            _hist.Add(s);

            Size ms = _render.MinSize(s);
            if (ms.Width != _minSize.Width || ms.Height != _minSize.Height)
            {
                _minSize = ms;
                MinimumSize = ms;
                if (Width < ms.Width || Height < ms.Height)
                    ClientSize = new Size(Math.Max(Width, ms.Width), Math.Max(Height, ms.Height));
            }
            else if (MinimumSize != ms)
            {
                MinimumSize = ms;
            }

            Render();
            UpdateTrayText(s);
        }

        void UpdateTrayText(Sample s)
        {
            if (_tray == null) return;
            try
            {
                string t = "CPU " + Fmt.Percent(s.Cpu) + " · 内存 " + Fmt.Percent(s.MemPercent);
                if (s.Gpu >= 0) t += " · GPU " + Fmt.Percent(s.Gpu);
                if (t.Length > 62) t = t.Substring(0, 62);
                _tray.Text = t;
            }
            catch { }
        }

        // ================= 鼠标：拖动 / 缩放 / 关闭 =================
        int HitZone(Point p)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            int e = (int)Math.Max(5, Math.Min(9, Math.Min(w, h) / 16.0));
            if (_render.CloseRect.Contains(p)) return 0;
            bool left = p.X < e, right = p.X >= w - e, top = p.Y < e, bottom = p.Y >= h - e;
            if (top && left) return 13;
            if (top && right) return 14;
            if (bottom && left) return 16;
            if (bottom && right) return 17;
            if (left) return 10;
            if (right) return 11;
            if (top) return 12;
            if (bottom) return 15;
            return 0;
        }

        static Cursor CursorFor(int zone)
        {
            switch (zone)
            {
                case 10: case 11: return Cursors.SizeWE;
                case 12: case 15: return Cursors.SizeNS;
                case 13: case 17: return Cursors.SizeNWSE;
                case 14: case 16: return Cursors.SizeNESW;
                default: return Cursors.Default;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                BuildMenu();
                _menu.Show(this, new Point(e.X, e.Y));
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (_render.CloseRect.Contains(e.Location)) { Close(); return; }

            int z = HitZone(e.Location);
            _dragCursor = Cursor.Position;
            _dragMoved = false;
            if (z != 0)
            {
                _resizing = true;
                _zone = z;
                _dragBounds = Bounds;
                Cursor = CursorFor(z);
            }
            else
            {
                _dragging = true;
                _dragOrigin = Location;
                Cursor = Cursors.SizeAll;
            }
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_dragging)
            {
                Point cur = Cursor.Position;
                int nx = _dragOrigin.X + (cur.X - _dragCursor.X);
                int ny = _dragOrigin.Y + (cur.Y - _dragCursor.Y);
                Snap(ref nx, ref ny, Width, Height);
                Location = new Point(nx, ny);
                if (Math.Abs(cur.X - _dragCursor.X) > 3 || Math.Abs(cur.Y - _dragCursor.Y) > 3) _dragMoved = true;
                return;
            }
            if (_resizing)
            {
                Point cur = Cursor.Position;
                int dx = cur.X - _dragCursor.X;
                int dy = cur.Y - _dragCursor.Y;
                Rectangle b = _dragBounds;
                int minW = _minSize.Width, minH = _minSize.Height;

                bool left = _zone == 10 || _zone == 13 || _zone == 16;
                bool right = _zone == 11 || _zone == 14 || _zone == 17;
                bool top = _zone == 12 || _zone == 13 || _zone == 14;
                bool bottom = _zone == 15 || _zone == 16 || _zone == 17;

                if (left) { b.X += dx; b.Width -= dx; }
                if (right) { b.Width += dx; }
                if (top) { b.Y += dy; b.Height -= dy; }
                if (bottom) { b.Height += dy; }

                if (b.Width < minW) { if (left) b.X = b.Right - minW; b.Width = minW; }
                if (b.Height < minH) { if (top) b.Y = b.Bottom - minH; b.Height = minH; }
                Bounds = b;
                if (Math.Abs(dx) > 2 || Math.Abs(dy) > 2) _dragMoved = true;
                return;
            }

            bool hover = _render.CloseRect.Contains(e.Location);
            if (hover != _hoverClose) { _hoverClose = hover; Render(); }
            int z = HitZone(e.Location);
            Cursor = z == 0 ? Cursors.SizeAll : CursorFor(z);
        }

        void Snap(ref int x, ref int y, int w, int h)
        {
            const int tol = 14;
            try
            {
                Screen scr = Screen.FromPoint(new Point(x + w / 2, y + h / 2));
                Rectangle wa = scr.WorkingArea;
                if (Math.Abs(x - wa.Left) < tol) x = wa.Left;
                if (Math.Abs(x + w - wa.Right) < tol) x = wa.Right - w;
                if (Math.Abs(y - wa.Top) < tol) y = wa.Top;
                if (Math.Abs(y + h - wa.Bottom) < tol) y = wa.Bottom - h;
            }
            catch { }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (_dragging || _resizing)
            {
                _dragging = false;
                _resizing = false;
                _zone = 0;
                Capture = false;
                Cursor = Cursors.Default;
                SavePlace();
                Render();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && !_dragMoved && !_render.CloseRect.Contains(e.Location))
                ToggleMini();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverClose) { _hoverClose = false; Render(); }
        }

        protected override void WndProc(ref Message m)
        {
            if (TraceOn && (m.Msg == 0x02E0 || m.Msg == 0x0047 || m.Msg == 0x0005 || m.Msg == 0x0024))
                Tr("msg 0x" + m.Msg.ToString("X4") + " wparam=" + m.WParam.ToInt64().ToString("X"));
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if (m.Result.ToInt32() == HTCLIENT && !_dragging && !_resizing)
                {
                    int lp = m.LParam.ToInt32();
                    Point sp = new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF)));
                    Point cp = PointToClient(sp);
                    int z = HitZone(cp);
                    if (z != 0) m.Result = new IntPtr(z);
                }
                return;
            }
            base.WndProc(ref m);
        }

        // ================= 菜单 =================
        void BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.ShowImageMargin = false;

            ToolStripMenuItem top = new ToolStripMenuItem("窗口置顶");
            top.Checked = _set.TopMost;
            top.Click += delegate { _set.TopMost = !_set.TopMost; TopMost = _set.TopMost; _set.Save(); };
            m.Items.Add(top);

            ToolStripMenuItem items = new ToolStripMenuItem("显示项目");
            items.DropDownItems.Add(Check("CPU 使用率", _set.ShowCpu, delegate { _set.ShowCpu = !_set.ShowCpu; AfterLayoutChange(); }));
            items.DropDownItems.Add(Check("内存占用", _set.ShowRam, delegate { _set.ShowRam = !_set.ShowRam; AfterLayoutChange(); }));
            items.DropDownItems.Add(Check("GPU 使用率", _set.ShowGpu, delegate { _set.ShowGpu = !_set.ShowGpu; AfterLayoutChange(); }));
            items.DropDownItems.Add(Check("磁盘容量", _set.ShowDisk, delegate { _set.ShowDisk = !_set.ShowDisk; AfterLayoutChange(); }));
            items.DropDownItems.Add(Check("网络速率", _set.ShowNet, delegate { _set.ShowNet = !_set.ShowNet; AfterLayoutChange(); }));
            items.DropDownItems.Add(new ToolStripSeparator());
            items.DropDownItems.Add(Check("每核心占用", _set.ShowCores, delegate { _set.ShowCores = !_set.ShowCores; AfterLayoutChange(); }));
            items.DropDownItems.Add(Check("历史曲线", _set.ShowGraph, delegate { _set.ShowGraph = !_set.ShowGraph; AfterLayoutChange(); }));
            items.DropDownItems.Add(Check("底部信息", _set.ShowFooter, delegate { _set.ShowFooter = !_set.ShowFooter; AfterLayoutChange(); }));
            m.Items.Add(items);

            ToolStripMenuItem op = new ToolStripMenuItem("背景不透明度");
            int[] vals = { 100, 92, 84, 75, 62, 48, 34 };
            for (int i = 0; i < vals.Length; i++)
            {
                int v = vals[i];
                ToolStripMenuItem it = new ToolStripMenuItem(v + "%");
                it.Checked = _set.OpacityPct == v;
                it.Click += delegate { _set.OpacityPct = v; AfterLayoutChange(); };
                op.DropDownItems.Add(it);
            }
            m.Items.Add(op);

            ToolStripMenuItem iv = new ToolStripMenuItem("刷新间隔");
            int[] ivs = { 500, 1000, 2000, 3000, 5000 };
            for (int i = 0; i < ivs.Length; i++)
            {
                int v = ivs[i];
                ToolStripMenuItem it = new ToolStripMenuItem(v < 1000 ? (v / 1000.0).ToString("0.0") + " 秒" : (v / 1000) + " 秒");
                it.Checked = _set.IntervalMs == v;
                it.Click += delegate { _set.IntervalMs = v; if (_sampler != null) _sampler.IntervalMs = v; _set.Save(); Render(); };
                iv.DropDownItems.Add(it);
            }
            m.Items.Add(iv);

            ToolStripMenuItem mini = new ToolStripMenuItem("迷你模式");
            mini.Checked = _set.Mini;
            mini.Click += delegate { ToggleMini(); };
            m.Items.Add(mini);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem reset = new ToolStripMenuItem("重置位置与大小");
            reset.Click += delegate
            {
                _set.W = 300; _set.H = 300;
                ClientSize = new Size(300, 300);
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - 24, wa.Top + 24);
                _set.X = Left; _set.Y = Top;
                _set.Save();
                Render();
            };
            m.Items.Add(reset);

            ToolStripMenuItem auto = new ToolStripMenuItem("开机自动启动");
            auto.Checked = Settings.AutoStartEnabled();
            auto.Click += delegate
            {
                bool on = !Settings.AutoStartEnabled();
                Settings.SetAutoStart(on);
            };
            m.Items.Add(auto);

            ToolStripMenuItem hide = new ToolStripMenuItem("隐藏到托盘");
            hide.Click += delegate { Hide(); };
            m.Items.Add(hide);

            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { Close(); };
            m.Items.Add(exit);

            if (_menu != null) _menu.Dispose();
            _menu = m;
            if (_tray != null) _tray.ContextMenuStrip = m;
        }

        ToolStripMenuItem Check(string text, bool check, EventHandler handler)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Checked = check;
            it.Click += handler;
            return it;
        }

        void AfterLayoutChange()
        {
            if (_sampler != null) _sampler.GpuEnabled = _set.ShowGpu;
            Size ms = _render.MinSize(_sampler != null ? _sampler.Current : null);
            _minSize = ms;
            MinimumSize = ms;
            if (Width < ms.Width || Height < ms.Height)
                ClientSize = new Size(Math.Max(Width, ms.Width), Math.Max(Height, ms.Height));
            _set.Save();
            Render();
        }

        void ToggleMini()
        {
            _set.ApplyMini(!_set.Mini);
            Size ms = _render.MinSize(_sampler != null ? _sampler.Current : null);
            _minSize = ms;
            MinimumSize = ms;
            int w = Math.Max(ms.Width, 240);
            int h = Math.Max(ms.Height, 0);
            ClientSize = new Size(w, h);
            _set.W = Width; _set.H = Height;
            _set.Save();
            Render();
        }

        // ================= 托盘 =================
        void SetupTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = _icon;
            _tray.Text = "硬件监控浮窗";
            _tray.Visible = true;
            _tray.ContextMenuStrip = _menu;
            _tray.DoubleClick += delegate
            {
                if (Visible) { Hide(); }
                else { Show(); Render(); }
            };
        }

        static Icon CreateIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (GraphicsPath p = RoundedPath(1, 1, 30, 30, 7))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 13, 16, 21)))
                        g.FillPath(b, p);
                    using (SolidBrush b1 = new SolidBrush(Color.FromArgb(255, 78, 201, 176))) g.FillRectangle(b1, 7, 17, 5, 8);
                    using (SolidBrush b2 = new SolidBrush(Color.FromArgb(255, 86, 156, 214))) g.FillRectangle(b2, 14, 9, 5, 16);
                    using (SolidBrush b3 = new SolidBrush(Color.FromArgb(255, 206, 145, 120))) g.FillRectangle(b3, 21, 13, 5, 12);
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h)) { return (Icon)tmp.Clone(); }
                }
                finally { Native.DestroyIcon(h); }
            }
        }

        static GraphicsPath RoundedPath(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                _set.X = Left; _set.Y = Top; _set.W = Width; _set.H = Height;
                _set.Save();
            }
            catch { }
            try { if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; } } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }
            try { ReleaseBuffers(); } catch { }
            try { if (_render != null) _render.Dispose(); } catch { }
            base.OnFormClosing(e);
        }
    }
}
