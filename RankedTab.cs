using System.Drawing;
using System.Runtime.InteropServices;

namespace FO3SessionTool;

public sealed class RankedTab : UserControl
{
    private readonly Action<string> _mainLog;
    private readonly RankedAutoRunner _runner = new();

    // UI controls
    private readonly Label _lblGameStatus = new() { AutoSize = true, Font = Theme.Bold, Text = "Game FO3: Đang kiểm tra...", ForeColor = Theme.Dim };
    private readonly DarkButton _btnCheckGame = new() { Text = "Kiểm tra game", Width = 110 };
    private readonly Label _lblDetectedScreen = new() { AutoSize = true, Font = Theme.Bold, Text = "Màn hình: Chưa quét", ForeColor = Color.FromArgb(120, 220, 255) };

    private readonly DarkNumber _numMatches = new() { Minimum = 0, Maximum = 1000, Width = 90 };
    private readonly DarkNumber _numMatchDuration = new() { Minimum = 1, Maximum = 30, Width = 80 }; // minutes
    private readonly DarkNumber _numMatchmakeWait = new() { Minimum = 5, Maximum = 180, Width = 80 }; // seconds
    private readonly DarkNumber _numPostMatchWait = new() { Minimum = 5, Maximum = 120, Width = 80 }; // seconds
    private readonly DarkNumber _numCooldown = new() { Minimum = 0, Maximum = 120, Width = 80 }; // seconds

    private readonly Label _lblCustomPos = new() { AutoSize = true, Text = "Tọa độ nút Tìm trận: Tự động (% màn hình)", ForeColor = Theme.Dim };
    private readonly DarkButton _btnResetPos = new() { Text = "Đặt lại tự động", Width = 120, Enabled = false };

    private readonly DarkButton _btnStart = new() { Text = "BẮT ĐẦU AUTO 1VS1 (F9)", Width = 230, Height = 40, Font = Theme.Bold, Tag = "primary" };
    private readonly DarkButton _btnStop = new() { Text = "DỪNG (F9)", Width = 140, Height = 40, Font = Theme.Bold, Enabled = false };
    private readonly DarkButton _btnTestInput = new() { Text = "Thử Click & Phím (sau 3s)", Width = 210, Height = 40, Font = Theme.Body };
    private readonly DarkButton _btnScanScreen = new() { Text = "🔍 Quét màn hình ngay", Width = 165, Height = 40, Font = Theme.Body };

