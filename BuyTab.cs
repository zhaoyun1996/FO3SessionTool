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

    private readonly DarkTextBox _name = new() { PlaceholderText = "Tên cầu thủ (có thể để trống, khi đó tìm theo các tiêu chí bên dưới)", Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly DarkCombo _season = new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left, Width = 210, DropDownWidth = 300, CustomItems = true };
    private readonly DarkCombo _pos = new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left, Width = 120 };
    private readonly DarkTextBox _ovrFrom = new() { PlaceholderText = "từ", Width = 64, Anchor = AnchorStyles.Left };
    private readonly DarkTextBox _ovrTo = new() { PlaceholderText = "đến", Width = 64, Anchor = AnchorStyles.Left };
    private readonly DarkButton _btnFind = new() { Text = "Tìm kiếm", Width = 110, Anchor = AnchorStyles.Left };
    private readonly DataGridView _grid = new();
    private readonly Label _status = new() { AutoSize = true, ForeColor = Theme.Dim, Anchor = AnchorStyles.Left };

    private readonly DarkButton _btnBuyOnce = new() { Text = "Mua 1 thẻ (thủ công)", Width = 190, Anchor = AnchorStyles.Left };
    private readonly DarkButton _btnAuto = new() { Text = "Bắt đầu mua tự động", Width = 190, Anchor = AnchorStyles.Left };
    private readonly DarkNumber _floorEp = new() { Minimum = 0, Maximum = 1_000_000_000_000M, ThousandsSeparator = true, Increment = 100_000, Width = 150, Anchor = AnchorStyles.Left };
    private readonly DarkNumber _maxPrice = new() { Minimum = 0, Maximum = 1_000_000_000_000M, ThousandsSeparator = true, Increment = 1000, Width = 150, Anchor = AnchorStyles.Left };
    private readonly DarkNumber _maxCards = new() { Minimum = 0, Maximum = 200, Width = 70, Anchor = AnchorStyles.Left };
    private readonly DarkCombo _gFrom = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly DarkCombo _gTo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private bool _gSync;
    private readonly DarkCombo _pace = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private static readonly double[] PaceFactor = { 1.8, 1.0, 0.5, 0.25 };

    private readonly HashSet<long> _picked = new();
    private LocalPlayerDb? _db;
    private int _ver;
    private CancellationTokenSource? _cts;
    private bool _manualBusy;

    private sealed record Item(long Spid, string Name, int Season, string Pos, int Overall, int Stat, string Club, bool Site = false);

    public BuyTab(Func<GameApi?> api, Func<Task<Bitmap?>> sprite, PlayerSearch players, Func<bool> otherBusy, Action<string> log)
    {
        _api = api; _sprite = sprite; _players = players; _otherBusy = otherBusy; _log = log;
        Dock = DockStyle.Fill;

        _season.Items.Add("Mọi mùa");
        _pos.Items.AddRange(new object[] { "Mọi vị trí", "Thủ môn", "Hậu vệ", "Tiền vệ", "Tiền đạo" });
        _pace.Items.AddRange(new object[] { "Chậm (×1.8)", "Bình thường", "Nhanh (×0.5)", "Rất nhanh (×0.25)" });
        for (int g = 1; g <= 13; g++) { _gFrom.Items.Add("+" + g); _gTo.Items.Add("+" + g); }
        _gFrom.SelectedIndex = 0; _gTo.SelectedIndex = 0;
        _gFrom.SelectedIndexChanged += (_, _) => { if (_gSync) return; _gSync = true; if (_gTo.SelectedIndex < _gFrom.SelectedIndex) _gTo.SelectedIndex = _gFrom.SelectedIndex; _gSync = false; };
        _gTo.SelectedIndexChanged += (_, _) => { if (_gSync) return; _gSync = true; if (_gFrom.SelectedIndex > _gTo.SelectedIndex) _gFrom.SelectedIndex = _gTo.SelectedIndex; _gSync = false; };
        _season.SelectedIndex = 0; _pos.SelectedIndex = 0; _pace.SelectedIndex = 0;
        _floorEp.Value = 0; _maxPrice.Value = 0; _maxCards.Value = 0;

        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 4) };
        void Add(Control c, int left = 3) { if (c is not Label) c.Margin = new Padding(left, 3, 3, 3); filters.Controls.Add(c); }
        Add(new Label { Text = "Mùa", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 3, 0) });
        Add(_season);
        Add(new Label { Text = "Vị trí", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 3, 0) });
        Add(_pos);
        Add(new Label { Text = "Tổng", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 3, 0) });
        Add(_ovrFrom);
        Add(new Label { Text = "-", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 0) });
        Add(_ovrTo);
        _btnFind.Margin = new Padding(12, 3, 3, 3); filters.Controls.Add(_btnFind);

        var nameRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = Theme.ButtonH + 6 };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameRow.Controls.Add(new Label { Text = "Theo cầu thủ", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 3, 0) }, 0, 0);
        nameRow.Controls.Add(_name, 1, 0);

        StyleGrid();

        // options on the right of the table, one per row
        var side = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 270, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10, 0, 0, 0) };
        void L(string t, int top = 6) => side.Controls.Add(new Label { Text = t, AutoSize = true, Margin = new Padding(3, top, 3, 2) });
        void C(Control c) { c.Margin = new Padding(3, 0, 3, 0); side.Controls.Add(c); }
        L("Dừng khi EP dưới", 0); _floorEp.Width = 240; C(_floorEp);
        L("Giá tối đa/thẻ (0 = không giới hạn)"); _maxPrice.Width = 240; C(_maxPrice);
        L("Tối đa số thẻ (0 = không giới hạn)"); _maxCards.Width = 240; C(_maxCards);
        L("Thẻ cộng");
        var grade = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        grade.Controls.Add(new Label { Text = "từ", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 3, 0) });
        _gFrom.Margin = new Padding(3, 0, 8, 0); grade.Controls.Add(_gFrom);
        grade.Controls.Add(new Label { Text = "đến", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 3, 0) });
        _gTo.Margin = new Padding(3, 0, 3, 0); grade.Controls.Add(_gTo);
        side.Controls.Add(grade);
        L("Tốc độ"); C(_pace);
        _btnBuyOnce.Width = 240; _btnAuto.Width = 240;
        _btnBuyOnce.Margin = new Padding(3, 16, 3, 0); _btnAuto.Margin = new Padding(3, 8, 3, 0);
        side.Controls.Add(_btnBuyOnce); side.Controls.Add(_btnAuto);
        _status.MaximumSize = new Size(250, 0); _status.Margin = new Padding(3, 10, 3, 0);
        side.Controls.Add(_status);

        Controls.Add(_grid);
        Controls.Add(side);
        Controls.Add(filters);
        Controls.Add(nameRow);
        _grid.BringToFront();

        _season.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0, sel = !edit && (e.State & DrawItemState.Selected) != 0;
            var g = e.Graphics;
            using (var br = new SolidBrush(sel ? Theme.Hover : Theme.Surface2)) g.FillRectangle(br, e.Bounds);
            int x = e.Bounds.X + 4;
            string text;
            if (_season.Items[e.Index] is int sv)
            {
                var r = new Rectangle(x, e.Bounds.Y + (e.Bounds.Height - 18) / 2, 18, 18);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (_icons != null && PlayerSearch.SeasonSpriteX.TryGetValue(sv, out var sx))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_icons, r, new Rectangle(sx, 0, 18, 18), GraphicsUnit.Pixel);
                }
                else SeasonArt.Badge(g, r, sv);
                x += 24; text = SeasonName(sv);
            }
            else text = "Mọi mùa";
            TextRenderer.DrawText(g, text, Theme.Body, new Rectangle(x, e.Bounds.Y, e.Bounds.Right - x, e.Bounds.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        };
        _btnFind.Click += async (_, _) => await FindAsync();
        _name.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await FindAsync(); } };
        _btnBuyOnce.Click += async (_, _) => await BuyOnceAsync();
        _btnAuto.Click += async (_, _) => await ToggleAutoAsync();
        _grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not Item it) return;
            if (!_picked.Remove(it.Spid)) _picked.Add(it.Spid);
            _grid.InvalidateCell(0, e.RowIndex);
            UpdateUi();
        };
        _grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Rows[e.RowIndex].Tag is not Item it) return;
            var cn = _grid.Columns[e.ColumnIndex].Name;
            if (cn != "season" && cn != "pick") return;
            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            var g = e.Graphics!; var b = e.CellBounds;
            if (cn == "pick")
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var box = new Rectangle(b.X + (b.Width - 16) / 2, b.Y + (b.Height - 16) / 2, 16, 16);
                bool on = _picked.Contains(it.Spid);
                using (var br = new SolidBrush(on ? Theme.Accent : Theme.Surface2)) g.FillRectangle(br, box);
                using (var pen = new Pen(on ? Theme.Accent : Theme.Border)) g.DrawRectangle(pen, box);
                if (on)
                    using (var pen = new Pen(Color.White, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                        g.DrawLines(pen, new[] { new Point(box.X + 3, box.Y + 8), new Point(box.X + 7, box.Y + 12), new Point(box.X + 13, box.Y + 4) });
                e.Handled = true;
                return;
            }
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

        UpdateUi();
        _ = EnsureDbAsync();   // fill the season list right away
        _ = LoadIconsAsync();
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
        Col("name", "Cầu thủ", 220, true, DataGridViewContentAlignment.MiddleLeft);
        Col("season", "", 45);
        Col("pos", "Vị trí", 60);
        Col("ovr", "Tổng", 60);
        Col("club", "Mùa", 190, false, DataGridViewContentAlignment.MiddleLeft);
        Col("spid", "SPID", 90);
    }

    public void SetLoggedIn(bool on) { if (!on) { _cts?.Cancel(); } UpdateUi(); }

    private async Task LoadIconsAsync() { try { _icons = await _sprite(); _season.Invalidate(); _grid.Invalidate(); } catch { } }

    private async Task EnsureDbAsync()
    {
        if (_db != null) return;
        _db = await LocalPlayerDb.GetAsync(null);
        if (_db == null) return;
        var seasons = _db.All.Select(r => (int)(r.Spid / 1_000_000)).Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
        var keep = _season.SelectedIndex;
        _season.Items.Clear();
        _season.Items.Add("Mọi mùa");
        foreach (var s in seasons) _season.Items.Add(s);   // boxed int; drawn as icon + name
        _season.SelectedIndex = Math.Min(keep, _season.Items.Count - 1);
    }

    private static int ParseOr(string t, int def) => int.TryParse(t.Trim(), out var v) ? v : def;
    private int OvrFrom => Math.Max(0, ParseOr(_ovrFrom.Text, 0));
    private int OvrTo => ParseOr(_ovrTo.Text, int.MaxValue);
    private bool InRange(int v) => v >= OvrFrom && v <= OvrTo;

    private int SelSeason => _season.SelectedItem is int v ? v : -1;

    private string SeasonName(int season) { var l = _players.SeasonLabelOf(season); return l.Length > 0 ? l : $"Mùa {season}"; }

    // some seasons carry a placeholder rating of 1 in the game files: unknown (0) until the real value is read from the site
    private static int StatOf(LocalPlayerDb.Row r) => r.Overall <= 1 ? 0 : r.Overall + PlayerSearch.StatBonus;

    private bool Match(LocalPlayerDb.Row r) => MatchBase(r) && InRange(StatOf(r));

    private bool MatchBase(LocalPlayerDb.Row r)
    {
        int s = SelSeason;
        if (r.Spid / 1_000_000 == 0) return false;   // season 0 (base cards) is never offered
        if (s >= 0 && r.Spid / 1_000_000 != s) return false;
        if (_pos.SelectedIndex > 0)
        {
            string want = _pos.SelectedIndex switch { 1 => "gk", 2 => "def", 3 => "mid", _ => "att" };
            if (r.Positions.Length == 0 || LocalPlayerDb.PosGroup(r.Positions[0]) != want) return false;
        }
        return true;
    }

    private static string PosName(LocalPlayerDb.Row r)
        => r.Positions.Length > 0 && r.Positions[0] < LocalPlayerDb.PosNames.Length ? LocalPlayerDb.PosNames[r.Positions[0]] : "";

    private string? _lastFilter;
    private string FilterKey() => $"{_name.Text.Trim().ToLowerInvariant()}|{SelSeason}|{_pos.SelectedIndex}|{_ovrFrom.Text.Trim()}|{_ovrTo.Text.Trim()}";

    private async Task FindAsync()
    {
        if (_lastFilter == FilterKey()) return;   // same filters as the last search: nothing to do
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
                    if (p.Season == 0 || p.Spid / 1_000_000 == 0) continue;
                    var r = _db?.Find(p.Spid);
                    if (r != null) { if (!MatchBase(r) || !InRange(p.Overall)) continue; items.Add(new Item(p.Spid, p.Name, p.Season, p.Position, p.Overall, p.Overall, _db!.ClubOf(p.Spid), true)); }
                    else
                    {
                        if (SelSeason >= 0 && p.Season != SelSeason) continue;
                        if (!InRange(p.Overall)) continue;
                        items.Add(new Item(p.Spid, p.Name, p.Season, p.Position, p.Overall, p.Overall, "", true));
                    }
                }
            }
            else
            {
                if (_db == null) { _status.Text = "Chưa đọc được dữ liệu game (Config gameDir). Hãy nhập tên cầu thủ để tìm."; return; }
                // the game files only narrow the candidates (season, position); the Tổng comes from the site afterwards, see ResolveAsync
                foreach (var r in _db.All.Where(MatchBase).OrderByDescending(StatOf).ThenBy(r => r.Spid).Take(MaxRows))
                    items.Add(new Item(r.Spid, "", (int)(r.Spid / 1_000_000), PosName(r), r.Overall + PlayerSearch.StatBonus, StatOf(r), _db.ClubOf(r.Spid)));
            }
            if (ver != _ver) return;
            _picked.Clear();   // filters changed: start with nothing ticked
            _lastFilter = FilterKey();
            Fill(items);
            // names of the local rows: fill in from the cache / siblings, a few at a time, without blocking the UI
            _ = ResolveAsync(items, ver);
        }
        catch (Exception ex) { _status.Text = "Lỗi: " + ex.Message; }
        finally { _btnFind.Enabled = true; }
    }

    private string ClubText(Item it) => SeasonName(it.Season);

    private void Fill(List<Item> items)
    {
        _grid.Rows.Clear();
        foreach (var it in items)
        {
            int i = _grid.Rows.Add("", it.Name.Length > 0 ? it.Name : "(SPID " + it.Spid + ")", it.Season, it.Pos, it.Stat > 0 ? it.Stat : "", ClubText(it), it.Spid);
            _grid.Rows[i].Tag = it;
        }
        _status.Text = $"{items.Count} cầu thủ" + (items.Count >= MaxRows ? $" (hiện {MaxRows} đầu tiên, hãy lọc thêm)" : "") + ". Bấm vào dòng để chọn nhiều cầu thủ rồi mua.";
        UpdateUi();
    }

    /// <summary>Candidates read from the game files get their Tổng (and name, season label) from the site, a few at a time; with a Tổng range
    /// the rows outside it are removed at the end. Cards the site does not have keep the game-file estimate.</summary>
    private async Task ResolveAsync(List<Item> items, int ver)
    {
        var todo = items.Where(i => !i.Site).ToList();
        if (todo.Count == 0) return;
        bool ranged = OvrFrom > 0 || OvrTo < int.MaxValue;
        int done = 0;
        var sem = new SemaphoreSlim(6);
        var tasks = todo.Select(async it =>
        {
            await sem.WaitAsync();
            try
            {
                if (ver != _ver) return;
                var site = await _players.GetSiteDetailCachedAsync(it.Spid);
                var d = site ?? await _players.GetDetailCachedAsync(it.Spid);
                if (ver != _ver) return;
                int rt = site?.Positions.FirstOrDefault(p => p.Active)?.Rating ?? 0;
                BeginInvoke(() =>
                {
                    if (ver != _ver) return;
                    foreach (DataGridViewRow row in _grid.Rows)
                        if (row.Tag is Item x && x.Spid == it.Spid)
                        {
                            var nx = x with { Name = d?.Name is { Length: > 0 } nm ? nm : x.Name, Stat = rt > 10 ? rt : x.Stat, Site = site != null };
                            row.Tag = nx;
                            if (nx.Name.Length > 0) row.Cells["name"].Value = nx.Name;
                            row.Cells["ovr"].Value = nx.Stat > 0 ? nx.Stat : "";
                            if (d != null && d.SeasonLabel.Length > 0) row.Cells["club"].Value = d.SeasonLabel;
                            break;
                        }
                    _status.Text = $"Đang đọc chỉ số tổng từ fifaaddict: {Interlocked.Increment(ref done)}/{todo.Count}...";
                });
            }
            catch { }
            finally { sem.Release(); }
        });
        try { await Task.WhenAll(tasks); } catch { }
        if (ver != _ver) return;
        BeginInvoke(() =>
        {
            if (ver != _ver) return;
            if (ranged)
                for (int i = _grid.Rows.Count - 1; i >= 0; i--)
                    if (_grid.Rows[i].Tag is Item x && !InRange(x.Stat)) _grid.Rows.RemoveAt(i);
            _status.Text = $"{_grid.Rows.Count} cầu thủ. Bấm vào dòng để chọn nhiều cầu thủ rồi mua.";
        });
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

    private BuyOptions Options() => new((long)_floorEp.Value, (long)_maxPrice.Value, _gFrom.SelectedIndex + 1, _gTo.SelectedIndex + 1, (int)_maxCards.Value,
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
