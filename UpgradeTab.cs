namespace FO3SessionTool;

public enum StepState { Pending, Running, Done, Error }

/// <summary>Everything the auto-upgrade run needs. Rules: target level -> (material A, material B).</summary>
/// SellAfter (one-shot run): list the cards that reached the target level on the market at the highest allowed price.
/// UseInventory: skip buying and merge the cards already in the storage. Pace: multiplier on the human-like waits.
public sealed record UpgradePlan(
    long Spid, string Name, int Quantity, long MaxPrice, int Target, IReadOnlyDictionary<int, (int A, int B)> Rules,
    bool UseInventory = false, double Pace = 1.0, bool SellAfter = false, bool CollectAfter = false);

/// <summary>"Ép thẻ tự động" tab: pick a player, set how many +1 cards to buy and the merge rules, then run.</summary>
public sealed class UpgradeTab : UserControl
{
    private const int MaxLevel = UpgradeRunner.MaxLevel;

    private readonly PlayerSearch _players;
    private readonly Action<string> _log;
    private int _searchVer, _detailVer;
    private bool _loggedIn, _running;

    // player
    private readonly DarkTextBox _name = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, PlaceholderText = "Tên cầu thủ, ví dụ: ronaldo" };
    private readonly DarkButton _btnFind = new() { Text = "Tìm", Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly DarkCombo _results = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, CustomItems = true };
    private Bitmap? _sprite;   // season badge sprite (badged.png)
    private readonly TextBox _spid = new();   // holds the picked SPID; not shown (the card carries the identity)
    private readonly PlayerCard _card = new() { Dock = DockStyle.Fill };

    // purchase
    private readonly DarkNumber _qty = new() { Minimum = 1, Maximum = 9999, Value = 16, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly DarkRadio _byCards = new() { Text = "Số thẻ +1", Checked = true, Margin = new Padding(3, 5, 14, 5) };
    private readonly DarkRadio _byPlayers = new() { Text = "Số cầu thủ", Margin = new Padding(3, 5, 3, 5) };
    private readonly DarkNumber _cnt = new() { Minimum = 1, Maximum = 999, Value = 1, Anchor = AnchorStyles.Left, Width = 130, Visible = false };
    private readonly Label _qtyLabel = new() { Text = "Số thẻ +1 mua", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 3, 0) };
    private readonly Label _qtyNote = new() { AutoSize = true, ForeColor = Theme.Dim, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 0, 0) };
    private readonly DarkNumber _maxPrice = new() { Minimum = 0, Maximum = 1_000_000_000_000M, ThousandsSeparator = true, Increment = 1000, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly DarkRadio[] _tgt = new DarkRadio[MaxLevel + 1];   // target level radios, +5 .. +10
    private readonly DarkRadio _modeBuy = new() { Text = "Mua mới", Checked = true, Margin = new Padding(3, 5, 14, 5) };
    private readonly DarkRadio _modeStock = new() { Text = "Thẻ trong kho", Margin = new Padding(3, 5, 3, 5) };
    private readonly DarkRadio _runOnce = new() { Text = "Một lần", Checked = true, Margin = new Padding(3, 5, 14, 5) };
    private readonly DarkRadio _runLoop = new() { Text = "Xoay vòng", Margin = new Padding(3, 5, 3, 5) };
    private readonly DarkRadio _outTrade = new() { Text = "Giao dịch", Checked = true };
    private readonly DarkRadio _outSell = new() { Text = "Bán thị trường", Margin = new Padding(3, 5, 3, 5) };
    private readonly DarkRadio _collectAuto = new() { Text = "Tự động", Checked = true };
    private readonly DarkRadio _collectManual = new() { Text = "Thủ công", Margin = new Padding(3, 5, 3, 5) };
    private readonly Label _outLabel = new() { Text = "Khi ép đủ mức", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 8, 0), Visible = false };
    private readonly Label _collectLabel = new() { Text = "Nhận tiền bán", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 8, 0), Visible = false };
    private readonly DarkNumber _tradeMin = new() { Minimum = 1, Maximum = 1_000_000_000, Value = 900_000_000, ThousandsSeparator = true, Increment = 10_000_000, Anchor = AnchorStyles.Left, Width = 160 };
    private readonly DarkNumber _stopBelow = new() { Minimum = 0, Maximum = 1_000_000_000_000M, Value = 0, ThousandsSeparator = true, Increment = 100_000_000, Anchor = AnchorStyles.Left, Width = 160 };
    private readonly Label _stopBelowLabel = new() { Text = "Dừng khi EP dưới", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 8, 0), Visible = false };
    private TableLayoutPanel _buyGrid = null!;
    private readonly Label _tradeMinLabel = new() { Text = "Đổi khi từ (EP)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 8, 0), Visible = false };
    private readonly DarkCombo _pace = new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private static readonly double[] PaceFactor = { 1.8, 1.0, 0.5 };

    // storage snapshot of the picked player (cards not in the lineup), index = grade

    /// <summary>Set by the main form: reads the storage (null while logged out).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Task<List<GameApi.OwnedCard>>>? InventoryProvider { get; set; }

    /// <summary>Set by the main form: listings of an SPID on the transfer market (for the +1 price on the card).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<long, Task<List<Listing>>>? MarketProvider { get; set; }

    private bool StockMode => _modeStock.Checked;

    // rules: index = target level (2..10)
    private readonly DarkCombo[] _ruleA = new DarkCombo[MaxLevel + 1];
    private readonly DarkCombo[] _ruleB = new DarkCombo[MaxLevel + 1];

    // preview + run
    private readonly Label[] _steps = new Label[MaxLevel + 1];   // Mua, Ép +2..+N, and (loop mode) Giao dịch last
    private readonly DarkProgress _bar = new() { Dock = DockStyle.Fill, Margin = new Padding(0) };
    private readonly Label _status = new() { Text = "Sẵn sàng.", AutoSize = true, ForeColor = Theme.Dim, Anchor = AnchorStyles.Left };
    private readonly DarkButton _btnRun = new() { Text = "▶  Thực hiện", Width = 140, Tag = "primary" };
    private readonly DarkButton _btnStop = new() { Text = "■  Dừng", Width = 100, Enabled = false };
    private readonly ToolTip _tip = new();

    public event Action<UpgradePlan>? RunRequested;
    public event Action<UpgradePlan, CycleOptions>? CycleRequested;   // loop mode: buy +1 cards non-stop, merge, trade   // loop mode: buy -> merge -> trade until stopped
    public event Action? StopRequested;

    public UpgradeTab(PlayerSearch players, Action<string> log)
    {
        _players = players;
        _log = log;
        Dock = DockStyle.Fill;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 262));   // block + Theme.Gap below it
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));   // rules
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));

        const int half = Theme.Gap / 2;
        Control Place(Control c, int col, int row, int span = 1)
        {
            c.Margin = new Padding(col == 0 ? 0 : half, 0, col + span >= 2 ? 0 : half, Theme.Gap);   // 10 px between blocks, both ways
            root.Controls.Add(c, col, row);
            if (span > 1) root.SetColumnSpan(c, span);
            return c;
        }
        Place(BuildPlayerBox(), 0, 0);
        Place(BuildBuyBox(), 1, 0);
        Place(BuildRuleBox(), 0, 1, 2);
        var bottom = BuildBottom();
        root.Controls.Add(bottom, 0, 2);
        root.SetColumnSpan(bottom, 2);
        Controls.Add(root);

        _pace.Items.AddRange(new object[] { "Chậm, thong thả (×1.8)", "Bình thường (×1)", "Nhanh (×0.5)" });
        _pace.SelectedIndex = 1;
        ResetRules();

        _btnFind.Click += async (_, _) => await SearchAsync(_name.Text);
        _name.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SearchAsync(_name.Text); } };
        _results.SelectedIndexChanged += (_, _) => OnPlayerPicked();
        _results.DrawItem += DrawResultItem;
        _modeStock.CheckedChanged += (_, _) => OnModeChanged();
        _runLoop.CheckedChanged += (_, _) => UpdateStepVisibility();
        _modeStock.CheckedChanged += (_, _) => UpdateStepVisibility();
        _outSell.CheckedChanged += (_, _) => { UpdateStepVisibility(); };
        _byPlayers.CheckedChanged += (_, _) => OnBuyByChanged();
        _cnt.ValueChanged += (_, _) => UpdateQtyNote();
        for (int l = 5; l <= MaxLevel; l++) if (l >= 0) { int lv = l; _tgt[lv].CheckedChanged += (_, _) => { if (!_tgt[lv].Checked) return; if (!StockMode) SyncQty(); UpdateStepVisibility(); }; }
        _btnRun.Click += (_, _) => OnRun();
        _btnStop.Click += (_, _) => StopRequested?.Invoke();

        UpdateStepVisibility();
        UpdateButtons();
    }

    private int TargetLevel { get { for (int l = 5; l <= MaxLevel; l++) if (_tgt[l].Checked) return l; return 5; } }

    // ---- layout: every row is Theme.RowH tall, every control is vertically centred ----

    private static TableLayoutPanel Grid(int cols, int rows)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = cols, RowCount = rows, Padding = new Padding(6, 2, 6, 2) };
        return t;
    }

    private Control BuildPlayerBox()
    {
        var box = new DarkGroup { Text = "1. Cầu thủ", Dock = DockStyle.Fill };
        var t = Grid(3, 3);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowH));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowH));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        t.Controls.Add(Lbl("Tên"), 0, 0);
        t.Controls.Add(_name, 1, 0);
        t.Controls.Add(_btnFind, 2, 0);
        t.Controls.Add(Lbl("Kết quả"), 0, 1);
        t.Controls.Add(_results, 1, 1);
        t.SetColumnSpan(_results, 2);
        t.Controls.Add(_card, 0, 2);
        t.SetColumnSpan(_card, 3);
        box.Controls.Add(t);
        return box;
    }

    private Control BuildBuyBox()
    {
        var box = new DarkGroup { Text = "2. Nguồn thẻ", Dock = DockStyle.Fill };
        var t = Grid(2, 11);
        _buyGrid = t;
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 10; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowH));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // first radio of each pair gets the same width so the second column lines up across the rows
        foreach (var r in new[] { _modeBuy, _byCards, _runOnce }) { r.AutoSize = false; r.Width = 92; r.Margin = new Padding(3, 5, 0, 5); }
        t.Controls.Add(Lbl("Nguồn thẻ"), 0, 0);
        var modes = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        modes.Controls.Add(_modeBuy);
        modes.Controls.Add(_modeStock);
        t.Controls.Add(modes, 1, 0);
        t.Controls.Add(Lbl("Mua theo"), 0, 2);
        var by = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        by.Controls.Add(_byCards);
        by.Controls.Add(_byPlayers);
        t.Controls.Add(by, 1, 2);
        t.Controls.Add(_qtyLabel, 0, 3);
        _qty.Anchor = AnchorStyles.Left; _qty.Width = 130;
        var qtyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(3, 0, 3, 0), Padding = Padding.Empty, Anchor = AnchorStyles.Left };
        _qty.Margin = Padding.Empty; _cnt.Margin = Padding.Empty;
        qtyRow.Controls.Add(_qty);
        qtyRow.Controls.Add(_cnt);
        qtyRow.Controls.Add(_qtyNote);
        t.Controls.Add(qtyRow, 1, 3);
        // no "max price per card" input: the ceiling is always the game's own upper price (resolved in MainForm when a run starts)
        t.RowStyles[4] = new RowStyle(SizeType.Absolute, 0);
        t.Controls.Add(Lbl("Tốc độ"), 0, 5);
        _pace.Anchor = AnchorStyles.Left; _pace.Width = 190;
        t.Controls.Add(_pace, 1, 5);
        t.Controls.Add(Lbl("Cách chạy"), 0, 1);
        var how = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        how.Controls.Add(_runOnce);
        how.Controls.Add(_runLoop);
        t.Controls.Add(how, 1, 1);
        t.Controls.Add(_outLabel, 0, 6);
        foreach (var r in new[] { _outTrade, _collectAuto }) { r.AutoSize = false; r.Width = 92; r.Margin = new Padding(3, 5, 0, 5); }
        var outRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        outRow.Controls.Add(_outTrade);
        outRow.Controls.Add(_outSell);
        t.Controls.Add(outRow, 1, 6);
        t.Controls.Add(_tradeMinLabel, 0, 7);
        _tradeMin.Margin = new Padding(3, 0, 3, 0);
        t.Controls.Add(_tradeMin, 1, 7);
        t.Controls.Add(_collectLabel, 0, 8);
        var colRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        colRow.Controls.Add(_collectAuto);
        colRow.Controls.Add(_collectManual);
        t.Controls.Add(colRow, 1, 8);
        t.Controls.Add(_stopBelowLabel, 0, 9);
        _stopBelow.Margin = new Padding(3, 0, 3, 0);
        t.Controls.Add(_stopBelow, 1, 9);
        _tip.SetToolTip(_outSell, "Thẻ vừa ép lên đúng mức đích được đăng bán ngay trên thị trường chuyển nhượng ở giá CAO NHẤT game cho phép (không giao dịch). Phí đăng 1% khi đăng, game giữ 20% khi bán xong.");
        _tip.SetToolTip(_collectManual, "Tool không nhận tiền: bạn tự nhận trong game (mục lệnh bán). Tiền chưa nhận thì chưa dùng được để mua tiếp.");
        _tip.SetToolTip(_collectAuto, "Thẻ bán xong thì tool tự bấm nhận tiền (chỉ lệnh bán của cầu thủ đang chọn). Xoay vòng: khi còn lệnh chờ người mua, tool không dừng dù EP đã hết. Một lần: đăng bán xong tool chờ tối đa 3 phút để nhận tiền.");
        _tip.SetToolTip(_stopBelow, "Không mua thêm nếu số EP còn lại sau khi mua sẽ dưới mức này; khi không còn gì ép/đổi được thì dừng. 0 = không đặt mức.");
        _tip.SetToolTip(_tradeMin, "Khi tổng giá trị các thẻ đích chưa đăng ký đạt mức này thì đổi hết (tối đa 1 tỷ EP mỗi lần giao dịch).");
        _tip.SetToolTip(_runLoop, "Xoay vòng: mua → ép → giao dịch. Mua và ép liên tục; khi tổng giá trị thẻ đích đạt 900 triệu - 1 tỷ EP thì đổi hết (chọn phương án lớn nhất). Lặp đến khi hết EP, lỗ 2 vòng liên tiếp hoặc bấm Dừng.");
        _tip.SetToolTip(_maxPrice, "EP; 0 = không giới hạn giá");
        box.Controls.Add(t);
        return box;
    }

    private Control BuildRuleBox()
    {
        var box = new DarkGroup { Text = "3. Rule ép thẻ - chọn mức ép đích ở hàng trên", Dock = DockStyle.Fill };
        var t = Grid(MaxLevel, 4);   // 1 label column + levels 2..MaxLevel
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        for (int lvl = 2; lvl <= MaxLevel; lvl++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / (MaxLevel - 1)));
        for (int i = 0; i < 4; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowH));

        t.Controls.Add(Lbl("Ép lên"), 0, 0);
        t.Controls.Add(Lbl("Nguyên liệu 1"), 0, 1);
        t.Controls.Add(Lbl("Nguyên liệu 2"), 0, 2);
        for (int lvl = 2; lvl <= MaxLevel; lvl++)
        {
            int col = lvl - 1;
            if (lvl >= 5)
            {
                _tgt[lvl] = new DarkRadio { Text = $"+{lvl}", Checked = lvl == 5, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 0, 0) };
                t.Controls.Add(_tgt[lvl], col, 0);
            }
            else
            {
                var head = new Label { Text = $"+{lvl}", AutoSize = true, Font = Theme.Bold, Anchor = AnchorStyles.Left, Margin = new Padding(27, 0, 0, 0) };
                t.Controls.Add(head, col, 0);
            }
            _ruleA[lvl] = RuleCombo(lvl);
            _ruleB[lvl] = RuleCombo(lvl);
            t.Controls.Add(_ruleA[lvl], col, 1);
            t.Controls.Add(_ruleB[lvl], col, 2);
        }

        var reset = new DarkButton { Text = "Mặc định", Width = 100, Anchor = AnchorStyles.Left };
        reset.Click += (_, _) => ResetRules();
        t.Controls.Add(reset, 0, 3);
        var note = Hint("Nguyên liệu phải thấp hơn thẻ đích. Mức đích +5 trở lên được chọn bằng nút tròn.");
        note.Margin = new Padding(3, 0, 3, 0);
        t.Controls.Add(note, 1, 3);
        t.SetColumnSpan(note, MaxLevel - 1);
        box.Controls.Add(t);
        return box;
    }

    private DarkCombo RuleCombo(int target)
    {
        var c = new DarkCombo { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 0, 3, 0) };
        for (int m = 1; m < target; m++) c.Items.Add($"+{m}");
        c.SelectedIndexChanged += (_, _) => { if (!StockMode && _tgt[5] != null && _tgt[MaxLevel] != null) SyncQty(); };
        return c;
    }

    private Control BuildBottom()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.ButtonH));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 14 + Theme.Gap));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var steps = new FlowLayoutPanel { Anchor = AnchorStyles.Left, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var names = new List<string> { "Mua" };
        for (int l = 2; l <= MaxLevel; l++) names.Add($"Ép +{l}");
        names.Add("Giao dịch");
        for (int i = 0; i < _steps.Length; i++)
        {
            _steps[i] = new Label { Text = names[i], AutoSize = false, Width = i == MaxLevel ? 80 : 56, Height = Theme.ButtonH, TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 0, 3, 0), Font = Theme.Bold };
            steps.Controls.Add(_steps[i]);
            SetStepState(i, StepState.Pending);
        }
        t.Controls.Add(steps, 0, 0);

        var buttons = new FlowLayoutPanel { Anchor = AnchorStyles.Right, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _btnRun.Margin = new Padding(0, 0, Theme.Gap, 0);
        _btnStop.Margin = Padding.Empty;
        buttons.Controls.Add(_btnRun);
        buttons.Controls.Add(_btnStop);
        t.Controls.Add(buttons, 1, 0);

        _bar.Margin = new Padding(0, Theme.Gap, 0, 0);
        t.Controls.Add(_bar, 0, 1);
        t.SetColumnSpan(_bar, 2);
        _status.Margin = new Padding(0);
        t.Controls.Add(_status, 0, 2);
        t.SetColumnSpan(_status, 2);
        return t;
    }

    private static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 8, 0) };
    private static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Theme.Dim, Anchor = AnchorStyles.Left, Margin = new Padding(6, 0, 3, 0) };

    // ---- rules & preview ------------------------------------------------

    public IReadOnlyDictionary<int, (int A, int B)> Rules
    {
        get
        {
            var d = new Dictionary<int, (int, int)>();
            for (int lvl = 2; lvl <= MaxLevel; lvl++) d[lvl] = (_ruleA[lvl].SelectedIndex + 1, _ruleB[lvl].SelectedIndex + 1);
            return d;
        }
    }

    private void ResetRules()
    {
        for (int lvl = 2; lvl <= MaxLevel; lvl++)
        {
            _ruleA[lvl].SelectedIndex = lvl - 2;   // +(N-1)
            _ruleB[lvl].SelectedIndex = lvl - 2;
        }
    }

    /// <summary>+1 cards needed for one card of each level under the current rules.</summary>
    private int[] Need()
    {
        var need = new int[MaxLevel + 1];
        need[1] = 1;
        for (int lvl = 2; lvl <= MaxLevel; lvl++)
        {
            int a = _ruleA[lvl].SelectedIndex + 1, b = _ruleB[lvl].SelectedIndex + 1;
            need[lvl] = need[a] + need[b];
        }
        return need;
    }

    /// <summary>Suggests how many +1 cards one card of the target level needs (the user can still change it).</summary>
    /// <summary>How many +1 cards to buy: typed directly, or players wanted x cards one player needs under the rules.</summary>
    private int BuyQty
    {
        get
        {
            if (!_byPlayers.Checked) return (int)_qty.Value;
            for (int lvl = 2; lvl <= MaxLevel; lvl++) if (_ruleA[lvl].SelectedIndex < 0 || _ruleB[lvl].SelectedIndex < 0) return (int)_qty.Value;
            return (int)_cnt.Value * Need()[TargetLevel];
        }
    }

    private void OnBuyByChanged()
    {
        bool players = _byPlayers.Checked;
        _qty.Visible = !players;
        _cnt.Visible = players;
        _qtyLabel.Text = players ? $"Số cầu thủ +{TargetLevel}" : "Số thẻ +1 mua";
        UpdateQtyNote();
    }

    private void UpdateQtyNote()
    {
        if (_qtyNote == null) return;
        if (!_byPlayers.Checked) { _qtyNote.Text = ""; return; }
        _qtyLabel.Text = $"Số cầu thủ +{TargetLevel}";
        _qtyNote.Text = $"= {BuyQty:N0} thẻ +1";
    }

    private void SyncQty()
    {
        for (int lvl = 2; lvl <= MaxLevel; lvl++) if (_ruleA[lvl].SelectedIndex < 0 || _ruleB[lvl].SelectedIndex < 0) return;
        var n = Need()[TargetLevel];
        if (n >= _qty.Minimum && n <= _qty.Maximum) _qty.Value = n;
        UpdateQtyNote();
    }

    private void OnModeChanged()
    {
        bool stock = StockMode;
        _qty.Enabled = _cnt.Enabled = !stock && !_running;
        _maxPrice.Enabled = !stock && !_running;
        _byCards.Enabled = _byPlayers.Enabled = !stock && !_running;
        _steps[0].Text = stock ? "Kho" : "Mua";
        _runLoop.Enabled = _runOnce.Enabled = !stock && !_running;
        if (stock) _runOnce.Checked = true;
        UpdateStepVisibility();
    }

    // ---- player search --------------------------------------------------

    private sealed record Item(PlayerInfo P)
    {
        public override string ToString() => $"{P.Name}  ·  Mùa {P.Season}  ·  {P.Position} {P.Overall}";
    }

    public async Task SearchAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) { SetStatus("Nhập tên cầu thủ.", Theme.Danger); return; }
        int ver = ++_searchVer;
        _btnFind.Enabled = false;
        _log($"[Tìm cầu thủ] Đang tìm \"{name}\" trên fifaaddict...");
        SetStatus("Đang tìm...", Theme.Dim);
        try
        {
            var list = (await _players.SearchAsync(name)).OrderByDescending(p => p.Overall).ThenByDescending(p => p.Spid).ToList();
            if (ver != _searchVer) return;
            _sprite ??= await _players.LoadImageAsync(PlayerSearch.SpriteUrl) as Bitmap;
            if (ver != _searchVer) return;
            _results.Items.Clear();
            foreach (var p in list) _results.Items.Add(new Item(p));
            SetStatus(list.Count == 0 ? $"Không tìm thấy \"{name}\"." : $"{list.Count} cầu thủ cho \"{name}\". Chọn một người trong danh sách.", Theme.Dim);
            _log($"[Tìm cầu thủ] \"{name}\": {list.Count} kết quả.");
            if (list.Count > 0) _results.SelectedIndex = 0;
        }
        catch (Exception ex) { SetStatus("Lỗi tìm cầu thủ: " + ex.Message, Theme.Danger); _log("[Tìm cầu thủ] LỖI: " + ex.Message); }
        finally { _btnFind.Enabled = true; }
    }

    /// <summary>Season icon (or "Mùa N" when the sprite is unavailable) + name + position/overall; no SPID.</summary>
    private void DrawResultItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _results.Items[e.Index] is not Item it) return;
        using (var bgb = new SolidBrush((e.State & DrawItemState.ComboBoxEdit) == 0 && (e.State & DrawItemState.Selected) != 0 ? Theme.Hover : Theme.Surface2))
            e.Graphics.FillRectangle(bgb, e.Bounds);
        var p = it.P;
        var g = e.Graphics;
        int x = e.Bounds.X + 4;
        if (_sprite != null && PlayerSearch.SeasonSpriteX.TryGetValue(p.Season, out var sx))
        {
            g.DrawImage(_sprite, new Rectangle(x, e.Bounds.Y + (e.Bounds.Height - 18) / 2, 18, 18), new Rectangle(sx, 0, 18, 18), GraphicsUnit.Pixel);
            x += 24;
        }
        else
        {
            var season = $"Mùa {p.Season}";
            TextRenderer.DrawText(g, season, _results.Font, new Point(x, e.Bounds.Y + 2), Theme.Dim, TextFormatFlags.NoPadding);
            x += TextRenderer.MeasureText(g, season, _results.Font, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding).Width + 8;
        }
        var text = $"{p.Name}  ·  {p.Position} {p.Overall}{(p.Limited ? "  ·  Limited" : "")}";
        TextRenderer.DrawText(g, text, _results.Font, new Point(x, e.Bounds.Y + 2), Theme.Text, TextFormatFlags.NoPadding);
    }

    private async void OnPlayerPicked()
    {
        if (_results.SelectedItem is not Item it) return;
        var p = it.P;
        _spid.Text = p.Spid.ToString();
        int ver = ++_detailVer;
        _card.Sprite = _sprite;
        _card.Clear("Đang tải thông tin cầu thủ...");
        _log($"[Ép thẻ] Đang tải thông tin {p.Name} (SPID {p.Spid}) từ fifaaddict...");
        try
        {
            var detailTask = _players.GetDetailAsync(p.Spid);
            var imgTask = LoadPortraitAsync(p);   // game CDN first (sharp 128x128), fifaaddict as fallback
            var d = await detailTask;
            if (ver != _detailVer) return;
            if (d == null) { _card.Clear($"{p.Name} · Mùa {p.Season}\nKhông đọc được trang chi tiết."); _log("[Ép thẻ] Không đọc được trang chi tiết cầu thủ."); }
            else
            {
                _card.Portrait = await imgTask;
                _card.Crest = d.CrestUrl.Length > 0 ? await _players.LoadImageAsync(d.CrestUrl) : null;
                _card.Flag = await LoadFlagAsync(d.NationCode);
                (_card.PriceText, _card.PriceNote) = await LoadPriceAsync(p.Spid);
                if (ver != _detailVer) return;
                _card.SetDetail(d);
                _log($"[Ép thẻ] Đã tải thông tin {d.Name} ({d.Nation}, {d.Club}).");
            }
        }
        catch (Exception ex)
        {
            if (ver == _detailVer) { _card.Clear($"{p.Name} · Mùa {p.Season}\nLỗi tải thông tin: {ex.Message}"); _log("[Ép thẻ] LỖI tải thông tin cầu thủ: " + ex.Message); }
        }
    }

    /// <summary>Called by the search tab: puts the player in the result list and selects him.</summary>
    /// <summary>Flag from flagcdn.com (the game's own flags are keyed by an id the game never sends us).</summary>
    private async Task<Image?> LoadFlagAsync(string code)
    {
        var iso = code switch { "_England" => "gb-eng", "_Scotland" => "gb-sct", "_Wales" => "gb-wls", "_Northern Ireland" => "gb-nir", _ => code };
        if (iso.Length < 2 || iso.StartsWith('_')) return null;
        _log($"[Ép thẻ] Đang tải cờ quốc gia ({iso})...");
        return await _players.LoadImageAsync($"https://flagcdn.com/w40/{iso}.png");
    }

    /// <summary>Cheapest +1 listing of the player on the game's transfer market.</summary>
    private async Task<(string Price, string Note)> LoadPriceAsync(long spid)
    {
        if (MarketProvider == null || !_loggedIn) return ("", "");
        _log($"[Ép thẻ] Đang xem giá thẻ +1 của SPID {spid} trên chợ chuyển nhượng...");
        try
        {
            var cheap = (await MarketProvider(spid)).Where(l => l.Grade == 1).OrderBy(l => l.Price).FirstOrDefault();
            if (cheap == null) { _log("[Ép thẻ] Chợ chưa có thẻ +1 nào đang bán."); return ("chưa có bán", ""); }
            _log($"[Ép thẻ] Giá thẻ +1 thấp nhất: {cheap.Price:N0} EP ({cheap.Count} thẻ đang bán).");
            return ($"{cheap.Price:N0} EP", $"{cheap.Count:N0} thẻ đang bán");
        }
        catch (Exception ex) { _log("[Ép thẻ] LỖI đọc giá chợ: " + ex.Message); return ("", ""); }
    }

    private async Task<Image?> LoadPortraitAsync(PlayerInfo p)
    {
        _log($"[Ép thẻ] Đang tải ảnh cầu thủ từ CDN của game (SPID {p.Spid})...");
        var img = await _players.LoadImageAsync($"http://cdn.3sea.online/cdn/externalAssets/players/p{p.Spid}.png");
        if (img != null) { _log($"[Ép thẻ] Đã tải ảnh từ CDN game ({img.Width}x{img.Height})."); return img; }
        _log("[Ép thẻ] CDN game không có ảnh này, dùng ảnh từ fifaaddict.");
        return await _players.LoadImageAsync(p.ImageUrl);
    }

    public async void PickPlayer(PlayerInfo p)
    {
        if (_sprite == null)
        {
            _log("[Ép thẻ] Đang tải bộ icon mùa giải...");
            _sprite = await _players.LoadImageAsync(PlayerSearch.SpriteUrl) as Bitmap;
        }
        _name.Text = p.Name;   // keep the name box in sync with the applied player
        _results.Items.Clear();
        _results.Items.Add(new Item(p));
        _results.SelectedIndex = 0;
    }

    // ---- run state ------------------------------------------------------

    public async void SetLoggedIn(bool value)
    {
        _loggedIn = value;
        UpdateButtons();
    }

    public async void SetRunning(bool value)
    {
        _running = value;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _btnRun.Enabled = _loggedIn && !_running;
        _btnStop.Enabled = _running;
        _tip.SetToolTip(_btnRun, _loggedIn ? "Lấy đủ thẻ (mua hoặc từ kho) rồi ép theo rule" : "Hãy đăng nhập trước");
        foreach (Control c in new Control[] { _name, _btnFind, _results, _modeBuy, _modeStock, _pace }) c.Enabled = !_running;
        _runOnce.Enabled = _runLoop.Enabled = !_running && !StockMode;
        _tradeMin.Enabled = _stopBelow.Enabled = _outTrade.Enabled = _outSell.Enabled = _collectAuto.Enabled = _collectManual.Enabled = !_running;
        foreach (var r in _tgt) if (r != null) r.Enabled = !_running;
        _qty.Enabled = _cnt.Enabled = !_running && !StockMode;
        _byCards.Enabled = _byPlayers.Enabled = !_running && !StockMode;
        _maxPrice.Enabled = !_running && !StockMode;
        foreach (var c in _ruleA.Concat(_ruleB)) if (c != null) c.Enabled = !_running;
    }

    /// <summary>Loop mode buys +1 cards non-stop, so the quantity rows go away; the trade threshold and the EP floor appear.</summary>
    private void ApplyLoopLayout()
    {
        if (_buyGrid == null) return;
        bool loop = _runLoop.Checked && !StockMode;
        void Row(int row, bool visible)
        {
            _buyGrid.RowStyles[row] = new RowStyle(SizeType.Absolute, visible ? Theme.RowH : 0);
            foreach (Control c in _buyGrid.Controls) if (_buyGrid.GetRow(c) == row) c.Visible = visible;
        }
        Row(2, !loop);   // "Mua theo"
        Row(3, !loop);   // quantity
        bool sell = _outSell.Checked;
        Row(6, true);            // after the merges: keep / sell (one-shot) or trade / sell (loop)
        Row(7, loop && !sell);   // trade threshold
        Row(8, sell);            // money of sold cards: collected by the tool or by hand
        Row(9, loop);
    }

    private void UpdateStepVisibility()
    {
        for (int i = 1; i < MaxLevel; i++) _steps[i].Visible = i + 1 <= TargetLevel;
        bool loopNow = _runLoop.Checked && !StockMode;
        _outLabel.Text = loopNow ? "Khi ép đủ mức" : "Khi ép xong";
        _outTrade.Text = loopNow ? "Giao dịch" : "Giữ thẻ";
        _steps[MaxLevel].Text = _outSell.Checked ? "Bán" : "Giao dịch";
        _steps[MaxLevel].Visible = loopNow || (_outSell.Checked && !loopNow);
        ApplyLoopLayout();
    }

    /// <summary>step 0 = buying, step k (1..4) = merging to +(k+1).</summary>
    public void SetStepState(int step, StepState st)
    {
        var l = _steps[step];
        (l.BackColor, l.ForeColor) = st switch
        {
            StepState.Running => (Theme.Warn, Color.FromArgb(30, 24, 0)),
            StepState.Done => (Theme.Success, Color.White),
            StepState.Error => (Theme.Danger, Color.White),
            _ => (Theme.Surface2, Theme.Dim),
        };
    }

    public void ResetSteps() { for (int i = 0; i < _steps.Length; i++) SetStepState(i, StepState.Pending); SetProgress(0, 1, "Sẵn sàng."); }

    public void SetProgress(int value, int max, string text)
    {
        _bar.Maximum = Math.Max(1, max);
        _bar.Value = Math.Clamp(value, 0, _bar.Maximum);
        SetStatus(text, Theme.Dim);
    }

    private void SetStatus(string text, Color c) { _status.Text = text; _status.ForeColor = c; }

    private void OnRun()
    {
        if (!long.TryParse(_spid.Text.Trim(), out var spid)) { SetStatus("Hãy chọn cầu thủ trước.", Theme.Danger); return; }
        var name = _results.SelectedItem is Item it && it.P.Spid == spid ? it.P.Name : $"SPID {spid}";
        double pace = PaceFactor[_pace.SelectedIndex < 0 ? 1 : _pace.SelectedIndex];
        bool loopMode = _runLoop.Checked && !StockMode;
        var plan = new UpgradePlan(spid, name, StockMode || loopMode ? 0 : BuyQty, StockMode ? 0 : (long)_maxPrice.Value,
            TargetLevel, Rules, StockMode, pace, SellAfter: !_runLoop.Checked && _outSell.Checked, CollectAfter: _collectAuto.Checked);
        if (_runLoop.Checked && !StockMode) CycleRequested?.Invoke(plan, new CycleOptions(TradeMin: (long)_tradeMin.Value, StopBelowEp: (long)_stopBelow.Value, SellMode: _outSell.Checked, AutoCollect: _collectAuto.Checked));
        else RunRequested?.Invoke(plan);
    }
}
