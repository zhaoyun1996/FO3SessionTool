using System.Drawing.Drawing2D;

namespace FO3SessionTool;

/// <summary>Manual mode: the server's offers shown as cards like the game's trade screen; click one, then confirm.</summary>
public sealed class TradeDialog : Form
{
    private readonly List<GameApi.TradeOption> _options;
    private readonly List<OfferCard> _cards = new();
    private readonly PlayerSearch _players;
    private int _selected;

    public GameApi.TradeOption? Picked { get; private set; }

    public TradeDialog(List<GameApi.OwnedCard> cards, List<GameApi.TradeOption> options, int bestIndex, PlayerSearch players, Func<Task<Bitmap?>> sprite)
    {
        _options = options; _players = players; _selected = bestIndex;
        Text = "Giao dịch - chọn cầu thủ nhận về";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Body;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        const int cw = OfferCard.W, gap = 14, pad = 16;
        int n = options.Count;
        ClientSize = new Size(Math.Max(520, pad * 2 + n * cw + (n - 1) * gap), 44 + OfferCard.H + 70);

        var info = new Label
        {
            Left = pad, Top = 12, Width = ClientSize.Width - pad * 2, Height = 22, AutoSize = false,
            Text = $"Đổi {cards.Count} thẻ (tổng giá trị {cards.Sum(c => c.Value):N0} EP). Chọn một trong các phương án:"
        };
        Controls.Add(info);

        int x = (ClientSize.Width - (n * cw + (n - 1) * gap)) / 2;
        foreach (var o in options)
        {
            var c = new OfferCard(o, o.Index == bestIndex) { Left = x, Top = 44, Selected = o.Index == _selected };
            int idx = o.Index;
            c.Click += (_, _) => Select(idx);
            _cards.Add(c);
            Controls.Add(c);
            x += cw + gap;
        }

        var ok = new DarkButton { Text = "Xác nhận trao đổi", Width = 180, Tag = "primary", Left = (ClientSize.Width - 180) / 2, Top = 44 + OfferCard.H + 16 };
        ok.Click += (_, _) => { Picked = _options[_selected]; DialogResult = DialogResult.OK; };
        Controls.Add(ok);
        AcceptButton = ok;
        ControlBox = false;   // no X: the trade cannot be abandoned half-way, it must be confirmed
        FormClosing += (_, e) => { if (DialogResult != DialogResult.OK) e.Cancel = true; };
        Theme.Apply(this);
        BackColor = Theme.Bg;

        // everything is requested at once (portraits from the game CDN, details from fifaaddict, the season sprite) and each card repaints as soon as
        // a piece arrives, so the popup is usable immediately instead of waiting for the slowest download
        var spriteTask = Task.Run(async () => { try { return await sprite(); } catch { return null; } });
        var loads = _cards.Select(c => c.LoadAsync(_players)).ToList();
        loads.Add(Task.Run(async () =>
        {
            var sp = await spriteTask;
            if (sp != null) BeginInvokeSafe(() => { foreach (var c in _cards) c.SetSprite(sp); });
        }));
        Loaded = Task.WhenAll(loads);
    }

    /// <summary>Completes when every card has all its pieces (or gave up). The dialog does not wait for it.</summary>
    public Task Loaded { get; private set; } = Task.CompletedTask;

    private void BeginInvokeSafe(Action a)
    {
        try { if (!IsDisposed) { if (IsHandleCreated) BeginInvoke(a); else a(); } } catch { }
    }

    private void Select(int i)
    {
        _selected = i;
        for (int k = 0; k < _cards.Count; k++) _cards[k].Selected = k == i;
    }

    /// <summary>One offered player: club header, portrait, name, positions, feet, body, price (Giá TTCN) and the extra EP.</summary>
    private sealed class OfferCard : Control
    {
        public const int W = 232, H = 404;
        private readonly GameApi.TradeOption _o;
        private readonly bool _best;
        private PlayerDetail? _d;
        private Image? _portrait;
        private Bitmap? _sprite;
        private string _status = "Đang tải...";
        private bool _selected;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

