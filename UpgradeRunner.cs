namespace FO3SessionTool;

/// <summary>Randomised waits so the actions are spaced like a person clicking, not a tight loop. Pace scales everything.</summary>
public static class Pacing
{
    private static readonly Random R = new();

    /// <summary>Wait between min and max seconds (two dice, so values cluster mid-range) times pace.</summary>
    public static Task Wait(double minSec, double maxSec, double pace, CancellationToken ct)
    {
        double u = (R.NextDouble() + R.NextDouble()) / 2;
        return Task.Delay(TimeSpan.FromSeconds((minSec + u * (maxSec - minSec)) * pace), ct);
    }

    /// <summary>Now and then a longer pause, like looking away for a moment.</summary>
    public static Task Think(double pace, CancellationToken ct) =>
        R.NextDouble() < 0.12 ? Wait(6, 14, pace, ct) : Task.CompletedTask;
}

/// <summary>
/// The automatic flow: 1) get the +1 cards (buy N, repeating until done, or take them from the storage),
/// 2) merge level by level from +2 up to the target following the rules.
/// Steps: 0 = buying / storage, k = merging to +(k+1).
/// </summary>
public sealed class UpgradeRunner
{
    public const int MaxLevel = 10;       // highest card level the flow can build
    private const int MaxWaitPolls = 25;      // waiting for stock / price, a few seconds apart
    private const int MaxBuyFailures = 5;     // consecutive failed purchase calls
    private const int MaxEnhanceTries = 3;    // per merge

    private readonly GameApi _api;
    private readonly Action<string> _log;
    private readonly Action<int, StepState> _step;
    private readonly Action<int, int, string> _progress;

    public UpgradeRunner(GameApi api, Action<string> log, Action<int, StepState> step, Action<int, int, string> progress)
    {
        _api = api; _log = log; _step = step; _progress = progress;
    }

    /// <summary>How many merges each level needs, starting from the given card counts per level.</summary>
    public static int[] PlannedMerges(IReadOnlyDictionary<int, (int A, int B)> rules, int target, int[] initial)
    {
        var cnt = (int[])initial.Clone();
        var times = new int[MaxLevel + 1];
        for (int lvl = 2; lvl <= target; lvl++)
        {
            var (a, b) = rules[lvl];
            int t = a == b ? cnt[a] / 2 : Math.Min(cnt[a], cnt[b]);
            cnt[a] -= a == b ? 2 * t : t;
            if (a != b) cnt[b] -= t;
            cnt[lvl] += t;
            times[lvl] = t;
        }
        return times;
    }

    /// <summary>Returns true when every step finished. Throws OperationCanceledException when stopped.</summary>
    /// <summary>Asked when a merge has a success probability under 100%: (target level, probability) -> true to accept the risk.
    /// Accepting a level covers every merge to that level for the rest of the run. Without a callback such merges are refused.</summary>
    public Func<int, int, Task<bool>>? ConfirmRisk { get; set; }
    private readonly HashSet<int> _riskAccepted = new();
    private readonly SemaphoreSlim _riskAsk = new(1, 1);
    /// <summary>false after a merge the server applied but did not succeed (cards may be lost or lowered): callers re-read the storage.</summary>
    internal bool LastEnhanceSucceeded { get; private set; } = true;
    /// <summary>EP paid for restores after the last failed merge (the first restore is free), and what happened to the failed card.</summary>
    internal long LastRestoreCost { get; private set; }
    internal bool LastCardDiscarded { get; private set; }
    private const int MaxRestores = 2;   // the first one is free; the second one costs EP and is only done when the first left the card at +0

    internal async Task<bool> AcceptRiskAsync(int lvl, int probability)
    {
        if (probability >= 100) return true;
        await _riskAsk.WaitAsync();
        try
        {
            if (_riskAccepted.Contains(lvl)) return true;
            if (ConfirmRisk == null) return false;
            if (!await ConfirmRisk(lvl, probability)) return false;
            _riskAccepted.Add(lvl);
            _log($"[Ép thẻ] Bạn đã chấp nhận rủi ro khi ép lên +{lvl} (xác suất {probability}%).");
            return true;
        }
        finally { _riskAsk.Release(); }
    }

