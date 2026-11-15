using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FO3SessionTool;

public enum GameScreenState
{
    Unknown,
    Lobby,        // Sảnh Xếp hạng (PVP lobby)
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
        Point? CustomMatchmakePos = null
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

    private async Task RunAdaptiveLoopAsync(Settings cfg, CancellationToken ct)
    {
        try
        {
            Log("[Auto 1v1] Khởi động Auto Xếp Hạng 1vs1 (Hệ thống nhận diện màn hình thông minh)...");
            StateChanged?.Invoke("Đang nhận diện màn hình...");

            var hWnd = WinApi.FindGameWindow();
            if (hWnd == IntPtr.Zero)
            {
                Log("[Auto 1v1] Lỗi: Chưa phát hiện cửa sổ game FO3 (fifazf.exe)! Vui lòng mở game trước.");
                return;
            }

            WinApi.FakeFocus(hWnd);
            int completedMatches = 0;
            bool wasInMatch = false;
            var matchStopwatch = new Stopwatch();
            var stateStopwatch = new Stopwatch();
            stateStopwatch.Start();

            var rnd = new Random();
            ushort[] moveKeys = { WinApi.VK_UP, WinApi.VK_DOWN, WinApi.VK_LEFT, WinApi.VK_RIGHT };

            GameScreenState lastScreen = GameScreenState.Unknown;
            int readyClickCount = 0;
            int lobbyClickCount = 0;
            int lastLogSec = 0;

            while (!ct.IsCancellationRequested)
            {
                // Capture and detect current game screen
                var (currentScreen, detail) = DetectScreen(hWnd);
                ScreenDetected?.Invoke(currentScreen, detail);

                if (currentScreen != lastScreen)
                {
                    Log($"[Màn hình] Chuyển trạng thái: {GetScreenName(currentScreen)} ({detail})");
                    lastScreen = currentScreen;
                    stateStopwatch.Restart();
                    readyClickCount = 0;
                    lobbyClickCount = 0;
                }

                switch (currentScreen)
                {
                    // -------------------------------------------------------------
                    // 1. SẢNH XẾP HẠNG (LOBBY)
                    // -------------------------------------------------------------
                    case GameScreenState.Lobby:
                        if (wasInMatch)
                        {
                            // We just returned from a match to the lobby!
                            wasInMatch = false;
                            completedMatches++;
                            ProgressChanged?.Invoke(completedMatches, cfg.TargetMatches);
                            Log($"\r\n>>> ĐÃ HOÀN THÀNH TRẬN ĐẤU THỨ {completedMatches} / {(cfg.TargetMatches > 0 ? cfg.TargetMatches : "∞")} <<<");

                            if (cfg.TargetMatches > 0 && completedMatches >= cfg.TargetMatches)
                            {
                                Log($"[Auto 1v1] ĐÃ ĐẠT MỤC TIÊU {cfg.TargetMatches} TRẬN XẾP HẠNG! TỰ ĐỘNG DỪNG.");
                                return;
                            }

                            Log($"[Auto 1v1] Nghỉ {cfg.CooldownSec}s trước khi tìm trận tiếp theo...");
                            StateChanged?.Invoke($"Nghỉ giữa 2 trận ({cfg.CooldownSec}s)...");
                            await Task.Delay(cfg.CooldownSec * 1000, ct);
                            continue;
                        }

                        StateChanged?.Invoke("Đang ở Sảnh: Bấm Tìm trận 1vs1...");
                        if (lobbyClickCount == 0 || stateStopwatch.Elapsed.TotalSeconds >= 10)
                        {
                            lobbyClickCount++;
                            stateStopwatch.Restart();
                            Log($"[Sảnh] Bấm nút '1 ĐẤU 1' tìm đối thủ...");

                            // Background click 1v1 button
                            if (cfg.CustomMatchmakePos.HasValue)
                                WinApi.ClickClientBackground(hWnd, cfg.CustomMatchmakePos.Value.X, cfg.CustomMatchmakePos.Value.Y);
                            else
                                WinApi.ClickClientPercentBackground(hWnd, 0.255, 0.800);

                            await Task.Delay(200, ct);
                            WinApi.SendKeyPress(hWnd, WinApi.VK_RETURN);
                        }
                        await Task.Delay(1500, ct);
                        break;

                    // -------------------------------------------------------------
                    // 2. MÀN HÌNH CHỌN ĐỘI HÌNH / CHỜ SẴN SÀNG (READY SCREEN)
                    // -------------------------------------------------------------
                    case GameScreenState.ReadyScreen:
                        StateChanged?.Invoke("Màn hình chuẩn bị: Gửi lệnh Sẵn sàng...");
                        if (readyClickCount < 5 || stateStopwatch.Elapsed.TotalSeconds >= 3)
                        {
                            readyClickCount++;
                            stateStopwatch.Restart();
                            Log($"[Sẵn sàng] Gửi lệnh Sẵn sàng (F3 / Enter / Click nút vàng)...");

                            // Click yellow ready button at bottom right (background)
                            WinApi.ClickClientPercentBackground(hWnd, 0.815, 0.970);
                            await Task.Delay(80, ct);
                            WinApi.SendKeyPress(hWnd, WinApi.VK_F3);
                            await Task.Delay(80, ct);
                            WinApi.SendKeyPress(hWnd, WinApi.VK_RETURN);
                        }
                        await Task.Delay(1500, ct);
                        break;

                    // -------------------------------------------------------------
                    // 3. TRONG TRẬN ĐẤU (IN-MATCH GAMEPLAY - ANTI-AFK)
                    // -------------------------------------------------------------
                    case GameScreenState.InMatch:
                        if (!wasInMatch)
                        {
                            wasInMatch = true;
                            matchStopwatch.Restart();
                            lastLogSec = 0;
                            Log($"\r\n[Trận đấu] BẮT ĐẦU TRẬN ĐẤU (Đang phát phím chống AFK)...");
                        }

                        int matchSec = (int)matchStopwatch.Elapsed.TotalSeconds;
                        if (matchSec - lastLogSec >= 30)
                        {
                            lastLogSec = matchSec;
                            Log($"[Trận đấu] Thời gian thi đấu: {matchSec / 60}m{matchSec % 60:D2}s - Đang điều khiển cầu thủ...");
                            StateChanged?.Invoke($"Đang thi đấu ({matchSec / 60}m{matchSec % 60:D2}s)...");
                        }

                        // Skip half-time if present
                        if (matchSec % 20 == 0)
                        {
                            WinApi.SendKeyPress(hWnd, WinApi.VK_RETURN);
                            await Task.Delay(60, ct);
                            WinApi.SendKeyPress(hWnd, WinApi.VK_SPACE);
                        }

                        // Anti-AFK gameplay keys (FO2 standard):
                        // 1. Directional move (200 - 350ms)
                        ushort dir = moveKeys[rnd.Next(moveKeys.Length)];
                        WinApi.SendKeyDown(hWnd, dir);
                        await Task.Delay(rnd.Next(200, 350), ct);
                        WinApi.SendKeyUp(hWnd, dir);

                        // 2. Action key
                        int roll = rnd.Next(100);
                        if (roll < 45) WinApi.SendKeyPress(hWnd, WinApi.VK_S, 60);       // Pass / Switch player
                        else if (roll < 70) WinApi.SendKeyPress(hWnd, WinApi.VK_D, 80);  // Shoot / Tackle
                        else if (roll < 85) WinApi.SendKeyPress(hWnd, WinApi.VK_W, 60);  // Through ball
                        else if (roll < 95) WinApi.SendKeyPress(hWnd, WinApi.VK_A, 80);  // Long pass / Slide
                        else WinApi.SendKeyPress(hWnd, WinApi.VK_E, 120);                 // Sprint

                        await Task.Delay(rnd.Next(1000, 1800), ct);
                        break;

                    // -------------------------------------------------------------
                    // 4. HẾT TRẬN / NHẬN THƯỞNG / LẬT THẺ (POST-MATCH)
                    // -------------------------------------------------------------
                    case GameScreenState.PostMatch:
                        StateChanged?.Invoke("Hết trận: Bỏ qua kết quả & Nhận thưởng...");
                        Log("[Hết trận] Bấm Enter / Space / Esc bỏ qua kết quả & lật thẻ quà...");

                        // Background clicks for card flips and confirm buttons
                        WinApi.ClickClientPercentBackground(hWnd, 0.500, 0.800);
                        await Task.Delay(60, ct);
                        WinApi.ClickClientPercentBackground(hWnd, 0.800, 0.950);
                        await Task.Delay(60, ct);

                        WinApi.SendKeyPress(hWnd, WinApi.VK_RETURN);
                        await Task.Delay(100, ct);
                        WinApi.SendKeyPress(hWnd, WinApi.VK_SPACE);
                        await Task.Delay(100, ct);
                        WinApi.SendKeyPress(hWnd, WinApi.VK_ESCAPE);
                        await Task.Delay(1000, ct);
                        break;

                    // -------------------------------------------------------------
                    // 5. CHƯA RÕ MÀN HÌNH (UNKNOWN / LOADING)
                    // -------------------------------------------------------------
                    default:
                        StateChanged?.Invoke("Đang tải hoặc chờ màn hình tiếp theo...");
                        // Gentle keep-alive / continue
                        WinApi.SendKeyPress(hWnd, WinApi.VK_SPACE);
                        await Task.Delay(1500, ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("[Auto 1v1] Đã dừng theo yêu cầu của bạn.");
        }
        catch (Exception ex)
        {
            Log($"[Auto 1v1] Lỗi: {ex.Message}");
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
        GameScreenState.Lobby => "Sảnh Xếp Hạng 1vs1",
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
        if (bmp == null) return (GameScreenState.Unknown, "Không thể chụp cửa sổ game");

        // Downscale to 160x90 thumbnail for ultra-fast (sub-millisecond) pixel analysis
        using var thumb = WinApi.Downscale(bmp, 160, 90);

        // ----------------------------------------------------------------
        // 1. ReadyScreen: "Sẵn sàng" button at bottom right
        //    In the actual game screenshot, the button is bright GREEN (not yellow):
        //    ~R:60-100, G:180-220, B:60-100 — typical "Confirm/Ready" green button
        //    Location: X: 118-155, Y: 83-89 (on 160x90 thumb)
        // Also look for yellow-green (some UI themes show it as yellow-green)
        // ----------------------------------------------------------------
        int readyGreenCount = 0;
        int readyYellowCount = 0;
        for (int y = 82; y < 90; y++)
        {
            for (int x = 115; x < 158; x++)
            {
                var c = thumb.GetPixel(x, y);
                // Bright green button: G dominant, R and B are substantially lower
                if (c.G >= 150 && c.G > c.R + 50 && c.G > c.B + 50)
                    readyGreenCount++;
                // Yellow/amber variant: High R + High G, Low B
                else if (c.R >= 180 && c.G >= 150 && c.B <= 90 && (c.R - c.B) > 100)
                    readyYellowCount++;
            }
        }

        if (readyGreenCount >= 10 || readyYellowCount >= 8)
        {
            return (GameScreenState.ReadyScreen,
                $"Nút Sẵn sàng: xanh={readyGreenCount}px, vàng={readyYellowCount}px");
        }

        // ----------------------------------------------------------------
        // 2. Count pitch-green pixels across full center area (X: 10-150, Y: 10-80)
        //    InMatch = sân cỏ chiếm hầu hết màn hình (> 35%)
        //    Lobby   = cỏ ít hơn vì có popup che (8-30%)
        // ----------------------------------------------------------------
        int greenCount = 0;
        int totalFieldPixels = 0;
        for (int y = 10; y < 80; y += 2)
        {
            for (int x = 10; x < 150; x += 2)
            {
                totalFieldPixels++;
                var c = thumb.GetPixel(x, y);
                // Pitch grass: G clearly dominant over R and B, not too dark
                if (c.G >= 55 && c.G > c.R + 12 && c.G > c.B + 12)
                    greenCount++;
            }
        }
        double grassRatio = totalFieldPixels > 0 ? (double)greenCount / totalFieldPixels : 0;

        if (grassRatio >= 0.33)
        {
            return (GameScreenState.InMatch, $"Sân cỏ chiếm {grassRatio:P0}");
        }

        // ----------------------------------------------------------------
        // 3. Lobby detection
        //    - Bottom navigation bar (Y ~86-89) is very dark (dark gray ~#1a1a1a)
        //    - Grass is visible in upper background (5-33%)
        //    - Center has a dark rounded popup panel
        // ----------------------------------------------------------------
        int darkBottomCount = 0;
        for (int x = 5; x < 155; x += 4)
        {
            var c = thumb.GetPixel(x, 87);
            if (c.R < 60 && c.G < 60 && c.B < 60) darkBottomCount++;
        }

        // Check for dark panel in center (Lobby popup: X 25-135, Y 40-80)
        int darkPanelCount = 0;
        for (int y = 42; y < 80; y += 3)
        {
            for (int x = 25; x < 135; x += 3)
            {
                var c = thumb.GetPixel(x, y);
                if (c.R < 55 && c.G < 55 && c.B < 55) darkPanelCount++;
            }
        }

        if (grassRatio >= 0.05 && grassRatio < 0.33 && (darkBottomCount >= 12 || darkPanelCount >= 15))
        {
            return (GameScreenState.Lobby,
                $"Sảnh PVP (cỏ={grassRatio:P0}, navBar={darkBottomCount}, panel={darkPanelCount})");
        }

        // ----------------------------------------------------------------
        // 4. PostMatch: dark result screen / card reveal (very low grass)
        // ----------------------------------------------------------------
        if (grassRatio < 0.08)
        {
            return (GameScreenState.PostMatch, $"Màn hình kết quả/thưởng (cỏ={grassRatio:P0})");
        }

        return (GameScreenState.Unknown, $"Chưa xác định (cỏ={grassRatio:P0}, navBar={darkBottomCount})");
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
