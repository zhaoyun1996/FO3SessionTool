namespace FO3SessionTool;

/// <summary>
/// Lists cards on the transfer market at the highest price the game allows (the "upper" of /transfer/price), one after the other.
/// Used by the one-shot run after the merging is done. The listing fee is paid when listing, the game keeps its cut when a card sells.
/// Money of sold cards is not collected here (the player does it in the game).
/// </summary>
public static class MarketSeller
{
    public sealed record Result(int Listed, long ListedValue, long Fees, string? Problem, int Collected = 0, long CollectedEp = 0);

    public static async Task<Result> SellAsync(GameApi api, UpgradePlan plan, IReadOnlyList<string> cardIds,
        Action<string> log, Action<int, StepState> step, CancellationToken ct)
    {
        int stepIndex = UpgradeRunner.MaxLevel;
        if (cardIds.Count == 0) { log("[Bán] Không có thẻ +" + plan.Target + " nào vừa ép ra để bán."); return new Result(0, 0, 0, null); }

        step(stepIndex, StepState.Running);
        var price = await api.GetSellPriceAsync(plan.Spid, plan.Target, ct);
        if (price == null) { step(stepIndex, StepState.Error); return new Result(0, 0, 0, "Không đọc được khung giá bán (/transfer/price)."); }
        log($"[Bán] Đăng bán {cardIds.Count} thẻ +{plan.Target} ở giá cao nhất {price.Upper:N0} EP (khung {price.Lower:N0}–{price.Upper:N0}), phí đăng {price.Fee:N0}/thẻ.");

        int listed = 0, failures = 0; long fees = 0;
        foreach (var id in cardIds)
        {
            ct.ThrowIfCancellationRequested();   // a stop request ends the selling between two cards, never in the middle of one
            if ((api.LastWallet?.Ep ?? 0) < price.Fee)
            {
                step(stepIndex, StepState.Error);
                return new Result(listed, listed * price.Upper, fees, $"Không đủ EP để trả phí đăng bán ({price.Fee:N0}, còn {api.LastWallet?.Ep ?? 0:N0}).");
            }
            await Pacing.Wait(1.5, 3, plan.Pace, ct);
            ct.ThrowIfCancellationRequested();
            var res = await api.SellAsync(id, price.Upper, CancellationToken.None);   // committed
            if (!res.Ok)
            {
                log($"[Bán] Đăng bán thẻ …{(id.Length > 6 ? id[^6..] : id)} thất bại: {GameApi.Trim(res.Raw)}");
                if (++failures >= 3) { step(stepIndex, StepState.Error); return new Result(listed, listed * price.Upper, fees, "Đăng bán thất bại 3 lần liên tiếp: " + GameApi.Trim(res.Raw)); }
                continue;
            }
            failures = 0; listed++; fees += price.Fee;
            if (GameApi.ParseWallet(res.Raw) == null) api.Spend(price.Fee);
            log($"[Bán] Đã đăng bán {listed}/{cardIds.Count}: giá {price.Upper:N0}, game giữ {(res.Offer?.CompleteFee > 0 ? res.Offer.CompleteFee : price.Upper / 5):N0} khi bán xong.");
        }
        step(stepIndex, StepState.Done);
        if (!plan.CollectAfter || listed == 0) return new Result(listed, listed * price.Upper, fees, null);

        // wait (a few minutes at most) for the buyers and collect the money of the cards listed just now
        log("[Bán] Chờ người mua và tự nhận tiền (tối đa 3 phút)...");
        var mine = new HashSet<string>(cardIds);
        int collected = 0; long got = 0;
        var seen = new HashSet<string>();
        var until = DateTime.UtcNow.AddMinutes(3);
        try
        {
            while (collected < listed && DateTime.UtcNow < until)
            {
                var offers = (await api.GetOffersAsync(ct)).Where(o => mine.Contains(o.CardId)).ToList();
                foreach (var o in offers.Where(o => o.Status != "waiting"))
                {
                    if (o.Status != "sold" && o.Status != "completed")
                    {
                        if (seen.Add("s" + o.Status)) log($"[Bán] Lệnh bán có trạng thái lạ \"{o.Status}\", bỏ qua.");
                        continue;
                    }
                    var r = await api.CollectOfferAsync(o.OfferId, CancellationToken.None);
                    if (r.Ok) { collected++; got += r.Received; log($"[Bán] Đã nhận {r.Received:N0} EP (bán {o.Price:N0}). EP hiện có {api.LastWallet?.Ep ?? 0:N0}."); }
                    else if (seen.Add("f" + o.OfferId)) log($"[Bán] Nhận tiền thất bại: {GameApi.Trim(r.Raw)}");
                }
                if (collected >= listed) break;
                await Task.Delay(TimeSpan.FromSeconds(8), ct);
            }
        }
        catch (OperationCanceledException) { log("[Bán] Đã dừng chờ nhận tiền."); }
        return new Result(listed, listed * price.Upper, fees, null, collected, got);
    }
}
