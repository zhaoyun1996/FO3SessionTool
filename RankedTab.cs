using System.Drawing;
using System.Runtime.InteropServices;

namespace FO3SessionTool;

/// <summary>Auto "Đấu giả lập xếp hạng": drives the game window (messages only, the user's mouse and keyboard are not used). Logs go to the shared log.</summary>
public sealed class RankedTab : UserControl
{
    private readonly Action<string> _log;
    private readonly RankedAutoRunner _runner = new();

    private readonly Label _lblGame = new() { AutoSize = true, Font = Theme.Bold, Text = "Game FO3: đang kiểm tra...", ForeColor = Theme.Dim };
    private readonly Label _lblScreen = new() { AutoSize = true, ForeColor = Theme.Dim, Text = "Màn hình: chưa chạy" };
    private readonly Label _lblState = new() { AutoSize = true, Font = Theme.Bold, Text = "Trạng thái: sẵn sàng", ForeColor = Theme.Gold };
    private readonly Label _lblProgress = new() { AutoSize = true, Font = Theme.Bold, Text = "Tiến độ: 0 trận", ForeColor = Theme.Accent };

    private readonly DarkNumber _numMatches = new() { Minimum = 0, Maximum = 1000, Width = 90 };
    private readonly DarkNumber _numCooldown = new() { Minimum = 0, Maximum = 120, Width = 90 };
    private readonly DarkCombo _formation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly Label _lblAdmin = new() { AutoSize = true, Font = Theme.Bold };
    private readonly Label _lblAdminNote = new() { AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Theme.Dim, Text = "Tab này điều khiển cửa sổ game bằng cách gửi chuột và phím vào đó. Windows chỉ cho phép việc này khi tool có quyền ngang game: nếu game chạy quyền admin thì tool cũng phải chạy admin (chuột phải FO3SessionTool.exe > Run as administrator). Chưa đủ quyền thì các ô bên dưới bị khoá." };
    private readonly Label _lblSizeNote = new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Theme.Dim, Text = "Game chỉ vẽ ở độ phân giải trong graphics.ini nên kéo giãn cửa sổ sẽ làm chuột thật bấm lệch. Tool sửa file này: hãy ĐÓNG game, chọn khung hình, rồi mở lại game. Ô này chỉ dùng được khi game đang đóng." };
    private readonly DarkCombo _size = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly DarkCombo _tactic = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };

    private readonly DarkButton _btnStart = new() { Text = "Bắt đầu (F9)", Width = 170, Height = 40, Font = Theme.Bold, Tag = "primary" };
    private readonly DarkButton _btnStop = new() { Text = "Dừng (F9)", Width = 140, Height = 40, Font = Theme.Bold, Enabled = false };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private const int HotkeyId = 9001;

    public RankedTab(Action<string> log)
    {
        _log = log;
        Dock = DockStyle.Fill;
        Font = Theme.Body;

        _numMatches.Value = 10;
        _numCooldown.Value = 8;
        _tactic.Items.AddRange(new object[] { "Giữ nguyên", "Chiến thuật 1", "Chiến thuật 2", "Chiến thuật 3", "Chiến thuật 4", "Chiến thuật 5", "Chiến thuật 6" });
        _tactic.SelectedIndex = 0;
        _formation.Items.AddRange(new object[] { "Giữ nguyên", "Đội hình A", "Đội hình B", "Đội hình C" });
        _formation.SelectedIndex = 0;
        _size.Items.AddRange(SizeNames);
        ShowIniSize();

        BuildLayout();

        _runner.LogMessage += msg => Ui(() => _log("[Giả lập XH] " + msg));
        _runner.StateChanged += s => Ui(() => _lblState.Text = "Trạng thái: " + s);
        _runner.ScreenDetected += (screen, _) => Ui(() => _lblScreen.Text = "Màn hình: " + ScreenName(screen));
        _runner.ProgressChanged += (cur, target) => Ui(() => _lblProgress.Text = $"Tiến độ: {cur} / {(target > 0 ? target.ToString() : "∞")} trận");
        _runner.Finished += () => Ui(OnFinished);

        _btnStart.Click += (_, _) => StartAuto();
        _btnStop.Click += (_, _) => StopAuto();
        _size.SelectionChangeCommitted += (_, _) => SaveSize();   // written as soon as one is picked
        _timer.Tick += (_, _) => CheckGame();
        _timer.Start();
        CheckGame();

        HandleCreated += (_, _) => { try { RegisterHotKey(Handle, HotkeyId, 0, WinApi.VK_F9); } catch { } };
        HandleDestroyed += (_, _) => { try { UnregisterHotKey(Handle, HotkeyId); } catch { } };
    }

    private static string ScreenName(GameScreenState s) => s switch
    {
        GameScreenState.Lobby => "màn hình chính",
        GameScreenState.ModeRank => "chọn chế độ xếp hạng",
        GameScreenState.Searching => "đang tìm trận",
        GameScreenState.Intro => "cảnh bỏ qua (Space)",
        GameScreenState.ReadyScreen => "nút vàng (Sẵn sàng / Tiếp)",
        GameScreenState.InMatch => "trong trận",
        _ => "đang tải / chờ"
    };

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        _lblGame.Margin = new Padding(0, 3, 24, 0);
        _lblScreen.Margin = new Padding(0, 3, 0, 0);
        top.Controls.Add(_lblGame);
        top.Controls.Add(_lblScreen);
        root.Controls.Add(top, 0, 0);

        bool admin = IsAdmin();
        _lblAdmin.Text = admin ? "Quyền của tool: ĐANG CHẠY ADMIN" : "Quyền của tool: CHƯA chạy admin";
        _lblAdmin.ForeColor = admin ? Color.FromArgb(70, 210, 120) : Color.FromArgb(230, 160, 60);
        var adminBox = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        _lblAdmin.Margin = new Padding(0, 0, 0, 2);
        adminBox.Controls.Add(_lblAdmin);
        adminBox.Controls.Add(_lblAdminNote);
        root.Controls.Add(adminBox, 0, 1);

        // settings: a plain two-column grid (label, control) so nothing overlaps
        var grp = new DarkGroup { Text = "Cài đặt trận đấu", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 22, 12, 12), Margin = new Padding(0, 0, 0, 8) };
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void Row(string label, Control c)
        {
            var l = new Label { Text = label, AutoSize = true, ForeColor = Theme.Text, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 16, 6) };
            c.Margin = new Padding(0, 4, 0, 4);
            c.Anchor = AnchorStyles.Left;
            grid.Controls.Add(l);
            grid.Controls.Add(c);
        }
        Row("Số trận (0 = chạy mãi)", _numMatches);
        Row("Sơ đồ trước trận", _formation);
        Row("Chiến thuật trong trận", _tactic);
        Row("Nghỉ giữa 2 trận (giây)", _numCooldown);
        Row("Khung hình game", SizeRow());
        grid.Controls.Add(new Label());
        grid.Controls.Add(_lblSizeNote);
        grp.Controls.Add(grid);
        root.Controls.Add(grp, 0, 2);

        var act = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        _btnStart.Margin = new Padding(0, 0, 12, 8);
        _btnStop.Margin = new Padding(0, 0, 24, 8);
        _lblState.Margin = new Padding(0, 12, 24, 0);
        _lblProgress.Margin = new Padding(0, 12, 0, 0);
        act.Controls.AddRange(new Control[] { _btnStart, _btnStop, _lblState, _lblProgress });
        root.Controls.Add(act, 0, 3);

        Controls.Add(root);
    }

    private Control SizeRow()
    {
        _size.Margin = Padding.Empty;
        return _size;
    }

    // The game renders at the resolution set in its own graphics.ini / settings menu: stretching its window does not change that (the picture
    // is only scaled and the real mouse no longer lands where it points). So the tool edits graphics.ini, which the game reads when it starts.
    private static readonly object[] SizeNames = { "Full Screen", "640 x 480 (4:3)", "800 x 600 (4:3)", "1024 x 768 (4:3)", "1280 x 720 (16:9)", "1280 x 960 (4:3)", "1600 x 900 (16:9)" };
    private static readonly (int W, int H)[] Sizes = { (0, 0), (640, 480), (800, 600), (1024, 768), (1280, 720), (1280, 960), (1600, 900) };
    private string? _iniPath;

    private static string? FindGraphicsIni()
    {
        var dirs = new List<string>();
        try { foreach (var p in System.Diagnostics.Process.GetProcessesByName("fifazf")) { var d = Path.GetDirectoryName(p.MainModule?.FileName); if (d != null) dirs.Add(d); } } catch { }
        var cur = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5 && cur != null; i++, cur = cur.Parent) dirs.Add(cur.FullName);
        return dirs.Select(d => Path.Combine(d, "graphics.ini")).FirstOrDefault(File.Exists);
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path)) { int k = line.IndexOf('='); if (k > 0) d[line[..k].Trim()] = line[(k + 1)..].Trim(); }
        return d;
    }

    private void ShowIniSize()
    {
        _iniPath = FindGraphicsIni();
        if (_iniPath == null) return;
        try
        {
            var ini = ReadIni(_iniPath);
            int w = int.Parse(ini.GetValueOrDefault("SCREEN_WIDTH", "0")), h = int.Parse(ini.GetValueOrDefault("SCREEN_HEIGHT", "0"));
            bool full = ini.GetValueOrDefault("FULLSCREEN", "0") == "1";
            int idx = full ? 0 : Array.FindIndex(Sizes, s => s.W == w && s.H == h);
            if (idx >= 0) _size.SelectedIndex = idx; else _size.SelectedIndex = -1;
        }
        catch { }
    }

    private void SaveSize()
    {
        _iniPath ??= FindGraphicsIni();
        if (_iniPath == null) { _log("[Giả lập XH] Không tìm thấy graphics.ini của game."); return; }
        if (System.Diagnostics.Process.GetProcessesByName("fifazf").Length > 0)
        {
            _log("[Giả lập XH] Hãy đóng game trước khi đổi khung hình (game ghi đè graphics.ini khi thoát).");
            ShowIniSize();
            return;
        }
        int i = _size.SelectedIndex;
        if (i < 0) return;
        try
        {
            var text = File.ReadAllText(_iniPath);
            string eol = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            void Set(string key, string val)
            {
                int k = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
                if (k >= 0) lines[k] = key + "=" + val; else lines.Insert(Math.Max(0, lines.Count - (lines.Last() == "" ? 1 : 0)), key + "=" + val);
            }
            if (i == 0) Set("FULLSCREEN", "1");
            else { Set("FULLSCREEN", "0"); Set("SCREEN_WIDTH", Sizes[i].W.ToString()); Set("SCREEN_HEIGHT", Sizes[i].H.ToString()); }
            File.WriteAllText(_iniPath, string.Join(eol, lines));
            _log($"[Giả lập XH] Đã ghi khung hình \"{SizeNames[i]}\" vào graphics.ini. Mở lại game để áp dụng.");
        }
        catch (Exception ex) { _log("[Giả lập XH] Không ghi được graphics.ini: " + ex.Message); }
    }

    private static bool IsAdmin()
    {
        try { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    private bool _gameFound;
    private bool _blocked;

    private void CheckGame()
    {
        var h = WinApi.FindGameWindow();
        _gameFound = h != IntPtr.Zero && WinApi.GetClientRect(h, out WinApi.RECT r0) && r0.Width > 0;
        _blocked = false;
        if (_gameFound)
        {
            WinApi.GetClientRect(h, out WinApi.RECT r);
            _blocked = !WinApi.CanPostTo(h);
            _lblGame.Text = _blocked
                ? "Game FO3 đang chạy quyền admin: hãy mở tool bằng quyền admin (chuột phải > Run as administrator)"
                : $"Game FO3: đã thấy cửa sổ ({r.Width}x{r.Height})";
            _lblGame.ForeColor = _blocked ? Color.FromArgb(230, 160, 60) : Color.FromArgb(70, 210, 120);
        }
        else
        {
            _lblGame.Text = "Game FO3: chưa thấy cửa sổ game";
            _lblGame.ForeColor = Color.FromArgb(230, 80, 80);
        }
        RefreshEnabled();
    }

    /// <summary>No game window = nothing on the tab can work, so everything is disabled; while running only Dừng is live.
    /// The frame size is the opposite: it edits a file the game reads at start, so it is only enabled while the game is closed.</summary>
    private void RefreshEnabled()
    {
        bool running = _runner.IsRunning;
        bool usable = _gameFound && !_blocked;
        bool on = usable && !running;
        _numMatches.Enabled = _numCooldown.Enabled = _tactic.Enabled = _formation.Enabled = on;
        _size.Enabled = !_gameFound && _iniPath != null;
        _btnStart.Enabled = on;
        _btnStop.Enabled = running;
    }

    private void StartAuto()
    {
        if (_runner.IsRunning) return;
        if (WinApi.FindGameWindow() == IntPtr.Zero)
        {
            MessageBox.Show("Chưa thấy cửa sổ game FIFA Online 3 (fifazf.exe). Hãy mở game ở chế độ cửa sổ trước.", "Giả lập xếp hạng", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var settings = new RankedAutoRunner.Settings((int)_numMatches.Value, 15 * 60, 60, 30, (int)_numCooldown.Value, null, _tactic.SelectedIndex, _formation.SelectedIndex);
        _runner.Start(settings);
        RefreshEnabled();
    }

    private void StopAuto() { if (_runner.IsRunning) _runner.Stop(); }

    private void OnFinished() => RefreshEnabled();

    protected override void WndProc(ref Message m)
    {
        const int WM_HOTKEY = 0x0312;
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            if (_runner.IsRunning) StopAuto(); else StartAuto();
        }
        base.WndProc(ref m);
    }

    private void Ui(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a); else a();
    }
}
