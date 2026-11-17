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

    public AutoPickDialog(string outputText, double pace, IReadOnlyDictionary<int, (int A, int B)> rules, CycleOptions output)
    {
        _pace = pace; _rules = rules; _out = output;
        Text = "Tự quét cầu thủ lãi nhất rồi ép lên +6";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent; Font = Theme.Body; ClientSize = new Size(660, 560);
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
        bottom.Controls.Add(_cancel); bottom.Controls.Add(_ok);
        Controls.Add(t); Controls.Add(bottom); Controls.Add(info);
        AcceptButton = _ok; CancelButton = _cancel;
        Theme.Apply(this);
        _ok.Click += (_, _) =>
        {
        };
    }
}