    /// <summary>Cards that reached the target level during the last run (older target-level cards are not included).</summary>
    public List<string> ProducedTargetIds { get; private set; } = new();

    public async Task<bool> RunAsync(UpgradePlan plan, CancellationToken ct)
    {
        ProducedTargetIds = new();
        double pace = plan.Pace;
        var pool = Enumerable.Range(0, MaxLevel + 1).ToDictionary(l => l, _ => new List<string>());   // level -> card ids we own
        long spent = 0;

        if (!plan.UseInventory)
        {
            // ---- 1) buy ----------------------------------------------------
            _step(0, StepState.Running);
            int bought = 0, polls = 0, failures = 0;
            while (bought < plan.Quantity)
            {
                ct.ThrowIfCancellationRequested();
                _log($"[Ép thẻ] Đang tìm giá thấp nhất thẻ +1 của SPID {plan.Spid}...");
                var cheapest = (await _api.SearchAsync(plan.Spid, ct)).Where(l => l.Grade == 1).OrderBy(l => l.Price).FirstOrDefault();

                if (cheapest == null || (plan.MaxPrice > 0 && cheapest.Price > plan.MaxPrice))
                {
                    if (++polls > MaxWaitPolls) return Fail(0, "Chờ quá lâu mà không có thẻ +1 phù hợp. Dừng.");
                    _progress(bought, plan.Quantity, cheapest == null
                        ? "Không có thẻ +1 đang bán, đang chờ..."
                        : $"Giá thấp nhất {cheapest.Price:N0} cao hơn mức tối đa {plan.MaxPrice:N0}, đang chờ...");
                    _log("[Ép thẻ] " + (cheapest == null ? "Chưa có thẻ +1 đang bán" : $"Giá thấp nhất {cheapest.Price:N0} vượt mức tối đa") + ", đang chờ rồi tìm lại...");
                    await Pacing.Wait(4, 8, pace, ct);
                    continue;
                }
                polls = 0;

                _log($"[Ép thẻ] Thấy thẻ +1 giá {cheapest.Price:N0} EP, chuẩn bị mua ({bought + 1}/{plan.Quantity})...");
                await Pacing.Wait(0.8, 2.0, pace, ct);   // look at the price, then click buy
                _log($"[Ép thẻ] Đang gửi lệnh mua thẻ +1 giá {cheapest.Price:N0} EP...");
                var buy = await _api.BuyAsync(plan.Spid, 1, cheapest.Price, ct);
                if (!buy.Ok)
                {
                    // usually the price moved between search and buy; re-search and try again
                    _log($"[Ép thẻ] Mua thất bại ({GameApi.Trim(buy.Raw)}), thử lại...");
                    if (++failures >= MaxBuyFailures) return Fail(0, $"Mua thất bại {MaxBuyFailures} lần liên tiếp. Dừng.");
                    await Pacing.Wait(3, 6, pace, ct);
                    continue;
                }
                failures = 0;
                if (!_api.LastHadMoney) _api.Spend(cheapest.Price);
                pool[1].Add(buy.CardId!);
                bought++;
                spent += cheapest.Price;
                _progress(bought, plan.Quantity, $"Đã mua {bought}/{plan.Quantity} thẻ +1");
                _log($"[Ép thẻ] Mua {bought}/{plan.Quantity}: giá {cheapest.Price:N0} (id …{Tail(buy.CardId!)})");
                await Pacing.Wait(2.0, 4.5, pace, ct);
                await Pacing.Think(pace, ct);
            }
            _log($"[Ép thẻ] Mua xong {bought} thẻ +1, tổng {spent:N0} EP.");
            await Pacing.Wait(1.5, 3, pace, ct);
        }

        // ---- 1b) sweep the storage: every usable card of this player, not only the ones just bought ----
        if (plan.UseInventory) _step(0, StepState.Running);
        _log("[Ép thẻ] Đang đọc kho thẻ (/coach/me) để gom toàn bộ thẻ hợp lệ của cầu thủ...");
        List<GameApi.OwnedCard>? inv = null;
        for (int attempt = 1; attempt <= 3 && inv == null; attempt++)
        {
            try { inv = await _api.GetInventoryAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log($"[Ép thẻ] Đọc kho lần {attempt}/3 lỗi: {ex.Message}");
                if (attempt < 3) await Pacing.Wait(3, 6, pace, ct);
            }
        }
        if (inv != null)
        {
            // the storage is the truth: it contains the cards just bought plus any older ones
            foreach (var l in pool.Values) l.Clear();
            foreach (var c in inv.Where(c => c.Spid == plan.Spid && !c.InSquad && c.Grade >= 1 && c.Grade <= plan.Target))
                pool[c.Grade].Add(c.Id);
            int inLineup = inv.Count(c => c.Spid == plan.Spid && c.InSquad);
            if (inLineup > 0) _log($"[Ép thẻ] {inLineup} thẻ của cầu thủ này đang trong đội hình, không được dùng.");
        }
        else if (plan.UseInventory) return Fail(0, "Không đọc được kho thẻ. Dừng.");
        else _log("[Ép thẻ] Không đọc được kho, chỉ dùng các thẻ vừa mua.");

        var targetBefore = pool[plan.Target].ToHashSet();
        var have = Enumerable.Range(1, plan.Target).Where(l => pool[l].Count > 0).Select(l => $"{pool[l].Count}×+{l}").ToList();
        if (have.Count == 0) return Fail(0, "Không có thẻ nào của cầu thủ này (ngoài đội hình) để ép.");
        _step(0, StepState.Done);
        _log($"[Ép thẻ] Thẻ hợp lệ trong kho: {string.Join(", ", have)}. Bắt đầu ép theo rule.");
        await Pacing.Wait(1.5, 3, pace, ct);

        // ---- 2) merge, +2 up to the target ------------------------------
        var initial = new int[MaxLevel + 1];
        for (int l = 1; l <= MaxLevel; l++) initial[l] = pool[l].Count;
        var planned = PlannedMerges(plan.Rules, plan.Target, initial);
        long fees = 0;
        for (int lvl = 2; lvl <= plan.Target; lvl++)
        {
            int step = lvl - 1;
            _step(step, StepState.Running);
            var (a, b) = plan.Rules[lvl];
            int done = 0;

            while (a == b ? pool[a].Count >= 2 : pool[a].Count >= 1 && pool[b].Count >= 1)
            {
                ct.ThrowIfCancellationRequested();
                string main, mat;
                if (a == b) { main = pool[a][0]; mat = pool[a][1]; }
                else { main = pool[Math.Max(a, b)][0]; mat = pool[Math.Min(a, b)][0]; }

                _log($"[Ép thẻ] Đang xem trước ép +{lvl} (…{Tail(main)} + …{Tail(mat)})...");
                var pv = await _api.EnhancePreviewAsync(main, mat, ct);
                if (pv.Grade != lvl && a != b)
                {
                    _log("[Ép thẻ] Kết quả xem trước không khớp, đổi vai thẻ chính/nguyên liệu và xem lại...");
                    (main, mat) = (mat, main);   // maybe the roles are the other way round
                    pv = await _api.EnhancePreviewAsync(main, mat, ct);
                }
                if (pv.Grade != lvl) return Fail(step, $"Xem trước ép cho ra +{pv.Grade}, mong đợi +{lvl}. Dừng.");
                if (pv.Probability < 100 && !await AcceptRiskAsync(lvl, pv.Probability))
                    return Fail(step, $"Xác suất ép +{lvl} chỉ {pv.Probability}% (<100%) và bạn không chấp nhận rủi ro, dừng. ({GameApi.Trim(pv.Raw)})");
                if (pv.Probability < 100) _log($"[Ép thẻ] Cảnh báo: xác suất ép +{lvl} chỉ {pv.Probability}%.");

                _log($"[Ép thẻ] Xem trước OK: ra +{pv.Grade}, xác suất {pv.Probability}%, phí {pv.EnhancePay:N0} EP. Chuẩn bị bấm ép...");
                await Pacing.Wait(2.0, 4.5, pace, ct);   // read the preview, then confirm
                if (!await EnhanceWithRecoveryAsync(main, mat, lvl, step, pace, ct)) return false;

                if (!_api.LastHadMoney) _api.Spend(pv.EnhancePay);
                fees += pv.EnhancePay + LastRestoreCost;
                if (!LastEnhanceSucceeded)
                {
                    // the server applied the merge and it failed: the storage tells what is left
                    _log($"[Ép thẻ] ÉP +{lvl} KHÔNG THÀNH CÔNG (xác suất {pv.Probability}%), phí {pv.EnhancePay:N0} EP đã mất. Đọc lại kho để biết thẻ còn lại...");
                    var inv2 = await _api.GetInventoryAsync(ct);
                    foreach (var l in pool.Values) l.Clear();
                    foreach (var c in inv2.Where(c => c.Spid == plan.Spid && !c.InSquad && c.Grade >= 1 && c.Grade <= plan.Target)) pool[c.Grade].Add(c.Id);
                    await Pacing.Wait(3, 7, pace, ct);
                    continue;
                }
                Remove(pool, main); Remove(pool, mat);
                pool[lvl].Add(main);
                done++;
                _progress(done, Math.Max(planned[lvl], done), $"Ép +{lvl}: {done}/{planned[lvl]}");
                _log($"[Ép thẻ] Ép +{lvl} ({done}/{planned[lvl]}): phí {pv.EnhancePay:N0} EP");
                await Pacing.Wait(3, 7, pace, ct);
                await Pacing.Think(pace, ct);
            }
            _step(step, StepState.Done);
            if (lvl < plan.Target) await Pacing.Wait(2, 4, pace, ct);
        }

        ProducedTargetIds = pool[plan.Target].Where(id => !targetBefore.Contains(id)).ToList();
        var left = Enumerable.Range(1, plan.Target - 1).Where(l => pool[l].Count > 0).Select(l => $"{pool[l].Count}×+{l}").ToList();
        _progress(1, 1, $"Hoàn tất: {pool[plan.Target].Count} thẻ +{plan.Target}");
        _log($"[Ép thẻ] HOÀN TẤT: {pool[plan.Target].Count} thẻ +{plan.Target}."
             + (plan.UseInventory ? "" : $" Mua {spent:N0} EP.") + $" Phí ép {fees:N0} EP"
             + (left.Count > 0 ? $". Còn dư: {string.Join(", ", left)}." : "."));
        return true;
    }

