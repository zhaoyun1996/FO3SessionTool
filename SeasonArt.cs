using System.Drawing.Drawing2D;

namespace FO3SessionTool;

/// <summary>
/// Looks per season: the card backdrop behind a portrait and the season badge. The game's own card art and season icons are not available
/// as files (they are packed in the client's UI archives, not on the CDN), so every season gets its own generated colours instead:
/// stable (same season = same look), spread over the colour wheel so seasons are easy to tell apart.
/// </summary>
public static class SeasonArt
{
    public readonly record struct Palette(Color Top, Color Bottom, Color Streak, Color Border, Color Badge);

    // The seasons the client's database knows (58). Colours are spread over this list so no two seasons share a hue.
    private static readonly int[] Seasons =
    {
        0, 6, 7, 8, 9, 10, 11, 14, 15, 16, 20, 21, 22, 23, 24, 26, 27, 28, 29, 30, 31, 32, 33, 35, 37, 38, 39, 40, 41, 42, 43, 44, 45, 47, 48, 49,
        51, 53, 55, 57, 61, 63, 65, 67, 69, 71, 73, 77, 81, 82, 85, 87, 88, 89, 91, 93, 95, 97,
    };

    public static Palette For(int season)
    {
        // season 0 (the base card) keeps the original gold/brown look
        if (season <= 0)
            return new Palette(Hsl(38, 0.30, 0.26), Hsl(38, 0.21, 0.08), Color.FromArgb(46, Hsl(38, 0.70, 0.62)), Hsl(38, 0.40, 0.46), Hsl(38, 0.60, 0.40));
        int i = Array.IndexOf(Seasons, season);
        if (i < 0) i = (season * 7) % Seasons.Length;
        // 21 and 58 are coprime: consecutive seasons land far apart on the colour wheel, all 58 hues differ
        double hue = (i * 21 % Seasons.Length) * 360.0 / Seasons.Length;
        double light = 0.24 + 0.05 * (i % 3), sat = (i / 3) % 2 == 0 ? 0.55 : 0.40;
        return new Palette(
            Hsl(hue, sat, light), Hsl(hue, sat * 0.7, 0.07), Color.FromArgb(50, Hsl(hue, 0.75, 0.62)),
            Hsl(hue, sat + 0.15, 0.50), Hsl(hue, 0.65, 0.40));
    }

    /// <summary>Gradient (angle and light streak vary by season) filled into r; the caller clips if the portrait must not spill out.</summary>
    public static void Backdrop(Graphics g, Rectangle r, int season)
    {
        var p = For(season);
        float angle = 40 + (Math.Abs(season) * 37) % 100;
        using (var bg = new LinearGradientBrush(r, p.Top, p.Bottom, angle)) g.FillRectangle(bg, r);
        var st = g.Save();
        g.SetClip(r, CombineMode.Intersect);
        int shift = (Math.Abs(season) % 3) * 22;
        using (var streak = new SolidBrush(p.Streak))
            g.FillPolygon(streak, new[]
            {
                new Point(r.Right - 38 - shift, r.Y), new Point(r.Right - 14 - shift, r.Y),
                new Point(r.Right - 54 - shift, r.Bottom), new Point(r.Right - 78 - shift, r.Bottom),
            });
        if (Math.Abs(season) % 2 == 1)   // a second, thinner streak on every other season
            using (var thin = new SolidBrush(Color.FromArgb(26, p.Streak)))
                g.FillPolygon(thin, new[] { new Point(r.X + 8, r.Y), new Point(r.X + 20, r.Y), new Point(r.X - 10, r.Bottom), new Point(r.X - 22, r.Bottom) });
        g.Restore(st);
    }

    public static Color Border(int season) => For(season).Border;

    /// <summary>Round badge with the season number, for seasons that have no icon in the website's sprite.</summary>
    public static void Badge(Graphics g, Rectangle r, int season)
    {
        var p = For(season);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var br = new SolidBrush(p.Badge)) g.FillEllipse(br, r);
        using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 1f)) g.DrawEllipse(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        g.SmoothingMode = old;
        using var f = new Font(Theme.Body.FontFamily, r.Width >= 20 ? 7.5f : 7f, FontStyle.Bold);
        TextRenderer.DrawText(g, season.ToString(), f, r, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    // ---- HSL -> RGB -----------------------------------------------------------------------------------------------------------------
    private static Color Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        (double r, double gg, double b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return Color.FromArgb((int)((r + m) * 255), (int)((gg + m) * 255), (int)((b + m) * 255));
    }
}
