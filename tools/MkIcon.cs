using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// 生成 icon.ico（多尺寸 32bpp + AND mask），供 csc -win32icon 使用
static class MkIcon
{
    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "icon.ico";
        int[] sizes = { 16, 24, 32, 48, 64 };
        List<byte[]> images = new List<byte[]>();
        foreach (int s in sizes) images.Add(Render(s));

        using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)sizes[i]);
                w.Write((byte)sizes[i]);
                w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write((uint)images[i].Length);
                w.Write((uint)offset);
                offset += images[i].Length;
            }
            foreach (byte[] b in images) w.Write(b);
        }
        Console.WriteLine("wrote " + outPath);
    }

    static byte[] Render(int s)
    {
        Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float k = s / 32f;
            using (GraphicsPath p = Round(1 * k, 1 * k, 30 * k, 30 * k, 7 * k))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(238, 13, 16, 21)))
                g.FillPath(b, p);
            using (Pen pen = new Pen(Color.FromArgb(200, 74, 92, 118), Math.Max(1f, k)))
            using (GraphicsPath p2 = Round(1.5f * k, 1.5f * k, 29 * k, 29 * k, 6.5f * k))
                g.DrawPath(pen, p2);
            using (SolidBrush b1 = new SolidBrush(Color.FromArgb(255, 78, 201, 176))) g.FillRectangle(b1, R(6, 17, 5, 8, k));
            using (SolidBrush b2 = new SolidBrush(Color.FromArgb(255, 86, 156, 214))) g.FillRectangle(b2, R(13.5f, 9, 5, 16, k));
            using (SolidBrush b3 = new SolidBrush(Color.FromArgb(255, 206, 145, 120))) g.FillRectangle(b3, R(21, 13, 5, 12, k));
        }

        int stride = s * 4;
        byte[] xor = new byte[stride * s];
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < s; y++)
            System.Runtime.InteropServices.Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), xor, (s - 1 - y) * stride, stride);
        bmp.UnlockBits(bd);
        bmp.Dispose();

        int maskStride = ((s + 31) / 32) * 4;
        byte[] mask = new byte[maskStride * s];

        MemoryStream ms = new MemoryStream();
        BinaryWriter w = new BinaryWriter(ms);
        w.Write((uint)40); w.Write(s); w.Write(s * 2); w.Write((ushort)1); w.Write((ushort)32);
        w.Write((uint)0); w.Write((uint)(xor.Length + mask.Length));
        w.Write(0); w.Write(0); w.Write((uint)0); w.Write((uint)0);
        w.Write(xor); w.Write(mask);
        return ms.ToArray();
    }

    static RectangleF R(float x, float y, float w, float h, float k) { return new RectangleF(x * k, y * k, w * k, h * k); }

    static GraphicsPath Round(float x, float y, float w, float h, float r)
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
}
