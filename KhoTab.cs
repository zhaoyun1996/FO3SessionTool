namespace FO3SessionTool;

/// <summary>
/// The storage, limited to the cards that are not registered in the squad (neither starting XI nor substitutes).
/// They can be ticked for a trade. Data comes from /coach/me (the game itself).
/// </summary>
public sealed class KhoTab : UserControl
{

    private readonly DataGridView _grid = new();
    private readonly DarkButton _btnReload = new() { Text = "Tải lại kho", Width = 120, Anchor = AnchorStyles.Left };
    private readonly DarkButton _btnClean = new() { Text = "Dọn thẻ +0", Width = 150, Enabled = false, Anchor = AnchorStyles.Left };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Theme.Dim, Anchor = AnchorStyles.Left, Margin = new Padding(Theme.Gap, 0, 0, 0) };
    private readonly DarkCheck _showAll = new() { Text = "Hiển thị cả cầu thủ đội hình chính / dự bị", Margin = new Padding(Theme.Gap, 5, 0, 0) };
    private readonly Label _pickInfo = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = Theme.Bold };
    private readonly DarkRadio _manual = new() { Text = "Tự chọn kết quả", Checked = true, Margin = new Padding(3, 5, 14, 5) };
    private readonly DarkRadio _auto = new() { Text = "Tự động: lấy phương án có giá trị lớn hơn (tiền tươi + cầu thủ)", Margin = new Padding(3, 5, 3, 5) };
    private readonly DarkButton _btnTrade = new() { Text = "Giao dịch", Width = 140, Tag = "primary", Enabled = false, Anchor = AnchorStyles.Right };
    private readonly Label _tradeNote = new() { AutoSize = true, ForeColor = Theme.Dim, Anchor = AnchorStyles.Left };

    private readonly Func<Task<List<GameApi.OwnedCard>>> _load;
    private readonly Func<Task<Bitmap?>> _sprite;
    private readonly Func<long, Task<PlayerDetail?>> _detail;
    private readonly Dictionary<long, string> _fullNames = new();
    private int _nameVer;
    private readonly Action<string> _log;
    private readonly HashSet<string> _picked = new();
    private List<GameApi.OwnedCard> _cards = new();
    private Bitmap? _icons;
    private bool _loggedIn, _busy;

    /// <summary>Raised with the ticked cards when the user presses Giao dịch.</summary>
    public event Action<List<GameApi.OwnedCard>, bool>? TradeRequested;   // (cards, automatic)

    /// <summary>Raised with the unregistered +0 cards when the user presses Dọn thẻ +0 (the owner confirms and deletes them).</summary>
    public event Action<List<GameApi.OwnedCard>>? CleanRequested;

    /// <summary>Cards that are +0 and not registered in the squad: the only ones the clean-up may delete.</summary>
    private List<GameApi.OwnedCard> ZeroCards() => _cards.Where(c => c.Grade == 0 && c.Group == GameApi.GroupNone).ToList();

    public KhoTab(Func<Task<List<GameApi.OwnedCard>>> load, Func<Task<Bitmap?>> sprite, Func<long, Task<PlayerDetail?>> detail, Action<string> log)
    {
        _load = load; _sprite = sprite; _detail = detail; _log = log;
        Dock = DockStyle.Fill;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowH + Theme.Gap));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, Theme.Gap) };
        _btnReload.Margin = new Padding(0, 2, 0, 0);
        _status.Margin = new Padding(Theme.Gap, 8, 0, 0);
        _btnClean.Margin = new Padding(Theme.Gap, 2, 0, 0);
        top.Controls.Add(_btnReload);
        top.Controls.Add(_btnClean);
        top.Controls.Add(_showAll);
        top.Controls.Add(_status);
        root.Controls.Add(top, 0, 0);
        root.SetColumnSpan(top, 2);

        StyleGrid();
        var listBox = new DarkGroup { Text = "Cầu thủ chưa đăng ký trong kho", Dock = DockStyle.Fill, Padding = new Padding(8, 2, 8, 8), Margin = new Padding(0, 0, Theme.Gap, 0) };
        _grid.Margin = Padding.Empty;
        listBox.Controls.Add(_grid);
        root.Controls.Add(listBox, 0, 1);

        var trade = new DarkGroup { Text = "Giao dịch cầu thủ chưa đăng ký", Dock = DockStyle.Fill, Margin = Padding.Empty };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(6, 2, 6, 2) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowH));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowH));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.ButtonH + Theme.Gap));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _pickInfo.AutoSize = false; _pickInfo.Dock = DockStyle.Fill; _pickInfo.Margin = new Padding(3, 4, 3, 0);
        t.Controls.Add(_pickInfo, 0, 0);
        _manual.Anchor = AnchorStyles.Left; _auto.Anchor = AnchorStyles.Left | AnchorStyles.Right; _auto.AutoSize = false; _auto.Height = 28;
        _auto.Text = "Tự động: chọn phương án lớn nhất";
        t.Controls.Add(_manual, 0, 1);
        t.Controls.Add(_auto, 0, 2);
        _btnTrade.Anchor = AnchorStyles.Left; _btnTrade.Margin = new Padding(3, 0, 0, 0);
        t.Controls.Add(_btnTrade, 0, 3);
        _tradeNote.AutoSize = false; _tradeNote.Dock = DockStyle.Fill; _tradeNote.Margin = new Padding(3, 0, 3, 0);
        t.Controls.Add(_tradeNote, 0, 4);
        trade.Controls.Add(t);
        root.Controls.Add(trade, 1, 1);
        Controls.Add(root);
        Theme.Apply(this);

        _btnReload.Click += async (_, _) => await ReloadAsync();
        _btnTrade.Click += (_, _) => OnTrade();
        _btnClean.Click += (_, _) => { var z = ZeroCards(); if (z.Count > 0) CleanRequested?.Invoke(z); };
        _showAll.CheckedChanged += (_, _) => { if (_cards.Count > 0) Fill(); };
        _grid.CellClick += (_, e) => { if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == "pick") TogglePick(e.RowIndex); };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) TogglePick(e.RowIndex); };
        _grid.CellPainting += PaintCell;
        UpdateTradeUi();
    }

    public void SetLoggedIn(bool value)
    {
        _loggedIn = value;
        if (!value) { _cards = new(); _picked.Clear(); _grid.Rows.Clear(); _status.Text = "Hãy đăng nhập để xem kho."; }
        UpdateTradeUi();
    }

    private void StyleGrid()
    {
        var g = _grid;
        g.Dock = DockStyle.Fill;
        g.ReadOnly = true; g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false; g.AllowUserToResizeRows = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect; g.MultiSelect = false;
        Theme.StyleGrid(g);
        g.ColumnHeadersHeight = 32;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Text;
        g.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        g.RowTemplate.Height = 30;

        void Col(string name, string header, int width, DataGridViewContentAlignment al = DataGridViewContentAlignment.MiddleCenter, bool fill = false)
            => g.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable,
                AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
                MinimumWidth = fill ? 160 : 20,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = al },
            });
        Col("pick", "Chọn", 56);
        Col("name", "Cầu thủ", 200, DataGridViewContentAlignment.MiddleLeft, true);
        Col("season", "Mùa", 60);
        Col("grade", "Mức", 60);
        Col("spid", "SPID", 90);
        Col("value", "Giá trị (EP)", 130, DataGridViewContentAlignment.MiddleRight);

        g.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= g.Rows.Count || g.Rows[e.RowIndex].Tag is not GameApi.OwnedCard c) return;
            var name = g.Columns[e.ColumnIndex].Name;
            if (name == "name") { e.CellStyle!.Font = Theme.Bold; }
            else if (name == "grade") { e.CellStyle!.ForeColor = Theme.Accent; e.CellStyle.Font = Theme.Bold; }
            else if (name == "value") { e.CellStyle!.ForeColor = Theme.Gold; }
        };
    }

    // tick box in the "pick" column; season badge in the "season" column
    private void PaintCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Rows[e.RowIndex].Tag is not GameApi.OwnedCard c) return;
        var col = _grid.Columns[e.ColumnIndex].Name;
        if (col != "pick" && col != "season") return;

        e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
        var g = e.Graphics!;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var b = e.CellBounds;
        if (col == "pick")
        {
            if (c.Group == GameApi.GroupNone)
            {
                var box = new Rectangle(b.X + (b.Width - 16) / 2, b.Y + (b.Height - 16) / 2, 16, 16);
                bool on = _picked.Contains(c.Id);
                using (var br = new SolidBrush(on ? Theme.Accent : Theme.Surface2)) g.FillRectangle(br, box);
                using (var pen = new Pen(on ? Theme.Accent : Theme.Border)) g.DrawRectangle(pen, box);
                if (on)
                    using (var pen = new Pen(Color.White, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                        g.DrawLines(pen, new[] { new Point(box.X + 3, box.Y + 8), new Point(box.X + 7, box.Y + 12), new Point(box.X + 13, box.Y + 4) });
            }
            // cards of the main squad / bench cannot be traded: no tick box at all
        }
        else
        {
            int season = PlayerAssetsSeason(c.Spid);
            if (_icons != null && PlayerSearch.SeasonSpriteX.TryGetValue(season, out var sx))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(_icons, new Rectangle(b.X + (b.Width - 20) / 2, b.Y + (b.Height - 20) / 2, 20, 20), new Rectangle(sx, 0, 18, 18), GraphicsUnit.Pixel);
            }
            else TextRenderer.DrawText(g, $"S{season}", Theme.SmallBold, b, Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        e.Handled = true;
    }

    private static int PlayerAssetsSeason(long spid) => (int)(spid / 1_000_000);

    private void TogglePick(int row)
    {
        if (_grid.Rows[row].Tag is not GameApi.OwnedCard c || c.Group != GameApi.GroupNone) return;
        if (!_picked.Remove(c.Id))
        {
            if (_picked.Count >= TradeMaxCards)
            {
                _log($"[Giao dịch] Không chọn được: một lần giao dịch tối đa {TradeMaxCards} thẻ.");
                MessageBox.Show(FindForm(), $"Không thể chọn thẻ này: một lần giao dịch chỉ được chọn tối đa {TradeMaxCards} cầu thủ." +
                    Environment.NewLine + Environment.NewLine + "Hãy bỏ bớt thẻ đã chọn hoặc giao dịch theo nhiều lần.", $"Giao dịch quá {TradeMaxCards} thẻ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            long after = Picked.Sum(x => x.Value) + c.Value;
            if (after > TradeWarnValue)
            {
                // refuse the tick: the selection stays as it was
                _log($"[Giao dịch] Không chọn được: thêm thẻ này tổng giá trị sẽ là {after:N0} EP, vượt {TradeWarnValue:N0} EP.");
                MessageBox.Show(FindForm(), $"Không thể chọn thẻ này: tổng giá trị các thẻ đã chọn sẽ là {after:N0} EP, vượt giới hạn {TradeWarnValue:N0} EP (1 tỷ) của một lần giao dịch." +
                    Environment.NewLine + Environment.NewLine + "Hãy bỏ bớt thẻ đã chọn hoặc giao dịch theo nhiều lần.", "Giao dịch vượt 1 tỷ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _picked.Add(c.Id);
        }
        _grid.InvalidateRow(row);
        UpdateTradeUi();
    }

    public async Task ReloadAsync()
    {
        if (!_loggedIn || _busy) return;
        _busy = true;
        _btnReload.Enabled = false;
        _status.Text = "Đang đọc kho...";
        _log("[Kho] Đang đọc kho cầu thủ (/coach/me)...");
        try
        {
            _icons ??= await _sprite();
            _cards = await _load();
            Fill();
            int free = _cards.Count(c => c.Group == GameApi.GroupNone);
            _status.Text = $"{free} chưa đăng ký · kho {_cards.Count} thẻ ({_cards.Count - free} trong đội hình)";
            _log($"[Kho] Đã đọc kho: {_status.Text}");
        }
        catch (Exception ex) { _status.Text = "Lỗi: " + ex.Message; _log("[Kho] LỖI đọc kho: " + ex.Message); }
        finally { _busy = false; _btnReload.Enabled = true; UpdateTradeUi(); }
    }

    private void Fill()
    {
        _picked.RemoveWhere(id => _cards.All(c => c.Id != id || c.Group != GameApi.GroupNone));
        _grid.Rows.Clear();
        // unregistered cards first (tick box); with "show all" the main squad and bench follow, dimmed and without a tick box
        foreach (var c in Shown().OrderBy(c => c.Group == GameApi.GroupNone ? 0 : 1).ThenByDescending(c => c.Value).ThenBy(c => c.Name))
        {
            bool reg = c.Group != GameApi.GroupNone;
            int i = _grid.Rows.Add("", RowName(c), "", $"+{c.Grade}", c.Spid, c.Value.ToString("N0"));
            _grid.Rows[i].Tag = c;
            if (reg) _grid.Rows[i].DefaultCellStyle.ForeColor = Theme.Dim;
        }
        _ = ResolveNamesAsync();
    }

    private IEnumerable<GameApi.OwnedCard> Shown() => _showAll.Checked ? _cards : _cards.Where(c => c.Group == GameApi.GroupNone);

    private string RowName(GameApi.OwnedCard c) =>
        NameOf(c) + (c.Group == GameApi.GroupNone ? "" : c.Group == GameApi.GroupBench ? "  (dự bị)" : "  (chính)");

    private string NameOf(GameApi.OwnedCard c) =>
        _fullNames.TryGetValue(c.Spid, out var n) ? n : c.Name.Length > 0 ? c.Name : "(không tên)";

    /// <summary>The game only sends the short jersey name ("Ronaldo"); the full name ("Cristiano Ronaldo") comes from the player's detail page.</summary>
    private async Task ResolveNamesAsync()
    {
        int ver = ++_nameVer;
        var spids = Shown().Select(c => c.Spid).Distinct().Where(id => !_fullNames.ContainsKey(id)).ToList();
        if (spids.Count == 0) return;
        _log($"[Kho] Đang lấy tên đầy đủ của {spids.Count} cầu thủ...");
        int got = 0;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(spids.Select(async id =>
        {
            await gate.WaitAsync();
            try
            {
                if (ver != _nameVer) return;
                var d = await _detail(id);
                if (d == null || d.Name.Length == 0 || ver != _nameVer || IsDisposed) return;
                _fullNames[id] = d.Name;
                got++;
                foreach (DataGridViewRow r in _grid.Rows)
                    if (r.Tag is GameApi.OwnedCard c && c.Spid == id) r.Cells["name"].Value = RowName(c);
            }
            catch { }
            finally { gate.Release(); }
        }));
        if (ver == _nameVer) _log($"[Kho] Đã lấy tên đầy đủ {got}/{spids.Count} cầu thủ.");
    }

    private List<GameApi.OwnedCard> Picked => _cards.Where(c => _picked.Contains(c.Id)).ToList();

    /// <summary>Largest total value of the cards ticked for one trade (1 billion EP, trade3 staticvalues max_trade).</summary>
    public const long TradeWarnValue = 1_000_000_000;
    public const int TradeMaxCards = 10;

    private void UpdateTradeUi()
    {
        var p = Picked;
        _pickInfo.Text = p.Count == 0
            ? "Chưa chọn thẻ nào. Nhấp vào ô Chọn hoặc nhấp đôi vào dòng để chọn."
            : $"Đã chọn {p.Count}/{TradeMaxCards} thẻ, tổng giá trị {p.Sum(c => c.Value):N0} EP" + (p.Sum(c => c.Value) > TradeWarnValue ? " - VƯỢT 1 TỶ" : "");
        _pickInfo.ForeColor = p.Count == 0 ? Theme.Dim : p.Sum(c => c.Value) > TradeWarnValue ? Theme.Warn : Theme.Text;
        _btnTrade.Enabled = _loggedIn && p.Count > 0 && TradeAvailable && !_tradeBusy;
        int zero = ZeroCards().Count;
        _btnClean.Text = zero > 0 ? $"Dọn thẻ +0 ({zero})" : "Dọn thẻ +0";
        _btnClean.Enabled = _loggedIn && zero > 0 && !_tradeBusy && !_busy;
        _tradeNote.Text = TradeAvailable ? "Phương án lớn nhất = tiền tươi + giá cầu thủ nhận về." : "";
    }

    /// <summary>true once the trade API is wired in (set by the main form).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool TradeAvailable { get => _tradeAvail; set { _tradeAvail = value; UpdateTradeUi(); } }
    private bool _tradeAvail;

    private bool _tradeBusy;
    public void SetTradeBusy(bool busy) { _tradeBusy = busy; UpdateTradeUi(); }

    private void OnTrade()
    {
        var p = Picked;
        if (p.Count == 0) return;
        TradeRequested?.Invoke(p, _auto.Checked);
    }
}
