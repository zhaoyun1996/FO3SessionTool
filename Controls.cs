using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FO3SessionTool;

/// <summary>Flat button painted by hand so the caption is exactly centred and every state uses the palette.</summary>
public sealed class DarkButton : Button
{
    private bool _hover, _down;

    public DarkButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = Theme.ButtonH;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        bool primary = Tag as string == "primary";
        Color bg, border, fg;
        if (!Enabled) { bg = Theme.Surface; border = Theme.Border; fg = Theme.Dim; }
        else if (primary)
        {
            bg = _down ? Color.FromArgb(50, 115, 225) : _hover ? Color.FromArgb(95, 160, 255) : Theme.Accent;
            border = bg; fg = Color.White;
        }
        else { bg = _down ? Theme.Border : _hover ? Theme.Hover : Theme.Surface2; border = Theme.Border; fg = Theme.Text; }

        g.Clear(Parent?.BackColor is { A: 255 } c ? c : Theme.Surface);
        using (var br = new SolidBrush(bg)) g.FillRectangle(br, 0, 0, Width, Height);
        using (var pen = new Pen(border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(g, Text, primary ? Theme.Bold : Theme.Body, new Rectangle(0, 0, Width, Height), fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

/// <summary>Single-line text input: borderless TextBox centred vertically inside a painted frame.</summary>
public sealed class DarkTextBox : UserControl
{
    private readonly TextBox _tb = new() { BorderStyle = BorderStyle.None, BackColor = Theme.Surface2, ForeColor = Theme.Text, Font = Theme.Body };

    public DarkTextBox()
    {
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface2;
        Height = Theme.ButtonH;
        Controls.Add(_tb);
        _tb.KeyDown += (_, e) => OnKeyDown(e);
        _tb.TextChanged += (_, _) => { OnTextChanged(EventArgs.Empty); Invalidate(); };
        _tb.GotFocus += (_, _) => Invalidate();
        _tb.LostFocus += (_, _) => Invalidate();
        Layout_();
    }

    private void Layout_()
    {
        _tb.Location = new Point(8, (Height - _tb.Height) / 2);
        _tb.Width = Math.Max(10, Width - 16);
    }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Layout_(); }
    public override string Text { get => _tb.Text; set => _tb.Text = value; }
    private string _placeholder = "";
    [DefaultValue("")] public string PlaceholderText { get => _placeholder; set { _placeholder = value; Invalidate(); } }
    [DefaultValue(false)] public bool UseSystemPasswordChar { get => _tb.UseSystemPasswordChar; set => _tb.UseSystemPasswordChar = value; }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); _tb.Enabled = Enabled; _tb.ForeColor = Enabled ? Theme.Text : Theme.Dim; Invalidate(); }
    public new bool Focus() => _tb.Focus();

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(_tb.Focused ? Theme.Accent : Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        if (_tb.Text.Length == 0 && _placeholder.Length > 0)
            TextRenderer.DrawText(e.Graphics, _placeholder, Theme.Body, new Rectangle(10, 0, Width - 14, Height), Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Numeric input with its own dark spin buttons (the system ones are always white).</summary>
public sealed class DarkNumber : UserControl
{
    private readonly TextBox _tb = new() { BorderStyle = BorderStyle.None, BackColor = Theme.Surface2, ForeColor = Theme.Text, Font = Theme.Body };
    private decimal _value, _min, _max = 100, _inc = 1;
    private bool _thousands;
    private const int BtnW = 23;   // same as DarkCombo so the dividers line up

    public event EventHandler? ValueChanged;

    public DarkNumber()
    {
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface2;
        Height = Theme.ButtonH;
        Controls.Add(_tb);
        _tb.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Up) { Step(1); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down) { Step(-1); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
        };
        _tb.Leave += (_, _) => { Commit(); Invalidate(); };
        _tb.GotFocus += (_, _) => Invalidate();
        _tb.MouseWheel += (_, e) => { Step(e.Delta > 0 ? 1 : -1); ((HandledMouseEventArgs)e).Handled = true; };
        Show_();
        Layout_();
    }

    private void Layout_()
    {
        _tb.Location = new Point(8, (Height - _tb.Height) / 2);
        _tb.Width = Math.Max(10, Width - BtnW - 12);
    }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Layout_(); }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal Value { get => _value; set { var v = Math.Clamp(value, _min, _max); bool ch = v != _value; _value = v; Show_(); if (ch) ValueChanged?.Invoke(this, EventArgs.Empty); } }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal Minimum { get => _min; set { _min = value; if (_value < _min) Value = _min; } }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal Maximum { get => _max; set { _max = value; if (_value > _max) Value = _max; } }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal Increment { get => _inc; set => _inc = value; }
    [DefaultValue(false)] public bool ThousandsSeparator { get => _thousands; set { _thousands = value; Show_(); } }

    private void Show_() => _tb.Text = _thousands ? _value.ToString("N0") : _value.ToString("0");
    private void Step(int dir) { Commit(); Value = _value + dir * _inc; }

    private void Commit()
    {
        var txt = new string(_tb.Text.Where(c => char.IsDigit(c) || c == '-').ToArray());
        if (decimal.TryParse(txt, out var v)) Value = v;
        Show_();
    }

    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); _tb.Enabled = Enabled; _tb.ForeColor = Enabled ? Theme.Text : Theme.Dim; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.X < Width - BtnW) return;
        Step(e.Y < Height / 2 ? 1 : -1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var col = Enabled ? Theme.Dim : Theme.Border;
        using (var br = new SolidBrush(Theme.Surface)) g.FillRectangle(br, Width - BtnW, 1, BtnW - 1, Height - 2);
        using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, Width - BtnW, 1, Width - BtnW, Height - 2);
        int cx = Width - BtnW / 2 - 1;
        using (var br = new SolidBrush(col))
        {
            g.FillPolygon(br, new[] { new Point(cx - 4, Height / 2 - 3), new Point(cx + 4, Height / 2 - 3), new Point(cx, Height / 2 - 7) });
            g.FillPolygon(br, new[] { new Point(cx - 4, Height / 2 + 3), new Point(cx + 4, Height / 2 + 3), new Point(cx, Height / 2 + 7) });
        }
        using var border = new Pen(_tb.Focused ? Theme.Accent : Theme.Border);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>Drop-down list drawn in the palette: owner-drawn items plus a repainted arrow button.</summary>
public sealed class DarkCombo : ComboBox
{
    private const int WM_PAINT = 0x000F, WM_PRINTCLIENT = 0x0318;
    /// <summary>true when the owner supplies its own DrawItem handler (e.g. the player list).</summary>
    [DefaultValue(false)] public bool CustomItems { get; set; }

