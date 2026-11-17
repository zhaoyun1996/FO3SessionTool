namespace FO3SessionTool;

/// <summary>Settings of the auto mode: every cycle it scans the market, picks the most profitable player to build to +6 / +7 and sell to the system, and runs for the interval.</summary>
public sealed class AutoPickDialog : Form
{
    private readonly DarkCheck _use6 = new() { Text = "Ép đến +6", Checked = true, AutoSize = true };
    private readonly DarkCheck _use7 = new() { Text = "Ép đến +7", Checked = true, AutoSize = true };
    private readonly DarkNumber _minPct = new() { Minimum = 0, Maximum = 200, Value = 5, Width = 90 };
    private readonly DarkNumber _interval = new() { Minimum = 3, Maximum = 240, Value = 10, Width = 90 };
    private readonly DarkNumber _pool = new() { Minimum = 20, Maximum = 3000, Value = 400, Width = 90, Increment = 50 };
    private readonly DarkNumber _loss = new() { Minimum = 1, Maximum = 50, Value = 3, Width = 90 };
    private readonly DarkNumber _stopBelow = new() { Minimum = 0, Maximum = 1_000_000_000_000M, Value = 0, ThousandsSeparator = true, Increment = 100_000_000, Width = 170 };
    private readonly DarkButton _ok = new() { Text = "Bắt đầu", Width = 120, Tag = "primary", DialogResult = DialogResult.OK };
    private readonly DarkButton _cancel = new() { Text = "Hủy", Width = 90, DialogResult = DialogResult.Cancel };
    private readonly IReadOnlySet<int>? _seasons;
    private readonly double _pace;
    private readonly IReadOnlyDictionary<int, (int A, int B)> _rules;

    public AutoSettings Result => new(_use6.Checked, _use7.Checked, (double)_minPct.Value / 100.0, (int)_interval.Value, (int)_pool.Value, _seasons, _pace,
        (long)_stopBelow.Value, _rules, (int)_loss.Value);

    public AutoPickDialog(IReadOnlySet<int>? seasons, string seasonText, double pace, IReadOnlyDictionary<int, (int A, int B)> rules)
    {
        _seasons = seasons; _pace = pace; _rules = rules;
        Text = "Tự quét cầu thủ lãi nhất rồi ép và bán";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent; Font = Theme.Body; ClientSize = new Size(640, 480);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        var info = new Label
        {
            Dock = DockStyle.Top, Height = 168, Padding = new Padding(12, 10, 12, 0),
            Text = "Mỗi chu kỳ: tool quét giá chợ, chọn cầu thủ có lãi cao nhất khi mua thẻ +1 rẻ nhất, ép lên +6 / +7 rồi đăng bán ở giá gợi ý của game " +
                   "(hệ thống thu mua sau 1-15 phút), chạy hết thời gian chu kỳ, rồi quét lại. Lãi = (giá gợi ý − thuế bán − phí đăng) − vốn mua − phí ép ước tính, chia cho vốn. " +
                   "Lệnh bán chưa ai mua mà giá cao hơn giá gợi ý hiện tại sẽ được đăng lại ở giá mới. Thẻ ép dở của cầu thủ cũ vẫn nằm trong kho. " +
                   "Chế độ này tự đặt: mức đích, bán cho hệ thống, nhận tiền tự động. Các ô khác của tab (Nguồn thẻ, Cách chạy, Đầu ra, Nhận tiền, Đổi từ, Dừng khi EP dưới) không dùng; " +
                   "luật ghép, tốc độ và mùa \"Ưu tiên\" của tab vẫn được dùng.",
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
        var lv = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; lv.Controls.Add(_use6); lv.Controls.Add(_use7);
        Add("Mức đích", lv);
        Add("Lãi tối thiểu để chạy (% vốn)", _minPct);
        Add("Quét lại sau mỗi (phút)", _interval);
        Add("Số cầu thủ mạnh nhất để quét", _pool);
        Add("Dừng sau số thẻ lỗ / không đáng bán liên tiếp", _loss);
        Add("Dừng khi EP còn dưới (0 = tắt)", _stopBelow);
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var seasonLbl = new Label { Text = "Mùa quét: " + seasonText, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Dim };
        t.Controls.Add(seasonLbl, 0, row); t.SetColumnSpan(seasonLbl, 2);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 8, 12, 0) };
        bottom.Controls.Add(_cancel); bottom.Controls.Add(_ok);
        Controls.Add(t); Controls.Add(bottom); Controls.Add(info);
        AcceptButton = _ok; CancelButton = _cancel;
        Theme.Apply(this);
        _ok.Click += (_, _) =>
        {
            if (!_use6.Checked && !_use7.Checked)
            {
                MessageBox.Show("Chọn ít nhất một mức đích.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };
    }
}
