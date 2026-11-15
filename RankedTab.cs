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
    private readonly DarkNumber _numMinutes = new() { Minimum = 1, Maximum = 30, Width = 90 };
    private readonly DarkNumber _numCooldown = new() { Minimum = 0, Maximum = 120, Width = 90 };
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
        _numMinutes.Value = 8;
        _numCooldown.Value = 8;
        _tactic.Items.AddRange(new object[] { "Giữ nguyên", "Chiến thuật 1", "Chiến thuật 2", "Chiến thuật 3", "Chiến thuật 4", "Chiến thuật 5", "Chiến thuật 6" });
        _tactic.SelectedIndex = 0;

        BuildLayout();

        _runner.LogMessage += msg => Ui(() => _log("[Giả lập XH] " + msg));
        _runner.StateChanged += s => Ui(() => _lblState.Text = "Trạng thái: " + s);
        _runner.ScreenDetected += (screen, _) => Ui(() => _lblScreen.Text = "Màn hình: " + ScreenName(screen));
        _runner.ProgressChanged += (cur, target) => Ui(() => _lblProgress.Text = $"Tiến độ: {cur} / {(target > 0 ? target.ToString() : "∞")} trận");
        _runner.Finished += () => Ui(OnFinished);

        _btnStart.Click += (_, _) => StartAuto();
        _btnStop.Click += (_, _) => StopAuto();
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
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        _lblGame.Margin = new Padding(0, 3, 24, 0);
        _lblScreen.Margin = new Padding(0, 3, 0, 0);
        top.Controls.Add(_lblGame);
        top.Controls.Add(_lblScreen);
        root.Controls.Add(top, 0, 0);

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
        Row("Chiến thuật trong trận", _tactic);
        Row("Tối đa mỗi trận (phút)", _numMinutes);
        Row("Nghỉ giữa 2 trận (giây)", _numCooldown);
        grp.Controls.Add(grid);
        root.Controls.Add(grp, 0, 1);

        var act = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        _btnStart.Margin = new Padding(0, 0, 12, 8);
        _btnStop.Margin = new Padding(0, 0, 24, 8);
        _lblState.Margin = new Padding(0, 12, 24, 0);
        _lblProgress.Margin = new Padding(0, 12, 0, 0);
        act.Controls.AddRange(new Control[] { _btnStart, _btnStop, _lblState, _lblProgress });
        root.Controls.Add(act, 0, 2);

        Controls.Add(root);
    }

    private void CheckGame()
    {
        var h = WinApi.FindGameWindow();
        if (h != IntPtr.Zero && WinApi.GetClientRect(h, out WinApi.RECT r) && r.Width > 0)
        {
            _lblGame.Text = $"Game FO3: đã thấy cửa sổ ({r.Width}x{r.Height})";
            _lblGame.ForeColor = Color.FromArgb(70, 210, 120);
        }
        else
        {
            _lblGame.Text = "Game FO3: chưa thấy cửa sổ game";
            _lblGame.ForeColor = Color.FromArgb(230, 80, 80);
        }
    }

    private void SetEditable(bool on)
    {
        _numMatches.Enabled = _numMinutes.Enabled = _numCooldown.Enabled = _tactic.Enabled = on;
        _btnStart.Enabled = on;
        _btnStop.Enabled = !on;
    }

    private void StartAuto()
    {
        if (_runner.IsRunning) return;
        if (WinApi.FindGameWindow() == IntPtr.Zero)
        {
            MessageBox.Show("Chưa thấy cửa sổ game FIFA Online 3 (fifazf.exe). Hãy mở game ở chế độ cửa sổ trước.", "Giả lập xếp hạng", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var settings = new RankedAutoRunner.Settings((int)_numMatches.Value, (int)_numMinutes.Value * 60, 60, 30, (int)_numCooldown.Value, null, _tactic.SelectedIndex);
        SetEditable(false);
        _runner.Start(settings);
    }

    private void StopAuto() { if (_runner.IsRunning) _runner.Stop(); }

    private void OnFinished() => SetEditable(true);

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
