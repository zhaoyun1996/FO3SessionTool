using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FO3SessionTool;

/// <summary>The single design standard for the whole tool: one palette, one font family, one size scale, one metric set.</summary>
public static class Theme
{
    // ---- palette ---------------------------------------------------------
    public static readonly Color Bg = Color.FromArgb(17, 21, 27);          // window
    public static readonly Color Surface = Color.FromArgb(24, 30, 38);     // cards / groups
    public static readonly Color Surface2 = Color.FromArgb(33, 41, 52);    // inputs, buttons
    public static readonly Color Hover = Color.FromArgb(46, 57, 71);
    public static readonly Color Border = Color.FromArgb(56, 67, 82);
    public static readonly Color Text = Color.FromArgb(224, 230, 237);
    public static readonly Color Dim = Color.FromArgb(140, 151, 164);
    public static readonly Color Accent = Color.FromArgb(70, 140, 255);
    public static readonly Color Gold = Color.FromArgb(224, 170, 32);
    public static readonly Color Success = Color.FromArgb(52, 178, 90);
    public static readonly Color Warn = Color.FromArgb(236, 176, 42);
    public static readonly Color Danger = Color.FromArgb(240, 84, 80);

    // ---- type scale (one family; sizes: 8.5 / 9.5 / 11 / 14) -------------
    public const string Family = "Segoe UI";
    public static readonly Font Body = new(Family, 9.5f);
    public static readonly Font Bold = new(Family, 9.5f, FontStyle.Bold);
    public static readonly Font Small = new(Family, 8.5f);
    public static readonly Font SmallBold = new(Family, 8.5f, FontStyle.Bold);
    public static readonly Font Large = new(Family, 11f, FontStyle.Bold);
    public static readonly Font Heading = new(Family, 14f, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9.5f);

    // ---- metrics ---------------------------------------------------------
    public const int Gap = 10;          // spacing between blocks
    public const int RowH = 32;         // one form row
    public const int ButtonH = 28;      // every button
    public static readonly Padding CellMargin = new(3, 3, 3, 3);

    // ---- native dark hooks -----------------------------------------------
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    public static void DarkTitleBar(Form f)
    {
        void Set() { int on = 1; DwmSetWindowAttribute(f.Handle, 20, ref on, 4); }
        if (f.IsHandleCreated) Set(); else f.HandleCreated += (_, _) => Set();
    }

    private static void NativeTheme(Control c, string name)
    {
        void Set() { try { SetWindowTheme(c.Handle, name, null); } catch { } }
        if (c.IsHandleCreated) Set(); else c.HandleCreated += (_, _) => Set();
    }

    // ---- apply to a control tree -----------------------------------------
    public static void Apply(Control c)
    {
        switch (c)
        {
            case Form f:
                f.BackColor = Bg; f.ForeColor = Text; f.Font = Body; DarkTitleBar(f);
                break;
            case DarkTabControl or DarkGroup or DarkProgress or DarkRadio or PlayerCard or DarkButton or DarkTextBox or DarkNumber or DarkCombo:
                break;   // paint themselves
            case TabPage p:
                p.BackColor = Bg; p.ForeColor = Text; p.UseVisualStyleBackColor = false;
                break;
            case Button b:
                StyleButton(b, b.Tag as string == "primary");
                break;
            case TextBox t:
                t.BackColor = Surface2; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle;
                NativeTheme(t, "DarkMode_Explorer");
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat; cb.BackColor = Surface2; cb.ForeColor = Text;
                NativeTheme(cb, "DarkMode_CFD");
                break;
            case NumericUpDown n:
                n.BackColor = Surface2; n.ForeColor = Text; n.BorderStyle = BorderStyle.FixedSingle;
                NativeTheme(n, "DarkMode_Explorer");
                break;
            case DataGridView g:
                NativeTheme(g, "DarkMode_Explorer");
                foreach (Control sb in g.Controls) if (sb is ScrollBar) NativeTheme(sb, "DarkMode_Explorer");
                g.ControlAdded += (_, e) => { if (e.Control is ScrollBar) NativeTheme(e.Control, "DarkMode_Explorer"); };
                break;
            case Label l:
                if (l.BackColor == SystemColors.Control) l.BackColor = Color.Transparent;
                if (l.ForeColor == SystemColors.ControlText) l.ForeColor = Text;
                break;
            case TableLayoutPanel or FlowLayoutPanel or Panel:
                c.BackColor = Color.Transparent;
                break;
            case PictureBox:
                break;
            default:
                break;
        }
        if (c is NumericUpDown or ComboBox or TextBox or DataGridView or Button or PlayerCard or DarkTextBox or DarkNumber or DarkCombo) return;   // internals are not ours to restyle
        foreach (Control child in c.Controls) Apply(child);
    }

