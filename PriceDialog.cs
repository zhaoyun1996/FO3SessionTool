namespace FO3SessionTool;

/// <summary>
/// Price table of chosen players by upgrade level (+1 ... +10): the overall the card has at that level (the game's PLAYERGRADE bonus table) and what the
/// game and the market say about the price: the game's price range for the card (/transfer/price) and the cheapest listing (/transfer/search). Read only.
/// </summary>
public sealed class PriceDialog : Form
{
    public sealed record Entry(long Spid, string Name, int Season, int Overall);   // Overall = as shown for the +1 card

    private int MaxGrade = 10;   // the game's PLAYERGRADE table goes up to +10; replaced by the table's own maximum once read
    private readonly GameApi _api;
    private readonly List<Entry> _entries;
    private readonly Func<int, string> _seasonName;
    private readonly DataGridView _grid = new();
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(10, 6, 0, 0), ForeColor = Theme.Dim };
    private readonly CancellationTokenSource _cts = new();

    public PriceDialog(GameApi api, List<Entry> entries, Func<int, string> seasonName)
    {
        _api = api; _entries = entries; _seasonName = seasonName;
        Text = "Giá cầu thủ theo chỉ số và thẻ cộng";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Body;
        ClientSize = new Size(940, 560);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        var info = new Label
        {
            Dock = DockStyle.Top, Height = 48, Padding = new Padding(10, 8, 10, 0),
            Text = "Chỉ số = chỉ số tổng của thẻ ở mức + đó (bảng cộng chỉ số của game). Giá sàn / mặc định / trần là khung giá game cho phép đăng bán thẻ ở mức đó; " +
                   "\"Rẻ nhất đang bán\" là thẻ rẻ nhất hiện có trên chợ ở đúng mức + đó.",
        };
        Theme.StyleGrid(_grid);
        _grid.Dock = DockStyle.Fill; _grid.ReadOnly = true; _grid.AllowUserToAddRows = false; _grid.RowHeadersVisible = false;
        _grid.ColumnHeadersHeight = 30; _grid.RowTemplate.Height = 24;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text;
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Text;
        void Col(string name, string header, int w, bool fill = false, bool left = false)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, Width = w, SortMode = DataGridViewColumnSortMode.NotSortable,
                AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None, MinimumWidth = fill ? 160 : 20,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = left ? DataGridViewContentAlignment.MiddleLeft : DataGridViewContentAlignment.MiddleRight },
            });
        Col("name", "Cầu thủ", 200, true, true);
        Col("grade", "Thẻ", 50);
        Col("ovr", "Chỉ số", 70);
        Col("lower", "Giá sàn", 130);
        Col("def", "Giá mặc định", 130);
        Col("upper", "Giá trần", 130);
        Col("cheap", "Rẻ nhất đang bán", 140);
        Col("count", "Số thẻ bán", 90);
        Controls.Add(_grid); Controls.Add(_status); Controls.Add(info);
        Theme.Apply(this);
        _status.ForeColor = Theme.Dim;
        FormClosing += (_, _) => _cts.Cancel();
        Shown += async (_, _) => await LoadAsync();
    }

    private static string N(long v) => v > 0 ? v.ToString("N0") : "—";

    private async Task LoadAsync()
    {
        var ct = _cts.Token;
        try
        {
            _status.Text = "Đang đọc bảng cộng chỉ số của game...";
            var boosts = await _api.GetGradeBoostsAsync(ct);
            int Boost(int g) => boosts != null && boosts.TryGetValue(g, out var b) ? b : 0;
            if (boosts != null) MaxGrade = boosts.Keys.Max();
            int done = 0, total = _entries.Count * MaxGrade;
            foreach (var e in _entries)
            {
                string title = $"{e.Name} ({_seasonName(e.Season)})";
                List<Listing> market = new();
                try { market = await _api.SearchAsync(e.Spid, ct); } catch (OperationCanceledException) { throw; } catch { }
                for (int g = 1; g <= MaxGrade; g++)
                {
                    ct.ThrowIfCancellationRequested();
                    GameApi.SellPrice? sp = null;
                    try { sp = await _api.GetSellPriceAsync(e.Spid, g, ct); } catch (OperationCanceledException) { throw; } catch { }
                    var cheap = market.Where(l => l.Grade == g && l.Price > 0).OrderBy(l => l.Price).FirstOrDefault();
                    int cnt = market.Where(l => l.Grade == g).Sum(l => l.Count);
                    string ovr = boosts == null ? "?" : (e.Overall - Boost(1) + Boost(g)).ToString();
                    _grid.Rows.Add(g == 1 ? title : "", "+" + g, ovr, N(sp?.Lower ?? 0), N(sp?.Default ?? 0), N(sp?.Upper ?? 0), cheap == null ? "chưa có bán" : N(cheap.Price), cnt > 0 ? cnt.ToString() : "—");
                    _status.Text = $"Đang đọc giá... {++done}/{total}";
                    await Pacing.Wait(0.15, 0.4, 1.0, ct);
                }
                if (_grid.Rows.Count > 0) _grid.Rows[_grid.Rows.Count - MaxGrade].DefaultCellStyle.ForeColor = Theme.Text;
            }
            _status.Text = $"Xong: {_entries.Count} cầu thủ, mức +1 đến +{MaxGrade}.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "Lỗi: " + ex.Message; }
    }
}
