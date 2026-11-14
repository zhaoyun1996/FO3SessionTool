namespace FO3SessionTool;

/// <summary>
/// Buying players. Candidates come from the client's own card data (filters: season, position, stats) and/or a name search on fifaaddict.
/// Manual: buy the cheapest listing of the selected cards once. Auto: one parallel worker per selected player (BuyRunner) until EP
/// reaches a floor, a card count is reached or the user stops.
/// </summary>
public sealed class BuyTab : UserControl
{
    private const int MaxRows = 400;

    private readonly Func<GameApi?> _api;
    private readonly PlayerSearch _players;
    private readonly Func<Task<Bitmap?>> _sprite;
    private Bitmap? _icons;
    private readonly Func<bool> _otherBusy;
    private readonly Action<string> _log;

    private readonly DarkTextBox _name = new() { PlaceholderText = "Tên cầu thủ (để trống = lọc theo dữ liệu game)", Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly DarkCombo _season = new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left, Width = 100 };
    private readonly DarkCombo _pos = new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left, Width = 120 };
    private readonly DarkCombo _stat = new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left, Width = 120 };
    private readonly DarkNumber _statMin = new() { Minimum = 0, Maximum = 200, Value = 0, Width = 70, Anchor = AnchorStyles.Left };
    private readonly DarkButton _btnFind = new() { Text = "Lọc / Tìm", Width = 110, Anchor = AnchorStyles.Left };
    private readonly DataGridView _grid = new();
    private readonly Label _status = new() { AutoSize = true, ForeColor = Theme.Dim, Anchor = AnchorStyles.Left };

    private readonly DarkButton _btnBuyOnce = new() { Text = "Mua 1 thẻ (thủ công)", Width = 190, Anchor = AnchorStyles.Left };
    private readonly DarkButton _btnAuto = new() { Text = "Bắt đầu mua tự động", Width = 190, Anchor = AnchorStyles.Left };
    private readonly DarkNumber _floorEp = new() { Minimum = 0, Maximum = 1_000_000_000_000M, ThousandsSeparator = true, Increment = 100_000, Width = 150, Anchor = AnchorStyles.Left };
    private readonly DarkNumber _maxPrice = new() { Minimum = 0, Maximum = 1_000_000_000_000M, ThousandsSeparator = true, Increment = 1000, Width = 150, Anchor = AnchorStyles.Left };
    private readonly DarkNumber _maxCards = new() { Minimum = 0, Maximum = 200, Width = 70, Anchor = AnchorStyles.Left };
    private readonly DarkCheck _anyGrade = new() { Text = "Mua cả thẻ trên +1", Anchor = AnchorStyles.Left };
    private readonly DarkCombo _pace = new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left, Width = 150 };
    private static readonly double[] PaceFactor = { 1.8, 1.0, 0.5 };

    private readonly HashSet<long> _picked = new();
    private LocalPlayerDb? _db;
    private int _ver;
    private CancellationTokenSource? _cts;
    private bool _manualBusy;

    private sealed record Item(long Spid, string Name, int Season, string Pos, int Overall, int Stat, string Club);

    public BuyTab(Func<GameApi?> api, Func<Task<Bitmap?>> sprite, PlayerSearch players, Func<bool> otherBusy, Action<string> log)
    {
        _api = api; _sprite = sprite; _players = players; _otherBusy = otherBusy; _log = log;
        Dock = DockStyle.Fill;

        _season.Items.Add("Mọi mùa");
        _pos.Items.AddRange(new object[] { "Mọi vị trí", "Thủ môn", "Hậu vệ", "Tiền vệ", "Tiền đạo" });
        _stat.Items.AddRange(new object[] { "Chỉ số tổng", "Dứt điểm", "Tốc độ chạy", "Sức mạnh", "Rê bóng", "Thể lực" });
        _pace.Items.AddRange(new object[] { "Chậm (×1.8)", "Bình thường", "Nhanh (×0.5)" });
        _season.SelectedIndex = 0; _pos.SelectedIndex = 0; _stat.SelectedIndex = 0; _pace.SelectedIndex = 0;
        _floorEp.Value = 0; _maxPrice.Value = 0; _maxCards.Value = 0;

        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 4) };
        void Add(Control c, int left = 3) { if (c is not Label) c.Margin = new Padding(left, 3, 3, 3); filters.Controls.Add(c); }
        Add(new Label { Text = "Mùa", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 3, 0) });
        Add(_season);
        Add(new Label { Text = "Vị trí", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 3, 0) });
        Add(_pos);
        Add(new Label { Text = "Chỉ số", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 3, 0) });
        Add(_stat);
        Add(new Label { Text = "tối thiểu", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 3, 0) });
        Add(_statMin);
        _btnFind.Margin = new Padding(12, 3, 3, 3); filters.Controls.Add(_btnFind);

        var nameRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = Theme.ButtonH + 6 };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameRow.Controls.Add(new Label { Text = "Theo cầu thủ", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 3, 0) }, 0, 0);
        nameRow.Controls.Add(_name, 1, 0);

        StyleGrid();

        var opts = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(0, 4, 0, 0) };
        void O(Control c, int left = 3) { if (c is not Label) c.Margin = new Padding(left, 3, 3, 3); opts.Controls.Add(c); }
        O(new Label { Text = "Dừng khi EP dưới", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 3, 0) });
        O(_floorEp);
        O(new Label { Text = "Giá tối đa/thẻ (0 = không)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(10, 0, 3, 0) });
        O(_maxPrice);
        O(new Label { Text = "Tối đa số thẻ (0 = không)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(10, 0, 3, 0) });
        O(_maxCards);
        O(_anyGrade, 10);
        O(new Label { Text = "Tốc độ", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(10, 0, 3, 0) });
        O(_pace);
        var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(0, 2, 0, 0) };
        _btnBuyOnce.Margin = new Padding(3); _btnAuto.Margin = new Padding(3); _status.Anchor = AnchorStyles.Left; _status.Margin = new Padding(12, 0, 3, 0);
        btns.Controls.Add(_btnBuyOnce); btns.Controls.Add(_btnAuto); btns.Controls.Add(_status);

        Controls.Add(_grid);
        Controls.Add(opts);
        Controls.Add(btns);
        Controls.Add(filters);
        Controls.Add(nameRow);
        _grid.BringToFront();

        _btnFind.Click += async (_, _) => await FindAsync();
        _name.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await FindAsync(); } };
        _btnBuyOnce.Click += async (_, _) => await BuyOnceAsync();
        _btnAuto.Click += async (_, _) => await ToggleAutoAsync();
        _grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not Item it) return;
            if (!_picked.Remove(it.Spid)) _picked.Add(it.Spid);
            _grid.Rows[e.RowIndex].Cells["pick"].Value = _picked.Contains(it.Spid) ? "✔" : "";
            UpdateUi();
        };
        _grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "season" || _grid.Rows[e.RowIndex].Tag is not Item it) return;
            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            var g = e.Graphics!; var b = e.CellBounds;
            var r = new Rectangle(b.X + (b.Width - 20) / 2, b.Y + (b.Height - 20) / 2, 20, 20);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (_icons != null && PlayerSearch.SeasonSpriteX.TryGetValue(it.Season, out var sx))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(_icons, r, new Rectangle(sx, 0, 18, 18), GraphicsUnit.Pixel);
            }
            else SeasonArt.Badge(g, r, it.Season);
            e.Handled = true;
        };
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == "pick") { e.CellStyle!.ForeColor = Theme.Accent; e.CellStyle.Font = Theme.Bold; }
        };
        UpdateUi();
    }

    private void StyleGrid()
    {
        var g = _grid;
        g.Dock = DockStyle.Fill;
        g.ReadOnly = true; g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false; g.AllowUserToResizeRows = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect; g.MultiSelect = false;
        Theme.StyleGrid(g);
        g.ColumnHeadersHeight = 30; g.RowTemplate.Height = 26;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Text;
        void Col(string name, string header, int w, bool fill = false, DataGridViewContentAlignment al = DataGridViewContentAlignment.MiddleCenter)
            => g.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, Width = w, SortMode = DataGridViewColumnSortMode.NotSortable,
                AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
                MinimumWidth = fill ? 140 : 20, DefaultCellStyle = new DataGridViewCellStyle { Alignment = al },
            });
        Col("pick", "Chọn", 56);
        Col("name", "Cầu thủ", 200, true, DataGridViewContentAlignment.MiddleLeft);
        Col("season", "Mùa", 55);
        Col("pos", "Vị trí", 60);
        Col("stat", "Chỉ số lọc", 85);
        Col("club", "CLB", 160, false, DataGridViewContentAlignment.MiddleLeft);
        Col("spid", "SPID", 90);
    }

    public void SetLoggedIn(bool on) { if (!on) { _cts?.Cancel(); } UpdateUi(); }

    private async Task EnsureDbAsync()
    {
        if (_db != null) return;
        _db = await LocalPlayerDb.GetAsync(null);
        if (_db == null) return;
        var seasons = _db.All.Select(r => (int)(r.Spid / 1_000_000)).Distinct().OrderBy(x => x).ToList();
        var keep = _season.SelectedIndex;
        _season.Items.Clear();
        _season.Items.Add("Mọi mùa");
        foreach (var s in seasons) _season.Items.Add($"Mùa {s}");
        _season.SelectedIndex = Math.Min(keep, _season.Items.Count - 1);
    }

    private int SelSeason => _season.SelectedIndex <= 0 ? -1 : int.Parse(((string)_season.SelectedItem!).Substring(4));

    private int StatOf(LocalPlayerDb.Row r) => _stat.SelectedIndex switch
    {
        1 => r.Finishing, 2 => r.Sprint, 3 => r.Strength, 4 => r.Dribbling, 5 => r.Stamina, _ => r.Overall + PlayerSearch.StatBonus,
    };

    private bool Match(LocalPlayerDb.Row r)
    {
        int s = SelSeason;
        if (s >= 0 && r.Spid / 1_000_000 != s) return false;
        if (_pos.SelectedIndex > 0)
        {
            string want = _pos.SelectedIndex switch { 1 => "gk", 2 => "def", 3 => "mid", _ => "att" };
            if (r.Positions.Length == 0 || LocalPlayerDb.PosGroup(r.Positions[0]) != want) return false;
        }
        return StatOf(r) >= (int)_statMin.Value;
    }

    private static string PosName(LocalPlayerDb.Row r)
        => r.Positions.Length > 0 && r.Positions[0] < LocalPlayerDb.PosNames.Length ? LocalPlayerDb.PosNames[r.Positions[0]] : "";

    private async Task FindAsync()
    {
        int ver = ++_ver;
        _btnFind.Enabled = false;
        try
        {
            _status.Text = "Đang lọc...";
            await EnsureDbAsync();
            if (_icons == null) { try { _icons = await _sprite(); _grid.Invalidate(); } catch { } }
            var items = new List<Item>();
            string name = _name.Text.Trim();
            if (name.Length > 0)
            {
                var found = await _players.SearchAsync(name);
                if (ver != _ver) return;
                foreach (var p in found)
                {
                    var r = _db?.Find(p.Spid);
                    if (r != null) { if (!Match(r)) continue; items.Add(new Item(p.Spid, p.Name, p.Season, p.Position, p.Overall, StatOf(r), _db!.ClubOf(p.Spid))); }
                    else
                    {
                        if (SelSeason >= 0 && p.Season != SelSeason) continue;
                        if (_stat.SelectedIndex == 0 && p.Overall < (int)_statMin.Value) continue;
                        items.Add(new Item(p.Spid, p.Name, p.Season, p.Position, p.Overall, p.Overall, ""));
                    }
                }
            }
            else
            {
                if (_db == null) { _status.Text = "Chưa đọc được dữ liệu game (Config gameDir). Hãy nhập tên cầu thủ để tìm."; return; }
                bool filtered = SelSeason >= 0 || _pos.SelectedIndex > 0 || (int)_statMin.Value > 0;
                if (!filtered) { _status.Text = "Chọn ít nhất một điều kiện (mùa / vị trí / chỉ số) hoặc nhập tên."; return; }
                foreach (var r in _db.All.Where(Match).OrderByDescending(StatOf).ThenBy(r => r.Spid).Take(MaxRows))
                    items.Add(new Item(r.Spid, "", (int)(r.Spid / 1_000_000), PosName(r), r.Overall + PlayerSearch.StatBonus, StatOf(r), _db.ClubOf(r.Spid)));
            }
            if (ver != _ver) return;
            Fill(items);
            // names of the local rows: fill in from the cache / siblings, a few at a time, without blocking the UI
            _ = FillNamesAsync(items, ver);
        }
        catch (Exception ex) { _status.Text = "Lỗi: " + ex.Message; }
        finally { _btnFind.Enabled = true; }
    }

    private void Fill(List<Item> items)
    {
        _grid.Rows.Clear();
        _grid.Columns["stat"].HeaderText = _stat.SelectedIndex == 0 ? "Chỉ số tổng" : (string)_stat.SelectedItem!;
        foreach (var it in items)
        {
            int i = _grid.Rows.Add(_picked.Contains(it.Spid) ? "✔" : "", it.Name.Length > 0 ? it.Name : "(SPID " + it.Spid + ")", it.Season, it.Pos, it.Stat, it.Club, it.Spid);
            _grid.Rows[i].Tag = it;
        }
        _status.Text = $"{items.Count} cầu thủ" + (items.Count >= MaxRows ? $" (hiện {MaxRows} đầu tiên, hãy lọc thêm)" : "") + ". Bấm vào dòng để chọn nhiều cầu thủ rồi mua.";
        UpdateUi();
    }

    private async Task FillNamesAsync(List<Item> items, int ver)
    {
        var sem = new SemaphoreSlim(4);
        var tasks = items.Where(it => it.Name.Length == 0).Take(60).Select(async it =>
        {
            await sem.WaitAsync();
            try
            {
                var d = await _players.GetDetailCachedAsync(it.Spid);
                if (d == null || ver != _ver || string.IsNullOrWhiteSpace(d.Name)) return;
                BeginInvoke(() =>
                {
                    foreach (DataGridViewRow row in _grid.Rows)
                        if (row.Tag is Item x && x.Spid == it.Spid) { row.Cells["name"].Value = d.Name; row.Tag = x with { Name = d.Name }; }
                });
            }
            catch { }
            finally { sem.Release(); }
        });
        try { await Task.WhenAll(tasks); } catch { }
    }

    private List<Item> Selected() => _grid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<Item>().Where(i => _picked.Contains(i.Spid)).ToList();

    private bool Auto => _cts != null;

    private void UpdateUi()
    {
        bool login = _api() != null;
        bool any = _picked.Count > 0;
        _btnBuyOnce.Enabled = login && any && !Auto && !_manualBusy;
        _btnAuto.Enabled = login && (Auto || any);
        _btnAuto.Text = Auto ? "Dừng mua" : "Bắt đầu mua tự động";
        _btnFind.Enabled = true;
    }

    private BuyOptions Options() => new((long)_floorEp.Value, (long)_maxPrice.Value, _anyGrade.Checked, (int)_maxCards.Value,
        PaceFactor[Math.Clamp(_pace.SelectedIndex, 0, PaceFactor.Length - 1)]);

    private async Task BuyOnceAsync()
    {
        var api = _api();
        if (api == null || _manualBusy || Auto) return;
        if (_otherBusy()) { MessageBox.Show("Đang chạy ép thẻ / giao dịch, hãy dừng trước.", "FO3 Session Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var sel = Selected();
        if (sel.Count == 0) return;
        _manualBusy = true; UpdateUi();
        try
        {
            var opt = Options();
            foreach (var it in sel)
            {
                var label = it.Name.Length > 0 ? it.Name : it.Spid.ToString();
                var (ok, msg, _) = await BuyRunner.BuyCheapestAsync(api, it.Spid, label, opt, CancellationToken.None);
                _log("[Mua] " + msg);
                _status.Text = msg;
                if (!ok && msg.Contains("Hết kho")) break;
                if (sel.Count > 1) await Pacing.Wait(3, 6, opt.Pace, CancellationToken.None);
            }
        }
        catch (Exception ex) { _log("[Mua] Lỗi: " + ex.Message); }
        finally { _manualBusy = false; UpdateUi(); }
    }

    private async Task ToggleAutoAsync()
    {
        if (Auto) { _cts!.Cancel(); _log("[Mua] Đang dừng..."); return; }
        var api = _api();
        if (api == null) return;
        if (_otherBusy()) { MessageBox.Show("Đang chạy ép thẻ / giao dịch, hãy dừng trước.", "FO3 Session Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var sel = Selected();
        if (sel.Count == 0) return;
        var opt = Options();
        var ask = MessageBox.Show(
            $"MUA TỰ ĐỘNG {sel.Count} cầu thủ = {sel.Count} luồng chạy song song." + Environment.NewLine +
            $"Dừng khi EP xuống dưới {opt.StopBelowEp:N0}" + (opt.MaxCards > 0 ? $" hoặc sau {opt.MaxCards} thẻ" : "") + "." + Environment.NewLine +
            (opt.MaxPrice > 0 ? $"Giá tối đa: {opt.MaxPrice:N0} EP/thẻ." : "Không giới hạn giá mỗi thẻ.") + Environment.NewLine +
            "Thao tác này tiêu EP thật. Tiếp tục?", "Mua tự động", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes) return;

        _cts = new CancellationTokenSource();
        UpdateUi();
        var runner = new BuyRunner(api, _log, p => BeginInvoke(() => _status.Text = $"Đã mua {p.Bought} thẻ, chi {p.Spent:N0} EP, EP còn {p.Ep:N0}, {p.Workers} luồng."));
        try
        {
            var result = await runner.RunAsync(sel.Select(i => new BuyTarget(i.Spid, i.Name)).ToList(), opt, _cts.Token);
            _log("[Mua] " + result);
            _status.Text = result;
        }
        catch (Exception ex) { _log("[Mua] Lỗi: " + ex.Message); }
        finally { _cts?.Dispose(); _cts = null; UpdateUi(); }
    }

    /// <summary>True while the automatic buying runs (the other tabs refuse to start in parallel).</summary>
    public bool Running => _cts != null;
    public void StopAuto() => _cts?.Cancel();
}
