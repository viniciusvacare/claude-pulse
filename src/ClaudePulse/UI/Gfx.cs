using System.Drawing.Drawing2D;

namespace ClaudePulse.UI;

/// <summary>Primitivas de desenho e cores de dados compartilhadas pelas janelas.</summary>
public static class Gfx
{
    // Paleta categórica validada (ordem fixa; passos próprios para o tema escuro).
    private static readonly Color[] CategoricalLight = [Hex("#2a78d6"), Hex("#eb6834"), Hex("#1baf7a"), Hex("#eda100"), Hex("#e87ba4"), Hex("#008300"), Hex("#6250d6")];
    private static readonly Color[] CategoricalDark = [Hex("#3987e5"), Hex("#d95926"), Hex("#199e70"), Hex("#c98500"), Hex("#d55181"), Hex("#008300"), Hex("#9085e9")];

    /// <summary>Cor da série única (barras do gráfico diário).</summary>
    public static Color Series(bool dark) => dark ? CategoricalDark[0] : CategoricalLight[0];

    /// <summary>Passo mais forte da mesma rampa, para o dia selecionado.</summary>
    public static Color SeriesStrong(bool dark) => dark ? Hex("#9ec5f4") : Hex("#184f95");

    /// <summary>A cor segue a família do modelo, nunca a posição no ranking.</summary>
    public static Color ModelColor(string model, bool dark)
    {
        var palette = dark ? CategoricalDark : CategoricalLight;
        var m = model.ToLowerInvariant();
        int slot = m.StartsWith("opus") ? 0
            : m.StartsWith("sonnet") ? 1
            : m.StartsWith("haiku") ? 2
            : m.StartsWith("fable") || m.StartsWith("mythos") ? 6
            : 3;
        return palette[slot];
    }

    public static void Text(Graphics g, string text, Font font, Color color, RectangleF r,
        StringAlignment align = StringAlignment.Near, StringAlignment lineAlign = StringAlignment.Near, bool wrap = false)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = align,
            LineAlignment = lineAlign,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = wrap ? 0 : StringFormatFlags.NoWrap,
        };
        g.DrawString(text, font, brush, r, format);
    }

    public static void Card(Graphics g, RectangleF r, Palette p)
    {
        using var path = Shapes.Round(r, 8);
        using var fill = new SolidBrush(p.Card);
        using var border = new Pen(p.CardBorder, 1);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
    }

    /// <summary>Retângulo com cantos arredondados só no topo (barra apoiada na base).</summary>
    public static GraphicsPath TopRounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d < 1)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddLine(r.X, r.Bottom, r.X, r.Y + d / 2);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddLine(r.Right, r.Y + d / 2, r.Right, r.Bottom);
        path.CloseFigure();
        return path;
    }

    private static Color Hex(string hex) => ColorTranslator.FromHtml(hex);
}
