namespace FO3SessionTool;

/// <summary>
/// Settings of the loop. The cards of the target level pile up until their total value reaches TradeMin, then they are all traded
/// (never more than TradeMax, the game's limit per trade). MaxLossRounds: stop after that many trades in a row that ended
/// with less EP than the interval before them started with. StorageSlots: size of the card storage (200 in the game); when it is
/// full and nothing can be merged or traded any more, the run stops. StopBelowEp (0 = off): never buy when the EP left afterwards
/// would be under this amount; the run ends when nothing else can make progress.
/// SellMode: instead of trading, every card that reaches the target level is listed at once on the transfer market at the highest
/// price the game allows (no trade threshold; the loss rule applies per sold card); AutoCollect: claim the money of sold cards automatically
/// (only offers of the player being worked on). KeepMode: neither trade nor sell, the cards that reach the target level are just kept
/// (buy and merge until EP runs out, the storage is full or the user stops).
/// </summary>
public sealed record CycleOptions(long TradeMin = 900_000_000, long TradeMax = 1_000_000_000, int MaxLossRounds = 2, int StorageSlots = 200, long StopBelowEp = 0, bool SellMode = false, bool AutoCollect = true, bool KeepMode = false);

/// <summary>One trade round: the cost of the cards of one batch against what the trade of that batch paid.</summary>
public sealed record RoundStat(int Round, int Bought, int Merged, int Cards, long CardValue, long Cost, long IncomeEp, long ReceivedPlayerPrice)
{
    /// <summary>Cost = EP spent on buying and merging the cards of this batch (counted from the previous batch being taken out
    /// to this one). Income = EP of the chosen offer. Net counts the value of the player received too.</summary>
    public long Net => IncomeEp + ReceivedPlayerPrice - Cost;
}

/// <summary>
/// Buy, merge and trade running at the same time, like three people working in parallel:
/// the buyer keeps buying +1 cards, the merger merges whatever pairs are ready, the trader sells the target-level cards
/// whenever their total value reaches the window. A fourth loop supervises (status, balance, session, stop conditions).
/// Buying is throttled by the number of cards already in the pipeline so EP is not sunk into stock that cannot be sold yet.
/// RunAsync returns why it ended; a stop request by the user also ends it normally (the trade in flight is always completed).
/// </summary>
public sealed class CycleRunner
{
    /// <summary>Index of the "Giao dịch" step in the step bar (after the merge steps).</summary>
    public const int TradeStep = UpgradeRunner.MaxLevel;

    private readonly GameApi _api;
    private readonly Action<string> _log;
    private readonly Action<int, StepState> _step;
    private readonly Action<int, int, string> _progress;
    private readonly Action _resetSteps;
    private readonly Action<RoundStat>? _round;
    private readonly UpgradeRunner _inner;
    /// <summary>Asked (target level, probability) when a merge is under 100%; true = accept the risk for that level for this run.</summary>
    public Func<int, int, Task<bool>>? ConfirmRisk { get => _inner.ConfirmRisk; set => _inner.ConfirmRisk = value; }

    public CycleRunner(GameApi api, Action<string> log, Action<int, StepState> step, Action<int, int, string> progress, Action resetSteps, Action<RoundStat>? round = null)
    {
        _api = api; _log = log; _step = step; _progress = progress; _resetSteps = resetSteps; _round = round;
        _inner = new UpgradeRunner(api, log, step, progress);
    }

    // ---- shared state (guarded by _lock) ----
    private readonly object _lock = new();
    private Dictionary<int, List<string>> _pool = new();   // level -> card ids free to use (not reserved by a merge or a trade)
    private int[] _need = Array.Empty<int>();              // +1 cards one card of each level is worth
    private int _inFlight;                                 // +1 equivalents inside merges running right now
    private int _cardCount;                                // cards in the storage (all of them, lineup included)
    private DateTime? _fullSince;
    private long _targetValue;                             // value of one target-level card (from the storage), 0 = unknown
    private long _lastFee;
    private int _bought, _merged, _tradesDone;
    private long _totalNet, _spent, _spentAtReserve;
    private int _round1 = 1, _roundBought, _roundMerged, _lossStreak;
    private DateTime _lastActivity = DateTime.UtcNow;
    private string _buyBlock = "";                         // why the buyer cannot buy right now ("" = not blocked)
    private bool _buyBlockedEp;
    private volatile bool _trading, _merging, _selling;
    private readonly Dictionary<string, long> _cardCost = new();   // card id -> EP that went into it (buy price + merge fees of its whole chain)
    private readonly HashSet<string> _offered = new();     // cards already listed (they may still show up in the storage)
    private int _sold, _pending, _sellFailures, _unknownStatusLogged, _kept;
    private bool _keepMode;
    private long _collected;
    private readonly HashSet<string> _statusSeen = new();
    private string? _reason;
    /// <summary>Seconds without any buy / merge / trade before the run gives up (watchdog against silent loops).</summary>
    internal int StallSeconds = 300;
    private CancellationTokenSource _stop = new();

    private long Ep() => _api.LastWallet?.Ep ?? 0;
    private void Activity() => _lastActivity = DateTime.UtcNow;
    private static string Tail(string id) => id.Length <= 6 ? id : id[^6..];
    private static string Signed(long v) => (v >= 0 ? "+" : "-") + Math.Abs(v).ToString("N0");

