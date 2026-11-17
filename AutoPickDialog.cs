namespace FO3SessionTool;

/// <summary>Settings of the auto mode: every cycle it scans the market, picks the most profitable player to build to +6 / +7 and sell to the system, and runs for the interval.</summary>
public sealed class AutoPickDialog : Form
{
    private readonly DarkNumber _minPct = new() { Minimum = 0, Maximum = 200, Value = 5, Width = 90 };
    private readonly DarkNumber _interval = new() { Minimum = 3, Maximum = 240, Value = 10, Width = 90 };
    private readonly DarkNumber _pool = new() { Minimum = 20, Maximum = 3000, Value = 400, Width = 90, Increment = 50 };
    private readonly DarkNumber _loss = new() { Minimum = 1, Maximum = 50, Value = 3, Width = 90 };
    private readonly DarkNumber _parallel = new() { Minimum = 1, Maximum = 8, Value = 3, Width = 90 };
    private readonly DarkNumber _minProfit = new() { Minimum = 0, Maximum = 5000, Value = 20, Width = 90, Increment = 10 };
    private readonly DarkCheck _rankProfit = new() { Text = "Xếp theo lãi tuyệt đối (tắt = xếp theo %)", Checked = true, AutoSize = true };
    private readonly DarkTextBox _seasonsBox = new() { Width = 250 };
    private readonly DarkButton _ok = new() { Text = "Bắt đầu", Width = 120, Tag = "primary", DialogResult = DialogResult.OK };
    private readonly DarkButton _cancel = new() { Text = "Hủy", Width = 90, DialogResult = DialogResult.Cancel };
    private readonly double _pace;
    private readonly IReadOnlyDictionary<int, (int A, int B)> _rules;