        public OfferCard(GameApi.TradeOption o, bool best)
        {
            _o = o; _best = best;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(W, H);
            Cursor = Cursors.Hand;
            Font = Theme.Body;
        }

        public void SetSprite(Bitmap? sprite) { _sprite = sprite; if (!IsDisposed) Invalidate(); }

        /// <summary>Portrait and details load side by side; the card repaints after each (the portrait usually comes first).</summary>
        public async Task LoadAsync(PlayerSearch players)
        {
            var det = players.GetDetailCachedAsync(_o.Spid);
            var img = players.LoadImageCachedAsync($"http://cdn.3sea.online/cdn/externalAssets/players/p{_o.Spid}.png");
            var portraitTask = Task.Run(async () =>
            {
                try { _portrait = await img; } catch { }
                if (!IsDisposed) Invalidate();
            });
            try
            {
                _d = await det;
                _status = _d == null ? "Không đọc được thông tin" : "";
            }
            catch { _status = "Không đọc được thông tin"; }
            if (!IsDisposed) Invalidate();
            await portraitTask;
            if (_portrait == null && _d != null && _d.ImageUrl.Length > 0)
            {
                try { _portrait = await players.LoadImageCachedAsync(_d.ImageUrl); } catch { }
                if (!IsDisposed) Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.Bg);
            var body = new Rectangle(1, 1, W - 3, H - 3);
            using (var path = Theme.Round(body, 10))
            {
                using (var br = new SolidBrush(Theme.Surface)) g.FillPath(br, path);
                using var pen = new Pen(_selected ? Theme.Gold : Theme.Border, _selected ? 2.5f : 1f);
                g.DrawPath(pen, path);
            }
            const TextFormatFlags C = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

            // header: club name
            var head = new Rectangle(14, 8, W - 28, 28);
            using (var br = new SolidBrush(Theme.Surface2)) g.FillRectangle(br, head);
            using (var pen = new Pen(_selected ? Theme.Gold : Color.FromArgb(120, 100, 70))) g.DrawLine(pen, head.X, head.Y, head.Right, head.Y);
            TextRenderer.DrawText(g, _d?.Club ?? "", Theme.Body, head, Theme.Text, C);
            var pr = PaintPortrait(g, C);
            if (_d == null)
            {
                TextRenderer.DrawText(g, _status, Theme.Body, new Rectangle(0, pr.Bottom + 10, W, 30), Theme.Dim, C);
                PaintPrice(g, C);
                return;
            }
            PaintRest(g, C, F, pr);
        }

        /// <summary>Portrait frame, "largest" tag and grade badge: shown as soon as the image is there, before the details arrive.</summary>
        private Rectangle PaintPortrait(Graphics g, TextFormatFlags C)
        {
            var pr = new Rectangle((W - 150) / 2, 46, 150, 120);
            using (var bg = new LinearGradientBrush(pr, Color.FromArgb(58, 52, 40), Color.FromArgb(18, 20, 26), 60f)) g.FillRectangle(bg, pr);
            if (_portrait != null)
            {
                var st = g.Save();
                g.SetClip(pr);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float k = Math.Min((float)pr.Width / _portrait.Width, (float)pr.Height / _portrait.Height);
                int w = (int)(_portrait.Width * k), h = (int)(_portrait.Height * k);
                g.DrawImage(_portrait, new Rectangle(pr.X + (pr.Width - w) / 2, pr.Bottom - h, w, h));
                g.Restore(st);
            }
            using (var pen = new Pen(Color.FromArgb(120, 100, 70))) g.DrawRectangle(pen, pr);
            if (_best)
            {
                var tag = new Rectangle(pr.X + 2, pr.Y + 2, 70, 16);
                using (var tb = new SolidBrush(Color.FromArgb(210, 20, 22, 28))) g.FillRectangle(tb, tag);
                TextRenderer.DrawText(g, "★ Lớn nhất", Theme.SmallBold, tag, Theme.Gold, C);
            }
            var badge = new Rectangle(pr.Right - 30, pr.Bottom - 20, 28, 18);
            using (var bb = new SolidBrush(Color.FromArgb(200, 20, 22, 28))) g.FillRectangle(bb, badge);
            TextRenderer.DrawText(g, "+" + _o.Grade, Theme.SmallBold, badge, Theme.Text, C);
            return pr;
        }