    private void Stop(string reason)
    {
        lock (_lock) _reason ??= reason;
        _log("[Xoay vòng] DỪNG: " + reason);
        _stop.Cancel();
    }

    public async Task<string> RunAsync(UpgradePlan plan, CycleOptions opt, CancellationToken ct)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tok = _stop.Token;
        _reason = null;
        _bought = _merged = _tradesDone = _roundBought = _roundMerged = _lossStreak = _inFlight = 0;
        _keepMode = opt.KeepMode; _kept = 0;
        _sold = _pending = _sellFailures = 0; _collected = 0; _offered.Clear(); _statusSeen.Clear(); _cardCost.Clear();
        _round1 = 1; _totalNet = 0; _spent = _spentAtReserve = 0; _targetValue = 0; _lastFee = 0; _buyBlock = ""; _buyBlockedEp = false;
        _lastActivity = DateTime.UtcNow;
        double pace = plan.Pace;

        // how many +1 cards each level is worth under the rules
        _need = new int[UpgradeRunner.MaxLevel + 1];
        _need[1] = 1;
        for (int l = 2; l <= plan.Target; l++) _need[l] = _need[plan.Rules[l].A] + _need[plan.Rules[l].B];

        try
        {
            var (valid, detail) = await _api.CheckSessionAsync(tok);
            if (!valid) return $"Phiên đã hết hạn ({detail}). Hãy đăng nhập lại.";
            await _api.GetWalletAsync(tok);
            _spent = _spentAtReserve = 0;
            if (opt.StopBelowEp > 0 && Ep() < opt.StopBelowEp) return $"EP hiện có ({Ep():N0}) đã dưới mức dừng {opt.StopBelowEp:N0}.";

            // starting stock: every usable card of this player (outside the lineup)
            var inv = await _api.GetInventoryAsync(tok);
            _cardCount = inv.Count;
            if (opt.SellMode)
            {
                // cards we already listed earlier may still be in the storage list: never sell them twice
                var mine = await _api.GetOffersAsync(tok);
                foreach (var o in mine) _offered.Add(o.CardId);
                _pending = mine.Count(o => o.Status == "waiting");
            }
            _pool = Enumerable.Range(1, plan.Target).ToDictionary(l => l, _ => new List<string>());
            foreach (var c in inv.Where(c => c.Spid == plan.Spid && c.Group == GameApi.GroupNone && c.Grade >= 1 && c.Grade <= plan.Target && !_offered.Contains(c.Id)))
            {
                if (opt.KeepMode && c.Grade == plan.Target) _kept++;   // already at the target level: nothing more to do with it
                else _pool[c.Grade].Add(c.Id);
            }
            foreach (var c0 in inv.Where(c0 => c0.Spid == plan.Spid)) _cardCost[c0.Id] = c0.Value;   // stock we already own: valued at its market price
            LearnTargetValue(inv, plan);
            _cap = Capacity(plan, opt);
            var have = Enumerable.Range(1, plan.Target).Where(l => _pool[l].Count > 0).Select(l => $"{_pool[l].Count}×+{l}");
            if (opt.KeepMode)
                _log($"[Xoay vòng] Bắt đầu song song Mua / Ép (không giao dịch, không bán: thẻ +{plan.Target} ép xong được giữ lại). EP {Ep():N0}. Thẻ +{plan.Target} đã có: {_kept}. Kho {_cardCount}/{opt.StorageSlots}.");
            else if (opt.SellMode)
                _log($"[Xoay vòng] Bắt đầu song song Mua / Ép / Bán. EP {Ep():N0}. Thẻ có sẵn: {(have.Any() ? string.Join(", ", have) : "không")}. Kho {_cardCount}/{opt.StorageSlots}. " +
                     $"Thẻ +{plan.Target} ép xong sẽ đăng bán ngay ở giá cao nhất game cho phép; {(opt.AutoCollect ? "tự động nhận tiền khi bán xong" : "KHÔNG tự nhận tiền (nhận trong game)")}; đang có {_pending} lệnh bán chờ.");
            else
                _log($"[Xoay vòng] Bắt đầu song song Mua / Ép / Giao dịch. EP {Ep():N0}. Thẻ có sẵn: {(have.Any() ? string.Join(", ", have) : "không")}. Kho {_cardCount}/{opt.StorageSlots}. " +
                     $"Đổi khi tổng thẻ +{plan.Target} đạt {opt.TradeMin:N0}–{opt.TradeMax:N0} EP; dừng sau {opt.MaxLossRounds} lần giao dịch lỗ liên tiếp.");

            var workers = new List<Task>
            {
                Loop("Mua", () => BuyOnce(plan, opt, pace, tok), tok),
                Loop("Ép", () => MergeOnce(plan, pace, tok), tok),
                Loop("Giám sát", () => SuperviseOnce(plan, opt, tok), tok),
            };
            if (opt.KeepMode) { }
            else if (opt.SellMode)
            {
                workers.Add(Loop("Bán", () => SellOnce(plan, opt, pace, tok), tok));
                if (opt.AutoCollect) workers.Add(Loop("Nhận tiền", () => CollectOnce(plan.Spid, pace, tok), tok));
            }
            else workers.Add(Loop("Giao dịch", () => TradeOnce(plan, opt, pace, tok), tok));
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (tok.IsCancellationRequested) { }
        catch (Exception ex) { Stop("Lỗi: " + ex.Message); }

        if (opt.SellMode && opt.AutoCollect && _sold > 0)
        {
            // one last pass so money that arrived while stopping is not left waiting
            try { using var last = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await CollectNow(plan.Spid, last.Token); } catch { }
        }
        string summary;
        lock (_lock)
        {
            var left = Enumerable.Range(1, plan.Target).Where(l => _pool.TryGetValue(l, out var p) && p.Count > 0).Select(l => $"{_pool[l].Count}×+{l}");
            summary = (opt.KeepMode
                          ? $" Đã mua {_bought} thẻ +1, ép {_merged} lần, giữ lại {_kept} thẻ +{plan.Target}."
                          : opt.SellMode
                          ? $" Đã mua {_bought} thẻ +1, ép {_merged} lần, đăng bán {_sold} thẻ, đã nhận {_collected:N0} EP, lãi/lỗ ước tính {Signed(_totalNet)} EP." +
                            (_pending > 0 ? $" Còn {_pending} lệnh bán chờ người mua (nhận tiền trong game hoặc chạy lại tool)." : "")
                          : $" Đã mua {_bought} thẻ +1, ép {_merged} lần, giao dịch {_tradesDone} lần, lãi/lỗ {Signed(_totalNet)} EP.") +
                      (left.Any() ? $" Còn trong kho: {string.Join(", ", left)}." : "");
        }
        if (_reason != null) return _reason + summary;
        return (ct.IsCancellationRequested ? "Bạn đã dừng." : "Kết thúc.") + summary;
    }

