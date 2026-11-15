namespace FO3SessionTool;

/// <summary>The game's own league mode (mùa giải), simulated by the server round by round. Needs the season calendar, learned from a capture.</summary>
public sealed class LeagueTab : UserControl
{
    private readonly Func<GameApi?> _api;
    private readonly Func<bool> _otherBusy;
    private readonly Action<string> _log;

    private readonly Label _info = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _sched = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Dim };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Dim };
    private readonly DarkButton _btnLoad = new() { Text = "Tải trạng thái", Width = 150 };
    private readonly DarkButton _btnImport = new() { Text = "Nạp lịch từ bản ghi...", Width = 200 };
    private readonly DarkButton _btnRun = new() { Text = "Bắt đầu giả lập", Width = 190 };
    private readonly DarkNumber _rounds = new() { Minimum = 0, Maximum = 100, Value = 0, Width = 80 };
    private readonly DarkCombo _pace = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private static readonly double[] PaceFactor = { 1.8, 1.0, 0.5, 0.25 };

    private LeagueSchedule _schedule = LeagueSchedule.Load();
    private CancellationTokenSource? _cts;

    public LeagueTab(Func<GameApi?> api, Func<bool> otherBusy, Action<string> log)
    {
        _api = api; _otherBusy = otherBusy; _log = log;
        Dock = DockStyle.Fill;
        _pace.Items.AddRange(new object[] { "Chậm (×1.8)", "Bình thường", "Nhanh (×0.5)", "Rất nhanh (×0.25)" });
        _pace.SelectedIndex = 1;

        var col = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4) };
        Label L(string t, int top = 10) => new() { Text = t, AutoSize = true, Margin = new Padding(3, top, 3, 2) };
        var note = new Label
        {
            AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = Theme.Dim,
            Text = "Chế độ Mùa giải của game: mỗi vòng tool gọi đúng các lệnh game gọi, server tự tính tỉ số. Tool chưa biết lịch thi đấu (ngày, đội nhà, đội khách) " +
                   "của từng vòng, nên cần nạp từ file bản ghi (capture\\game_capture_redacted.txt) của một mùa đã giả lập trong game.",
        };
        col.Controls.Add(note);
        col.Controls.Add(L("Trạng thái mùa giải")); col.Controls.Add(_info);
        col.Controls.Add(L("Lịch thi đấu đã nạp")); col.Controls.Add(_sched);
        var row1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        _btnLoad.Margin = new Padding(3); _btnImport.Margin = new Padding(3);
        row1.Controls.Add(_btnLoad); row1.Controls.Add(_btnImport);
        col.Controls.Add(row1);
        col.Controls.Add(L("Số vòng giả lập (0 = đến hết mùa)")); col.Controls.Add(_rounds);
        col.Controls.Add(L("Tốc độ")); col.Controls.Add(_pace);
        _btnRun.Margin = new Padding(3, 16, 3, 0);
        col.Controls.Add(_btnRun);
        _status.Margin = new Padding(3, 10, 3, 0);
        col.Controls.Add(_status);
        Controls.Add(col);

        _btnLoad.Click += async (_, _) => await LoadAsync();
        _btnImport.Click += (_, _) => Import();
        _btnRun.Click += async (_, _) => await ToggleAsync();
        ShowSchedule();
    }

    public bool Running => _cts != null;
    public void StopRun() => _cts?.Cancel();
    public void SetLoggedIn(bool on) { if (!on) { _cts?.Cancel(); _info.Text = ""; _status.Text = ""; } }

    private void ShowSchedule() => _sched.Text = _schedule.Describe();

    public async Task LoadAsync()
    {
        var api = _api();
        if (api == null) { _info.Text = "Hãy đăng nhập."; return; }
        try
        {
            var me = await api.GetLeagueMeAsync();
            var cards = await api.GetItemCountAsync(GameApi.LeagueSimCardId);
            _info.Text = me == null ? "Không đọc được league2/me."
                : $"Đội {me.TeamId}, vòng kế tiếp {me.NextRound}/{me.MaxRound}. Thẻ mô phỏng giải đấu: {(cards?.ToString() ?? "không đọc được")}";
        }
        catch (Exception ex) { _info.Text = "Lỗi: " + ex.Message; }
    }

    private async void Import()
    {
        using var dlg = new OpenFileDialog { Title = "Chọn file bản ghi game", Filter = "Bản ghi (*.txt)|*.txt|Tất cả|*.*", CheckFileExists = true };
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "..", "capture"), Path.Combine(AppContext.BaseDirectory, "capture") })
            if (Directory.Exists(dir)) { dlg.InitialDirectory = Path.GetFullPath(dir); break; }
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        _btnImport.Enabled = false; _sched.Text = "Đang đọc file bản ghi (file lớn, có thể mất vài giây)...";
        try
        {
            string file = dlg.FileName;
            int n = await Task.Run(() => _schedule.MergeFromCapture(file));
            if (n == 0) { ShowSchedule(); MessageBox.Show("Không thấy lệnh league2/match/sim hay league2/match nào trong file này.", "Nạp lịch", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            _schedule.Save();
            ShowSchedule();
            _log($"[Mùa giải] Đã nạp lịch từ bản ghi: {_schedule.Describe()}.");
        }
        catch (Exception ex) { ShowSchedule(); MessageBox.Show("Không nạp được: " + ex.Message, "Nạp lịch", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _btnImport.Enabled = true; }
    }

    private async Task ToggleAsync()
    {
        if (_cts != null) { _cts.Cancel(); _log("[Mùa giải] Đang dừng..."); return; }
        var api = _api();
        if (api == null) { MessageBox.Show("Hãy đăng nhập trước.", "Mùa giải", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (_otherBusy()) { MessageBox.Show("Đang chạy một việc khác (ép thẻ, mua, giao dịch), hãy dừng trước.", "Mùa giải", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (_schedule.Rounds.Count == 0) { MessageBox.Show("Chưa có lịch thi đấu. Bấm nút \"Nạp lịch từ bản ghi...\" ở trên, chọn file capture\\game_capture_redacted.txt, đợi dòng \"Lịch thi đấu đã nạp\" hiện số vòng rồi chạy lại.", "Mùa giải", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        int n = (int)_rounds.Value;
        var ask = MessageBox.Show($"Giả lập {(n > 0 ? n + " vòng" : "đến hết mùa")} của chế độ Mùa giải (server tự tính tỉ số, kết quả và phần thưởng tính vào tài khoản). Tiếp tục?",
            "Mùa giải", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes) return;

        _cts = new CancellationTokenSource(); _btnRun.Text = "Dừng";
        var runner = new LeagueRunner(api, _schedule, _log, s => BeginInvoke(() => _status.Text = s));
        try
        {
            var result = await runner.RunAsync(n, PaceFactor[Math.Clamp(_pace.SelectedIndex, 0, PaceFactor.Length - 1)], _cts.Token);
            _log("[Mùa giải] " + result); _status.Text = result;
        }
        finally { _cts?.Dispose(); _cts = null; _btnRun.Text = "Bắt đầu giả lập"; await LoadAsync(); }
    }
}
