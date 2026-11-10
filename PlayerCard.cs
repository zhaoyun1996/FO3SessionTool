using System.Drawing.Drawing2D;

namespace FO3SessionTool;

/// <summary>Minimum player profile (like the fifaaddict header): season, name, nation/club, body, positions, foot, skill moves.</summary>
public sealed class PlayerCard : Control
{
    private PlayerDetail? _d;
    private string _message = "Chọn cầu thủ để xem thông tin.";

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Image? Portrait { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Image? Crest { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Bitmap? Sprite { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Image? Flag { get; set; }
    /// <summary>Market line shown to the right of the birth date: price of the cheapest +1 card.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string PriceText { get; set; } = "";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string PriceNote { get; set; } = "";

    public PlayerCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 154;
        Font = Theme.Body;
    }

    public void Clear(string message) { _d = null; Portrait = null; Crest = null; Flag = null; PriceText = ""; PriceNote = ""; _message = message; Invalidate(); }
    public void SetDetail(PlayerDetail d) { _d = d; Invalidate(); }

    private static readonly Color SeasonBg = Color.FromArgb(150, 36, 36);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Surface);   // corners outside the rounded card
        using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 6))
        using (var fill = new SolidBrush(Theme.Surface2))
        using (var pen = new Pen(Theme.Border))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        if (_d == null)
        {
            TextRenderer.DrawText(g, _message, Theme.Body, new Rectangle(0, 0, Width, Height), Theme.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        var d = _d;
        const TextFormatFlags R = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.Right | TextFormatFlags.EndEllipsis;

        // portrait on its own backdrop (the game's PNG is transparent, so the backdrop is drawn here)
        var pr = new Rectangle(12, 8, 124, 104);
        using (var bg = new LinearGradientBrush(pr, Color.FromArgb(58, 52, 40), Color.FromArgb(18, 20, 26), 60f)) g.FillRectangle(bg, pr);
        var st = g.Save();
        g.SetClip(pr);
        using (var streak = new SolidBrush(Color.FromArgb(40, 215, 170, 80)))
            g.FillPolygon(streak, new[] { new Point(pr.Right - 38, pr.Y), new Point(pr.Right - 14, pr.Y), new Point(pr.Right - 54, pr.Bottom), new Point(pr.Right - 78, pr.Bottom) });
        if (Portrait != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Portrait, FitInto(Portrait.Size, pr));
        }
        g.Restore(st);
        using (var pen = new Pen(Color.FromArgb(120, 100, 70))) g.DrawRectangle(pen, pr);
        var badge = new Rectangle(pr.Right - 28, pr.Bottom - 18, 26, 16);   // card grade
        using (var bb = new SolidBrush(Color.FromArgb(200, 20, 22, 28))) g.FillRectangle(bb, badge);
        TextRenderer.DrawText(g, "+1", Theme.SmallBold, badge, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // season icon + name under the portrait
        int ny = pr.Bottom + 9, nx = pr.X;
        if (Sprite != null && PlayerSearch.SeasonSpriteX.TryGetValue(d.Season, out var sx))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Sprite, new Rectangle(nx, ny + 1, 18, 18), new Rectangle(sx, 0, 18, 18), GraphicsUnit.Pixel);
            nx += 22;
        }
        TextRenderer.DrawText(g, d.Name, Theme.Bold, new Rectangle(nx, ny, 148 - (nx - pr.X), 20), Theme.Text, F | TextFormatFlags.EndEllipsis);

        // right column: height | nation+flag, weight | club+crest, birth date
        int x = pr.Right + 22, right = Width - 14;
        int y = 12, x2 = x + 84;
        TextRenderer.DrawText(g, d.Height + " cm", Theme.Bold, new Point(x, y), Theme.Text, F);
        int wN = TextRenderer.MeasureText(g, d.Nation, Theme.Bold, new Size(int.MaxValue, 0), F).Width;
        TextRenderer.DrawText(g, d.Nation, Theme.Bold, new Point(x2, y), Theme.Text, F);
        if (Flag != null) g.DrawImage(Flag, new Rectangle(x2 + wN + 8, y + 1, 26, 17));
        y = 34;
        TextRenderer.DrawText(g, d.Weight + " kg", Theme.Bold, new Point(x, y), Theme.Text, F);
        int wC = TextRenderer.MeasureText(g, d.Club, Theme.Bold, new Size(int.MaxValue, 0), F).Width;
        TextRenderer.DrawText(g, d.Club, Theme.Bold, new Point(x2, y), Theme.Text, F);
        if (Crest != null) g.DrawImage(Crest, new Rectangle(x2 + wC + 8, y - 1, 20, 20));
        y = 56;
        TextRenderer.DrawText(g, d.BirthDate, Theme.Bold, new Point(x, y), Theme.Text, F);
        if (PriceText.Length > 0)
        {
            int bw = TextRenderer.MeasureText(g, d.BirthDate, Theme.Bold, new Size(int.MaxValue, 0), F).Width;
            int px0 = x + Math.Max(84, bw + 18);
            TextRenderer.DrawText(g, "Giá +1", Theme.Body, new Point(px0, y), Theme.Dim, F);
            int lw = TextRenderer.MeasureText(g, "Giá +1", Theme.Body, new Size(int.MaxValue, 0), F).Width + 8;
            var txt = PriceNote.Length > 0 ? $"{PriceText}  ·  {PriceNote}" : PriceText;
            TextRenderer.DrawText(g, txt, Theme.Bold, new Rectangle(px0 + lw, y, Math.Max(10, right - px0 - lw), 20), Theme.Gold, F | TextFormatFlags.EndEllipsis);
        }

        // feet (left) and positions + ratings (right), stars at the far right
        int top = Height - 12 - 44;
        Foot(g, x, top, d.LeftFoot, !d.PrefersRight, -12);
        Foot(g, x + 30, top, d.RightFoot, d.PrefersRight, 12);
        int px = x + 30 + 34 + 12;
        int row = 0;
        foreach (var pos in d.Positions.OrderByDescending(p => p.Active).ThenByDescending(p => p.Rating).Take(6))
        {
            int col = row / 3, r = row % 3;
            int cx = px + col * 70, cy = Height - 14 - 3 * 17 + r * 17;
            using (var br = new SolidBrush(Theme.PosColor(pos.Group))) g.FillRectangle(br, new Rectangle(cx, cy + 2, 4, 14));
            TextRenderer.DrawText(g, pos.Pos, Theme.Bold, new Point(cx + 10, cy), Theme.Text, F);
            int pw = TextRenderer.MeasureText(g, pos.Pos, Theme.Bold, new Size(int.MaxValue, 0), F).Width;
            TextRenderer.DrawText(g, pos.Rating.ToString(), Theme.Bold, new Point(cx + 10 + pw + 6, cy), pos.Active ? Theme.Accent : Theme.Dim, F);
            if (++row >= 6) break;
        }
        if (d.SkillMoves > 0)
            TextRenderer.DrawText(g, new string('★', d.SkillMoves), Theme.Body, new Rectangle(x, 78, right - x, 18), Theme.Gold, F);
    }

    /// <summary>Boot-sole icon like the game's: dark sole for the weak foot, blue for the preferred one, rating in white.</summary>
    internal static void Foot(Graphics g, int x, int y, int value, bool preferred, float tilt)
    {
        bool right = tilt > 0;
        PointF[] pts = { new(7, 9), new(15, 1), new(25, 4), new(29, 15), new(26, 29), new(22, 40), new(14, 42), new(9, 36), new(7, 24) };
        if (!right) pts = pts.Select(p => new PointF(34 - p.X, p.Y)).ToArray();
        var st = g.Save();
        g.TranslateTransform(x + 17, y + 22);
        g.RotateTransform(right ? 6 : -6);
        g.TranslateTransform(-17, -22);
        var fill = preferred ? Color.FromArgb(30, 110, 235) : Color.FromArgb(34, 36, 42);
        using (var br = new SolidBrush(fill)) g.FillClosedCurve(br, pts, FillMode.Winding, 0.35f);
        using (var pen = new Pen(preferred ? Color.FromArgb(70, 150, 255) : Color.FromArgb(70, 74, 84), 1.2f)) g.DrawClosedCurve(pen, pts, 0.35f, FillMode.Winding);
        g.Restore(st);
        TextRenderer.DrawText(g, value.ToString(), Theme.Large, new Rectangle(x + 2, y + 8, 30, 24), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    // scale to fill the box (cropping the sides), bottom-aligned so the face never gets an empty band above it
    private static Rectangle CoverBox(Size src, Rectangle box)
    {
        if (src.Width == 0 || src.Height == 0) return box;
        float k = Math.Max((float)box.Width / src.Width, (float)box.Height / src.Height);
        int w = (int)(src.Width * k), h = (int)(src.Height * k);
        return new Rectangle(box.X + (box.Width - w) / 2, box.Bottom - h, w, h);
    }

    private static Rectangle FitInto(Size src, Rectangle box)
    {
        if (src.Width == 0 || src.Height == 0) return box;
        float k = Math.Min((float)box.Width / src.Width, (float)box.Height / src.Height);
        int w = (int)(src.Width * k), h = (int)(src.Height * k);
        return new Rectangle(box.X + (box.Width - w) / 2, box.Bottom - h, w, h);
    }
}
