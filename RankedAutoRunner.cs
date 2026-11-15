using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FO3SessionTool;

public enum GameScreenState
{
    Unknown,
    Lobby,        // Màn hình chính các tab (PVP...), có thanh tab dưới cùng
    ModeRank,     // Màn hình chọn chế độ Xếp hạng (1đấu1, 2đấu2, 3đấu3, Đấu giả lập)
    Searching,    // Đang tìm trận (huy hiệu vàng xoay giữa màn hình)
    Intro,        // Cảnh giới thiệu sân có chữ "Bỏ qua [SPACE]"
    ReadyScreen,  // Màn hình chọn đội hình / chờ Sẵn sàng (Nút vàng Sẵn sàng)
    InMatch,      // Trong trận đấu (Sân cỏ bóng đá chiếm ưu thế)
    PostMatch     // Hết trận, bảng kết quả, lật thẻ phần thưởng
}

/// <summary>
/// Smart, vision-based automation runner for FO3 Ranked 1v1:
/// Analyzes the game screen in real time (Lobby vs Ready vs InMatch vs PostMatch)
/// and adaptively takes the appropriate action. Seamlessly supports starting mid-match!
/// </summary>
public sealed class RankedAutoRunner
{
    public sealed record Settings(
        int TargetMatches,       // 0 = infinite
        int MatchDurationSec,    // safety cap (default ~600s)
        int MatchmakeTimeoutSec, // default ~60s
        int PostMatchSec,        // default ~30s
        int CooldownSec,         // default ~8s
        Point? CustomMatchmakePos = null,
        int Tactic = 0,          // 0 = leave as is, 1..6 = tactic tab to select at the start of a match
        int Formation = 0,       // 0 = leave as is, 1..3 = lineup A, B, C on the lineup screen
        bool RealKeys = false    // Space for the "Bỏ qua" screens as a real key press (game brought to the front for a moment)
    );

    public event Action<string>? LogMessage;
    public event Action<string>? StateChanged;
    public event Action<GameScreenState, string>? ScreenDetected;
    public event Action<int, int>? ProgressChanged; // currentMatch, targetMatches
    public event Action? Finished;

    private CancellationTokenSource? _cts;
    private bool _running;
    public bool IsRunning => _running;

