namespace FO3SessionTool;

/// <summary>
/// Market scan for the rotation (Xoay vòng): for every card of one season that has enough +1 cards for sale, what one trade batch costs
/// (the cheapest +1 listings, 16 per +5 card) against what the game values those +5 cards at (1.25 x the floor price of the +5 card).
/// The ratio is an upper bound on the profit: the offers of the trade and the fees of selling the received card come on top. Read only.
/// </summary>
public sealed class MarketScanDialog : Form
{
    private const long TradeMax = 1_000_000_000;   // the game's limit per trade

    private sealed record Row(long Spid, int Overall, long D1, long Cheapest, int OnSale, int Cards, long Cost, long Value)
    {
        public double Ratio => Cost > 0 ? (double)Value / Cost : 0;
        public long Gain => Value - Cost;
    }

    private readonly GameApi _api;
    private readonly PlayerSearch _players;
    private readonly DarkCombo _season = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly DarkCombo _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly DarkCombo _sort = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly DarkNumber _pool = new() { Minimum = 20, Maximum = 3000, Value = 400, Increment = 50, Width = 80 };
    private readonly DarkNumber _minGain = new() { Minimum = 0, Maximum = 100000, Value = 0, Increment = 10, Width = 80 };
    private readonly DarkButton _btnScan = new() { Text = "Quét", Width = 100, Tag = "primary" };
    private readonly DarkButton _btnPick = new() { Text = "Dùng cầu thủ này", Width = 170, Enabled = false };
    private readonly DataGridView _grid = new();
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(10, 6, 0, 0), ForeColor = Theme.Dim };
    private readonly List<int> _seasons;
    private CancellationTokenSource? _cts;
    private readonly List<(Row Row, string Name)> _rows = new();

    /// <summary>The chosen card (SPID) and its plain name for the search box, when the user pressed "Dùng cầu thủ này".</summary>
    public long PickedSpid { get; private set; }
    public string PickedName { get; private set; } = "";

    public MarketScanDialog(GameApi api, PlayerSearch players, IEnumerable<int> seasons, Func<int, string> seasonName, int defaultSeason)
    {
        _api = api; _players = players;
        _seasons = seasons.Distinct().OrderBy(s => s).ToList();
        Text = "Quét thị trường cho xoay vòng";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Body;
        ClientSize = new Size(1000, 600);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        _season.Items.Add("Tất cả mùa (theo độ mạnh)");
        foreach (var s in _seasons) _season.Items.Add($"Mùa {s} · {seasonName(s)}");
        _season.SelectedIndex = _seasons.IndexOf(defaultSeason) + 1;
        _level.Items.AddRange(new object[] { "+4", "+5", "+6" }); _level.SelectedIndex = 1;
        _sort.Items.AddRange(new object[] { "Xếp theo tỷ lệ", "Xếp theo chênh lệch" }); _sort.SelectedIndex = 1;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(8, 6, 8, 0), WrapContents = true };
        top.Controls.Add(new Label { Text = "Mùa thẻ", AutoSize = true, Margin = new Padding(3, 8, 6, 0) });
        top.Controls.Add(_season);
        top.Controls.Add(new Label { Text = "Mức", AutoSize = true, Margin = new Padding(10, 8, 4, 0) }); top.Controls.Add(_level);
        top.Controls.Add(new Label { Text = "Quét top", AutoSize = true, Margin = new Padding(10, 8, 4, 0) }); top.Controls.Add(_pool);
        top.Controls.Add(new Label { Text = "Chênh ≥ (triệu)", AutoSize = true, Margin = new Padding(10, 8, 4, 0) }); top.Controls.Add(_minGain);
        top.Controls.Add(_sort);
        _btnScan.Margin = new Padding(12, 2, 3, 3); _btnPick.Margin = new Padding(12, 2, 3, 3);
        top.Controls.Add(_btnScan); top.Controls.Add(_btnPick);

        var info = new Label
        {
            Dock = DockStyle.Top, Height = 62, Padding = new Padding(10, 6, 10, 0),
            Text = "Mỗi lần đổi mua các thẻ +1 rẻ nhất (2^(mức-1) thẻ +1 cho mỗi thẻ ở mức đã chọn, tối đa 1 tỷ giá trị đổi một lần). \"Giá trị đổi\" = 1,25 × giá sàn của thẻ ở mức đó (cách game tính). Chênh lệch = giá trị đổi − vốn. " +
                   "Tỷ lệ = giá trị đổi / vốn mua; chưa gồm phí ép, thẻ ép thất bại, phương án đổi nhận được và phí bán thẻ nhận về. Chợ chỉ cho thấy khoảng 100 thẻ +1 mỗi cầu thủ.",
        };
        Theme.StyleGrid(_grid);
        _grid.Dock = DockStyle.Fill; _grid.ReadOnly = true; _grid.AllowUserToAddRows = false; _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.MultiSelect = false;
        _grid.ColumnHeadersHeight = 30; _grid.RowTemplate.Height = 24;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text;
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Text;
        void Col(string name, string header, int w, bool fill = false, bool left = false)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, Width = w, SortMode = DataGridViewColumnSortMode.NotSortable,
                AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None, MinimumWidth = fill ? 180 : 20,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = left ? DataGridViewContentAlignment.MiddleLeft : DataGridViewContentAlignment.MiddleRight },
            });
        Col("name", "Cầu thủ", 200, true, true);
        Col("ovr", "Chỉ số", 60);
        Col("d1", "Giá mặc định +1", 120);
        Col("cheap", "+1 rẻ nhất", 110);
        Col("sale", "Số thẻ +1 bán", 100);
        Col("cards", "Thẻ/lần", 70);
        Col("cost", "Vốn mỗi lần", 130);
        Col("value", "Giá trị đổi", 130);
        Col("gain", "Chênh lệch", 130);
        Col("ratio", "Tỷ lệ", 70);
        Col("season", "Mùa", 60);

        Controls.Add(_grid); Controls.Add(_status); Controls.Add(info); Controls.Add(top);
        Theme.Apply(this);
        _status.ForeColor = Theme.Dim;

        _btnScan.Click += async (_, _) => await ToggleAsync();
        _btnPick.Click += (_, _) => Pick();
        _grid.SelectionChanged += (_, _) => _btnPick.Enabled = _cts == null && _grid.CurrentRow != null && _grid.CurrentRow.Index < _rows.Count;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Pick(); };
        FormClosing += (_, _) => _cts?.Cancel();
    }

    private static string N(long v) => v > 0 ? v.ToString("N0") : "—";

    private void Pick()
    {
        int i = _grid.CurrentRow?.Index ?? -1;
        if (i < 0 || i >= _rows.Count) return;
        PickedSpid = _rows[i].Row.Spid; PickedName = _rows[i].Name;
        DialogResult = DialogResult.OK;
    }

    private async Task ToggleAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        int season = _season.SelectedIndex <= 0 ? 0 : _seasons[Math.Clamp(_season.SelectedIndex - 1, 0, _seasons.Count - 1)];
        int level = 4 + Math.Max(0, _level.SelectedIndex);
        _cts = new CancellationTokenSource();
        _btnScan.Text = "Dừng"; _season.Enabled = false; _level.Enabled = false; _pool.Enabled = false; _minGain.Enabled = false; _sort.Enabled = false; _btnPick.Enabled = false;
        _grid.Rows.Clear(); _rows.Clear();
        var ct = _cts.Token;
        try { await ScanAsync(season, level, ct); }
        catch (OperationCanceledException) { _status.Text = "Đã dừng."; }
        catch (Exception ex) { _status.Text = "Lỗi: " + ex.Message; }
        _cts.Dispose(); _cts = null;
        _btnScan.Text = "Quét"; _season.Enabled = true; _level.Enabled = true; _pool.Enabled = true; _minGain.Enabled = true; _sort.Enabled = true;
        _btnPick.Enabled = _grid.CurrentRow != null && _rows.Count > 0;
    }

    private async Task ScanAsync(int season, int level, CancellationToken ct)
    {
        int Needed = 1 << (level - 1);   // +1 cards per card of the chosen level (every merge takes two cards of the level below)
        _status.Text = "Đang đọc dữ liệu cầu thủ của game...";
        var db = await LocalPlayerDb.GetAsync();
        if (db == null) { _status.Text = "Không đọc được dữ liệu cầu thủ trong thư mục game."; return; }
        var cands = db.All.Where(r => r.Overall > 1 && (season == 0 ? _seasons.Contains((int)(r.Spid / 1_000_000)) : r.Spid / 1_000_000 == season)).OrderByDescending(r => r.Overall).ToList();
        if (season == 0) cands = cands.Take((int)_pool.Value).ToList();
        var found = new List<Row>();
        int done = 0;
        foreach (var r in cands)
        {
            ct.ThrowIfCancellationRequested();
            _status.Text = $"Đang quét thị trường... {++done}/{cands.Count} cầu thủ, {found.Count} cầu thủ đủ thẻ +1.";
            List<Listing> market;
            try { market = await _api.SearchAsync(r.Spid, ct); } catch (OperationCanceledException) { throw; } catch { continue; }
            var ask = new List<long>();
            foreach (var l in market.Where(l => l.Grade == 1 && l.Price > 0).OrderBy(l => l.Price))
                for (int i = 0; i < l.Count && ask.Count < 1000; i++) ask.Add(l.Price);
            if (ask.Count >= Needed)
            {
                GameApi.SellPrice? p1 = null, p5 = null;
                try { p1 = await _api.GetSellPriceAsync(r.Spid, 1, ct); p5 = await _api.GetSellPriceAsync(r.Spid, level, ct); }
                catch (OperationCanceledException) { throw; } catch { }
                if (p1 != null && p5 != null && p5.Lower > 0)
                {
                    long v5 = p5.Lower * 5 / 4;                          // the game's value of a +5 card = 1.25 x floor price
                    int k = (int)Math.Max(1, TradeMax / Math.Max(1, v5)); // +5 cards that fit in one trade
                    k = Math.Min(k, ask.Count / Needed);
                    long cost = ask.Take(k * Needed).Sum();
                    found.Add(new Row(r.Spid, r.Overall, p1.Default, ask[0], ask.Count, k, cost, k * v5));
                }
            }
            await Pacing.Wait(0.06, 0.16, 1.0, ct);
        }
        long minGain = (long)_minGain.Value * 1_000_000L;
        found = found.Where(x => x.Gain >= minGain).ToList();
        found = (_sort.SelectedIndex == 0 ? found.OrderByDescending(x => x.Ratio) : found.OrderByDescending(x => x.Gain)).ToList();
        _status.Text = $"Đang tra tên {found.Count} cầu thủ...";
        foreach (var f in found.Take(60))
        {
            ct.ThrowIfCancellationRequested();
            string name = "";
            try { name = (await _players.GetDetailCachedAsync(f.Spid).WaitAsync(TimeSpan.FromSeconds(8)))?.Name ?? ""; } catch (OperationCanceledException) { throw; } catch { }
            _rows.Add((f, name));
            _grid.Rows.Add(name.Length > 0 ? name : $"SPID {f.Spid}", f.Overall, N(f.D1), N(f.Cheapest), f.OnSale >= 100 ? "100+" : f.OnSale.ToString(), f.Cards, N(f.Cost), N(f.Value), N(f.Gain), $"×{f.Ratio:0.00}", f.Spid / 1_000_000);
        }
        _status.Text = $"Xong: {found.Count}/{cands.Count} cầu thủ {(season == 0 ? "(mọi mùa)" : "mùa " + season)} mức +{level} có từ {Needed} thẻ +1 đang bán" + (found.Count > 60 ? ", hiện 60 cầu thủ tốt nhất." : ".") + " Bấm đúp hoặc \"Dùng cầu thủ này\" để chọn.";
        if (_grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[0];
    }
}