    private readonly Label _lblState = new() { AutoSize = true, Font = Theme.Bold, Text = "Trạng thái: Sẵn sàng", ForeColor = Theme.Gold };
    private readonly Label _lblProgress = new() { AutoSize = true, Font = Theme.Bold, Text = "Tiến độ: 0 / 10 trận", ForeColor = Theme.Accent };

    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None,
        Font = Theme.Mono,
        BackColor = Theme.Surface2,
        ForeColor = Theme.Text,
        Margin = Padding.Empty
    };

    private Point? _customPos;
    private readonly System.Windows.Forms.Timer _checkTimer = new() { Interval = 3000 };

    // Win32 Hotkeys
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int HOTKEY_F9_ID = 9001;
    private const int HOTKEY_F7_ID = 9002;

    public RankedTab(Action<string> mainLog)
    {
        _mainLog = mainLog;
        Dock = DockStyle.Fill;
        Font = Theme.Body;

        _numMatches.Value = 10;
        _numMatchDuration.Value = 8; // 8 minutes default
        _numMatchmakeWait.Value = 45; // 45 seconds
        _numPostMatchWait.Value = 25; // 25 seconds
        _numCooldown.Value = 8;       // 8 seconds

        BuildLayout();

        _runner.LogMessage += msg => InvokeIfRequired(() => Log(msg));
        _runner.StateChanged += state => InvokeIfRequired(() => _lblState.Text = $"Trạng thái: {state}");
        _runner.ScreenDetected += (screen, detail) => InvokeIfRequired(() =>
        {
            var name = screen switch
            {
                GameScreenState.Lobby => "Sảnh Xếp Hạng 1vs1",
                GameScreenState.ReadyScreen => "Màn hình Sẵn Sàng (Nút vàng)",
                GameScreenState.InMatch => "Trong trận đấu (Đang đá)",
                GameScreenState.PostMatch => "Hết trận / Nhận thưởng",
                _ => "Đang tải / Màn hình chuyển tiếp"
            };
            _lblDetectedScreen.Text = $"Màn hình: {name}";
        });
        _runner.ProgressChanged += (current, target) => InvokeIfRequired(() =>
        {
            var tStr = target > 0 ? target.ToString() : "∞";
            _lblProgress.Text = $"Tiến độ: {current} / {tStr} trận";
        });
        _runner.Finished += () => InvokeIfRequired(OnAutoFinished);

        _btnCheckGame.Click += (_, _) => CheckGameStatus();
        _btnStart.Click += (_, _) => StartAuto();
        _btnStop.Click += (_, _) => StopAuto();
        _btnTestInput.Click += async (_, _) => await RunInputTestAsync();
        _btnScanScreen.Click += (_, _) => ScanScreenNow();
        _btnResetPos.Click += (_, _) =>
        {
            _customPos = null;
            _lblCustomPos.Text = "Tọa độ nút Tìm trận: Tự động (% màn hình)";
            _btnResetPos.Enabled = false;
            Log("[Cài đặt] Đã đưa tọa độ nút Tìm trận về mặc định (Tự động theo % cửa sổ).");
        };

        _checkTimer.Tick += (_, _) => CheckGameStatus();
        _checkTimer.Start();

        HandleCreated += (_, _) => RegisterGlobalHotkeys();
        HandleDestroyed += (_, _) => UnregisterGlobalHotkeys();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Game status
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Config panel
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Control & status
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Log

        // Row 1: Game detection bar
        var pnlGame = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        pnlGame.Controls.Add(_lblGameStatus);
        _lblGameStatus.Margin = new Padding(0, 5, 12, 0);
        pnlGame.Controls.Add(_btnCheckGame);
        _lblDetectedScreen.Margin = new Padding(24, 5, 0, 0);
        pnlGame.Controls.Add(_lblDetectedScreen);
        root.Controls.Add(pnlGame, 0, 0);

        // Row 2: Settings Group
        var grpSettings = new DarkGroup { Text = "Cài đặt trận đấu", Dock = DockStyle.Fill, Padding = new Padding(12, 20, 12, 12), Margin = new Padding(0, 0, 0, 8) };
        var pnlCfg = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };

        var lblMatches = new Label { Text = "Số trận (0 = vô hạn):", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 5, 4, 0) };
        var lblDuration = new Label { Text = "Thời gian 1 trận (phút):", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(16, 5, 4, 0) };
        var lblWait = new Label { Text = "Chờ ghép trận (giây):", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(16, 5, 4, 0) };
        var lblPost = new Label { Text = "Nhận thưởng sau trận (giây):", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(16, 5, 4, 0) };
        var lblCool = new Label { Text = "Nghỉ giữa 2 trận (giây):", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(16, 5, 4, 0) };

        pnlCfg.Controls.AddRange(new Control[]
        {
            lblMatches, _numMatches,
            lblDuration, _numMatchDuration,
            lblWait, _numMatchmakeWait,
            lblPost, _numPostMatchWait,
            lblCool, _numCooldown
        });

        var pnlHotkeyInfo = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        var lblHotkeys = new Label
        {
            Text = "Phím tắt: [F9] Bật / Tắt Auto bất cứ lúc nào | [F7] Rê chuột đến nút Tìm trận và ấn F7 để gán tọa độ tùy chỉnh",
            AutoSize = true,
            ForeColor = Theme.Accent
        };
        pnlHotkeyInfo.Controls.Add(lblHotkeys);

        var pnlPos = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        _lblCustomPos.Margin = new Padding(0, 5, 8, 0);
        pnlPos.Controls.Add(_lblCustomPos);
        pnlPos.Controls.Add(_btnResetPos);

        var pnlGroupInner = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 3 };
        pnlGroupInner.Controls.Add(pnlCfg, 0, 0);
        pnlGroupInner.Controls.Add(pnlHotkeyInfo, 0, 1);
        pnlGroupInner.Controls.Add(pnlPos, 0, 2);
        grpSettings.Controls.Add(pnlGroupInner);

        root.Controls.Add(grpSettings, 0, 1);

        // Row 3: Action Buttons & Progress
        var grpAction = new DarkGroup { Text = "Điều khiển", Dock = DockStyle.Fill, Padding = new Padding(12, 20, 12, 12), Margin = new Padding(0, 0, 0, 8) };
        var pnlAction = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };

        _btnStart.Margin = new Padding(0, 0, 12, 0);
        _btnStop.Margin = new Padding(0, 0, 12, 0);
        _btnTestInput.Margin = new Padding(0, 0, 12, 0);
        _btnScanScreen.Margin = new Padding(0, 0, 24, 0);
        _lblState.Margin = new Padding(0, 12, 24, 0);
        _lblProgress.Margin = new Padding(0, 12, 0, 0);

        pnlAction.Controls.AddRange(new Control[] { _btnStart, _btnStop, _btnTestInput, _btnScanScreen, _lblState, _lblProgress });
        grpAction.Controls.Add(pnlAction);
        root.Controls.Add(grpAction, 0, 2);

        // Row 4: Log box
        var grpLog = new DarkGroup { Text = "Nhật ký Auto 1vs1", Dock = DockStyle.Fill, Padding = new Padding(8, 18, 8, 8) };
        grpLog.Controls.Add(_log);
        root.Controls.Add(grpLog, 0, 3);

        Controls.Add(root);
    }

    private void CheckGameStatus()
    {
        var hWnd = WinApi.FindGameWindow();
        if (hWnd != IntPtr.Zero)
        {
            if (WinApi.GetClientRect(hWnd, out WinApi.RECT r) && r.Width > 0 && r.Height > 0)
            {
                _lblGameStatus.Text = $"Game FO3: ĐÃ KẾT NỐI (fifazf.exe - {r.Width}x{r.Height})";
                _lblGameStatus.ForeColor = Color.FromArgb(70, 210, 120); // Green
                return;
            }
        }
        _lblGameStatus.Text = "Game FO3: CHƯA PHÁT HIỆN CỬA SỔ GAME (Hãy mở FO3)";
        _lblGameStatus.ForeColor = Color.FromArgb(230, 80, 80); // Red
    }

    /// <summary>
    /// Chụp game ngay lập tức và in kết quả nhận diện màn hình vào log — dùng để debug threshold.
    /// </summary>
    private void ScanScreenNow()
    {
        var hWnd = WinApi.FindGameWindow();
        if (hWnd == IntPtr.Zero)
        {
            Log("[Quét] Chưa tìm thấy cửa sổ game FO3!");
            return;
        }

        Log("[Quét] Đang chụp và phân tích màn hình game...");
        var (screen, detail) = RankedAutoRunner.DetectScreenPublic(hWnd);
        string name = screen switch
        {
            GameScreenState.Lobby       => "🟡 Sảnh Xếp Hạng 1vs1",
            GameScreenState.ReadyScreen => "🟢 Màn hình Chuẩn bị / Sẵn sàng",
            GameScreenState.InMatch     => "⚽ Trong trận đấu (Đang đá)",
            GameScreenState.PostMatch   => "🏆 Hết trận / Nhận thưởng",
            _                           => "❓ Chưa xác định / Đang tải"
        };
        Log($"[Quét] Kết quả: {name}");
        Log($"[Quét] Chi tiết: {detail}");
        _lblDetectedScreen.Text = $"Màn hình: {name}";
    }

    private void StartAuto()
    {
        if (_runner.IsRunning) return;

        var hWnd = WinApi.FindGameWindow();
        if (hWnd == IntPtr.Zero)
        {
            MessageBox.Show(
                "Không tìm thấy cửa sổ game FIFA Online 3 (fifazf.exe)!\nVui lòng khởi động game và đăng nhập vào sảnh Xếp Hạng trước khi bắt đầu.",
                "Chưa phát hiện game",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        int target = (int)_numMatches.Value;
        int durationSec = (int)(_numMatchDuration.Value * 60);
        int waitSec = (int)_numMatchmakeWait.Value;
        int postSec = (int)_numPostMatchWait.Value;
        int coolSec = (int)_numCooldown.Value;

        var settings = new RankedAutoRunner.Settings(target, durationSec, waitSec, postSec, coolSec, _customPos);

        _btnStart.Enabled = false;
        _btnStop.Enabled = true;
        _numMatches.Enabled = false;
        _numMatchDuration.Enabled = false;
        _numMatchmakeWait.Enabled = false;
        _numPostMatchWait.Enabled = false;
        _numCooldown.Enabled = false;
        _btnResetPos.Enabled = false;

        _runner.Start(settings);
    }

    private void StopAuto()
    {
        if (!_runner.IsRunning) return;
        _runner.Stop();
    }

    private void OnAutoFinished()
    {
        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        _numMatches.Enabled = true;
        _numMatchDuration.Enabled = true;
        _numMatchmakeWait.Enabled = true;
        _numPostMatchWait.Enabled = true;
        _numCooldown.Enabled = true;
        _btnResetPos.Enabled = _customPos.HasValue;
    }

    private void Log(string msg)
    {
        if (_log.IsDisposed) return;
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n";
        _log.AppendText(line);
        _mainLog($"[Auto 1v1] {msg}");
    }

    private async Task RunInputTestAsync()
    {
        var hWnd = WinApi.FindGameWindow();
        if (hWnd == IntPtr.Zero)
        {
            MessageBox.Show("Chưa tìm thấy cửa sổ game FO3! Hãy mở game trước.", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnTestInput.Enabled = false;
        Log("[Thử nghiệm] Bắt đầu thử nghiệm sau 3 giây... Hãy nhìn vào game!");
        for (int i = 3; i >= 1; i--)
        {
            Log($"[Thử nghiệm] {i}s...");
            await Task.Delay(1000);
        }

        WinApi.FakeFocus(hWnd);
        await Task.Delay(200);

        Log("[Thử nghiệm] Thử click chuột vào nút 1vs1 (X: 25.5%, Y: 80%)...");
        WinApi.ClickClientPercentBackground(hWnd, 0.255, 0.800);
        await Task.Delay(300);

        Log("[Thử nghiệm] Thử gửi phím Enter...");
        WinApi.SendKeyPress(hWnd, WinApi.VK_RETURN, 100);
        await Task.Delay(300);

        Log("[Thử nghiệm] Thử gửi phím mũi tên Lên...");
        WinApi.SendKeyDown(hWnd, WinApi.VK_UP);
        await Task.Delay(300);
        WinApi.SendKeyUp(hWnd, WinApi.VK_UP);

        Log("[Thử nghiệm] Hoàn tất thử nghiệm!");
        _btnTestInput.Enabled = true;
    }

    private void CaptureMousePos()
    {
        var hWnd = WinApi.FindGameWindow();
        if (hWnd == IntPtr.Zero)
        {
            Log("[F7] Không tìm thấy cửa sổ game FO3 để lấy tọa độ!");
            return;
        }

        if (WinApi.GetCursorPos(out WinApi.POINT screenPt))
        {
            var clientPt = screenPt;
            WinApi.ScreenToClient(hWnd, ref clientPt);

            WinApi.GetClientRect(hWnd, out WinApi.RECT r);
            double xPct = r.Width > 0 ? (double)clientPt.X / r.Width : 0;
            double yPct = r.Height > 0 ? (double)clientPt.Y / r.Height : 0;

            _customPos = new Point(clientPt.X, clientPt.Y);
            _lblCustomPos.Text = $"Tọa độ nút Tìm trận: ({clientPt.X}, {clientPt.Y}) - [{xPct:P1}, {yPct:P1}]";
            _btnResetPos.Enabled = true;

            Log($"[F7] ĐÃ LƯU TỌA ĐỘ NÚT TÌM TRẬN: ({clientPt.X}, {clientPt.Y}) theo cửa sổ game.");
        }
    }

    // ==========================================
    // Hotkeys & WndProc
    // ==========================================

    private void RegisterGlobalHotkeys()
    {
        try
        {
            // F9 to start / stop
            RegisterHotKey(Handle, HOTKEY_F9_ID, 0, WinApi.VK_F9);
            // F7 to capture mouse position
            RegisterHotKey(Handle, HOTKEY_F7_ID, 0, WinApi.VK_F7);
        }
        catch { }
    }

    private void UnregisterGlobalHotkeys()
    {
        try
        {
            UnregisterHotKey(Handle, HOTKEY_F9_ID);
            UnregisterHotKey(Handle, HOTKEY_F7_ID);
        }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_HOTKEY = 0x0312;
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HOTKEY_F9_ID)
            {
                if (_runner.IsRunning) StopAuto();
                else StartAuto();
            }
            else if (id == HOTKEY_F7_ID)
            {
                CaptureMousePos();
            }
        }
        base.WndProc(ref m);
    }

    private void InvokeIfRequired(Action action)
    {
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}