    public void Start(Settings settings)
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        Task.Run(() => RunAdaptiveLoopAsync(settings, _cts.Token));
    }

    public void Stop()
    {
        if (!_running) return;
        _cts?.Cancel();
        _running = false;
        Log("[Auto 1v1] Đã nhận lệnh Dừng!");
        StateChanged?.Invoke("Đã dừng");
    }

    // Button positions as a fraction of the game's client area (measured on real screenshots of the game).
    private const double PvpTabX = 0.381, PvpTabY = 0.968;       // PVP tab in the bottom bar
    private const double RankCardX = 0.563, RankCardY = 0.865;   // "Xếp hạng" card on the PVP tab
    private const double SimBtnX = 0.728, SimBtnY = 0.792;       // "ĐẤU GIẢ LẬP" on the ranked mode screen
    // tactic tabs 1..6 along the bottom panel in the match (measured on a 1600x900 client)
    private static readonly double[] TacticTabX = { 0.198, 0.3125, 0.428, 0.541, 0.656, 0.7725 };
    private const double TacticTabY = 0.896;
    // lineup screen: the "Đội hình B ▲" button and the A / B / C entries of the menu it opens upwards (measured on a screenshot of the open menu)
    private const double FormationBtnX = 0.443, FormationBtnY = 0.903, FormationItemX = 0.451;
    private static readonly double[] FormationItemY = { 0.780, 0.817, 0.853 };
    private const double ReadyX = 0.795, ReadyY = 0.966;        // "Sẵn sàng" (lineup screen and pre-match screen)

    private async Task RunAdaptiveLoopAsync(Settings cfg, CancellationToken ct)
    {
        try
        {
            Log("[Giả lập XH] Bắt đầu: tab PVP > Xếp hạng > Đấu giả lập > Sẵn sàng > chờ hết trận. Chỉ gửi lệnh vào cửa sổ game, không dùng chuột/bàn phím của bạn.");
            var hWnd = WinApi.FindGameWindow();
            if (hWnd == IntPtr.Zero)
            {
                Log("[Giả lập XH] Lỗi: chưa thấy cửa sổ game FO3 (fifazf.exe). Hãy mở game trước.");
                return;
            }
            WinApi.FakeFocus(hWnd);

            int completed = 0;
            bool inMatch = false;
            var matchWatch = new Stopwatch();
            var sinceNotMatch = new Stopwatch();
            var step = new Stopwatch(); step.Start();
            int menuStep = 0;
            int tacticClicks = 0;
            bool needFormation = false;
            GameScreenState last = GameScreenState.Unknown;

            while (!ct.IsCancellationRequested)
            {
                var (screen, detail) = DetectScreen(hWnd);
                ScreenDetected?.Invoke(screen, detail);
                if (screen != last)
                {
                    Log($"[Màn hình] {GetScreenName(screen)} ({detail})");
                    last = screen;
                    step.Restart();
                    menuStep = 0;
                }

                if (screen == GameScreenState.InMatch)
                {
                    sinceNotMatch.Reset();
                    if (!inMatch)
                    {
                        inMatch = true;
                        tacticClicks = 0;
                        matchWatch.Restart();
                        Log("[Trận đấu] Trận bắt đầu, chờ hết trận (game tự đá, không cần bấm phím).");
                    }
                    int sec = (int)matchWatch.Elapsed.TotalSeconds;
                    StateChanged?.Invoke($"Đang thi đấu ({sec / 60}m{sec % 60:D2}s)...");
                    // tactic tab (1..6): clicked a few seconds in, and once more later in case the first click came before the panel was up
                    if (cfg.Tactic is >= 1 and <= 6 && tacticClicks < 2 && sec >= (tacticClicks == 0 ? 3 : 15))
                    {
                        tacticClicks++;
                        Log($"[Chiến thuật] Chọn chiến thuật {cfg.Tactic}.");
                        WinApi.ClickClientPercentBackground(hWnd, TacticTabX[cfg.Tactic - 1], TacticTabY);
                    }
                    if (cfg.MatchDurationSec > 0 && sec > cfg.MatchDurationSec)
                    {
                        Log($"[Trận đấu] Quá {cfg.MatchDurationSec}s mà chưa hết trận, dừng để bạn kiểm tra.");
                        return;
                    }
                    await Task.Delay(2000, ct);
                    continue;
                }

                // a match counts as finished once the pitch has been gone for a few seconds
                if (inMatch)
                {
                    // Replays, close-ups and "Bỏ qua" cutscenes look nothing like the pitch view, so "the pitch is gone" does NOT mean the match is over.
                    // The match is over only when the game is back on the mode list / menu (that is where it lands after the last reward screen).
                    // The yellow-button screens (Kết quả, Nhận quà) are NOT used: they are clicked through but do not count.
                    bool afterMatch = screen is GameScreenState.ModeRank or GameScreenState.Lobby;
                    if (!afterMatch) sinceNotMatch.Reset();
                    else if (!sinceNotMatch.IsRunning) sinceNotMatch.Restart();
                    if (cfg.MatchDurationSec > 0 && matchWatch.Elapsed.TotalSeconds > cfg.MatchDurationSec + 120)
                    {
                        Log("[Trận đấu] Chờ quá lâu mà chưa thấy màn kết quả, dừng để bạn kiểm tra.");
                        return;
                    }
                    if (afterMatch && sinceNotMatch.Elapsed.TotalSeconds >= 2 && matchWatch.Elapsed.TotalSeconds >= 60)
                    {
                        inMatch = false;
                        completed++;
                        ProgressChanged?.Invoke(completed, cfg.TargetMatches);
                        Log($"[Giả lập XH] Xong trận {completed} / {(cfg.TargetMatches > 0 ? cfg.TargetMatches : "∞")}.");
                        if (cfg.TargetMatches > 0 && completed >= cfg.TargetMatches)
                        {
                            Log("[Giả lập XH] Đủ số trận, dừng.");
                            return;
                        }
                        StateChanged?.Invoke($"Nghỉ {cfg.CooldownSec}s...");
                        await Task.Delay(cfg.CooldownSec * 1000, ct);
                        continue;
                    }
                }

                switch (screen)
                {
                    case GameScreenState.ReadyScreen:
                        if (needFormation && !inMatch)
                        {
                            needFormation = false;
                            Log($"[Sơ đồ] Chọn Đội hình {"ABC"[cfg.Formation - 1]}.");
                            WinApi.ClickClientPercentBackground(hWnd, FormationBtnX, FormationBtnY);
                            await Task.Delay(600, ct);
                            WinApi.ClickClientPercentBackground(hWnd, FormationItemX, FormationItemY[cfg.Formation - 1]);
                            await Task.Delay(800, ct);
                            break;
                        }
                        StateChanged?.Invoke("Bấm Sẵn sàng...");
                        if (menuStep == 0 || step.Elapsed.TotalSeconds >= 2)
                        {
                            menuStep++;
                            step.Restart();
                            WinApi.ClickClientPercentBackground(hWnd, ReadyX, ReadyY);
                        }
                        await Task.Delay(700, ct);
                        break;

                    case GameScreenState.Searching:
                        needFormation = cfg.Formation is >= 1 and <= 3;   // the next yellow-button screen is the lineup screen
                        StateChanged?.Invoke($"Đang tìm trận ({(int)step.Elapsed.TotalSeconds}s)...");
                        if (cfg.MatchmakeTimeoutSec > 0 && step.Elapsed.TotalSeconds > cfg.MatchmakeTimeoutSec && menuStep == 0)
                        {
                            menuStep = 1;
                            Log($"[Tìm trận] Đã chờ quá {cfg.MatchmakeTimeoutSec}s chưa có trận, vẫn tiếp tục chờ.");
                        }
                        await Task.Delay(1000, ct);
                        break;

                    case GameScreenState.Intro:
                        StateChanged?.Invoke("Bỏ qua cảnh giới thiệu...");
                        if (menuStep == 0 || step.Elapsed.TotalSeconds >= 1.5)
                        {
                            menuStep++;
                            step.Restart();
                            Log($"[Bỏ qua] Gửi phím Space (lần {menuStep}).");
                            if (cfg.RealKeys) WinApi.SendRealKey(hWnd, WinApi.VK_SPACE);
                            else WinApi.SendKeyPress(hWnd, WinApi.VK_SPACE, 90);
                        }
                        await Task.Delay(500, ct);
                        break;

                    case GameScreenState.ModeRank:
                        StateChanged?.Invoke("Chọn ĐẤU GIẢ LẬP...");
                        if (menuStep == 0 || step.Elapsed.TotalSeconds >= 4)
                        {
                            menuStep++;
                            step.Restart();
                            Log("[Xếp hạng] Bấm ĐẤU GIẢ LẬP.");
                            if (cfg.CustomMatchmakePos.HasValue) WinApi.ClickClientBackground(hWnd, cfg.CustomMatchmakePos.Value.X, cfg.CustomMatchmakePos.Value.Y);
                            else WinApi.ClickClientPercentBackground(hWnd, SimBtnX, SimBtnY);
                        }
                        await Task.Delay(800, ct);
                        break;

                    case GameScreenState.Lobby:
                        StateChanged?.Invoke("Vào tab PVP > Xếp hạng...");
                        // the "Xếp hạng" card is only clicked after the PVP tab was clicked, and the screen is re-checked every loop
                        if (menuStep == 0) { menuStep = 1; step.Restart(); Log("[Menu] Bấm tab PVP."); WinApi.ClickClientPercentBackground(hWnd, PvpTabX, PvpTabY); }
                        else if (menuStep == 1 && step.Elapsed.TotalSeconds >= 1.5) { menuStep = 2; step.Restart(); Log("[Menu] Bấm thẻ Xếp hạng."); WinApi.ClickClientPercentBackground(hWnd, RankCardX, RankCardY); }
                        else if (menuStep >= 2 && step.Elapsed.TotalSeconds >= 6) { menuStep = 0; step.Restart(); }
                        await Task.Delay(700, ct);
                        break;

                    default:
                        // loading and the points screen: the game moves on by itself, so only wait (no blind clicks or keys)
                        StateChanged?.Invoke("Đang tải / chờ màn hình tiếp theo...");
                        await Task.Delay(1000, ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("[Giả lập XH] Đã dừng theo yêu cầu.");
        }
        catch (Exception ex)
        {
            Log($"[Giả lập XH] Lỗi: {ex.Message}");
        }
        finally
        {
            _running = false;
            StateChanged?.Invoke("Đã dừng");
            Finished?.Invoke();
        }
    }

    private void Log(string msg) => LogMessage?.Invoke(msg);

    private static string GetScreenName(GameScreenState s) => s switch
    {
        GameScreenState.Lobby => "Màn hình chính (tab PVP...)",
        GameScreenState.ModeRank => "Chọn chế độ Xếp hạng",
        GameScreenState.Searching => "Đang tìm trận",
        GameScreenState.Intro => "Cảnh giới thiệu (Bỏ qua)",
        GameScreenState.ReadyScreen => "Màn hình Chuẩn bị / Sẵn sàng",
        GameScreenState.InMatch => "Trong trận đấu (Đá bóng)",
        GameScreenState.PostMatch => "Hết trận / Kết quả & Mở thẻ",
        _ => "Đang tải / Màn hình chuyển tiếp"
    };

    // ==========================================
    // Visual Screen Recognition Engine
    // ==========================================
    // Screen layout references (1366x768 or similar):
    // - Lobby (màn hình PVP): bottom nav bar dark, grass background ~10-30%, dark popup panel center
    // - ReadyScreen: "Sẵn sàng" green button at bottom-right + timer text; both sides show team lineup
    // - InMatch: entire background is football pitch (grass > 35%)
    // - PostMatch: dark overlay/result panel, low grass

    // Public wrapper for external callers (e.g. debug scan button in RankedTab)
    public static (GameScreenState State, string Detail) DetectScreenPublic(IntPtr hWnd)
        => DetectScreen(hWnd);

    private static (GameScreenState State, string Detail) DetectScreen(IntPtr hWnd)
    {
        using var bmp = WinApi.CaptureGameWindow(hWnd);
        if (bmp == null) return (GameScreenState.Unknown, "Không chụp được cửa sổ game (đang thu nhỏ?)");
        using var t = WinApi.Downscale(bmp, 160, 90);

        // Measured on real screenshots of the game: the yellow "Sẵn sàng" button bottom right gives 60+ px,
        // the pitch is 0.81-0.89 of the picture in a match and 0.17-0.31 in menus (which have a dark tab bar),
        // and the mode screen has a big dark panel in the middle (83% dark, the home tab 16%).
        int yellow = 0;
        for (int y = 80; y < 90; y++)
            for (int x = 112; x < 152; x++)
            {
                var c = t.GetPixel(x, y);
                if (c.R >= 150 && c.G >= 125 && c.B <= 100 && c.R - c.B > 90) yellow++;
            }
        if (yellow >= 20) return (GameScreenState.ReadyScreen, $"nút Sẵn sàng ({yellow}px vàng)");

        int grass = 0, n = 0;
        for (int y = 10; y < 80; y += 2)
            for (int x = 10; x < 150; x += 2)
            {
                n++;
                var c = t.GetPixel(x, y);
                if (c.G >= 55 && c.G > c.R + 12 && c.G > c.B + 12) grass++;
            }
        double grassRatio = (double)grass / n;
        int darkBar = 0;
        for (int x = 5; x < 155; x += 4) { var c = t.GetPixel(x, 87); if (Math.Max(c.R, Math.Max(c.G, c.B)) < 60) darkBar++; }

        // searching: big gold badge in the middle (282 px on the real screenshot, other screens < 10)
        int gold = 0;
        for (int y = 28; y < 62; y++)
            for (int x = 55; x < 105; x++)
            {
                var c = t.GetPixel(x, y);
                if (c.R >= 170 && c.G >= 130 && c.B <= 90 && c.R - c.B > 100) gold++;
            }
        if (gold >= 100) return (GameScreenState.Searching, $"đang tìm trận (huy hiệu vàng {gold}px)");

        // pitch everywhere: in a match the tactics panel at the bottom is dark (20+), the intro cutscene ("Bỏ qua [SPACE]") has none
        if (grassRatio >= 0.55)
            return darkBar <= 3 ? (GameScreenState.Intro, $"cảnh giới thiệu, sân cỏ {grassRatio:P0}") : (GameScreenState.InMatch, $"sân cỏ {grassRatio:P0}");

        darkBar = 0;
        for (int x = 5; x < 155; x += 4) { var c = t.GetPixel(x, 87); if (Math.Max(c.R, Math.Max(c.G, c.B)) < 60) darkBar++; }
        if (darkBar >= 25 && grassRatio < 0.45)
        {
            int dark = 0, m = 0;
            for (int y = 25; y < 50; y++)
                for (int x = 50; x < 110; x++) { m++; var c = t.GetPixel(x, y); if (Math.Max(c.R, Math.Max(c.G, c.B)) < 60) dark++; }
            double centerDark = (double)dark / m;
            return centerDark >= 0.6
                ? (GameScreenState.ModeRank, $"màn chọn chế độ (tối giữa {centerDark:P0})")
                : (GameScreenState.Lobby, $"màn hình chính (thanh tab tối, tối giữa {centerDark:P0})");
        }
        return (GameScreenState.Unknown, $"chưa rõ (cỏ {grassRatio:P0}, thanh tab {darkBar})");
    }
}

/// <summary>
/// Native Win32 helpers for window discovery, background mouse clicking, and DirectX hardware keyboard simulation.
/// </summary>
public static class WinApi
{
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_ESCAPE = 0x1B;
    public const ushort VK_SPACE  = 0x20;
    public const ushort VK_F3     = 0x72;
    public const ushort VK_F7     = 0x76;
    public const ushort VK_F9     = 0x78;

    // Movement keys (Extended scan codes)
    public const ushort VK_LEFT  = 0x25;
    public const ushort VK_UP    = 0x26;
    public const ushort VK_RIGHT = 0x27;
    public const ushort VK_DOWN  = 0x28;

    // Default FO2 Action keys
    public const ushort VK_A = 0x41;
    public const ushort VK_C = 0x43;
    public const ushort VK_D = 0x44;
    public const ushort VK_E = 0x45;
    public const ushort VK_Q = 0x51;
    public const ushort VK_S = 0x53;
    public const ushort VK_W = 0x57;
    public const ushort VK_Z = 0x5A;

    public const uint WM_MOUSEMOVE   = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP   = 0x0202;
    public const uint WM_KEYDOWN     = 0x0100;
    public const uint WM_KEYUP       = 0x0101;
    public const uint WM_CHAR        = 0x0102;
    public const uint WM_ACTIVATE    = 0x0006;
    public const uint WM_SETFOCUS    = 0x0007;
    public const uint WM_ACTIVATEAPP = 0x001C;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    /// <summary>Finds the FIFA Online 3 game window by process name or window title.</summary>
    public static IntPtr FindGameWindow()
    {
        // 1. Try exact title first
        IntPtr h = FindWindow(null, "FIFA ONLINE3 - Developed by SPEARHEAD");
        if (h != IntPtr.Zero && IsWindowVisible(h)) return h;

        // 2. Search through processes named "fifazf"
        var procs = Process.GetProcessesByName("fifazf");
        foreach (var p in procs)
        {
            if (p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle))
                return p.MainWindowHandle;
        }

        // 3. Fallback: EnumWindows to find any window with "FIFA ONLINE" in title
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            string title = sb.ToString();
            if (title.Contains("FIFA ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    public static Bitmap? CaptureGameWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return null;
        if (!GetClientRect(hWnd, out RECT rect) || rect.Width <= 100 || rect.Height <= 100) return null;

        try
        {
            // 1. PrintWindow: renders the window itself, so it works when other windows cover it
            var shot = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(shot))
            {
                IntPtr dc = g.GetHdc();
                bool ok;
                try { ok = PrintWindow(hWnd, dc, 3 /* PW_CLIENTONLY | PW_RENDERFULLCONTENT */); }
                finally { g.ReleaseHdc(dc); }
                if (ok && !LooksBlank(shot)) return shot;
            }
            shot.Dispose();
        }
        catch { }

        // 2. fallback: copy from the screen (needs the game area to be visible)
        var pt = new POINT { X = 0, Y = 0 };
        ClientToScreen(hWnd, ref pt);

        try
        {
            var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(pt.X, pt.Y, 0, 0, new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    /// <summary>DirectX windows often come out of PrintWindow all black: sample a grid and see.</summary>
    private static bool LooksBlank(Bitmap b)
    {
        for (int y = 5; y < b.Height; y += Math.Max(1, b.Height / 12))
            for (int x = 5; x < b.Width; x += Math.Max(1, b.Width / 12))
            {
                var c = b.GetPixel(x, y);
                if (c.R + c.G + c.B > 30) return false;
            }
        return true;
    }

    public static Bitmap Downscale(Bitmap src, int width, int height)
    {
        var thumb = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(src, 0, 0, width, height);
        }
        return thumb;
    }

    [StructLayout(LayoutKind.Sequential)] private struct KI { public ushort vk; public ushort sc; public uint fl; public uint t; public UIntPtr ex; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct IN { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KI ki; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, IN[] i, int cb);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

    /// <summary>A real key press (SendInput): the game is brought to the front for a moment and the previous window is restored afterwards.</summary>
    public static void SendRealKey(IntPtr hWnd, ushort vk)
    {
        var prev = GetForegroundWindow();
        ActivateWindow(hWnd);
        Thread.Sleep(250);
        ushort sc = (ushort)MapVirtualKey(vk, 0);
        var down = new IN[1]; down[0].type = 1; down[0].ki.vk = vk; down[0].ki.sc = sc;
        var up = new IN[1]; up[0].type = 1; up[0].ki.vk = vk; up[0].ki.sc = sc; up[0].ki.fl = 2;
        SendInput(1, down, Marshal.SizeOf<IN>());
        Thread.Sleep(80);
        SendInput(1, up, Marshal.SizeOf<IN>());
        Thread.Sleep(120);
        if (prev != IntPtr.Zero && prev != hWnd) SetForegroundWindow(prev);
    }

    /// <summary>Resizes the game window so that its client area (the drawn part) is width x height. Returns false if the window did not take it.</summary>
    public static bool SetClientSize(IntPtr hWnd, int width, int height)
    {
        if (hWnd == IntPtr.Zero || !GetWindowRect(hWnd, out RECT w) || !GetClientRect(hWnd, out RECT c)) return false;
        int extraW = w.Width - c.Width, extraH = w.Height - c.Height;
        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, width + extraW, height + extraH, 0x0002 | 0x0004 | 0x0010);   // NOMOVE | NOZORDER | NOACTIVATE
        Thread.Sleep(300);
        return GetClientRect(hWnd, out RECT n) && Math.Abs(n.Width - width) <= 2 && Math.Abs(n.Height - height) <= 2;
    }

    public static void ActivateWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg != hWnd)
            {
                uint curThread = GetCurrentThreadId();
                uint fgThread = GetWindowThreadProcessId(fg, out _);
                uint targetThread = GetWindowThreadProcessId(hWnd, out _);

                if (curThread != targetThread)
                    AttachThreadInput(curThread, targetThread, true);
                if (fgThread != 0 && fgThread != curThread && fgThread != targetThread)
                    AttachThreadInput(curThread, fgThread, true);

                ShowWindow(hWnd, 9); // SW_RESTORE
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);

                if (curThread != targetThread)
                    AttachThreadInput(curThread, targetThread, false);
                if (fgThread != 0 && fgThread != curThread && fgThread != targetThread)
                    AttachThreadInput(curThread, fgThread, false);
            }
        }
        catch { }
    }

    /// <summary>
    /// Background click via PostMessage directly to client area: NEVER moves or hijacks the user's mouse cursor!
    /// </summary>
    public static void ClickClientPercentBackground(IntPtr hWnd, double xPercent, double yPercent)
    {
        if (hWnd == IntPtr.Zero) return;
        if (!GetClientRect(hWnd, out RECT rect) || rect.Width <= 0 || rect.Height <= 0) return;

        int clientX = (int)(rect.Width * xPercent);
        int clientY = (int)(rect.Height * yPercent);
        ClickClientBackground(hWnd, clientX, clientY);
    }

    /// <summary>
    /// Background click via PostMessage directly to client area: NEVER moves or hijacks the user's mouse cursor!
    /// </summary>
    public static void ClickClientBackground(IntPtr hWnd, int clientX, int clientY)
    {
        if (hWnd == IntPtr.Zero) return;
        IntPtr lParam = (IntPtr)((clientY << 16) | (clientX & 0xFFFF));
        PostMessage(hWnd, WM_MOUSEMOVE, IntPtr.Zero, lParam);
        PostMessage(hWnd, WM_LBUTTONDOWN, (IntPtr)1, lParam);
        Thread.Sleep(40);
        PostMessage(hWnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
    }

    // Input goes ONLY to the game window through its message queue (PostMessage): nothing touches the real mouse cursor,
    // the foreground window or the global keyboard state, so the user can keep working in other windows.
    public static void SendKeyDown(IntPtr hWnd, ushort vk)
    {
        if (hWnd == IntPtr.Zero) return;
        uint scan = MapVirtualKey(vk, 0);
        uint lParam = 1u | (scan << 16);
        if (IsExtendedKey(vk)) lParam |= 1u << 24;
        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)lParam);
        if (vk is VK_SPACE or VK_RETURN) PostMessage(hWnd, WM_CHAR, (IntPtr)vk, (IntPtr)lParam);   // some UIs read the character message
    }

    public static void SendKeyUp(IntPtr hWnd, ushort vk)
    {
        if (hWnd == IntPtr.Zero) return;
        uint scan = MapVirtualKey(vk, 0);
        uint lParam = 1u | (scan << 16) | (1u << 30) | (1u << 31);
        if (IsExtendedKey(vk)) lParam |= 1u << 24;
        PostMessage(hWnd, WM_KEYUP, (IntPtr)vk, (IntPtr)lParam);
    }

    public static void SendKeyPress(IntPtr hWnd, ushort vk, int holdMs = 60)
    {
        SendKeyDown(hWnd, vk);
        if (holdMs > 0) Thread.Sleep(holdMs);
        SendKeyUp(hWnd, vk);
    }

    /// <summary>Tells the game window it is active and focused (messages only, the real foreground window is not changed),
    /// because many games ignore input while they think they are in the background.</summary>
    public static void FakeFocus(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        PostMessage(hWnd, WM_ACTIVATEAPP, (IntPtr)1, IntPtr.Zero);
        PostMessage(hWnd, WM_ACTIVATE, (IntPtr)1, IntPtr.Zero);
        PostMessage(hWnd, WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool IsExtendedKey(ushort vk) =>
        vk is VK_UP or VK_DOWN or VK_LEFT or VK_RIGHT;
}