    public static void StyleButton(Button b, bool primary = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.Height = ButtonH;
        b.Font = primary ? Large : Body;
        b.ForeColor = Text;
        b.BackColor = primary ? Accent : Surface2;
        b.FlatAppearance.BorderColor = primary ? Accent : Border;
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(95, 160, 255) : Hover;
        b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(50, 115, 225) : Border;
        b.Cursor = Cursors.Hand;
        b.EnabledChanged += (_, _) =>
        {
            b.ForeColor = b.Enabled ? Text : Dim;
            b.BackColor = b.Enabled ? (primary ? Accent : Surface2) : Surface;
            b.Cursor = b.Enabled ? Cursors.Hand : Cursors.Default;
        };
    }

    public static void StyleGrid(DataGridView g)
    {
        g.BackgroundColor = Surface; g.GridColor = Surface; g.BorderStyle = BorderStyle.None;
        g.EnableHeadersVisualStyles = false;
        g.CellBorderStyle = DataGridViewCellBorderStyle.None;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface2, ForeColor = Gold, SelectionBackColor = Surface2, SelectionForeColor = Gold, Font = SmallBold,
            Alignment = DataGridViewContentAlignment.MiddleCenter, WrapMode = DataGridViewTriState.True,
        };
        g.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface, ForeColor = Text, SelectionBackColor = Hover, SelectionForeColor = Text, Font = Body,
            Alignment = DataGridViewContentAlignment.MiddleCenter,
        };
        g.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(28, 35, 44) };
        g.RowHeadersVisible = false;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
    }

    public static Color PosColor(string group) => group switch
    {
        "att" => Color.FromArgb(214, 72, 60),
        "mid" => Color.FromArgb(62, 160, 80),
        "def" => Color.FromArgb(50, 120, 220),
        _ => Gold,
    };

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Card-like group with a title, drawn in the palette (replaces the light system GroupBox).</summary>
public sealed class DarkGroup : GroupBox
{
    public DarkGroup()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Bold;
        Padding = new Padding(8, 2, 8, 6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        var rect = new Rectangle(0, 9, Width - 1, Height - 10);
        using (var path = Theme.Round(rect, 6))
        using (var fill = new SolidBrush(Theme.Surface))
        using (var pen = new Pen(Theme.Border))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        var size = TextRenderer.MeasureText(g, Text, Theme.Bold, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding);
        var label = new Rectangle(10, 0, size.Width + 8, 18);
        using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, label);
        TextRenderer.DrawText(g, Text, Theme.Bold, new Point(14, 1), Theme.Accent, TextFormatFlags.NoPadding);
    }
}

/// <summary>Tab control with fully custom painting: dark strip, accent underline on the active tab.</summary>
public sealed class DarkTabControl : TabControl
{
    public DarkTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(170, 34);
        Padding = new Point(0, 0);
        Font = Theme.Body;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        for (int i = 0; i < TabCount; i++)
        {
            var r = GetTabRect(i);
            bool sel = i == SelectedIndex;
            using (var bg = new SolidBrush(sel ? Theme.Surface : Theme.Bg)) g.FillRectangle(bg, r);
            TextRenderer.DrawText(g, TabPages[i].Text, sel ? Theme.Bold : Theme.Body, r, sel ? Theme.Text : Theme.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (sel) using (var pen = new Pen(Theme.Accent, 2)) g.DrawLine(pen, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
        }
        using (var line = new Pen(Theme.Border)) g.DrawLine(line, 0, ItemSize.Height, Width, ItemSize.Height);
    }
}

/// <summary>Thin progress bar in the palette.</summary>
public sealed class DarkProgress : Control
{
    private int _value, _max = 100;

    public DarkProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 14;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Maximum { get => _max; set { _max = Math.Max(1, value); Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value { get => _value; set { _value = Math.Clamp(value, 0, _max); Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface2);
        int w = (int)((Width - 1) * (double)_value / _max);
        if (w > 0) using (var br = new SolidBrush(Theme.Accent)) g.FillRectangle(br, 0, 0, w, Height);
        using var pen = new Pen(Theme.Border);
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>Radio button drawn in the palette (the system glyph is always light).</summary>
public sealed class DarkRadio : RadioButton
{
    public DarkRadio()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = false;
        Cursor = Cursors.Hand;
        Font = Theme.Body;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding);
        return new Size(t.Width + 30, 24);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Size = GetPreferredSize(Size.Empty); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor == Color.Transparent || Parent == null ? Theme.Surface : Parent.BackColor);
        var ring = new Rectangle(1, (Height - 16) / 2, 16, 16);
        using (var pen = new Pen(Checked ? Theme.Accent : Theme.Dim, 1.5f)) g.DrawEllipse(pen, ring);
        if (Checked) using (var br = new SolidBrush(Theme.Accent)) g.FillEllipse(br, ring.X + 4, ring.Y + 4, 8, 8);
        TextRenderer.DrawText(g, Text, Font, new Point(24, (Height - Font.Height) / 2), Enabled ? Theme.Text : Theme.Dim, TextFormatFlags.NoPadding);
    }
}