    private readonly CycleOptions _out;
    private readonly GameApi? _api;
    private readonly DarkButton _suggest = new() { Text = "Gợi ý từ thị trường", Width = 170 };
    private readonly Label _suggestInfo = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Theme.Dim };
    private CancellationTokenSource? _scanCts;

    /// <summary>Scans the real market with permissive thresholds and fills the fields with values that fit what it found: the lowest profit / % the best few ideas still reach, how many of them fit the budget, which seasons they come from.</summary>
    private async Task SuggestAsync()
    {
        if (_api == null) { MessageBox.Show("Hãy đăng nhập trước.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        _suggest.Enabled = false; _ok.Enabled = false;
        _scanCts = new CancellationTokenSource();
        try
        {
            var probe = Result with { MinPct = 0, MinProfit = 0, Seasons = Result.Seasons };
            var list = await AutoPicker.ScanAsync(_api, probe, t => { try { _suggestInfo.Text = t; } catch { } }, _scanCts.Token);
            long budget = (_api.LastWallet?.Ep ?? 0) - _out.StopBelowEp;
            var ok = list.Where(c => c.Profit > 0 && c.Cost <= budget).ToList();
            if (ok.Count == 0) { _suggestInfo.Text = "Thị trường hiện không có phương án nào có lãi (sau thuế, phí) trong vốn hiện có. Giữ nguyên thông số."; return; }
            var byProfit = ok.OrderByDescending(c => c.Profit).ToList();
            var top = byProfit.Take(8).ToList();
            // the thresholds sit at the 5th best idea so that about 5 ideas always qualify
            var ref5 = byProfit[Math.Min(4, byProfit.Count - 1)];
            long minProfit = Math.Max(5_000_000L, ref5.Profit / 5_000_000L * 5_000_000L);
            double minPct = Math.Max(0.02, Math.Floor(Math.Min(ok.OrderByDescending(c => c.Pct).Take(5).Min(c => c.Pct), ref5.Pct) * 100) / 100);
            var qualified = byProfit.Where(c => c.Profit >= minProfit && c.Pct >= minPct).ToList();
            int par = 1; long spent = 0;
            foreach (var c in qualified) { if (par > 5 || spent + c.Cost > budget * 6 / 10) break; spent += c.Cost; par++; }
            par = Math.Clamp(Math.Max(1, par - 1), 1, 5);
            var seasons = top.Select(c => (int)(c.Spid / 1_000_000)).Distinct().OrderBy(x => x).ToList();
            _minProfit.Value = Math.Min(_minProfit.Maximum, minProfit / 1_000_000m);
            _minPct.Value = Math.Min(_minPct.Maximum, (decimal)Math.Round(minPct * 100));
            _parallel.Value = par;
            _rankProfit.Checked = true;
            _seasonsBox.Text = string.Join(", ", seasons);
            var names = new List<string>();
            foreach (var c in top.Take(5)) names.Add($"  {await PlayerNames.DescribeAsync(c.Spid)} +{c.Target}: vốn {c.Cost:N0}, lãi ≈{c.Profit:N0} ({c.Pct:P1})");
            var lines = string.Join("\n", names);
            _suggestInfo.Text = $"Đã điền theo {list.Count} phương án quét được ({ok.Count} có lãi, vốn dùng được {budget:N0} EP). Lãi tối thiểu {minProfit / 1_000_000:N0} triệu, {minPct:P0}, chạy song song {par}, mùa {string.Join(", ", seasons)}.\nTốt nhất:\n{lines}";
        }
        catch (OperationCanceledException) { _suggestInfo.Text = "Đã hủy quét."; }
        catch (Exception ex) { _suggestInfo.Text = "Lỗi khi quét: " + ex.Message; }
        finally { _suggest.Enabled = true; _ok.Enabled = true; }
    }

    private static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FO3SessionTool", "auto-settings.json");

    /// <summary>Remembers the last values of this dialog so the next opening starts from them.</summary>
    private sealed record Saved(decimal MinPct, decimal Interval, decimal Pool, decimal Loss, decimal Parallel, decimal MinProfit, bool RankProfit, string Seasons);

    private void LoadSaved()
    {
        try
        {
            var v = System.Text.Json.JsonSerializer.Deserialize<Saved>(File.ReadAllText(SettingsFile));
            if (v == null) return;
            static decimal Clamp(DarkNumber n, decimal x) => Math.Min(n.Maximum, Math.Max(n.Minimum, x));
            _minPct.Value = Clamp(_minPct, v.MinPct); _interval.Value = Clamp(_interval, v.Interval); _pool.Value = Clamp(_pool, v.Pool);
            _loss.Value = Clamp(_loss, v.Loss); _parallel.Value = Clamp(_parallel, v.Parallel); _minProfit.Value = Clamp(_minProfit, v.MinProfit);
            _rankProfit.Checked = v.RankProfit; _seasonsBox.Text = v.Seasons ?? "";
        }
        catch { }
    }

    private void SaveSaved()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, System.Text.Json.JsonSerializer.Serialize(new Saved(_minPct.Value, _interval.Value, _pool.Value, _loss.Value, _parallel.Value, _minProfit.Value, _rankProfit.Checked, _seasonsBox.Text)));
        }
        catch { }
    }

    public AutoSettings Result
    {
        get
        {
            var typed = new HashSet<int>();
            foreach (var part in _seasonsBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out var n) && n > 0) typed.Add(n);
            return new(true, false, (double)_minPct.Value / 100.0, (int)_interval.Value, (int)_pool.Value, typed.Count > 0 ? typed : null, _pace, _rules, (int)_loss.Value, _out, (int)_parallel.Value, (long)_minProfit.Value * 1_000_000L, _rankProfit.Checked);
        }
    }

    public AutoPickDialog(string outputText, double pace, IReadOnlyDictionary<int, (int A, int B)> rules, CycleOptions output, GameApi? api = null)
    {
        _api = api; _pace = pace; _rules = rules; _out = output;
        Text = "Tự quét cầu thủ lãi nhất rồi ép lên +6";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent; Font = Theme.Body; ClientSize = new Size(660, 760);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        var info = new Label
        {
            Dock = DockStyle.Top, Height = 140, Padding = new Padding(12, 10, 12, 0),
            Text = "Mỗi chu kỳ: tool quét giá chợ, chọn cầu thủ có lãi cao nhất khi mua thẻ +1 rẻ nhất rồi ép lên +6, chạy hết thời gian chu kỳ, rồi quét lại. " +
                   "Tự quét chỉ lo đầu vào và ép. Sau khi ép xong làm gì (bán, giao dịch, giữ thẻ, nhận tiền, dừng khi EP dưới) theo đúng các ô ở tab: " + outputText + ". " +
                   "Luật ghép, tốc độ và mức EP dừng của tab cũng được dùng. Thẻ ép dở của cầu thủ cũ vẫn nằm trong kho.",
        };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 8, 12, 8) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int row = 0;
        void Add(string label, Control c)
        {
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            t.Controls.Add(new Label { Text = label, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            c.Anchor = AnchorStyles.Left; t.Controls.Add(c, 1, row++);
        }
        Add("Lãi tối thiểu để chạy (% vốn)", _minPct);
        Add("Số cầu thủ chạy song song", _parallel);
        Add("Lãi tối thiểu mỗi thẻ (triệu EP)", _minProfit);
        Add("Cách xếp hạng", _rankProfit);
        Add("Quét lại sau mỗi (phút)", _interval);
        Add("Số cầu thủ mạnh nhất để quét", _pool);
        Add("Dừng sau số thẻ lỗ / không đáng bán liên tiếp", _loss);
        Add("Chỉ quét các mùa (số, cách nhau dấu phẩy; trống = tất cả)", _seasonsBox);
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 8, 12, 0) };
        bottom.Controls.Add(_cancel); bottom.Controls.Add(_ok); bottom.Controls.Add(_suggest);
        _suggest.Click += async (_, _) => await SuggestAsync();
        FormClosing += (_, _) => { try { _scanCts?.Cancel(); } catch { } };
        _suggestInfo.Height = 150; _suggestInfo.Dock = DockStyle.Bottom;
        Controls.Add(t); Controls.Add(_suggestInfo); Controls.Add(bottom); Controls.Add(info);
        AcceptButton = _ok; CancelButton = _cancel;
        LoadSaved();
        Theme.Apply(this);
        _ok.Click += (_, _) => SaveSaved();
    }
}
