namespace FO3SessionTool;

public sealed class MainForm : Form
{
    private readonly Config _cfg;
    private readonly PlayerSearch _players = new();
    private GameApi? _api;
    private int _searchVer;
    private Bitmap? _sprite;   // season badge sprite (badged.png)

    // account bar
    private readonly Label _acct = new() { Text = "Chưa đăng nhập", AutoSize = true, ForeColor = Theme.Dim, Anchor = AnchorStyles.Left, Font = Theme.Bold };
    private readonly Label _wallet = new() { Text = "", AutoSize = true, ForeColor = Theme.Gold, Anchor = AnchorStyles.Left, Font = Theme.Bold, Margin = new Padding(24, 0, 3, 0) };
    private readonly System.Windows.Forms.Timer _walletTimer = new() { Interval = 60_000 };
    private readonly DarkButton _btnFromGame = new() { Text = "Lấy phiên từ game", Width = 150, Anchor = AnchorStyles.Right, Margin = new Padding(3, 3, 8, 3) };
    private readonly System.Windows.Forms.Timer _pingTimer = new() { Interval = 10 * 60_000 };
    private readonly DarkButton _btnAuth = new() { Text = "Đăng nhập", Width = 110, Anchor = AnchorStyles.Right };

    private readonly DarkTabControl _tabs = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Theme.Gap) };

    // --- tab "Tìm kiếm cầu thủ"
    private readonly DarkTextBox _findName = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, PlaceholderText = "Tên cầu thủ, ví dụ: casillas" };
    private readonly DarkButton _btnFind = new() { Text = "Tìm kiếm", Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly DataGridView _gridFind = new();
    private readonly Label _findStatus = new() { AutoSize = true, ForeColor = Theme.Dim, Anchor = AnchorStyles.Left };
    private readonly DarkButton _btnUse = new() { Text = "Dùng cho Ép thẻ", Width = 190, Enabled = false, Anchor = AnchorStyles.Right };

    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Font = Theme.Mono,
    };

    private TabPage _tabFind = null!;
    private TabPage _tabUpgrade = null!;
    private UpgradeTab _upgrade = null!;
    private KhoTab _kho = null!;
    private TabPage _tabKho = null!;
    private CancellationTokenSource? _upCts;

    public MainForm(Config cfg)
    {
        _cfg = cfg;
        Text = "FO3 Session Tool - Ép thẻ tự động";
        MinimumSize = new Size(960, 766);
        ClientSize = new Size(990, 856);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Body;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        BuildLayout();
        Theme.Apply(this);
        _log.BackColor = Theme.Surface2; _log.ForeColor = Theme.Text; _log.Font = Theme.Mono; _log.BorderStyle = BorderStyle.None;

        _btnFromGame.Click += async (_, _) => await ImportFromGameAsync();
        _pingTimer.Tick += PingTick;
        _btnAuth.Click += (_, _) => { if (_api == null) Login(); else Logout(); };

        _btnFind.Click += async (_, _) => await SearchPlayersAsync(_findName.Text);
        _findName.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SearchPlayersAsync(_findName.Text); } };
        _btnUse.Click += (_, _) => UseSelectedPlayer();
        _gridFind.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) UseSelectedPlayer(); };
        _gridFind.SelectionChanged += (_, _) => _btnUse.Enabled = _gridFind.CurrentRow?.Tag is PlayerInfo;
        FormClosing += (_, _) => { _searchVer++; _walletTimer.Stop(); _pingTimer.Stop(); _players.Dispose(); };
        Shown += async (_, _) => await TryRestoreAsync();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(Theme.Gap) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 576));   // tabs: fixed; the log takes whatever is left
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Account bar: status on the left, Đăng nhập / Đăng xuất on the right.
        var bar = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, AutoSize = true, Padding = new Padding(0, 0, 0, Theme.Gap) };
        bar.ColumnCount = 4;
        bar.ColumnStyles.Clear();
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(_acct, 0, 0);
        bar.Controls.Add(_wallet, 1, 0);
        bar.Controls.Add(_btnFromGame, 2, 0);
        bar.Controls.Add(_btnAuth, 3, 0);

        _tabFind = new TabPage("Tìm kiếm cầu thủ") { Padding = new Padding(0, Theme.Gap, 0, 0) };
        _tabFind.Controls.Add(BuildFindTab());

        _upgrade = new UpgradeTab(_players, Log);
        _upgrade.InventoryProvider = () => _api == null ? Task.FromResult(new List<GameApi.OwnedCard>()) : _api.GetInventoryAsync();
        _upgrade.MarketProvider = spid => _api == null ? Task.FromResult(new List<Listing>()) : _api.SearchAsync(spid);
        _upgrade.RunRequested += OnUpgradeRun;
        _upgrade.CycleRequested += OnCycleRun;
        _upgrade.StopRequested += () => { _upCts?.Cancel(); Log("[Ép thẻ] Đang dừng..."); };
        _tabUpgrade = new TabPage("Ép thẻ tự động") { Padding = new Padding(0, Theme.Gap, 0, 0) };
        _tabUpgrade.Controls.Add(_upgrade);
        _kho = new KhoTab(
            () => _api == null ? Task.FromResult(new List<GameApi.OwnedCard>()) : _api.GetInventoryAsync(),
            async () => _sprite ??= await _players.LoadImageAsync(PlayerSearch.SpriteUrl) as Bitmap,
            _players.GetDetailCachedAsync,
            Log);
        _kho.TradeAvailable = true;
        _kho.TradeRequested += async (cards, auto) => await OnTradeAsync(cards, auto);
        _tabKho = new TabPage("Kho cầu thủ") { Padding = new Padding(0, Theme.Gap, 0, 0) };
        _tabKho.Controls.Add(_kho);
        _tabs.TabPages.Add(_tabUpgrade);
        _tabs.TabPages.Add(_tabKho);
        _tabs.SelectedIndexChanged += async (_, _) => { if (_tabs.SelectedTab == _tabKho) await _kho.ReloadAsync(); };
        _tabs.TabPages.Add(_tabFind);

        var logBox = new DarkGroup { Text = "Nhật ký", Dock = DockStyle.Fill, Padding = new Padding(8, 18, 8, 8) };
        _log.Margin = Padding.Empty;
        logBox.Controls.Add(_log);

        root.Controls.Add(bar, 0, 0);
        root.Controls.Add(_tabs, 0, 1);
        root.Controls.Add(logBox, 0, 2);
        Controls.Add(root);
    }

    private static Label FormLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 8, 0) };

    private Control BuildFindTab()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowH));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(FormLabel("Tên cầu thủ"), 0, 0);
        t.Controls.Add(_findName, 1, 0);
        t.Controls.Add(_btnFind, 2, 0);

        StyleFindGrid();
        var listBox = new DarkGroup { Text = "Kết quả", Dock = DockStyle.Fill, Padding = new Padding(8, 2, 8, 8), Margin = new Padding(0, Theme.Gap, 0, Theme.Gap) };
        _gridFind.Margin = Padding.Empty;
        listBox.Controls.Add(_gridFind);
        t.Controls.Add(listBox, 0, 1);
        t.SetColumnSpan(listBox, 3);

        var foot = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true };
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _btnUse.Margin = Padding.Empty;
        _findStatus.Margin = Padding.Empty;
        foot.Controls.Add(_findStatus, 0, 0);
        foot.Controls.Add(_btnUse, 1, 0);
        t.Controls.Add(foot, 0, 2);
        t.SetColumnSpan(foot, 3);
        return t;
    }

    private void StyleFindGrid()
    {
        var g = _gridFind;
        g.Dock = DockStyle.Fill;
        g.ReadOnly = true; g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false; g.AllowUserToResizeRows = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect; g.MultiSelect = false;
        Theme.StyleGrid(g);
        g.ColumnHeadersHeight = 34;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Text;
        g.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        g.RowTemplate.Height = 66;
        g.DefaultCellStyle.ForeColor = Theme.Dim;

        g.Columns.Add(new DataGridViewImageColumn { Name = "img", HeaderText = "", Width = 50, ImageLayout = DataGridViewImageCellLayout.Zoom, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "name", HeaderText = "Cầu thủ", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 100, MinimumWidth = 200,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, ForeColor = Theme.Text, Font = Theme.Large, WrapMode = DataGridViewTriState.True },
        });
        AddCol("spid", "SPID", 70, typeof(long));
        AddCol("pos", "Vị trí", 66, typeof(string));
        AddCol("ovr", "Chỉ số chung", 100, typeof(int));
        for (int i = 0; i < PlayerSearch.StatNames.Length; i++)
            AddCol("s" + i, PlayerSearch.StatNames[i], new[] { 84, 76, 84, 76, 76 }[i], typeof(int));

        g.CellPainting += PaintNameCell;
        g.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.Value == null) return;
            var name = g.Columns[e.ColumnIndex].Name;
            if (name == "ovr")
            {
                e.CellStyle!.ForeColor = Theme.Accent;
                e.CellStyle.Font = Theme.Large;
            }
            else if (name == "pos")
            {
                var col = Theme.PosColor(PositionGroup(g.Rows[e.RowIndex].Tag as PlayerInfo));
                e.CellStyle!.ForeColor = col;
                e.CellStyle.SelectionForeColor = col;
                e.CellStyle.Font = Theme.Bold;
            }
            else if (name.StartsWith('s') && name.Length == 2 && Convert.ToInt32(e.Value) >= 60)
            {
                e.CellStyle!.ForeColor = Theme.Text;
                e.CellStyle.Font = Theme.Bold;
            }
        };
    }


    private static readonly Color Limited = Color.FromArgb(18, 124, 188);

    private static string PositionGroup(PlayerInfo? p)
    {
        if (p == null) return "gk";
        var m = p.Positions.FirstOrDefault(x => x.Pos == p.Position);
        if (m != null) return m.Group;
        return p.Positions.FirstOrDefault(x => x.Active)?.Group ?? "gk";
    }

    private static Color GroupColor(string g) => Theme.PosColor(g);

    /// <summary>Row 1: season icon + name + Limited tag. Row 2: every position with its rating. Row 3: skill-move stars.</summary>
    private void PaintNameCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _gridFind.Columns[e.ColumnIndex].Name != "name") return;
        if (_gridFind.Rows[e.RowIndex].Tag is not PlayerInfo p) return;

        e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
        var g = e.Graphics!;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var b = e.CellBounds;
        int x = b.X + 8, y = b.Y + 7;

        // season icon cut out of the site's sprite
        if (_sprite != null && PlayerSearch.SeasonSpriteX.TryGetValue(p.Season, out var sx))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(_sprite, new Rectangle(x, y, 22, 22), new Rectangle(sx, 0, 18, 18), GraphicsUnit.Pixel);
            x += 27;
        }
        else
        {
            TextRenderer.DrawText(g, $"S{p.Season}", Theme.SmallBold, new Point(x, y + 3), Theme.Dim);
            x += 30;
        }

        var nameFont = Theme.Large;
        var sz = TextRenderer.MeasureText(g, p.Name, nameFont, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, p.Name, nameFont, new Point(x, y), Theme.Text, TextFormatFlags.NoPadding);
        x += sz.Width + 6;

        if (p.Limited)
        {
            var f = Theme.SmallBold;
            var w = TextRenderer.MeasureText(g, "Limited", f, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding).Width + 10;
            using var br = new SolidBrush(Limited);
            g.FillRectangle(br, new Rectangle(x, y + 3, w, 16));
            TextRenderer.DrawText(g, "Limited", f, new Rectangle(x, y + 3, w, 16), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // positions: coloured chip + rating, active position first
        int cx = b.X + 8, cy = b.Y + 31;
        var chipFont = Theme.SmallBold;
        var rateFont = Theme.SmallBold;
        foreach (var pos in p.Positions.OrderByDescending(q => q.Active).ThenByDescending(q => q.Rating))
        {
            int cw = Math.Max(30, TextRenderer.MeasureText(g, pos.Pos, chipFont, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding).Width + 10);
            using (var br = new SolidBrush(GroupColor(pos.Group)))
                g.FillRectangle(br, new Rectangle(cx, cy, cw, 16));
            TextRenderer.DrawText(g, pos.Pos, chipFont, new Rectangle(cx, cy, cw, 16),
                pos.Group == "gk" ? Color.FromArgb(40, 30, 0) : Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            cx += cw + 3;
            var rc = pos.Active ? Theme.Accent : Theme.Dim;
            TextRenderer.DrawText(g, pos.Rating.ToString(), rateFont, new Point(cx, cy + 1), rc, TextFormatFlags.NoPadding);
            cx += TextRenderer.MeasureText(g, pos.Rating.ToString(), rateFont, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding).Width + 10;
        }

        // skill moves
        if (p.SkillMoves > 0)
            TextRenderer.DrawText(g, new string('★', p.SkillMoves), new Font("Segoe UI Symbol", 9f), new Point(b.X + 8, b.Y + 49),
                Theme.Gold, TextFormatFlags.NoPadding);

        e.Handled = true;
    }

    private void AddCol(string name, string header, int width, Type type)
    {
        _gridFind.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name, HeaderText = header, Width = width, ValueType = type, SortMode = DataGridViewColumnSortMode.Automatic,
        });
    }

    // ---- login / logout -------------------------------------------------

    private void Login()
    {
        Log("Đang mở cửa sổ đăng nhập...");
        using var dlg = new LoginDialog(_cfg, LoadUser());
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Api == null) return;

        SaveUser(dlg.User);
        SessionStore.Save(new SavedSession(dlg.User, dlg.Token, dlg.Api.ExportCookies(), DateTime.Now));
        ApplyLogin(dlg.Api, dlg.User, dlg.Token);
        Log($"Đăng nhập thành công: {dlg.User}. Đã lưu phiên (DPAPI).");
    }

    private void ApplyLogin(GameApi api, string user, string token)
    {
        _api = api;
        api.WalletChanged += OnWallet;
        _pingTimer.Start();
        _walletTimer.Tick -= WalletTick; _walletTimer.Tick += WalletTick; _walletTimer.Start();
        _ = RefreshWalletAsync(api);
        _upgrade.SetLoggedIn(true);
        _kho.SetLoggedIn(true);
        _btnAuth.Text = "Đăng xuất";
        _acct.Text = $"● {user}    |    Session: {MaskToken(token)}";
        _acct.ForeColor = Theme.Success;
    }

    /// <summary>On startup: reuse the saved session if the server still accepts it, else fall back to the login popup.</summary>
    private async Task TryRestoreAsync()
    {
        var saved = SessionStore.Load();
        if (saved == null || saved.Cookies.Count == 0) return;

        _acct.Text = $"Đang khôi phục phiên của {saved.User}...";
        Log($"Đang kiểm tra phiên đã lưu của {saved.User} với server...");
        try
        {
            var api = new GameApi(_cfg);
            api.ImportCookies(saved.Cookies);
            var (valid, detail) = await api.CheckSessionAsync();
            if (valid)
            {
                ApplyLogin(api, saved.User, saved.Token);
                Log($"Đã khôi phục phiên của {saved.User} (lưu lúc {saved.SavedAt:dd/MM HH:mm}), không cần xếp hàng.");
                return;
            }
            Log($"Phiên cũ của {saved.User} đã hết hạn ({detail}). Hãy đăng nhập lại.");
        }
        catch (Exception ex) { Log("Không kiểm tra được phiên cũ: " + ex.Message); }

        SessionStore.Clear();
        _acct.Text = "Chưa đăng nhập";
    }

    private void Logout()
    {
        Log("Đang đăng xuất...");
        _upCts?.Cancel();
        if (_api != null) _api.WalletChanged -= OnWallet;
        _walletTimer.Stop();
        _pingTimer.Stop();
        _wallet.Text = "";
        _api = null;
        SessionStore.Clear();
        _upgrade.SetLoggedIn(false);
        _kho.SetLoggedIn(false);
        _btnAuth.Text = "Đăng nhập";
        _acct.Text = "Chưa đăng nhập";
        _acct.ForeColor = Theme.Dim;
        Log("Đã đăng xuất, đã xóa phiên đã lưu.");
    }

    // ---- trade ---------------------------------------------------------------------------

    private bool _trading;

    private async Task OnTradeAsync(List<GameApi.OwnedCard> cards, bool auto)
    {
        var api = _api;
        if (api == null || _trading) return;
        if (_upCts != null) { Warn("Đang chạy ép thẻ, hãy dừng trước khi giao dịch."); return; }
        _trading = true;
        _kho.SetTradeBusy(true);
        try
        {
            long inVal = cards.Sum(c => c.Value);
            Log($"[Giao dịch] Hỏi phương án cho {cards.Count} thẻ (tổng giá trị {inVal:N0} EP, chế độ {(auto ? "tự động" : "tự chọn")})...");
            var prop = await api.TradeProposalAsync(cards.Select(c => c.Id));
            if (!prop.Ok)
            {
                Log("[Giao dịch] Server không đưa ra phương án: " + Trim(prop.Raw));
                Warn("Server không đưa ra phương án giao dịch. Chi tiết trong nhật ký.");
                return;
            }
            foreach (var o in prop.Options)
                Log($"[Giao dịch] Phương án {o.Index + 1}: SPID {o.Spid} +{o.Grade} (giá {o.Price:N0}) + {o.Ep:N0} EP = {o.Value:N0}");

            var best = prop.Options.OrderByDescending(o => o.Value).First();
            GameApi.TradeOption? pick;
            if (auto)
            {
                pick = best;
                Log($"[Giao dịch] Tự động chọn phương án {pick.Index + 1} (tổng lớn nhất {pick.Value:N0}).");
            }
            else
            {
                using var dlg = new TradeDialog(cards, prop.Options, best.Index, _players,
                    async () => _sprite ??= await _players.LoadImageAsync(PlayerSearch.SpriteUrl) as Bitmap);
                pick = dlg.ShowDialog(this) == DialogResult.OK ? dlg.Picked : null;
                if (pick == null) { Log("[Giao dịch] Đã hủy, chưa đổi thẻ nào."); return; }
            }

            long wallet = api.LastWallet?.Ep ?? 0;
            string cap = wallet + pick.Ep > 5_000_000_000L ? "\n\nCẢNH BÁO: EP sau giao dịch vượt trần 5 tỷ của game, giao dịch có thể bị từ chối." : "";
            var ask = MessageBox.Show(this,
                $"Đổi {cards.Count} thẻ (tổng giá trị {inVal:N0} EP) lấy:\n  • Cầu thủ SPID {pick.Spid} +{pick.Grade} (giá {pick.Price:N0})\n  • {pick.Ep:N0} EP tiền tươi\n\n" +
                "Các thẻ đã chọn sẽ MẤT và KHÔNG hoàn tác được. Tiếp tục?" + cap,
                "Xác nhận giao dịch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (ask != DialogResult.Yes) { Log("[Giao dịch] Đã hủy, chưa đổi thẻ nào."); return; }

            Log($"[Giao dịch] Đang chốt phương án {pick.Index + 1}...");
            var res = await api.TradeContractAsync(pick.Index);
            if (!res.Ok)
            {
                Log("[Giao dịch] THẤT BẠI: " + Trim(res.Raw));
                Warn("Giao dịch không thành công. Chi tiết trong nhật ký.");
                return;
            }
            Log($"[Giao dịch] Thành công: nhận SPID {pick.Spid} +{pick.Grade} và {pick.Ep:N0} EP.");
            await _kho.ReloadAsync();
        }
        catch (Exception ex) { Log("[Giao dịch] LỖI: " + ex.Message); }
        finally { _trading = false; _kho.SetTradeBusy(false); }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "...";

    // ---- session: keep-alive, and taking over the session of the running game -------------

    /// <summary>The game pings /sign/ping every ~10 minutes; do the same so an idle tool is not dropped.</summary>
    private async void PingTick(object? sender, EventArgs e)
    {
        var api = _api;
        if (api == null) return;
        try
        {
            Log("[Phiên] Đang gửi ping giữ phiên (/sign/ping)...");
            if (!await api.PingAsync()) Log("[Phiên] Ping không thành công, phiên có thể đã hết hạn.");
        }
        catch (Exception ex) { Log("[Phiên] LỖI ping: " + ex.Message); }
    }

    /// <summary>Use the cookies of the game that is running under run_game_capture.bat instead of queueing again.</summary>
    private async Task ImportFromGameAsync()
    {
        if (_upCts != null) { Warn("Đang chạy ép thẻ, hãy dừng trước khi đổi phiên."); return; }
        var got = SessionStore.LoadGameCookie();
        if (got == null)
        {
            Warn("Chưa có phiên từ game." + Environment.NewLine + Environment.NewLine + "Chạy capture\\run_game_capture.bat (quyền Administrator) TRƯỚC, rồi mở launcher và vào game. " +
                 "Khi game vào được, bấm lại nút này.");
            return;
        }
        _btnFromGame.Enabled = false;
        Log($"[Phiên] Đang kiểm tra phiên của game (thấy lúc {got.Value.At:dd/MM HH:mm:ss})...");
        try
        {
            var api = new GameApi(_cfg);
            api.ImportCookies(got.Value.Cookies);
            var (valid, detail) = await api.CheckSessionAsync();
            if (!valid) { Log($"[Phiên] Phiên của game không dùng được ({detail}). Hãy vào lại game rồi thử lại."); Warn("Phiên lấy từ game đã hết hạn hoặc chưa đúng. Hãy vào lại game rồi bấm lại."); return; }

            var name = await api.GetCoachNameAsync();
            if (name.Length == 0) name = "game";
            if (_api != null) _api.WalletChanged -= OnWallet;
            SaveUser(name);
            SessionStore.Save(new SavedSession(name, "từ game", api.ExportCookies(), DateTime.Now));
            ApplyLogin(api, name, "từ game");
            Log($"[Phiên] Đã dùng phiên của game cho {name}. Đã lưu phiên (DPAPI).");
        }
        catch (Exception ex) { Log("[Phiên] LỖI lấy phiên từ game: " + ex.Message); }
        finally { _btnFromGame.Enabled = true; }
    }

    // ---- balance (EP / ES) at the top, updated live ------------------------

    private void OnWallet(GameApi.Wallet w)
    {
        void Show() => _wallet.Text = $"EP: {w.Ep:N0}    ES: {w.Es:N0}";
        if (IsHandleCreated && InvokeRequired) BeginInvoke(Show); else Show();
    }

    private async Task RefreshWalletAsync(GameApi api)
    {
        try { await api.GetWalletAsync(); }
        catch (Exception ex) { Log("Không đọc được số EP: " + ex.Message); }
    }

    /// <summary>Resync with the server now and then; during a run the runner keeps the number moving itself.</summary>
    private async void WalletTick(object? sender, EventArgs e)
    {
        if (_api == null || _upCts != null) return;
        await RefreshWalletAsync(_api);
    }

    private static string MaskToken(string t) => t.Length <= 16 ? t : $"{t[..8]}...{t[^8..]}";

    // ---- tab "Tìm kiếm cầu thủ" -----------------------------------------

    /// <summary>Search fifaaddict by name and fill the list (also loads the portraits).</summary>
    public async Task SearchPlayersAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) { Warn("Nhập tên cầu thủ."); return; }

        int ver = ++_searchVer;
        _btnFind.Enabled = false;
        _findStatus.Text = "Đang tìm...";
        Log($"[Tìm cầu thủ] Đang tìm \"{name}\" trên fifaaddict...");
        try
        {
            var list = (await _players.SearchAsync(name)).OrderByDescending(p => p.Overall).ThenByDescending(p => p.Spid).ToList();
            if (ver != _searchVer) return;

            if (_sprite == null) Log("[Tìm cầu thủ] Đang tải bộ icon mùa giải...");
            _sprite ??= await _players.LoadImageAsync(PlayerSearch.SpriteUrl) as Bitmap;
            if (ver != _searchVer) return;
            _gridFind.Rows.Clear();
            var placeholder = new Bitmap(1, 1);
            foreach (var p in list)
            {
                int i = _gridFind.Rows.Add(placeholder,
                    p.Name,
                    p.Spid, p.Position, p.Overall, p.Stats[0], p.Stats[1], p.Stats[2], p.Stats[3], p.Stats[4]);
                _gridFind.Rows[i].Tag = p;
            }
            _findStatus.Text = list.Count == 0 ? $"Không tìm thấy \"{name}\"." : $"{list.Count} cầu thủ cho \"{name}\". Nhấp đôi vào dòng để dùng cho Ép thẻ.";
            Log($"[Tìm cầu thủ] \"{name}\": {list.Count} kết quả. Đang tải ảnh chân dung...");

            await Task.WhenAll(list.Select(async (p, idx) =>
            {
                var img = await _players.LoadImageAsync(p.ImageUrl);
                if (img != null && ver == _searchVer && idx < _gridFind.Rows.Count) _gridFind.Rows[idx].Cells["img"].Value = img;
            }));
        }
        catch (Exception ex) { _findStatus.Text = "Lỗi: " + ex.Message; Log("Lỗi tìm cầu thủ: " + ex.Message); }
        finally { _btnFind.Enabled = true; }
    }

    private void UseSelectedPlayer()
    {
        if (_gridFind.CurrentRow?.Tag is not PlayerInfo p) return;
        _tabs.SelectedTab = _tabUpgrade;
        _upgrade.PickPlayer(p);
        Log($"Đã chọn {p.Name} (SPID {p.Spid}) cho tab Ép thẻ.");
    }

    // ---- tab "Ép thẻ tự động" -------------------------------------------

    /// <summary>Runs the whole flow (buy N, then merge up to the target) with progress + log; Dừng cancels it.</summary>
    /// <summary>The price of a +1 card is never above the game's upper limit for it (/transfer/price): that ceiling is the implicit max price per card.</summary>
    private async Task<UpgradePlan> WithCeilingAsync(UpgradePlan plan)
    {
        if (plan.UseInventory || _api == null) return plan;
        try
        {
            var p = await _api.GetSellPriceAsync(plan.Spid, 1);
            if (p != null) { Log($"[Ép thẻ] Giá trần của thẻ +1: {p.Upper:N0} EP (mua không vượt mức này)."); return plan with { MaxPrice = p.Upper }; }
        }
        catch (Exception ex) { Log("[Ép thẻ] Không đọc được giá trần: " + ex.Message + " (không giới hạn giá)."); }
        return plan;
    }

    /// <summary>A merge with a success chance under 100%: warn and let the user accept (the run goes on) or refuse (the run stops).</summary>
    private Task<bool> AskRiskAsync(int level, int probability)
    {
        var tcs = new TaskCompletionSource<bool>();
        BeginInvoke(() =>
        {
            var r = MessageBox.Show(
                $"CẢNH BÁO: xác suất ép lên +{level} chỉ {probability}% (dưới 100%).{Environment.NewLine}{Environment.NewLine}" +
                "Nếu ép không thành công thì thẻ có thể bị mất hoặc giảm cấp, và phí ép vẫn bị trừ." + Environment.NewLine + Environment.NewLine +
                $"Chấp nhận rủi ro và tiếp tục ép lên +{level} (áp dụng cho các lần ép lên +{level} còn lại của lần chạy này)?" + Environment.NewLine +
                "Chọn Không để dừng lại.",
                "Xác suất ép thấp", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            tcs.SetResult(r == DialogResult.Yes);
        });
        return tcs.Task;
    }

    private async void OnUpgradeRun(UpgradePlan plan)
    {
        if (_api == null) { Warn("Hãy đăng nhập trước."); return; }
        if (_upCts != null) return;
        plan = await WithCeilingAsync(plan);

        string sellNote = "";
        if (plan.SellAfter)
        {
            try
            {
                var sp = await _api.GetSellPriceAsync(plan.Spid, plan.Target);
                sellNote = sp != null
                    ? $"\n\nSAU KHI ÉP XONG: các thẻ +{plan.Target} vừa ép ra được đăng bán NGAY lên thị trường chuyển nhượng ở giá cao nhất game cho phép ({sp.Upper:N0} EP/thẻ), phí đăng {sp.Fee:N0}/thẻ, game giữ 20% khi bán xong. Tiền bán được bạn tự nhận trong game."
                    : "\n\nSAU KHI ÉP XONG: sẽ đăng bán các thẻ vừa ép ra ở giá cao nhất (chưa đọc được khung giá).";
            }
            catch (Exception ex) { sellNote = "\n\nSAU KHI ÉP XONG: sẽ đăng bán (không đọc được khung giá: " + ex.Message + ")."; }
        }
        var cost = plan.MaxPrice > 0 ? $"tối đa {plan.Quantity * plan.MaxPrice:N0} EP (giá trần {plan.MaxPrice:N0}/thẻ)" : "không giới hạn giá";
        var rules = string.Join("\n", Enumerable.Range(2, plan.Target - 1).Select(l => $"   +{l} = +{plan.Rules[l].A} và +{plan.Rules[l].B}"));
        var ask = MessageBox.Show(
            (plan.UseInventory
                ? $"Ép thẻ có sẵn trong kho của {plan.Name} (SPID {plan.Spid}) đến +{plan.Target}, không mua thêm." + "\n"
                : $"Mua {plan.Quantity} thẻ +1 của {plan.Name} (SPID {plan.Spid}), rồi gom mọi thẻ hợp lệ của cầu thủ này trong kho (ngoài đội hình) và ép đến +{plan.Target}." + "\n") +
            (plan.UseInventory ? "Không mua thẻ mới." : $"Tiền mua: {cost}.") + $" Cộng thêm phí ép theo server.\n\nRule:\n{rules}{sellNote}\n\nTiếp tục? Thao tác này tiêu tốn EP thật.",
            "Xác nhận chạy quy trình", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (ask != DialogResult.Yes) return;

        _upCts = new CancellationTokenSource();
        _upgrade.ResetSteps();
        _upgrade.SetRunning(true);
        Log($"[Ép thẻ] Bắt đầu: {plan.Name} (SPID {plan.Spid}){(plan.UseInventory ? "" : $" ×{plan.Quantity}")}, ép đến +{plan.Target}" + (plan.UseInventory ? " (thẻ trong kho)." : "."));
        try
        {
            var runner = new UpgradeRunner(_api, Log, _upgrade.SetStepState, _upgrade.SetProgress) { ConfirmRisk = AskRiskAsync };
            bool ok = await runner.RunAsync(plan, _upCts.Token);
            if (ok && plan.SellAfter)
            {
                var sold = await MarketSeller.SellAsync(_api, plan, runner.ProducedTargetIds, Log, _upgrade.SetStepState, _upCts.Token);
                Log($"[Bán] KẾT THÚC: đã đăng bán {sold.Listed} thẻ +{plan.Target} (tổng giá {sold.ListedValue:N0} EP, phí đăng {sold.Fees:N0})." +
                    (sold.Problem != null ? " Dừng vì: " + sold.Problem : "") + (sold.Listed > 0 ? " Tiền bán được hãy nhận trong game khi có người mua." : ""));
            }
        }
        catch (OperationCanceledException) { Log("[Ép thẻ] Đã dừng theo yêu cầu."); _upgrade.SetProgress(0, 1, "Đã dừng."); }
        catch (Exception ex) { Log("[Ép thẻ] LỖI: " + ex.Message); _upgrade.SetProgress(0, 1, "Lỗi: " + ex.Message); }
        finally
        {
            _upCts?.Dispose();
            _upCts = null;
            _upgrade.SetRunning(false);
            if (_api != null) _ = RefreshWalletAsync(_api);   // exact balance after the run
        }
    }

    private async void OnCycleRun(UpgradePlan plan, CycleOptions opt)
    {
        if (_api == null) { Warn("Hãy đăng nhập trước."); return; }
        if (_upCts != null) return;
        plan = await WithCeilingAsync(plan);

        var nl = Environment.NewLine;
        var rules = string.Join(nl, Enumerable.Range(2, plan.Target - 1).Select(l => $"   +{l} = +{plan.Rules[l].A} và +{plan.Rules[l].B}"));
        var ask = MessageBox.Show(
            $"XOAY VÒNG TỰ ĐỘNG cho {plan.Name} (SPID {plan.Spid}), ba việc chạy SONG SONG:" + nl + nl +
            $"• MUA thẻ +1 liên tục, từng thẻ một (giá trần: {(plan.MaxPrice > 0 ? plan.MaxPrice.ToString("N0") + " EP/thẻ" : "không giới hạn")}), chỉ mua khi dây chuyền còn chỗ." + nl +
            $"• ÉP ngay khi có đủ cặp thẻ theo rule, đến +{plan.Target}." + nl +
            (opt.SellMode
                ? $"• BÁN: mỗi thẻ +{plan.Target} ép xong được đăng bán NGAY lên thị trường chuyển nhượng ở giá CAO NHẤT game cho phép. Phí đăng 1% khi đăng, game giữ 20% khi bán xong." + nl +
                  (opt.AutoCollect ? "• NHẬN TIỀN tự động khi thẻ bán xong (thẻ chưa có người mua thì tool chờ, không dừng dù EP đã hết)." : "• Tiền bán KHÔNG được nhận tự động: bạn tự nhận trong game.") + nl
                : $"• GIAO DỊCH khi tổng giá trị thẻ +{plan.Target} đạt {opt.TradeMin:N0} - {opt.TradeMax:N0} EP: đổi hết (chọn phương án lớn nhất), không giữ lại thẻ nào." + nl) +
            "   Gồm cả thẻ +" + plan.Target + " của cầu thủ này đang có sẵn trong kho (ngoài đội hình); các thẻ thấp hơn có sẵn cũng được dùng để ép." + nl + nl +
            $"Rule:{nl}{rules}{nl}{nl}" +
            $"Dừng khi: hết EP{(opt.StopBelowEp > 0 ? $" (không mua nếu EP còn lại sau khi mua dưới {opt.StopBelowEp:N0})" : "")}, lỗ {opt.MaxLossRounds} lần {(opt.SellMode ? "bán" : "giao dịch")} liên tiếp, phiên hết hạn, có lỗi, hoặc bạn bấm Dừng." + nl +
            (opt.SellMode ? "Lãi/lỗ mỗi lần bán là ước tính (giá bán trừ 20% và phí đăng, trừ chi phí mua+ép). Mỗi lệnh đăng bán sẽ không hỏi lại. Thao tác này tiêu tốn EP thật." : "Mỗi giao dịch KHÔNG hoàn tác và sẽ không hỏi lại. Thao tác này tiêu tốn EP thật.") + nl + nl + "Bắt đầu?",
            "Xác nhận xoay vòng tự động", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (ask != DialogResult.Yes) return;

        _upCts = new CancellationTokenSource();
        _upgrade.ResetSteps();
        _upgrade.SetRunning(true);
        Log(opt.SellMode
            ? $"[Xoay vòng] Bắt đầu song song: {plan.Name} (SPID {plan.Spid}), ép đến +{plan.Target}, bán thị trường giá cao nhất, nhận tiền {(opt.AutoCollect ? "tự động" : "thủ công")}."
            : $"[Xoay vòng] Bắt đầu song song: {plan.Name} (SPID {plan.Spid}), ép đến +{plan.Target}, đổi khi đạt {opt.TradeMin:N0}-{opt.TradeMax:N0} EP.");
        try
        {
            var runner = new CycleRunner(_api, Log, _upgrade.SetStepState, _upgrade.SetProgress, _upgrade.ResetSteps) { ConfirmRisk = AskRiskAsync };
            var reason = await runner.RunAsync(plan, opt, _upCts.Token);
            Log("[Xoay vòng] KẾT THÚC: " + reason);
            _upgrade.SetProgress(0, 1, "Đã dừng: " + reason);
        }
        catch (OperationCanceledException) { Log("[Xoay vòng] Đã dừng theo yêu cầu."); _upgrade.SetProgress(0, 1, "Đã dừng."); }
        catch (Exception ex) { Log("[Xoay vòng] LỖI: " + ex.Message); _upgrade.SetProgress(0, 1, "Lỗi: " + ex.Message); }
        finally
        {
            _upCts?.Dispose();
            _upCts = null;
            _upgrade.SetRunning(false);
            if (_api != null) _ = RefreshWalletAsync(_api);
        }
    }

    public Task UpgradeSearchAsync(string name) => _upgrade.SearchAsync(name);
    public void ShowTab(int index) => _tabs.SelectedIndex = index;

    // ---- helpers --------------------------------------------------------

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        _log.AppendText(line + Environment.NewLine);
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FO3SessionTool", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"{DateTime.Now:yyyyMMdd}.log"), $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
        }
        catch { /* logging to disk is best effort */ }
    }
    private static void Warn(string msg) => MessageBox.Show(msg, "FO3 Session Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static string UserFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FO3SessionTool", "user.txt");

    private static string LoadUser()
    {
        try { return File.Exists(UserFile) ? File.ReadAllText(UserFile).Trim() : ""; } catch { return ""; }
    }

    private static void SaveUser(string user)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(UserFile)!); File.WriteAllText(UserFile, user); } catch { }
    }
}