    /// <summary>
    /// Enhance with retries. When a call fails, wait, then look at the storage: if the merge was in fact applied
    /// (main card already at the new grade, material gone) count it as done instead of merging again.
    /// </summary>
    internal async Task<bool> EnhanceWithRecoveryAsync(string main, string mat, int lvl, int step, double pace, CancellationToken ct)
    {
        string lastRaw = "";
        LastEnhanceSucceeded = true; LastRestoreCost = 0; LastCardDiscarded = false;
        for (int attempt = 1; attempt <= MaxEnhanceTries; attempt++)
        {
            try
            {
                _log($"[Ép thẻ] Đang gửi lệnh ép +{lvl} (lần {attempt}/{MaxEnhanceTries})...");
                var r = await _api.EnhanceAsync(main, mat, ct);
                if (r.Ok && r.Success && r.ResultGrade == lvl) { LastEnhanceSucceeded = true; return true; }
                if (r.Ok && !r.Success)   // applied, but it failed: never repeat it blindly
                {
                    LastEnhanceSucceeded = false;
                    await RecoverFailedAsync(main, lvl, pace);
                    return true;
                }
                lastRaw = r.Raw;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastRaw = ex.Message; }

            _log($"[Ép thẻ] Ép +{lvl} lần {attempt}/{MaxEnhanceTries} lỗi: {GameApi.Trim(lastRaw)}");
            await Pacing.Wait(6, 12, pace, ct);

            try
            {
                _log("[Ép thẻ] Đang đọc lại kho để kiểm tra lệnh ép có được áp dụng không...");
                var inv = await _api.GetInventoryAsync(ct);
                var m = inv.FirstOrDefault(c => c.Id == main);
                if (m != null && m.Grade == lvl && inv.All(c => c.Id != mat))
                {
                    _log($"[Ép thẻ] Server đã ép xong +{lvl} dù phản hồi lỗi, tính là thành công.");
                    return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log("[Ép thẻ] Không đọc được kho để kiểm tra: " + ex.Message); }
        }
        return Fail(step, $"Ép +{lvl} thất bại sau {MaxEnhanceTries} lần: {GameApi.Trim(lastRaw)}");
    }

    /// <summary>
    /// After a failed merge: restore the card (free, then once more for a fee) while the game offers it ("restore" field on the card), and when
    /// the card ends at +0 discard it. Never throws and is not cancelled half-way: the callers re-read the storage afterwards.
    /// </summary>
    private async Task RecoverFailedAsync(string main, int lvl, double pace)
    {
        var ct = CancellationToken.None;
        try
        {
            async Task<GameApi.OwnedCard?> Find() => (await _api.GetInventoryAsync(ct)).FirstOrDefault(c => c.Id == main);
            await Pacing.Wait(1.5, 3, pace, ct);
            var c = await Find();
            if (c == null) { _log($"[Phục hồi] Thẻ …{Tail(main)} không còn trong kho."); return; }
            for (int n = 1; n <= MaxRestores && c != null && c.RestoreGrade > c.Grade; n++)
            {
                if (n > 1 && c.Grade > 0) break;   // the free restore got the card above +0: keep it, do not pay for a random second try
                long before = _api.LastWallet?.Ep ?? 0;
                _log($"[Phục hồi] Thẻ …{Tail(main)} đang ở +{c.Grade}, phục hồi được tối đa +{c.RestoreGrade} (kết quả ngẫu nhiên). Phục hồi lần {n}/{MaxRestores}" + (n > 1 ? " (có phí)..." : " (miễn phí)..."));
                var r = await _api.RestoreAsync(ct);
                if (!r.Ok) { _log("[Phục hồi] Lệnh phục hồi lỗi: " + GameApi.Trim(r.Raw)); break; }
                long cost = Math.Max(0, before - (_api.LastWallet?.Ep ?? before));
                LastRestoreCost += cost;
                await Pacing.Wait(1.5, 3, pace, ct);
                c = await Find();
                _log(c == null ? "[Phục hồi] Thẻ không còn trong kho sau khi phục hồi."
                    : $"[Phục hồi] Sau lần {n}: thẻ ở +{c.Grade}" + (cost > 0 ? $", phí {cost:N0} EP" : ", miễn phí") + (c.RestoreGrade > c.Grade ? "." : " (xong)."));
            }
            if (c != null && c.Grade == 0 && c.Group == GameApi.GroupNone)
            {
                _log($"[Phục hồi] Thẻ …{Tail(main)} về +0, bỏ thẻ này (xoá khỏi kho).");
                await Pacing.Wait(1.5, 3, pace, ct);
                var d = await _api.DeleteCardAsync(c.Id, ct);
                LastCardDiscarded = d.Ok;
                _log(d.Ok ? "[Phục hồi] Đã bỏ thẻ +0." : "[Phục hồi] Không bỏ được thẻ +0: " + GameApi.Trim(d.Raw));
            }
        }
        catch (Exception ex) { _log("[Phục hồi] Lỗi khi phục hồi: " + ex.Message + " (kiểm tra kho trong game)."); }
    }

    private static string Tail(string id) => id.Length <= 6 ? id : id[^6..];

    private static void Remove(Dictionary<int, List<string>> pool, string id)
    {
        foreach (var l in pool.Values) if (l.Remove(id)) return;
    }

    private bool Fail(int step, string message)
    {
        _step(step, StepState.Error);
        _progress(0, 1, message);
        _log("[Ép thẻ] LỖI: " + message);
        return false;
    }
}
