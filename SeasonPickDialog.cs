namespace FO3SessionTool;

/// <summary>Pick the card seasons to prefer when trading (tick any number). Only seasons the server has in the game are listed when known.</summary>
public sealed class SeasonPickDialog : Form
{
    private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true, BorderStyle = BorderStyle.None, IntegralHeight = false };
    private readonly List<int> _seasons;

    public HashSet<int> Selected { get; private set; }

    public SeasonPickDialog(IEnumerable<int> seasons, Func<int, string> label, HashSet<int> current)
    {
        _seasons = seasons.Distinct().OrderBy(s => s).ToList();
        Selected = new HashSet<int>(current);
        Text = "Ưu tiên mùa thẻ khi giao dịch";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Body;
        ClientSize = new Size(380, 460);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        var info = new Label
        {
            Dock = DockStyle.Top, Height = 62, Padding = new Padding(10, 8, 10, 0),
            Text = "Khi server đưa ra các phương án giao dịch, tool chọn phương án có cầu thủ thuộc mùa được tick (nếu có nhiều thì chọn tổng giá trị lớn nhất trong số đó). Không có mùa nào khớp thì chọn tổng lớn nhất như bình thường.",
        };
        foreach (var s in _seasons) _list.Items.Add(label(s), Selected.Contains(s));
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        var ok = new DarkButton { Text = "Lưu", Width = 100, Left = ClientSize.Width - 220, Top = 8 };
        var cancel = new DarkButton { Text = "Hủy", Width = 100, Left = ClientSize.Width - 110, Top = 8 };
        var clear = new DarkButton { Text = "Bỏ chọn hết", Width = 120, Left = 10, Top = 8 };
        clear.Click += (_, _) => { for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, false); };
        ok.Click += (_, _) =>
        {
            Selected = new HashSet<int>(Enumerable.Range(0, _seasons.Count).Where(i => _list.GetItemChecked(i)).Select(i => _seasons[i]));
            DialogResult = DialogResult.OK;
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        panel.Controls.AddRange(new Control[] { clear, ok, cancel });
        Controls.Add(_list); Controls.Add(panel); Controls.Add(info);
        AcceptButton = ok; CancelButton = cancel;
        Theme.Apply(this);
        _list.BackColor = Theme.Surface2; _list.ForeColor = Theme.Text;
    }
}