    public DarkCombo()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.Surface2;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        ItemHeight = Theme.ButtonH - 6;
        IntegralHeight = false;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (CustomItems) { base.OnDrawItem(e); return; }
        if (e.Index < 0) return;
        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool sel = !edit && (e.State & DrawItemState.Selected) != 0;
        using (var br = new SolidBrush(sel ? Theme.Hover : Theme.Surface2)) e.Graphics.FillRectangle(br, e.Bounds);
        var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Theme.Body, r, Enabled ? Theme.Text : Theme.Dim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WM_PAINT && m.Msg != WM_PRINTCLIENT) return;
        using var g = m.Msg == WM_PRINTCLIENT ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var btn = new Rectangle(Width - 23, 0, 23, Height);   // covers the native button edge to edge
        using (var br = new SolidBrush(Theme.Surface)) g.FillRectangle(br, btn);
        using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, btn.Left, 1, btn.Left, Height - 2);
        int cx = btn.X + btn.Width / 2, cy = Height / 2;
        using (var br = new SolidBrush(Enabled ? Theme.Dim : Theme.Border))
            g.FillPolygon(br, new[] { new Point(cx - 4, cy - 2), new Point(cx + 4, cy - 2), new Point(cx, cy + 3) });
        // the native frame turns white when the control is disabled: repaint a 3 px ring in the body colour first
        using (var ring = new Pen(Theme.Surface2))
            for (int i = 1; i <= 2; i++) g.DrawRectangle(ring, i, i, Width - 1 - 2 * i, Height - 1 - 2 * i);
        using var border = new Pen(Focused && Enabled ? Theme.Accent : Theme.Border);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}