    /// <summary>Runs one worker: repeats its unit of work; a few consecutive errors in a row stop the whole thing.</summary>
    private async Task Loop(string name, Func<Task> once, CancellationToken tok)
    {
        int errors = 0;
        while (!tok.IsCancellationRequested)
        {
            try { await once(); errors = 0; }
            catch (OperationCanceledException) when (tok.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log($"[Xoay vòng/{name}] Lỗi ({++errors}/5): {ex.Message}");
                if (errors >= 5) { Stop($"{name} lỗi 5 lần liên tiếp: {ex.Message}"); return; }
                try { await Task.Delay(TimeSpan.FromSeconds(5), tok); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private void LearnTargetValue(List<GameApi.OwnedCard> inv, UpgradePlan plan)
    {
        var v = inv.Where(c => c.Spid == plan.Spid && c.Grade == plan.Target && c.Value > 0).Select(c => c.Value).DefaultIfEmpty(0).Max();
        if (v > 0) _targetValue = v;
    }

    /// <summary>+1 equivalents of everything in the pipeline: cards waiting, in merges, and target cards not yet traded.</summary>
    private int Pipeline()
    {
        lock (_lock) return _inFlight + _pool.Sum(kv => kv.Value.Count * _need[kv.Key]);
    }

    /// <summary>How many +1 cards the pipeline may hold: enough target cards to reach the trade window, nothing more.</summary>
    private int Capacity(UpgradePlan plan, CycleOptions opt)
    {
        if (opt.SellMode || opt.KeepMode) return 2 * _need[plan.Target];   // cards are sold one by one: a little pipelining is enough
        int players = _targetValue > 0
            ? (int)Math.Max(1, (opt.TradeMin + _targetValue - 1) / _targetValue)
            : 1;   // value of a target card not known yet: start with one player's worth of +1 cards
        return players * _need[plan.Target];
    }

    // ================= buyer =================
    private int _buyFailures;
    private DateTime? _waitFreeSince;

    private async Task BuyOnce(UpgradePlan plan, CycleOptions opt, double pace, CancellationToken tok)
    {
        if (_cardCount >= opt.StorageSlots)
        {
            // full storage: buying is impossible; merging / trading will free slots, the supervisor stops the run when nothing can
            _buyBlock = $"Kho đã đầy ({_cardCount}/{opt.StorageSlots}).";
            _buyBlockedEp = false;
            await Pacing.Wait(2, 4, pace, tok);
            return;
        }
        // capacity is read in SuperviseOnce-independent way: the buyer asks for it here
        if (Pipeline() >= _cap)
        {
            _buyBlock = ""; _buyBlockedEp = false;
            await Pacing.Wait(1.5, 3, pace, tok);
            return;
        }
        if (_waitFreeSince != null && _lastActivity <= _waitFreeSince)
        {
            // storage looked full: do not hammer the server until a merge or trade has freed something
            await Pacing.Wait(3, 6, pace, tok);
            return;
        }
        _waitFreeSince = null;
        var cheapest = (await _api.SearchAsync(plan.Spid, tok)).Where(l => l.Grade == 1).OrderBy(l => l.Price).FirstOrDefault();
        if (cheapest == null || (plan.MaxPrice > 0 && cheapest.Price > plan.MaxPrice))
        {
            _buyBlock = cheapest == null ? "Không có thẻ +1 đang bán." : $"Giá thấp nhất {cheapest.Price:N0} vượt mức tối đa {plan.MaxPrice:N0}.";
            _buyBlockedEp = false;
            await Pacing.Wait(4, 8, pace, tok);
            return;
        }
        if (Ep() < cheapest.Price + 2 * _lastFee)
        {
            _buyBlock = $"Không đủ EP để mua tiếp (giá {cheapest.Price:N0}, còn {Ep():N0}).";
            _buyBlockedEp = true;
            await Pacing.Wait(3, 6, pace, tok);
            return;
        }
        if (opt.StopBelowEp > 0 && Ep() - cheapest.Price - 2 * _lastFee < opt.StopBelowEp)
        {
            _buyBlock = $"EP còn lại sau khi mua sẽ dưới mức dừng {opt.StopBelowEp:N0} (hiện còn {Ep():N0}, giá thẻ {cheapest.Price:N0}).";
            _buyBlockedEp = true;
            await Pacing.Wait(3, 6, pace, tok);
            return;
        }
        _buyBlock = ""; _buyBlockedEp = false;

        _step(0, StepState.Running);
        await Pacing.Wait(1.5, 3.0, pace, tok);
        var buy = await _api.BuyAsync(plan.Spid, 1, cheapest.Price, tok);
        if (!buy.Ok && GameApi.IsStorageFull(buy.Raw))
        {
            _step(0, StepState.Error);
            Stop("Hết kho: kho cầu thủ đã đầy (server báo \"Exceed plyer limit\"). Hãy bán/xóa bớt cầu thủ trong kho rồi chạy lại.");
            return;
        }
        if (!buy.Ok)
        {
            _log($"[Xoay vòng/Mua] Mua thất bại ({GameApi.Trim(buy.Raw)}), thử lại...");
            if (++_buyFailures >= 5)
            {
                // Often a full storage. If merging or trading is still going on it will free slots (2 cards -> 1, N cards -> 1), so wait for it;
                // if nothing can free a slot, stop instead of looping for ever.
                if (_trading || _merging || CanMerge(plan))
                {
                    _buyBlock = "Mua thất bại nhiều lần (có thể kho đã đầy chỗ), đang chờ ép/giao dịch giải phóng chỗ.";
                    _buyFailures = 4;   // one more failure after the next progress sends it back to waiting
                    _waitFreeSince = DateTime.UtcNow;
                    await Pacing.Wait(10, 20, pace, tok);
                    return;
                }
                _step(0, StepState.Error);
                Stop("Mua thất bại 5 lần liên tiếp và không còn gì để ép/đổi. Có thể kho cầu thủ đã đầy chỗ (hãy bán/xóa bớt cầu thủ không cần) hoặc server từ chối. Phản hồi cuối: " + GameApi.Trim(buy.Raw));
                return;
            }
            await Pacing.Wait(4, 8, pace, tok);
            return;
        }
        _buyFailures = 0; _waitFreeSince = null;
        if (!_api.LastHadMoney) _api.Spend(cheapest.Price);
        Interlocked.Add(ref _spent, cheapest.Price);
        lock (_lock) { _pool[1].Add(buy.CardId!); _cardCost[buy.CardId!] = cheapest.Price; _bought++; _roundBought++; }
        Interlocked.Increment(ref _cardCount);
        Activity();
        _log($"[Xoay vòng/Mua] Mua thẻ +1 giá {cheapest.Price:N0} (id …{Tail(buy.CardId!)}); trong dây chuyền {Pipeline()}/{_cap} thẻ +1.");
        await Pacing.Wait(3.0, 6.0, pace, tok);
        await Pacing.Think(pace, tok);
    }

    private int _cap = 1;

    // ================= merger =================
    private sealed record MergeJob(int Level, string Main, string Mat, int Eq);

    private MergeJob? TakeMerge(UpgradePlan plan)
    {
        lock (_lock)
        {
            for (int lvl = plan.Target; lvl >= 2; lvl--)
            {
                var (a, b) = plan.Rules[lvl];
                string main, mat;
                if (a == b)
                {
                    if (_pool[a].Count < 2) continue;
                    main = _pool[a][0]; mat = _pool[a][1];
                }
                else
                {
                    if (_pool[a].Count < 1 || _pool[b].Count < 1) continue;
                    main = _pool[Math.Max(a, b)][0]; mat = _pool[Math.Min(a, b)][0];
                }
                _pool[_pool.First(kv => kv.Value.Contains(main)).Key].Remove(main);
                _pool[_pool.First(kv => kv.Value.Contains(mat)).Key].Remove(mat);
                int eq = _need[a] + _need[b];
                _inFlight += eq;
                return new MergeJob(lvl, main, mat, eq);
            }
        }
        return null;
    }

    private void GiveBack(MergeJob job, UpgradePlan plan, bool merged)
    {
        lock (_lock)
        {
            _inFlight -= job.Eq;
            if (merged)
            {
                if (_keepMode && job.Level == plan.Target) _kept++; else _pool[job.Level].Add(job.Main);
                _merged++; _roundMerged++; Interlocked.Decrement(ref _cardCount);
            }
            else
            {
                // the merge did not happen: both cards are still ours
                var (a, b) = plan.Rules[job.Level];
                _pool[a == b ? a : Math.Max(a, b)].Add(job.Main);
                _pool[a == b ? a : Math.Min(a, b)].Add(job.Mat);
            }
        }
    }

    private bool CanMerge(UpgradePlan plan)
    {
        lock (_lock)
            for (int lvl = plan.Target; lvl >= 2; lvl--)
            {
                var (a, b) = plan.Rules[lvl];
                if (a == b ? _pool[a].Count >= 2 : _pool[a].Count >= 1 && _pool[b].Count >= 1) return true;
            }
        return false;
    }

    private async Task MergeOnce(UpgradePlan plan, double pace, CancellationToken tok)
    {
        var job = TakeMerge(plan);
        if (job == null) { _merging = false; await Task.Delay(1000, tok); return; }
        _merging = true;
        bool done = false, resolved = false;
        try
        {
            int lvl = job.Level, step = lvl - 1;
            _step(step, StepState.Running);
            string main = job.Main, mat = job.Mat;
            var (a, b) = plan.Rules[lvl];
            _log($"[Xoay vòng/Ép] Xem trước ép +{lvl} (…{Tail(main)} + …{Tail(mat)})...");
            var pv = await _api.EnhancePreviewAsync(main, mat, tok);
            if (pv.Grade != lvl && a != b)
            {
                (main, mat) = (mat, main);
                pv = await _api.EnhancePreviewAsync(main, mat, tok);
            }
            if (pv.Grade != lvl) { _step(step, StepState.Error); Stop($"Xem trước ép cho ra +{pv.Grade}, mong đợi +{lvl}."); return; }
            if (pv.Probability < 100 && !await _inner.AcceptRiskAsync(lvl, pv.Probability))
            { _step(step, StepState.Error); Stop($"Xác suất ép +{lvl} chỉ {pv.Probability}% (<100%) và bạn không chấp nhận rủi ro."); return; }
            if (pv.Probability < 100) _log($"[Xoay vòng/Ép] Cảnh báo: xác suất ép +{lvl} chỉ {pv.Probability}%.");
            _lastFee = pv.EnhancePay;
            await Pacing.Wait(2.0, 4.5, pace, tok);
            if (!await _inner.EnhanceWithRecoveryAsync(main, mat, lvl, step, pace, tok)) { Stop($"Ép +{lvl} thất bại (xem nhật ký phía trên)."); return; }
            if (!_api.LastHadMoney) _api.Spend(pv.EnhancePay);
            Interlocked.Add(ref _spent, pv.EnhancePay + _inner.LastRestoreCost);
            job = job with { Main = main, Mat = mat };
            if (!_inner.LastEnhanceSucceeded)
            {
                // the merge was applied and failed: the storage tells which of the two cards is left (and at which level)
                _log($"[Xoay vòng/Ép] ÉP +{lvl} KHÔNG THÀNH CÔNG (xác suất {pv.Probability}%), phí {pv.EnhancePay:N0} EP đã mất. Đọc lại kho...");
                var inv2 = await _api.GetInventoryAsync(CancellationToken.None);
                int gone = 0;
                lock (_lock)
                {
                    foreach (var id in new[] { main, mat })
                    {
                        var c = inv2.FirstOrDefault(x => x.Id == id);
                        if (c != null && c.Spid == plan.Spid && c.Group == GameApi.GroupNone && c.Grade >= 1 && c.Grade <= plan.Target) _pool[c.Grade].Add(id);
                        else gone++;
                    }
                    _inFlight -= job.Eq;
                }
                Interlocked.Add(ref _cardCount, -gone);
                resolved = true;
                Activity();
                await Pacing.Wait(3, 7, pace, tok);
                return;
            }
            lock (_lock) _cardCost[main] = _cardCost.GetValueOrDefault(main) + _cardCost.GetValueOrDefault(mat) + pv.EnhancePay;
            done = true;
            _step(step, StepState.Done);
            Activity();
            _log($"[Xoay vòng/Ép] Ép +{lvl} xong, phí {pv.EnhancePay:N0} EP.");
            await Pacing.Wait(3, 7, pace, tok);
            await Pacing.Think(pace, tok);
        }
        finally
        {
            if (!resolved) GiveBack(job, plan, done);
            _merging = false;
        }
    }

    // ================= trader =================
    private async Task TradeOnce(UpgradePlan plan, CycleOptions opt, double pace, CancellationToken tok)
    {
        int waiting; lock (_lock) waiting = _pool[plan.Target].Count;
        if (waiting == 0 || (_targetValue > 0 && waiting * _targetValue < opt.TradeMin))
        {
            await Task.Delay(3000, tok);
            return;
        }

        // the storage is the truth about card values
        var inv = await _api.GetInventoryAsync(tok);
        LearnTargetValue(inv, plan);
        Volatile.Write(ref _cardCount, inv.Count);
        var held = inv.Where(c => c.Spid == plan.Spid && c.Grade == plan.Target && c.Group == GameApi.GroupNone).ToList();
        if (held.Any(c => c.Value <= 0)) { Stop("Không đọc được giá trị của thẻ trong kho, dừng để không tính sai."); return; }
        long heldValue = held.Sum(c => c.Value);
        if (heldValue < opt.TradeMin) { await Task.Delay(6000, tok); return; }

        var eligible = held.Where(c => c.Value <= opt.TradeMax).OrderByDescending(c => c.Value).ToList();
        if (held.Count > eligible.Count)
            _log($"[Xoay vòng/Giao dịch] {held.Count - eligible.Count} thẻ có giá trị riêng vượt {opt.TradeMax:N0} EP nên không đổi được, bỏ qua.");
        var batch = new List<GameApi.OwnedCard>();
        long sum = 0;
        foreach (var c in eligible)
            if (sum + c.Value <= opt.TradeMax) { batch.Add(c); sum += c.Value; }
        if (batch.Count == 0) { Stop("Không có thẻ nào đổi được trong giới hạn của một lần giao dịch."); return; }

        _trading = true;
        _step(CycleRunner.TradeStep, StepState.Running);
        try
        {
            // reserve: from now the buyer sees room for the next batch and keeps working while this trade goes through
            long batchCost;
            lock (_lock)
            {
                foreach (var c in batch) _pool[plan.Target].Remove(c.Id);
                long spentNow = Interlocked.Read(ref _spent);
                batchCost = spentNow - _spentAtReserve;   // spent since the previous batch was taken out: this batch's buys and merges
                _spentAtReserve = spentNow;
            }

            _log($"[Xoay vòng/Giao dịch] Đổi {batch.Count} thẻ +{plan.Target}, tổng giá trị {sum:N0} EP" +
                 (batch.Count < held.Count ? $" (còn {held.Count - batch.Count} thẻ chờ lượt sau)" : "") + ". Đang hỏi phương án...");
            await Pacing.Wait(1.5, 3, pace, tok);
            var prop = await _api.TradeProposalAsync(batch.Select(c => c.Id), tok);
            if (!prop.Ok) { RestoreBatch(batch, plan); _step(CycleRunner.TradeStep, StepState.Error); Stop("Server không đưa ra phương án giao dịch: " + GameApi.Trim(prop.Raw)); return; }
            foreach (var o in prop.Options)
                _log($"[Xoay vòng/Giao dịch] Phương án {o.Index + 1}: SPID {o.Spid} +{o.Grade} (giá {o.Price:N0}) + {o.Ep:N0} EP = {o.Value:N0}");
            var best = prop.Options.OrderByDescending(o => o.Value).First();
            _log($"[Xoay vòng/Giao dịch] Chọn phương án {best.Index + 1} (tổng lớn nhất {best.Value:N0}).");

            try
            {
                await Pacing.Wait(2.0, 4.5, pace, tok);
                tok.ThrowIfCancellationRequested();   // last chance to stop: nothing has been traded yet
            }
            catch (OperationCanceledException) { RestoreBatch(batch, plan); throw; }

            // committed: a stop request does not interrupt it from here
            _log($"[Xoay vòng/Giao dịch] Đang chốt phương án {best.Index + 1}...");
            var res = await _api.TradeContractAsync(best.Index, 0, CancellationToken.None);
            if (!res.Ok) { _step(CycleRunner.TradeStep, StepState.Error); Stop("Giao dịch thất bại: " + GameApi.Trim(res.Raw)); return; }
            await _api.GetWalletAsync(CancellationToken.None);
            Interlocked.Add(ref _cardCount, 1 - batch.Count);

            RoundStat st;
            lock (_lock)
            {
                st = new RoundStat(_round1, _roundBought, _roundMerged, batch.Count, sum, batchCost, best.Ep, best.Price);
                _totalNet += st.Net;
                _lossStreak = st.Net < 0 ? _lossStreak + 1 : 0;
                _tradesDone++; _round1++; _roundBought = _roundMerged = 0;
            }
            _step(CycleRunner.TradeStep, StepState.Done);
            Activity();
            _round?.Invoke(st);
            _log($"[Xoay vòng] KẾT THÚC VÒNG {st.Round}: mua {st.Bought} thẻ +1, ép {st.Merged} lần, đổi {st.Cards} thẻ ({st.CardValue:N0}); chi phí mua+ép lô này {st.Cost:N0}, nhận {st.IncomeEp:N0} EP + " +
                 $"cầu thủ giá {st.ReceivedPlayerPrice:N0}, " + (st.Net >= 0 ? "LÃI " : "LỖ ") + $"{Math.Abs(st.Net):N0} EP. Tổng cả phiên {Signed(_totalNet)} EP.");
            if (_lossStreak >= opt.MaxLossRounds) Stop($"Đã lỗ {_lossStreak} lần giao dịch liên tiếp, dừng theo cài đặt.");
        }
        finally { _trading = false; }
    }

    private void RestoreBatch(List<GameApi.OwnedCard> batch, UpgradePlan plan)
    {
        lock (_lock) foreach (var c in batch) _pool[plan.Target].Add(c.Id);
    }

    // ================= seller (sell mode) =================
    /// <summary>Takes one target-level card and lists it at the highest price the game allows. The listing fee is paid now, the game's cut when it sells.</summary>
    private async Task SellOnce(UpgradePlan plan, CycleOptions opt, double pace, CancellationToken tok)
    {
        string? id;
        lock (_lock)
        {
            id = _pool[plan.Target].FirstOrDefault(x => !_offered.Contains(x));
            if (id != null) _pool[plan.Target].Remove(id);
        }
        if (id == null) { _selling = false; await Task.Delay(1500, tok); return; }
        _selling = true;
        bool listed = false;
        _step(CycleRunner.TradeStep, StepState.Running);
        try
        {
            var price = await _api.GetSellPriceAsync(plan.Spid, plan.Target, tok);
            if (price == null) { _step(CycleRunner.TradeStep, StepState.Error); Stop("Không đọc được khung giá bán của thẻ này (/transfer/price)."); return; }
            if (Ep() < price.Fee)
            {
                if (_pending > 0 && opt.AutoCollect) { await Task.Delay(5000, tok); return; }   // money of sold cards will arrive
                Stop($"Không đủ EP để trả phí đăng bán ({price.Fee:N0}, còn {Ep():N0}).");
                return;
            }
            await Pacing.Wait(1.5, 3, pace, tok);
            tok.ThrowIfCancellationRequested();   // last chance to stop: nothing is listed yet

            long cost; lock (_lock) cost = _cardCost.GetValueOrDefault(id);   // what went into this very card
            _log($"[Xoay vòng/Bán] Đăng bán thẻ +{plan.Target} (…{Tail(id)}) giá cao nhất {price.Upper:N0} EP (khung {price.Lower:N0}–{price.Upper:N0}), phí đăng {price.Fee:N0}...");
            // committed: a stop request does not interrupt it from here
            var res = await _api.SellAsync(id, price.Upper, CancellationToken.None);
            if (!res.Ok)
            {
                _step(CycleRunner.TradeStep, StepState.Error);
                _log($"[Xoay vòng/Bán] Đăng bán thất bại: {GameApi.Trim(res.Raw)}");
                if (++_sellFailures >= 3) { Stop("Đăng bán thất bại 3 lần liên tiếp: " + GameApi.Trim(res.Raw)); return; }
                await Task.Delay(5000, tok);
                return;
            }
            listed = true; _sellFailures = 0;
            if (GameApi.ParseWallet(res.Raw) == null) _api.Spend(price.Fee);
            long cut = res.Offer?.CompleteFee > 0 ? res.Offer.CompleteFee : price.Upper / 5;
            long expectedIn = price.Upper - cut;
            RoundStat st;
            lock (_lock)
            {
                _offered.Add(id);
                st = new RoundStat(_round1, _roundBought, _roundMerged, 1, price.Upper, cost + price.Fee, expectedIn, 0);
                _totalNet += st.Net;
                _lossStreak = st.Net < 0 ? _lossStreak + 1 : 0;
                _sold++; _pending++; _round1++; _roundBought = _roundMerged = 0;
            }
            Interlocked.Decrement(ref _cardCount);   // a listed card leaves the storage, it does not take a slot
            _step(CycleRunner.TradeStep, StepState.Done);
            Activity();
            _round?.Invoke(st);
            _log($"[Xoay vòng] ĐÃ ĐĂNG BÁN thẻ {_sold}: giá {price.Upper:N0}, game giữ {cut:N0} khi bán xong → nhận ≈{expectedIn:N0}; chi phí của thẻ này (mua+ép) {cost:N0} + phí đăng {price.Fee:N0}, " +
                 (st.Net >= 0 ? "LÃI " : "LỖ ") + $"≈{Math.Abs(st.Net):N0} EP (ước tính). Tổng ước tính {Signed(_totalNet)} EP.");
            if (_lossStreak >= opt.MaxLossRounds) Stop($"Đã lỗ {_lossStreak} lần bán liên tiếp, dừng theo cài đặt.");
        }
        finally
        {
            if (!listed) lock (_lock) _pool[plan.Target].Add(id);
            _selling = false;
        }
    }

    /// <summary>Claims the money of every sold listing.</summary>
    private async Task CollectOnce(long spid, double pace, CancellationToken tok)
    {
        await Pacing.Wait(12, 20, pace, tok);
        await CollectNow(spid, tok);
    }

    private async Task CollectNow(long spid, CancellationToken tok)
    {
        var offers = (await _api.GetOffersAsync(tok)).Where(o => o.Spid == spid).ToList();   // never touch the sales of other players
        _pending = offers.Count(o => o.Status == "waiting");
        foreach (var o in offers.Where(o => o.Status != "waiting"))
        {
            if (o.Status != "sold" && o.Status != "completed")   // "sold" is what the game reports (seen on the live server, 2026-11-12)
            {
                if (_statusSeen.Add("status:" + o.Status))
                    _log($"[Xoay vòng/Nhận tiền] Lệnh bán SPID {o.Spid} +{o.Grade} có trạng thái lạ \"{o.Status}\", bỏ qua (chưa biết cách xử lý trạng thái này).");
                continue;
            }
            var r = await _api.CollectOfferAsync(o.OfferId, CancellationToken.None);
            if (r.Ok)
            {
                Interlocked.Add(ref _collected, r.Received);
                Activity();
                _log($"[Xoay vòng/Nhận tiền] Đã nhận {r.Received:N0} EP (thẻ SPID {o.Spid} +{o.Grade} bán {o.Price:N0}). EP hiện có {Ep():N0}. Tổng đã nhận {_collected:N0}.");
            }
            else if (_statusSeen.Add("fail:" + o.OfferId))
                _log($"[Xoay vòng/Nhận tiền] Nhận tiền thất bại: {GameApi.Trim(r.Raw)}");
        }
    }

    // ================= supervisor =================
    private DateTime _lastWallet = DateTime.UtcNow, _lastSession = DateTime.UtcNow;

    private bool busy0() => _trading || _merging || _selling;

    private async Task SuperviseOnce(UpgradePlan plan, CycleOptions opt, CancellationToken tok)
    {
        await Task.Delay(2000, tok);
        _cap = Capacity(plan, opt);

        int waiting; lock (_lock) waiting = _pool[plan.Target].Count;
        long est = waiting * _targetValue;
        string text = opt.KeepMode
            ? $"EP {Ep():N0} · mua {_bought} · ép {_merged} · đã có {_kept} thẻ +{plan.Target} · trong dây chuyền {Pipeline()}/{_cap}" + (_buyBlock.Length > 0 ? " · " + _buyBlock : "")
            : opt.SellMode
            ? $"EP {Ep():N0} · mua {_bought} · ép {_merged} · chờ bán {waiting}×+{plan.Target} · đã đăng {_sold} · chờ người mua {_pending} · đã nhận {_collected:N0} · lãi/lỗ ≈{Signed(_totalNet)}" +
              (_buyBlock.Length > 0 ? " · " + _buyBlock : "")
            : $"Vòng {_round1} · EP {Ep():N0} · mua {_bought} · ép {_merged} · chờ đổi {waiting}×+{plan.Target}" +
              (_targetValue > 0 ? $" ({est:N0}/{opt.TradeMin:N0})" : "") + $" · đã đổi {_tradesDone} lần · lãi/lỗ {Signed(_totalNet)}" +
              (_buyBlock.Length > 0 ? " · " + _buyBlock : "");
        _progress(opt.SellMode || opt.KeepMode ? Math.Min(100, Pipeline() * 100 / Math.Max(1, _cap)) : opt.TradeMin > 0 ? (int)Math.Min(100, est * 100 / opt.TradeMin) : 0, 100, text);

        var now = DateTime.UtcNow;
        if ((now - _lastWallet).TotalSeconds >= 15)
        {
            _lastWallet = now;
            await _api.GetWalletAsync(tok);   // also refreshes the card count
            if (_api.LastCardCount is int n) Volatile.Write(ref _cardCount, n);
        }
        if ((now - _lastSession).TotalSeconds >= 60)
        {
            _lastSession = now;
            var (valid, detail) = await _api.CheckSessionAsync(tok);
            if (!valid) { Stop($"Phiên đã hết hạn ({detail}). Hãy đăng nhập lại."); return; }
        }

        // storage full and nothing left to merge or sell: no way to free a slot -> stop (a few seconds of grace for work in flight)
        bool canSell = opt.SellMode ? waiting > 0 || (_pending > 0 && opt.AutoCollect) : waiting * Math.Max(_targetValue, 1) >= opt.TradeMin;
        bool waitingForBuyers = opt.SellMode && opt.AutoCollect && _pending > 0;   // money of listed cards is on its way
        if (_cardCount >= opt.StorageSlots && !busy0() && !CanMerge(plan) && !canSell)
        {
            _fullSince ??= now;
            if ((now - _fullSince.Value).TotalSeconds >= 10)
            {
                string cards; lock (_lock) cards = string.Join(", ", Enumerable.Range(1, plan.Target).Where(l => _pool[l].Count > 0).Select(l => $"{_pool[l].Count}×+{l}"));
                Stop($"Kho đã đầy ({_cardCount}/{opt.StorageSlots}) và không còn thẻ hợp lệ để ép hoặc đổi. Thẻ của cầu thủ này trong dây chuyền: {(cards.Length > 0 ? cards : "không")}. Hãy bán/xóa bớt cầu thủ trong kho rồi chạy lại.");
                return;
            }
        }
        else _fullSince = null;

        // nothing left that could make progress: no EP and nothing in flight / ready to sell, or no stock on the market for a long time
        double idle = (now - _lastActivity).TotalSeconds;
        bool busy = busy0();
        if (_buyBlockedEp && !busy && idle > 20 && Pipeline() > 0 && !waitingForBuyers && !(opt.SellMode ? waiting > 0 : waiting * Math.Max(_targetValue, 1) >= opt.TradeMin))
        { Stop(_buyBlock + (opt.KeepMode ? " Còn thẻ chưa ép đủ mức." : opt.SellMode ? " Còn thẻ chưa ép đủ mức để bán." : " Còn thẻ chưa đủ giá trị để đổi.")); return; }
        if (_buyBlockedEp && !busy && idle > 20 && Pipeline() == 0 && !waitingForBuyers) { Stop(_buyBlock); return; }
        if (_buyBlock.Length > 0 && !_buyBlockedEp && !busy && idle > 180 && !waitingForBuyers) { Stop(_buyBlock + " Đã chờ quá lâu."); return; }

        // watchdog: nothing bought, merged or traded for a long time and nothing is running -> something is stuck, never loop silently
        if (!busy && idle > StallSeconds && !waitingForBuyers)
        {
            string cards; lock (_lock) cards = string.Join(", ", Enumerable.Range(1, plan.Target).Where(l => _pool[l].Count > 0).Select(l => $"{_pool[l].Count}×+{l}"));
            Stop($"Không có tiến triển trong {StallSeconds / 60.0:0.#} phút (không mua, ép hay đổi được). Thẻ trong dây chuyền: {(cards.Length > 0 ? cards : "không")}; " +
                 $"sức chứa {_cap} thẻ +1. " + (_buyBlock.Length > 0 ? _buyBlock : "Có thể kho đã đầy chỗ hoặc các thẻ còn lại không ghép được theo rule."));
        }
    }
}
