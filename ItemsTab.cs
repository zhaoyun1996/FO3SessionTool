namespace FO3SessionTool;

/// <summary>Opens the packs ("Mở thẻ": card packs give a player card) and the lucky EP cards ("Mở thẻ EP") held in the bag, one use per call, like the game's "Dùng" button.</summary>
public sealed class ItemsTab : UserControl
{
    private readonly Func<GameApi?> _api;
    private readonly Func<bool> _otherBusy;
    private readonly Action<string> _log;
    private readonly Action _walletChanged;

    private readonly Label _info = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Dim };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Dim };
    private readonly DarkButton _btnLoad = new() { Text = "Tải danh sách", Width = 150 };
    private readonly DarkCombo _packs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly DarkNumber _packQty = new() { Minimum = 0, Maximum = 100000, Value = 0, Width = 90 };
    private readonly DarkButton _btnPack = new() { Text = "Mở thẻ", Width = 190 };
    private readonly Label _epInfo = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly DarkNumber _epQty = new() { Minimum = 0, Maximum = 100000, Value = 0, Width = 90 };
    private readonly DarkButton _btnEp = new() { Text = "Mở thẻ EP", Width = 190 };
    private readonly DarkCombo _pace = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private static readonly double[] PaceFactor = { 1.8, 1.0, 0.5, 0.25 };

    private List<GameApi.BagItem> _bag = new();
    private List<GameApi.BagItem?> _packRows = new();   // null = "Tất cả các gói"
    private CancellationTokenSource? _cts;

    public ItemsTab(Func<GameApi?> api, Func<bool> otherBusy, Action<string> log, Action walletChanged)
    {
        _api = api; _otherBusy = otherBusy; _log = log; _walletChanged = walletChanged;
        Dock = DockStyle.Fill;
        _pace.Items.AddRange(new object[] { "Chậm, thong thả (×1.8)", "Bình thường (×1)", "Nhanh (×0.5)", "Rất nhanh (×0.25)" });
        _pace.SelectedIndex = 1;

        var col = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4) };
        Label L(string t, int top = 10) => new() { Text = t, AutoSize = true, Margin = new Padding(3, top, 3, 2) };
        var note = new Label
        {
            AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = Theme.Dim,
            Text = "Mở các vật phẩm trong túi bằng đúng lệnh \"Dùng\" của game: gói thẻ cho ra 1 thẻ cầu thủ, Thẻ EP may mắn cho ra từ 1.000 đến 50.000 EP. " +
                   "Mỗi lần mở dùng hết 1 vật phẩm và không hoàn lại. Gói thẻ cần còn chỗ trong kho cầu thủ.",
        };
        col.Controls.Add(note);
        var row0 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        _btnLoad.Margin = new Padding(3);
        row0.Controls.Add(_btnLoad);
        col.Controls.Add(row0);
        _info.Margin = new Padding(3, 6, 3, 0);
        col.Controls.Add(_info);

        col.Controls.Add(L("Mở thẻ (gói thẻ cầu thủ)", 14));
        col.Controls.Add(_packs);
        var rowP = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        var lblP = new Label { Text = "Số lượng mở (0 = hết):", AutoSize = true, Margin = new Padding(3, 8, 6, 0) };
        _packQty.Margin = new Padding(3); _btnPack.Margin = new Padding(12, 3, 3, 3);
        rowP.Controls.Add(lblP); rowP.Controls.Add(_packQty); rowP.Controls.Add(_btnPack);
        col.Controls.Add(rowP);

        col.Controls.Add(L("Mở thẻ EP (Thẻ EP may mắn)", 16));
        col.Controls.Add(_epInfo);
        var rowE = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        var lblE = new Label { Text = "Số lượng mở (0 = hết):", AutoSize = true, Margin = new Padding(3, 8, 6, 0) };
        _epQty.Margin = new Padding(3); _btnEp.Margin = new Padding(12, 3, 3, 3);
        rowE.Controls.Add(lblE); rowE.Controls.Add(_epQty); rowE.Controls.Add(_btnEp);
        col.Controls.Add(rowE);

        col.Controls.Add(L("Tốc độ", 16)); col.Controls.Add(_pace);
        _status.Margin = new Padding(3, 12, 3, 0);
        col.Controls.Add(_status);
        Controls.Add(col);

        _btnLoad.Click += async (_, _) => await LoadAsync();
        _btnPack.Click += async (_, _) => await ToggleAsync(GameApi.KindPlayerPack);
        _btnEp.Click += async (_, _) => await ToggleAsync(GameApi.KindEpLottery);
        RefreshControls();
    }

    public bool Running => _cts != null;
    public void StopRun() => _cts?.Cancel();

    public void SetLoggedIn(bool on)
    {
        if (!on) { _cts?.Cancel(); _bag = new(); _info.Text = ""; _status.Text = ""; Fill(); }
    }

    private void RefreshControls()
    {
        bool running = _cts != null;
        _btnLoad.Enabled = !running;
        _packs.Enabled = _packQty.Enabled = !running;
        _epQty.Enabled = !running;
        _btnPack.Enabled = running ? _btnPack.Text == "Dừng" : Count(GameApi.KindPlayerPack) > 0;
        _btnEp.Enabled = running ? _btnEp.Text == "Dừng" : Count(GameApi.KindEpLottery) > 0;
    }

    private int Count(string kind) => _bag.Where(b => b.Kind == kind).Sum(b => b.Count);

    private void Fill()
    {
        _packRows = new List<GameApi.BagItem?> { null };
        _packRows.AddRange(_bag.Where(b => b.Kind == GameApi.KindPlayerPack).OrderBy(b => b.Name));
        _packs.Items.Clear();
        foreach (var p in _packRows) _packs.Items.Add(p == null ? $"Tất cả các gói ({Count(GameApi.KindPlayerPack)})" : $"{p.Name} ×{p.Count}");
        _packs.SelectedIndex = 0;
        int ep = Count(GameApi.KindEpLottery);
        _epInfo.Text = ep > 0 ? $"Đang có {ep} Thẻ EP may mắn." : "Không có Thẻ EP may mắn trong túi.";
        RefreshControls();
    }

    public async Task LoadAsync()
    {
        var api = _api();
        if (api == null) { _info.Text = "Hãy đăng nhập."; return; }
        try
        {
            _bag = await api.GetBagItemsAsync();
            int packs = Count(GameApi.KindPlayerPack), ep = Count(GameApi.KindEpLottery);
            _info.Text = $"Trong túi: {packs} gói thẻ cầu thủ, {ep} Thẻ EP may mắn.";
            Fill();
        }
        catch (Exception ex) { _info.Text = "Lỗi: " + ex.Message; }
    }

    private async Task ToggleAsync(string kind)
    {
        if (_cts != null) { _cts.Cancel(); _log("[Mở vật phẩm] Đang dừng..."); return; }
        var api = _api();
        if (api == null) { MessageBox.Show("Hãy đăng nhập trước.", "Mở vật phẩm", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (_otherBusy()) { MessageBox.Show("Đang chạy một việc khác (ép thẻ, mua, giao dịch, mùa giải), hãy dừng trước.", "Mở vật phẩm", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        bool isPack = kind == GameApi.KindPlayerPack;
        var stacks = isPack
            ? (_packs.SelectedIndex > 0 && _packs.SelectedIndex < _packRows.Count ? new List<GameApi.BagItem> { _packRows[_packs.SelectedIndex]! } : _bag.Where(b => b.Kind == kind).ToList())
            : _bag.Where(b => b.Kind == kind).ToList();
        int have = stacks.Sum(s => s.Count);
        if (have <= 0) { MessageBox.Show("Không có vật phẩm nào để mở. Bấm \"Tải danh sách\" để cập nhật.", "Mở vật phẩm", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        int want = (int)(isPack ? _packQty.Value : _epQty.Value);
        int total = want <= 0 ? have : Math.Min(want, have);
        string what = isPack ? "thẻ" : "Thẻ EP may mắn";
        var ask = MessageBox.Show($"Mở {total} {what}? Mỗi lần mở dùng hết 1 vật phẩm, không hoàn lại.", "Mở vật phẩm", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes) return;

        _cts = new CancellationTokenSource();
        (isPack ? _btnPack : _btnEp).Text = "Dừng";
        RefreshControls();
        double pace = PaceFactor[Math.Clamp(_pace.SelectedIndex, 0, PaceFactor.Length - 1)];
        string result;
        try { result = await OpenAsync(api, stacks, total, isPack, pace, _cts.Token); }
        catch (Exception ex) { result = "Lỗi: " + ex.Message; }
        _log("[Mở vật phẩm] " + result); _status.Text = result;
        _cts?.Dispose(); _cts = null;
        _btnPack.Text = "Mở thẻ"; _btnEp.Text = "Mở thẻ EP";
        await LoadAsync();
        _walletChanged();
    }

    private async Task<string> OpenAsync(GameApi api, List<GameApi.BagItem> stacks, int total, bool isPack, double pace, CancellationToken ct)
    {
        int opened = 0, failures = 0;
        string stop = "";
        _log($"[Mở vật phẩm] Bắt đầu mở {total} {(isPack ? "gói thẻ" : "Thẻ EP may mắn")}.");
        try
        {
            foreach (var st in stacks)
            {
                for (int i = 0; i < st.Count && opened < total && stop.Length == 0; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = await api.UseItemAsync(st.Id, ct);
                    if (r.Ok)
                    {
                        failures = 0; opened++;
                        _log(r.Spid > 0
                            ? $"[Mở vật phẩm] {st.Name}: ra thẻ {await PlayerNames.DescribeAsync(r.Spid, null, r.Grade)} ({opened}/{total})."
                            : $"[Mở vật phẩm] {st.Name}: {GameApi.Trim(r.Raw)} ({opened}/{total})");
                        BeginInvoke(() => _status.Text = $"Đã mở {opened}/{total}...");
                        await Pacing.Wait(0.8, 1.6, pace, ct);
                        continue;
                    }
                    if (GameApi.IsStorageFull(r.Raw)) { stop = "Hết kho: kho cầu thủ đã đầy (\"Exceed plyer limit\"). Hãy bán/xóa bớt cầu thủ rồi mở tiếp."; break; }
                    if (r.Raw.Contains("NOT_SIGNED")) { stop = "Phiên đăng nhập đã hết hạn."; break; }
                    _log($"[Mở vật phẩm] {st.Name}: mở thất bại ({GameApi.Trim(r.Raw)}).");
                    if (++failures >= 3) { stop = "Thất bại 3 lần liên tiếp, dừng."; break; }
                    await Pacing.Wait(2, 4, pace, ct);
                    i--;   // try the same unit again
                }
                if (stop.Length > 0 || opened >= total) break;
            }
        }
        catch (OperationCanceledException) { stop = "Bạn đã dừng."; }
        return (stop.Length > 0 ? stop + " " : "Xong. ") + $"Đã mở {opened} {(isPack ? "gói thẻ" : "Thẻ EP may mắn")}.";
    }
}
