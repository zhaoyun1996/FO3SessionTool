namespace FO3SessionTool;

/// <summary>The game's own league mode (mùa giải), simulated by the server round by round (uses one simulation card per round).</summary>
public sealed class LeagueTab : UserControl
{
    private readonly Func<GameApi?> _api;
    private readonly Func<bool> _otherBusy;
    private readonly Action<string> _log;

    private readonly Label _info = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Dim };
    private readonly DarkButton _btnLoad = new() { Text = "Tải trạng thái", Width = 150 };
    private readonly DarkButton _btnRun = new() { Text = "Bắt đầu giả lập", Width = 190 };
    private readonly DarkNumber _rounds = new() { Minimum = 0, Maximum = 100, Value = 0, Width = 80 };
    private readonly DarkCombo _pace = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private static readonly double[] PaceFactor = { 1.8, 1.0, 0.5, 0.25 };

    private CancellationTokenSource? _cts;

    public LeagueTab(Func<GameApi?> api, Func<bool> otherBusy, Action<string> log)
    {
        _api = api; _otherBusy = otherBusy; _log = log;
        Dock = DockStyle.Fill;
        _pace.Items.AddRange(new object[] { "Chậm, thong thả (×1.8)", "Bình thường (×1)", "Nhanh (×0.5)", "Rất nhanh (×0.25)" });
        _pace.SelectedIndex = 1;

        var col = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4) };
        Label L(string t, int top = 10) => new() { Text = t, AutoSize = true, Margin = new Padding(3, top, 3, 2) };
        var note = new Label
        {
            AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = Theme.Dim,
            Text = "Chế độ Mùa giải của game: mỗi vòng tool gửi lên server đúng thông số game gửi (đội nhà, đội khách, vòng) cho một trận mô phỏng, " +
                   "server tự tính tỉ số. Mỗi vòng tốn 1 thẻ mô phỏng giải đấu. Thông số vòng được tool tự dựng từ danh sách đội của mùa giải.",
        };
        col.Controls.Add(note);
        col.Controls.Add(L("Trạng thái mùa giải")); col.Controls.Add(_info);
        var row1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        _btnLoad.Margin = new Padding(3);
        row1.Controls.Add(_btnLoad);
        col.Controls.Add(row1);
        col.Controls.Add(L("Số vòng giả lập (0 = đến hết mùa)")); col.Controls.Add(_rounds);
        col.Controls.Add(L("Tốc độ")); col.Controls.Add(_pace);
        _btnRun.Margin = new Padding(3, 16, 3, 0);
        col.Controls.Add(_btnRun);
        _status.Margin = new Padding(3, 10, 3, 0);
        col.Controls.Add(_status);
        Controls.Add(col);

        _btnLoad.Click += async (_, _) => await LoadAsync();
        _btnRun.Click += async (_, _) => await ToggleAsync();
    }

    public bool Running => _cts != null;
    public void StopRun() => _cts?.Cancel();
    public void SetLoggedIn(bool on) { if (!on) { _cts?.Cancel(); _info.Text = ""; _status.Text = ""; } }

    public async Task LoadAsync()
    {
        var api = _api();
        if (api == null) { _info.Text = "Hãy đăng nhập."; return; }
        try
        {
            await LocalPlayerDb.GetAsync();
            var me = await api.GetLeagueMeAsync();
            var cards = await api.GetItemCountAsync(GameApi.LeagueSimCardId);
            _info.Text = me == null ? "Không đọc được league2/me."
                : $"{LocalPlayerDb.TeamName(me.TeamId)}, vòng kế tiếp {me.NextRound}/{me.MaxRound}. Thẻ mô phỏng giải đấu: {(cards?.ToString() ?? "không đọc được")}";
        }
        catch (Exception ex) { _info.Text = "Lỗi: " + ex.Message; }
    }

    private async Task ToggleAsync()
    {
        if (_cts != null) { _cts.Cancel(); _log("[Mùa giải] Đang dừng..."); return; }
        var api = _api();
        if (api == null) { MessageBox.Show("Hãy đăng nhập trước.", "Mùa giải", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (_otherBusy()) { MessageBox.Show("Đang chạy một việc khác (ép thẻ, mua, giao dịch), hãy dừng trước.", "Mùa giải", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        int n = (int)_rounds.Value;
        var ask = MessageBox.Show($"Giả lập {(n > 0 ? n + " vòng" : "đến hết mùa")} của chế độ Mùa giải (server tự tính tỉ số, kết quả và phần thưởng tính vào tài khoản). Tiếp tục?",
            "Mùa giải", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes) return;

        _cts = new CancellationTokenSource(); _btnRun.Text = "Dừng";
        var runner = new LeagueRunner(api, _log, s => BeginInvoke(() => _status.Text = s));
        try
        {
            var result = await runner.RunAsync(n, PaceFactor[Math.Clamp(_pace.SelectedIndex, 0, PaceFactor.Length - 1)], _cts.Token);
            _log("[Mùa giải] " + result); _status.Text = result;
        }
        finally { _cts?.Dispose(); _cts = null; _btnRun.Text = "Bắt đầu giả lập"; await LoadAsync(); }
    }
}