        private void PaintRest(Graphics g, TextFormatFlags C, TextFormatFlags F, Rectangle pr)
        {

            // season icon + name, centered
            int nameW = TextRenderer.MeasureText(g, _d.Name, Theme.Bold, new Size(int.MaxValue, 0), F).Width;
            bool icon = _sprite != null && PlayerSearch.SeasonSpriteX.ContainsKey(_d.Season);
            int total = nameW + (icon ? 22 : 0);
            if (total > W - 24) { nameW = W - 24 - (icon ? 22 : 0); total = W - 24; }
            int nx = (W - total) / 2;
            if (icon)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_sprite!, new Rectangle(nx, 172, 18, 18), new Rectangle(PlayerSearch.SeasonSpriteX[_d.Season], 0, 18, 18), GraphicsUnit.Pixel);
                nx += 22;
            }
            TextRenderer.DrawText(g, _d.Name, Theme.Bold, new Rectangle(nx, 172, nameW, 20), Theme.Text, F | TextFormatFlags.EndEllipsis);

            // positions
            int py = 202, row = 0;
            foreach (var pos in _d.Positions.OrderByDescending(p => p.Active).ThenByDescending(p => p.Rating).Take(3))
            {
                int cy = py + row++ * 19;
                using (var br = new SolidBrush(Theme.PosColor(pos.Group))) g.FillRectangle(br, new Rectangle(22, cy + 2, 4, 14));
                TextRenderer.DrawText(g, pos.Pos, Theme.Bold, new Point(34, cy), Theme.Text, F);
                TextRenderer.DrawText(g, pos.Rating.ToString(), Theme.Bold, new Rectangle(W - 22 - 40, cy, 40, 18), pos.Active ? Theme.Accent : Theme.Dim, F | TextFormatFlags.Right);
            }

            // feet (left), birth date + body (right)
            int fy = 268;
            PlayerCard.Foot(g, 20, fy, _d.LeftFoot, !_d.PrefersRight, -12);
            PlayerCard.Foot(g, 48, fy, _d.RightFoot, _d.PrefersRight, 12);
            var r = new Rectangle(96, fy + 8, W - 96 - 20, 18);
            TextRenderer.DrawText(g, _d.BirthDate, Theme.Body, r, Theme.Text, F | TextFormatFlags.Right);
            r.Y += 20;
            TextRenderer.DrawText(g, $"{_d.Height}cm / {_d.Weight}Kg", Theme.Body, r, Theme.Text, F | TextFormatFlags.Right);

            PaintPrice(g, C);
        }

        private void PaintPrice(Graphics g, TextFormatFlags C)
        {
            var box = new Rectangle(14, 322, W - 28, 74);
            using (var br = new SolidBrush(Color.FromArgb(_selected ? 60 : 36, 120, 140, 170))) g.FillRectangle(br, box);
            TextRenderer.DrawText(g, "Giá TTCN", Theme.Body, new Rectangle(box.X, box.Y + 4, box.Width, 18), Theme.Dim, C);
            TextRenderer.DrawText(g, $"{_o.Price:N0} EP", Theme.Large, new Rectangle(box.X, box.Y + 20, box.Width, 22), Theme.Gold, C);
            if (_o.Ep > 0)
            {
                TextRenderer.DrawText(g, "Nhận thêm", Theme.Body, new Rectangle(box.X, box.Y + 44, box.Width, 14), Theme.Dim, C);
                TextRenderer.DrawText(g, $"+{_o.Ep:N0} EP", Theme.Large, new Rectangle(box.X, box.Y + 54, box.Width, 20), Theme.Success, C);
            }
        }
    }
}
