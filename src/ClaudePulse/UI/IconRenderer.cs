using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ClaudePulse.Core;

namespace ClaudePulse.UI;

/// <summary>Desenha o ícone da bandeja em tempo real a partir dos percentuais.</summary>
public static class IconRenderer
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <param name="primary">Percentual principal (null = sem dados).</param>
    /// <param name="secondary">Segundo percentual, usado no estilo de barras.</param>
    public static Icon Render(double? primary, double? secondary, IconStyle style, Func<double, Color> colorFor)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            var fg = Theme.TaskbarUsesDark() ? Color.White : Color.FromArgb(30, 30, 30);

            if (style == IconStyle.Bars)
                DrawBars(g, size, primary, secondary, fg, colorFor);
            else
                DrawBadge(g, size, primary, fg, colorFor);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>Ícone do app (faísca do Claude), usado em janelas e notificações.</summary>
    public static Icon AppIcon(int size = 32)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            DrawSpark(g, new RectangleF(0, 0, size, size), Palette.Accent);
        }
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>Faísca de 8 raios com comprimentos alternados.</summary>
    public static void DrawSpark(Graphics g, RectangleF r, Color color)
    {
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        float outer = r.Width / 2 * 0.92f;
        using var pen = new Pen(color, Math.Max(1.6f, r.Width / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < 8; i++)
        {
            double angle = Math.PI / 4 * i - Math.PI / 2;
            float len = i % 2 == 0 ? outer : outer * 0.68f;
            float inner = r.Width * 0.08f;
            g.DrawLine(pen,
                cx + (float)Math.Cos(angle) * inner, cy + (float)Math.Sin(angle) * inner,
                cx + (float)Math.Cos(angle) * len, cy + (float)Math.Sin(angle) * len);
        }
    }

    private static void DrawBadge(Graphics g, int size, double? percent, Color fg, Func<double, Color> colorFor)
    {
        // Quadrado arredondado na cor do nível, com o número em destaque: legível até em 16 px.
        var rect = new RectangleF(0, 0, size, size);
        var background = percent is double p ? colorFor(p) : Color.FromArgb(110, 128, 128, 128);

        string text = percent switch
        {
            null => "–",
            >= 99.5 => "!",
            double v => Math.Round(v).ToString("0"),
        };
        // Texto escuro sobre o âmbar, branco nas demais cores.
        bool darkText = background.GetBrightness() > 0.55f && background.R > 200 && background.G > 140;
        var textColor = darkText ? Color.FromArgb(20, 20, 20) : Color.White;
        if (percent is not null && !darkText)
            background = Deepen(background);
        using (var path = Shapes.Round(rect, size * 0.22f))
        using (var brush = new SolidBrush(background))
            g.FillPath(brush, path);

        float fontPx = size * (text.Length >= 2 ? 0.70f : 0.80f);
        Font font;
        while (true)
        {
            font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            if (measured.Width <= size - 1.5f || fontPx <= 6)
                break;
            font.Dispose();
            fontPx -= 0.5f;
        }
        using (font)
        using (var brush = new SolidBrush(textColor))
        using (var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(text, font, brush, new RectangleF(0, -size * 0.02f, size, size), format);
    }

    /// <summary>Escurece a cor até o branco atingir contraste 4,5:1 (WCAG AA) sobre ela.</summary>
    private static Color Deepen(Color c)
    {
        var result = c;
        for (float k = 0.96f; Contrast(Color.White, result) < 4.5 && k > 0.3f; k -= 0.04f)
            result = Color.FromArgb(c.A, (int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
        return result;
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static void DrawBars(Graphics g, int size, double? top, double? bottom, Color fg, Func<double, Color> colorFor)
    {
        float h = MathF.Round(size * 0.38f), gap = Math.Max(2, size - h * 2 - 2);
        float y1 = (size - (h * 2 + gap)) / 2;
        DrawBar(g, new RectangleF(0, y1, size, h), top, colorFor);
        DrawBar(g, new RectangleF(0, y1 + h + gap, size, h), bottom, colorFor);
    }

    private static void DrawBar(Graphics g, RectangleF r, double? percent, Func<double, Color> colorFor)
    {
        using var track = Shapes.Round(r, r.Height * 0.35f);
        using (var trackBrush = new SolidBrush(Color.FromArgb(110, 128, 128, 128)))
            g.FillPath(trackBrush, track);
        if (percent is not double p || p <= 0)
            return;
        var state = g.Save();
        g.SetClip(track);
        using (var brush = new SolidBrush(colorFor(p)))
            g.FillRectangle(brush, r.X, r.Y, Math.Max(1.5f, r.Width * (float)Math.Clamp(p, 0, 100) / 100), r.Height);
        g.Restore(state);
    }
}

public static class Shapes
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.1f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
