using Microsoft.Win32;

namespace ClaudePulse.UI;

public sealed record Palette(
    bool Dark,
    Color Background,
    Color Card,
    Color CardBorder,
    Color Text,
    Color SubText,
    Color Track,
    Color Hover,
    Color Separator)
{
    public static readonly Color Accent = Color.FromArgb(217, 119, 87); // laranja Claude

    public static Palette Current() => Theme.AppsUseDark()
        ? new Palette(true,
            Background: Color.FromArgb(32, 32, 32),
            Card: Color.FromArgb(43, 43, 43),
            CardBorder: Color.FromArgb(56, 56, 56),
            Text: Color.FromArgb(245, 245, 245),
            SubText: Color.FromArgb(160, 160, 160),
            Track: Color.FromArgb(62, 62, 62),
            Hover: Color.FromArgb(58, 58, 58),
            Separator: Color.FromArgb(54, 54, 54))
        : new Palette(false,
            Background: Color.FromArgb(243, 243, 243),
            Card: Color.FromArgb(255, 255, 255),
            CardBorder: Color.FromArgb(229, 229, 229),
            Text: Color.FromArgb(26, 26, 26),
            SubText: Color.FromArgb(96, 96, 96),
            Track: Color.FromArgb(226, 226, 226),
            Hover: Color.FromArgb(232, 232, 232),
            Separator: Color.FromArgb(234, 234, 234));
}

public static class Theme
{
    public static readonly Color Good = Color.FromArgb(76, 194, 120);
    public static readonly Color Warn = Color.FromArgb(242, 169, 59);
    public static readonly Color Bad = Color.FromArgb(232, 84, 76);

    public static readonly Color[] Categories =
    [
        Palette.Accent,
        Color.FromArgb(96, 153, 230),
        Color.FromArgb(155, 120, 220),
        Color.FromArgb(120, 190, 170),
    ];

    /// <summary>Cor para um percentual de limite do plano.</summary>
    public static Color ForLimit(double percent) => percent >= 85 ? Bad : percent >= 60 ? Warn : Good;

    /// <summary>Cor para ocupação da janela de contexto.</summary>
    public static Color ForContext(double percent) => percent >= 80 ? Bad : percent >= 50 ? Warn : Good;

    public static bool AppsUseDark() => !ReadPersonalize("AppsUseLightTheme", 1);

    public static bool TaskbarUsesDark() => !ReadPersonalize("SystemUsesLightTheme", 0);

    private static bool ReadPersonalize(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (key?.GetValue(name) as int? ?? fallback) != 0;
        }
        catch
        {
            return fallback != 0;
        }
    }
}
