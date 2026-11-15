namespace FO3SessionTool;

/// <summary>Read-only view of the ranking: season, this account's standing per mode and the reward list per division.</summary>
public sealed class RankingTab : UserControl
{
    private readonly Func<GameApi?> _api;
    private readonly Action<string> _log;
    private readonly DarkButton _btnReload = new() { Text = "Tải lại", Width = 110, Anchor = AnchorStyles.Left };
    private readonly Label _season = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(Theme.Gap, 0, 0, 0) };
    private readonly Label _total = new() { AutoSize = true, ForeColor = Theme.Dim, Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 6) };
    private readonly DataGridView _modes = new();
    private readonly DataGridView _rewards = new();
    private bool _busy;

    public RankingTab(Func<GameApi?> api, Action<string> log)
    {
        _api = api; _log = log;
        Dock = DockStyle.Fill;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 4) };
        _btnReload.Margin = new Padding(3);
        top.Controls.Add(_btnReload); top.Controls.Add(_season);

        Style(_modes, 30);
        Col(_modes, "mode", "Chế độ", 140, true, left: true); Col(_modes, "div", "Hạng", 190, false, left: true);
        Col(_modes, "elo", "ELO", 70); Col(_modes, "high", "ELO cao nhất", 100); Col(_modes, "w", "Thắng", 65); Col(_modes, "l", "Thua", 65);
        Col(_modes, "f", "Bỏ cuộc", 75); Col(_modes, "rank", "Thứ hạng", 80);
        _modes.Dock = DockStyle.Top; _modes.Height = 30 + 4 * 30 + 2;

        var box = new DarkGroup { Text = "Phần thưởng cuối mùa theo hạng", Dock = DockStyle.Fill, Padding = new Padding(8, 18, 8, 8) };
        Style(_rewards, 26);
        Col(_rewards, "d", "Hạng", 190, false, left: true); Col(_rewards, "items", "Phần thưởng", 300, true, left: true);
        box.Controls.Add(_rewards);

        Controls.Add(box);
        Controls.Add(_total);
        Controls.Add(_modes);
        Controls.Add(top);
        _btnReload.Click += async (_, _) => await ReloadAsync();
    }

    private static void Style(DataGridView g, int rowH)
    {
        g.Dock = DockStyle.Fill; g.ReadOnly = true; g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false; g.AllowUserToResizeRows = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect; g.MultiSelect = false;
        Theme.StyleGrid(g);
        g.ColumnHeadersHeight = 30; g.RowTemplate.Height = rowH;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Text;
    }

    private static void Col(DataGridView g, string name, string header, int w, bool fill = false, bool left = false)
        => g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name, HeaderText = header, Width = w, SortMode = DataGridViewColumnSortMode.NotSortable,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None, MinimumWidth = fill ? 120 : 20,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = left ? DataGridViewContentAlignment.MiddleLeft : DataGridViewContentAlignment.MiddleCenter },
        });

    private static string DivName(string d)
    {
        var parts = d.Replace("DIVISION_", "").Split('_');
        string name = parts[0] switch
        {
            "LEGENDARY" => "Huyền thoại", "WORLDCLASS" => "Đẳng cấp thế giới", "PROFESSIONAL" => "Chuyên nghiệp", "SEMIPRO" => "Bán chuyên", "AMATEUR" => "Nghiệp dư",
            _ => parts[0],
        };
        return parts.Length > 1 ? $"{name} {parts[1]}" : name;
    }

    private static string Date(string iso) => DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
        ? d.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : iso;

    public async Task ReloadAsync()
    {
        var api = _api();
        if (_busy) return;
        if (api == null) { _season.Text = "Hãy đăng nhập để xem xếp hạng."; return; }
        _busy = true; _btnReload.Enabled = false; _season.Text = "Đang đọc xếp hạng...";
        try
        {
            var r = await api.GetRankingAsync();
            _season.Text = $"{r.Title}   ({Date(r.Start)} → {Date(r.Next)})";
            _total.Text = $"Tổng: {r.PlayedGames} trận, {r.TotalWins} thắng, ELO cao nhất {r.HighElo}.";
            _modes.Rows.Clear();
            foreach (var m in r.Modes)
                _modes.Rows.Add(m.Name, m.Division.Length > 0 ? DivName("DIVISION_" + m.Division) : "", m.Elo, m.HighElo, m.Wins, m.Losses, m.Forfeits, m.Rank > 0 ? m.Rank.ToString("N0") : "-");
            _rewards.Rows.Clear();
            foreach (var d in r.Rewards) _rewards.Rows.Add(DivName(d.Division), d.Items);
            _log($"[Xếp hạng] {r.Title}: " + string.Join("; ", r.Modes.Where(x => x.Wins + x.Losses > 0).Select(x => $"{x.Name} ELO {x.Elo}, {x.Wins}T/{x.Losses}B")));
        }
        catch (Exception ex) { _season.Text = "Lỗi: " + ex.Message; _log("[Xếp hạng] Lỗi đọc xếp hạng: " + ex.Message); }
        finally { _busy = false; _btnReload.Enabled = true; }
    }

    public void SetLoggedIn(bool on)
    {
        if (!on) { _modes.Rows.Clear(); _rewards.Rows.Clear(); _total.Text = ""; _season.Text = "Hãy đăng nhập để xem xếp hạng."; }
    }
}
