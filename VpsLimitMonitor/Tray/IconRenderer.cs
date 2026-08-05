using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace VpsLimitMonitor.Tray;

/// <summary>用 GDI+ 动态绘制托盘图标：圆角色块 + 白色文字（已用百分比或状态符号）。</summary>
public static class IconRenderer
{
    public static MemoryStream RenderPng(string text, Color background)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        using var path = RoundedRect(new RectangleF(0, 0, size, size), 8);
        using var brush = new SolidBrush(background);
        g.FillPath(brush, path);

        var fontSize = text.Length switch
        {
            <= 1 => 23f,
            2 => 20f,
            _ => 14f,
        };
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using var outlineBrush = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
        foreach (var (x, y) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            g.DrawString(text, font, outlineBrush, new RectangleF(x, y + 1, size, size), format);
        g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, size, size), format);

        var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        return ms;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
