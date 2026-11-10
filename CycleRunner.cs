namespace FO3SessionTool;

/// <summary>
/// Settings of the endless loop. The cards of the target level pile up (buy + merge, again and again) until their total value
/// reaches TradeMin; then they are all traded (never more than TradeMax, the game's limit per trade).
/// MaxLossRounds: stop after that many trade rounds in a row that ended with less EP than they started with.
/// </summary>
public sealed record CycleOptions(long TradeMin = 900_000_000, long TradeMax = 1_000_000_000, int MaxLossRounds = 2);

/// <summary>One trade round: everything from the previous trade (or the start) up to and including this trade.</summary>
public sealed record RoundStat(int Round, int Cycles, int Cards, long CardValue, long EpBefore, long EpAfter, long ReceivedPlayerPrice)
{
    /// <summary>EP gained (or lost) over the round, counting the value of the player received in the trade.</summary>
    public long Net => EpAfter - EpBefore + ReceivedPlayerPrice;
}

/// <summary>
/// Buy -> merge -> trade, over and over, until the EP runs out, the loop loses money, something fails or it is stopped.
/// Buying and merging reuse UpgradeRunner (one "cycle" = one full run of it); the trade picks the offer worth the most.
/// RunAsync returns why it ended; stopping by the user throws OperationCanceledException.
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

    public CycleRunner(GameApi api, Action<string> log, Action<int, StepState> step, Action<int, int, string> progress, Action resetSteps, Action<RoundStat>? round = null)
    {
        _api = api; _log = log; _step = step; _progress = progress; _resetSteps = resetSteps; _round = round;
    }

    /// <summary>Cards that would be traded: this player, target grade, not in the lineup.</summary>
    private async Task<List<GameApi.OwnedCard>> HeldAsync(UpgradePlan plan, CancellationToken ct)
    {
        var inv = await _api.GetInventoryAsync(ct);
        return inv.Where(c => c.Spid == plan.Spid && c.Grade == plan.Target && c.Group == GameApi.GroupNone).ToList();
    }

    public async Task<string> RunAsync(UpgradePlan plan, CycleOptions opt, CancellationToken ct)
    {
        var runner = new UpgradeRunner(_api, _log, _step, _progress);
        var buyPlan = plan with { UseInventory = false };
        int round = 1, cycles = 0, lossStreak = 0, cyclesTotal = 0;
        long totalNet = 0, lastCycleCost = 0;

        long Ep() => _api.LastWallet?.Ep ?? 0;
        await _api.GetWalletAsync(ct);
        long roundStartEp = Ep();
        _log($"[Xoay vòng] Bắt đầu. EP hiện có {roundStartEp:N0}. Đổi khi tổng giá trị thẻ +{plan.Target} đạt {opt.TradeMin:N0}–{opt.TradeMax:N0} EP; dừng sau {opt.MaxLossRounds} vòng lỗ liên tiếp.");

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (valid, detail) = await _api.CheckSessionAsync(ct);
            if (!valid) return $"Phiên đã hết hạn ({detail}). Hãy đăng nhập lại.";

            // ---- how much is waiting to be traded? ----
            _log("[Xoay vòng] Đang đọc kho để đếm thẻ +" + plan.Target + " chờ giao dịch...");
            var held = await HeldAsync(plan, ct);
            if (held.Any(c => c.Value <= 0)) return "Không đọc được giá trị của thẻ trong kho, dừng để không tính sai.";
            long heldValue = held.Sum(c => c.Value);
            _log($"[Xoay vòng] Vòng {round}: có {held.Count} thẻ +{plan.Target}, tổng giá trị {heldValue:N0} EP (ngưỡng đổi {opt.TradeMin:N0}).");

            if (heldValue >= opt.TradeMin)
            {
                // ---- trade ----
                var stop = await TradeAsync(plan, opt, held, round, cycles, roundStartEp, ct);
                if (stop.Error != null) return stop.Error;
                var st = stop.Stat!;
                totalNet += st.Net;
                lossStreak = st.Net < 0 ? lossStreak + 1 : 0;
                _round?.Invoke(st);
                _log($"[Xoay vòng] KẾT THÚC VÒNG {round}: {st.Cycles} lần mua+ép, đổi {st.Cards} thẻ ({st.CardValue:N0}), EP {st.EpBefore:N0} → {st.EpAfter:N0}, " +
                     $"cầu thủ nhận về giá {st.ReceivedPlayerPrice:N0}, " + (st.Net >= 0 ? "LÃI " : "LỖ ") + $"{Math.Abs(st.Net):N0} EP. Tổng cả phiên: {(totalNet >= 0 ? "+" : "-")}{Math.Abs(totalNet):N0} EP.");
                if (lossStreak >= opt.MaxLossRounds) return $"Đã lỗ {lossStreak} vòng liên tiếp, dừng theo cài đặt. Tổng cả phiên {(totalNet >= 0 ? "+" : "-")}{Math.Abs(totalNet):N0} EP.";
                round++; cycles = 0;
                roundStartEp = Ep();
                continue;
            }

            // ---- one buy + merge cycle: check there is enough EP first ----
            await _api.GetWalletAsync(ct);
            long cheapest = 0;
            var listings = await _api.SearchAsync(plan.Spid, ct);
            var cheap = listings.Where(l => l.Grade == 1).OrderBy(l => l.Price).FirstOrDefault();
            if (cheap != null) cheapest = cheap.Price;
            long need = Math.Max(plan.Quantity * cheapest, lastCycleCost);
            if (Ep() < need)
                return $"Không đủ EP cho một lượt mua + ép tiếp theo (cần khoảng {need:N0}, còn {Ep():N0}). Tổng cả phiên {(totalNet >= 0 ? "+" : "-")}{Math.Abs(totalNet):N0} EP; còn {held.Count} thẻ +{plan.Target} chưa đổi.";

            cycles++; cyclesTotal++;
            _resetSteps();
            long before = Ep();
            _log($"[Xoay vòng] Vòng {round}, lượt mua+ép thứ {cycles}: mua {plan.Quantity} thẻ +1 rồi ép lên +{plan.Target}.");
            bool ok = await runner.RunAsync(buyPlan, ct);
            await _api.GetWalletAsync(ct);
            lastCycleCost = Math.Max(0, before - Ep());
            if (!ok)
            {
                var (v2, d2) = await _api.CheckSessionAsync(ct);
                return v2 ? "Mua hoặc ép thẻ dừng do lỗi (xem nhật ký phía trên)." : $"Phiên đã hết hạn ({d2}). Hãy đăng nhập lại.";
            }
            _log($"[Xoay vòng] Lượt mua+ép xong, tốn khoảng {lastCycleCost:N0} EP.");
            await Pacing.Wait(2, 4, plan.Pace, ct);
        }
    }

    private sealed record TradeOutcome(RoundStat? Stat, string? Error);

    private async Task<TradeOutcome> TradeAsync(UpgradePlan plan, CycleOptions opt, List<GameApi.OwnedCard> held, int round, int cycles, long roundStartEp, CancellationToken ct)
    {
        _resetSteps();
        _step(TradeStep, StepState.Running);

        var eligible = held.Where(c => c.Value <= opt.TradeMax).OrderByDescending(c => c.Value).ToList();
        if (held.Count > eligible.Count)
            _log($"[Xoay vòng] {held.Count - eligible.Count} thẻ có giá trị riêng vượt {opt.TradeMax:N0} EP nên không đổi được, bỏ qua.");
        var batch = new List<GameApi.OwnedCard>();
        long sum = 0;
        foreach (var c in eligible)
            if (sum + c.Value <= opt.TradeMax) { batch.Add(c); sum += c.Value; }
        if (batch.Count == 0) { _step(TradeStep, StepState.Error); return new(null, "Không có thẻ nào đổi được trong giới hạn của một lần giao dịch."); }
        if (batch.Count < held.Count && held.Count == eligible.Count)
            _log($"[Xoay vòng] Tổng giá trị vượt {opt.TradeMax:N0}: đổi {batch.Count}/{held.Count} thẻ ({sum:N0} EP), phần còn lại chờ lượt sau.");

        _progress(0, 1, $"Giao dịch {batch.Count} thẻ (tổng {sum:N0} EP)...");
        _log($"[Xoay vòng] Giao dịch {batch.Count} thẻ +{plan.Target}, tổng giá trị {sum:N0} EP. Đang hỏi phương án...");
        await Pacing.Wait(1.5, 3, plan.Pace, ct);
        var prop = await _api.TradeProposalAsync(batch.Select(c => c.Id), ct);
        if (!prop.Ok)
        {
            _step(TradeStep, StepState.Error);
            return new(null, "Server không đưa ra phương án giao dịch: " + GameApi.Trim(prop.Raw));
        }
        foreach (var o in prop.Options)
            _log($"[Xoay vòng] Phương án {o.Index + 1}: SPID {o.Spid} +{o.Grade} (giá {o.Price:N0}) + {o.Ep:N0} EP = {o.Value:N0}");
        var best = prop.Options.OrderByDescending(o => o.Value).First();
        _log($"[Xoay vòng] Chọn phương án {best.Index + 1} (tổng lớn nhất {best.Value:N0}).");

        await Pacing.Wait(2.0, 4.5, plan.Pace, ct);   // read the offers, then confirm
        ct.ThrowIfCancellationRequested();            // last chance to stop: nothing has been traded yet

        // from here the trade is committed: it is not interrupted by a stop request
        _log($"[Xoay vòng] Đang chốt phương án {best.Index + 1}...");
        var res = await _api.TradeContractAsync(best.Index, 0, CancellationToken.None);
        if (!res.Ok)
        {
            _step(TradeStep, StepState.Error);
            return new(null, "Giao dịch thất bại: " + GameApi.Trim(res.Raw));
        }
        _step(TradeStep, StepState.Done);
        await _api.GetWalletAsync(CancellationToken.None);
        long epAfter = _api.LastWallet?.Ep ?? 0;
        return new(new RoundStat(round, cycles, batch.Count, sum, roundStartEp, epAfter, best.Price), null);
    }
}
