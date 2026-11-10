namespace FO3SessionTool;

/// <summary>
/// Settings of the loop. The cards of the target level pile up until their total value reaches TradeMin, then they are all traded
/// (never more than TradeMax, the game's limit per trade). MaxLossRounds: stop after that many trades in a row that ended
/// with less EP than the interval before them started with. StorageSlots: size of the card storage (200 in the game); when it is
/// full and nothing can be merged or traded any more, the run stops.
/// </summary>
public sealed record CycleOptions(long TradeMin = 900_000_000, long TradeMax = 1_000_000_000, int MaxLossRounds = 2, int StorageSlots = 200);

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
    private volatile bool _trading, _merging;
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

            // starting stock: every usable card of this player (outside the lineup)
            var inv = await _api.GetInventoryAsync(tok);
            _cardCount = inv.Count;
            _pool = Enumerable.Range(1, plan.Target).ToDictionary(l => l, _ => new List<string>());
            foreach (var c in inv.Where(c => c.Spid == plan.Spid && c.Group == GameApi.GroupNone && c.Grade >= 1 && c.Grade <= plan.Target)) _pool[c.Grade].Add(c.Id);
            LearnTargetValue(inv, plan);
            _cap = Capacity(plan, opt);
            var have = Enumerable.Range(1, plan.Target).Where(l => _pool[l].Count > 0).Select(l => $"{_pool[l].Count}×+{l}");
            _log($"[Xoay vòng] Bắt đầu song song Mua / Ép / Giao dịch. EP {Ep():N0}. Thẻ có sẵn: {(have.Any() ? string.Join(", ", have) : "không")}. Kho {_cardCount}/{opt.StorageSlots}. " +
                 $"Đổi khi tổng thẻ +{plan.Target} đạt {opt.TradeMin:N0}–{opt.TradeMax:N0} EP; dừng sau {opt.MaxLossRounds} lần giao dịch lỗ liên tiếp.");

            await Task.WhenAll(
                Loop("Mua", () => BuyOnce(plan, opt, pace, tok), tok),
                Loop("Ép", () => MergeOnce(plan, pace, tok), tok),
                Loop("Giao dịch", () => TradeOnce(plan, opt, pace, tok), tok),
                Loop("Giám sát", () => SuperviseOnce(plan, opt, tok), tok));
        }
        catch (OperationCanceledException) when (tok.IsCancellationRequested) { }
        catch (Exception ex) { Stop("Lỗi: " + ex.Message); }

        string summary;
        lock (_lock)
        {
            var left = Enumerable.Range(1, plan.Target).Where(l => _pool.TryGetValue(l, out var p) && p.Count > 0).Select(l => $"{_pool[l].Count}×+{l}");
            summary = $" Đã mua {_bought} thẻ +1, ép {_merged} lần, giao dịch {_tradesDone} lần, lãi/lỗ {Signed(_totalNet)} EP." +
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
        int players = _targetValue > 0
            ? (int)Math.Max(1, (opt.TradeMin + _targetValue - 1) / _targetValue)
            : Math.Max(1, plan.Quantity / Math.Max(1, _need[plan.Target]));
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
        _buyBlock = ""; _buyBlockedEp = false;

        _step(0, StepState.Running);
        await Pacing.Wait(0.8, 2.0, pace, tok);
        var buy = await _api.BuyAsync(plan.Spid, 1, cheapest.Price, tok);
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
            await Pacing.Wait(3, 6, pace, tok);
            return;
        }
        _buyFailures = 0; _waitFreeSince = null;
        if (!_api.LastHadMoney) _api.Spend(cheapest.Price);
        Interlocked.Add(ref _spent, cheapest.Price);
        lock (_lock) { _pool[1].Add(buy.CardId!); _bought++; _roundBought++; }
        Interlocked.Increment(ref _cardCount);
        Activity();
        _log($"[Xoay vòng/Mua] Mua thẻ +1 giá {cheapest.Price:N0} (id …{Tail(buy.CardId!)}); trong dây chuyền {Pipeline()}/{_cap} thẻ +1.");
        await Pacing.Wait(2.0, 4.5, pace, tok);
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
            if (merged) { _pool[job.Level].Add(job.Main); _merged++; _roundMerged++; Interlocked.Decrement(ref _cardCount); }
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
        bool done = false;
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
            if (pv.Probability < 100) { _step(step, StepState.Error); Stop($"Xác suất ép +{lvl} chỉ {pv.Probability}% (<100%), dừng để không mất thẻ."); return; }
            _lastFee = pv.EnhancePay;
            await Pacing.Wait(2.0, 4.5, pace, tok);
            if (!await _inner.EnhanceWithRecoveryAsync(main, mat, lvl, step, pace, tok)) { Stop($"Ép +{lvl} thất bại (xem nhật ký phía trên)."); return; }
            if (!_api.LastHadMoney) _api.Spend(pv.EnhancePay);
            Interlocked.Add(ref _spent, pv.EnhancePay);
            job = job with { Main = main, Mat = mat };
            done = true;
            _step(step, StepState.Done);
            Activity();
            _log($"[Xoay vòng/Ép] Ép +{lvl} xong, phí {pv.EnhancePay:N0} EP.");
            await Pacing.Wait(3, 7, pace, tok);
            await Pacing.Think(pace, tok);
        }
        finally
        {
            GiveBack(job, plan, done);
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

    // ================= supervisor =================
    private DateTime _lastWallet = DateTime.UtcNow, _lastSession = DateTime.UtcNow;

    private bool busy0() => _trading || _merging;

    private async Task SuperviseOnce(UpgradePlan plan, CycleOptions opt, CancellationToken tok)
    {
        await Task.Delay(2000, tok);
        _cap = Capacity(plan, opt);

        int waiting; lock (_lock) waiting = _pool[plan.Target].Count;
        long est = waiting * _targetValue;
        string text = $"Vòng {_round1} · EP {Ep():N0} · mua {_bought} · ép {_merged} · chờ đổi {waiting}×+{plan.Target}" +
                      (_targetValue > 0 ? $" ({est:N0}/{opt.TradeMin:N0})" : "") + $" · đã đổi {_tradesDone} lần · lãi/lỗ {Signed(_totalNet)}" +
                      (_buyBlock.Length > 0 ? " · " + _buyBlock : "");
        _progress(opt.TradeMin > 0 ? (int)Math.Min(100, est * 100 / opt.TradeMin) : 0, 100, text);

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
        bool canSell = waiting * Math.Max(_targetValue, 1) >= opt.TradeMin;
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
        if (_buyBlockedEp && !busy && idle > 20 && Pipeline() > 0 && waiting * Math.Max(_targetValue, 1) < opt.TradeMin)
        { Stop(_buyBlock + " Còn thẻ chưa đủ giá trị để đổi."); return; }
        if (_buyBlockedEp && !busy && idle > 20 && Pipeline() == 0) { Stop(_buyBlock); return; }
        if (_buyBlock.Length > 0 && !_buyBlockedEp && !busy && idle > 180) { Stop(_buyBlock + " Đã chờ quá lâu."); return; }

        // watchdog: nothing bought, merged or traded for a long time and nothing is running -> something is stuck, never loop silently
        if (!busy && idle > StallSeconds)
        {
            string cards; lock (_lock) cards = string.Join(", ", Enumerable.Range(1, plan.Target).Where(l => _pool[l].Count > 0).Select(l => $"{_pool[l].Count}×+{l}"));
            Stop($"Không có tiến triển trong {StallSeconds / 60.0:0.#} phút (không mua, ép hay đổi được). Thẻ trong dây chuyền: {(cards.Length > 0 ? cards : "không")}; " +
                 $"sức chứa {_cap} thẻ +1. " + (_buyBlock.Length > 0 ? _buyBlock : "Có thể kho đã đầy chỗ hoặc các thẻ còn lại không ghép được theo rule."));
        }
    }
}
