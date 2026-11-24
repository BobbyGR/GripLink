using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GripLink.UI;

/// <summary>Draws the app/tray icon (a controller glyph) so no image files are needed.</summary>
internal static class AppIcon
{
    public static readonly Color OnColor = Color.FromArgb(0x1F, 0x8F, 0xFF);
    public static readonly Color OffColor = Color.FromArgb(0x80, 0x86, 0x8F);

    public static Bitmap Draw(int size, Color color)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float s = size / 32f;

        // Rounded background tile
        using (var bg = RoundRect(new RectangleF(1 * s, 1 * s, 30 * s, 30 * s), 7 * s))
        using (var brush = new SolidBrush(color))
            g.FillPath(brush, bg);

        // Controller body: a rounded bar plus two grips
        using (var bar = RoundRect(new RectangleF(4 * s, 9 * s, 24 * s, 11 * s), 5.5f * s))
            g.FillPath(Brushes.White, bar);
        var state = g.Save();
        g.TranslateTransform(8.5f * s, 19 * s); g.RotateTransform(20);
        g.FillEllipse(Brushes.White, -3.6f * s, -5 * s, 7.2f * s, 11 * s);
        g.Restore(state);
        state = g.Save();
        g.TranslateTransform(23.5f * s, 19 * s); g.RotateTransform(-20);
        g.FillEllipse(Brushes.White, -3.6f * s, -5 * s, 7.2f * s, 11 * s);
        g.Restore(state);

        // D-pad and buttons
        using var dark = new SolidBrush(color);
        g.FillRectangle(dark, 8 * s, 13.2f * s, 5 * s, 1.6f * s);
        g.FillRectangle(dark, 9.7f * s, 11.5f * s, 1.6f * s, 5 * s);
        g.FillEllipse(dark, 21 * s, 11.2f * s, 2.4f * s, 2.4f * s);
        g.FillEllipse(dark, 21 * s, 15.4f * s, 2.4f * s, 2.4f * s);
        g.FillEllipse(dark, 18.9f * s, 13.3f * s, 2.4f * s, 2.4f * s);
        g.FillEllipse(dark, 23.1f * s, 13.3f * s, 2.4f * s, 2.4f * s);
        return bmp;
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Icon Create(int size, Color color)
    {
        using var bmp = Draw(size, color);
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    /// <summary>Writes a multi-size .ico (PNG entries) for the exe's file icon.</summary>
    public static void SaveIco(string path)
    {
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var images = sizes.Select(sz =>
        {
            using var bmp = Draw(sz, OnColor);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToList();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(images[i].Length); w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
}
