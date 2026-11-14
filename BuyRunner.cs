namespace FO3SessionTool;

/// <summary>One player to buy (the cheapest listing of this exact card is bought each time).</summary>
public sealed record BuyTarget(long Spid, string Name);

/// <summary>
/// StopBelowEp: never spend EP below this balance. MaxPrice: highest price paid for one card (0 = no cap). AnyGrade: buy a card of any level,
/// otherwise only +1. MaxCards: stop after this many purchases (0 = until EP or Stop). Pace: multiplier of the human-like waits.
/// </summary>
public sealed record BuyOptions(long StopBelowEp = 0, long MaxPrice = 0, bool AnyGrade = false, int MaxCards = 0, double Pace = 1.0);

public sealed record BuyProgress(int Bought, long Spent, long Ep, int Workers);

/// <summary>
/// Automatic buying: one worker (thread) per target player, all running in parallel. Each worker loops: look at the market for its card,
/// take the cheapest listing within the limits, wait a human-like moment, buy it, wait again. Everything stops when the balance would fall
/// below StopBelowEp, the storage is full, the session is gone, MaxCards is reached or the user stops it.
/// </summary>
public sealed class BuyRunner
{
    private const int MaxFailures = 8;   // consecutive failed searches / purchases before a worker gives up

    private readonly GameApi _api;
    private readonly Action<string> _log;
    private readonly Action<BuyProgress> _progress;
    private CancellationTokenSource _stop = new();
    private string? _reason;
    private int _bought, _reserved, _alive;
    private long _spent;
    private readonly object _lock = new();
    private static readonly Random R = new();

    public BuyRunner(GameApi api, Action<string> log, Action<BuyProgress> progress)
    {
        _api = api; _log = log; _progress = progress;
    }

    public int Bought => Volatile.Read(ref _bought);
    public long Spent => Interlocked.Read(ref _spent);

    private void Stop(string reason)
    {
        bool first;
        lock (_lock) { first = _reason == null; _reason ??= reason; }
        if (first) _log("[Mua] DỪNG: " + reason);
        _stop.Cancel();
    }

    private static string Label(BuyTarget t) => t.Name.Length > 0 ? t.Name : t.Spid.ToString();

    public async Task<string> RunAsync(IReadOnlyList<BuyTarget> targets, BuyOptions opt, CancellationToken ct)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tok = _stop.Token;
        _reason = null; _bought = _reserved = 0; _spent = 0; _alive = targets.Count;
        if (targets.Count == 0) return "Chưa chọn cầu thủ nào.";

        try { if (_api.LastWallet == null) await _api.GetWalletAsync(tok); } catch (OperationCanceledException) { } catch { }
        long ep0 = _api.LastWallet?.Ep ?? 0;
        _log($"[Mua] Bắt đầu: {targets.Count} cầu thủ = {targets.Count} luồng mua song song. EP {ep0:N0}, dừng khi EP xuống dưới {opt.StopBelowEp:N0}" +
             (opt.MaxPrice > 0 ? $", giá tối đa {opt.MaxPrice:N0}/thẻ" : "") + (opt.MaxCards > 0 ? $", tối đa {opt.MaxCards} thẻ" : "") +
             (opt.AnyGrade ? ", mọi mức thẻ." : ", chỉ thẻ +1."));
        if (ep0 <= opt.StopBelowEp && ep0 > 0)
            return $"EP hiện có ({ep0:N0}) đã không cao hơn mức dừng ({opt.StopBelowEp:N0}), không mua gì.";

        var workers = targets.Select((t, i) => Worker(t, i, opt, tok)).ToList();
        try { await Task.WhenAll(workers); }
        catch (OperationCanceledException) { }

        string why = _reason ?? (ct.IsCancellationRequested ? "Bạn đã dừng." : "Các luồng đã kết thúc.");
        return $"{why} Đã mua {Bought} thẻ, tổng chi {Spent:N0} EP.";
    }

    private void Report() => _progress(new BuyProgress(Bought, Spent, _api.LastWallet?.Ep ?? 0, Volatile.Read(ref _alive)));

    private static Task Wait(double min, double max, double pace, CancellationToken ct) => Pacing.Wait(min, max, pace, ct);

    private async Task Worker(BuyTarget t, int index, BuyOptions opt, CancellationToken tok)
    {
        string tag = $"[Mua/{Label(t)}]";
        int failures = 0, empty = 0;
        bool unaffordableLogged = false;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(index * 0.8 + R.NextDouble() * 1.5), tok);   // not all workers fire at the same moment
            while (!tok.IsCancellationRequested)
            {
                List<Listing> list;
                try { list = await _api.SearchAsync(t.Spid, tok); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log($"{tag} Lỗi đọc chợ: {ex.Message}");
                    if (++failures >= MaxFailures) { _log($"{tag} Lỗi {MaxFailures} lần liên tiếp, luồng này dừng."); return; }
                    await Wait(5, 10, opt.Pace, tok);
                    continue;
                }

                var offer = list.Where(l => (opt.AnyGrade || l.Grade == 1) && l.Price > 0 && (opt.MaxPrice <= 0 || l.Price <= opt.MaxPrice))
                                .OrderBy(l => l.Price).ThenBy(l => l.Grade).FirstOrDefault();
                if (offer == null)
                {
                    if (empty++ % 10 == 0) _log($"{tag} Chưa có thẻ phù hợp trên chợ, đang chờ...");
                    await Wait(5, 10, opt.Pace, tok);
                    continue;
                }
                empty = 0;

                long ep = _api.LastWallet?.Ep ?? 0;
                if (ep <= opt.StopBelowEp) { Stop($"EP còn {ep:N0}, đã tới mức dừng {opt.StopBelowEp:N0}."); return; }
                if (ep - offer.Price < opt.StopBelowEp)
                {
                    if (!unaffordableLogged) { _log($"{tag} Giá {offer.Price:N0} sẽ làm EP ({ep:N0}) xuống dưới mức dừng {opt.StopBelowEp:N0}: luồng này dừng."); unaffordableLogged = true; }
                    return;
                }

                // reserve a slot of MaxCards before buying, so parallel workers cannot overshoot it
                if (opt.MaxCards > 0 && Interlocked.Increment(ref _reserved) > opt.MaxCards)
                {
                    Interlocked.Decrement(ref _reserved);
                    Stop($"Đã mua đủ {opt.MaxCards} thẻ.");
                    return;
                }

                await Wait(1.5, 3.0, opt.Pace, tok);   // look at the price, then click buy
                var buy = await _api.BuyAsync(t.Spid, offer.Grade, offer.Price, tok);
                if (buy.Ok)
                {
                    failures = 0;
                    if (!buy.HadMoney) _api.Spend(offer.Price);
                    int n = Interlocked.Increment(ref _bought);
                    Interlocked.Add(ref _spent, offer.Price);
                    _log($"{tag} Mua +{offer.Grade} giá {offer.Price:N0} (id …{(buy.CardId!.Length <= 6 ? buy.CardId : buy.CardId[^6..])}). Tổng đã mua {n}.");
                    Report();
                    if (opt.MaxCards > 0 && n >= opt.MaxCards) { Stop($"Đã mua đủ {opt.MaxCards} thẻ."); return; }
                    await Wait(3.0, 6.0, opt.Pace, tok);
                    await Pacing.Think(opt.Pace, tok);
                    continue;
                }

                if (opt.MaxCards > 0) Interlocked.Decrement(ref _reserved);
                if (GameApi.IsStorageFull(buy.Raw)) { Stop("Hết kho: kho cầu thủ đã đầy (server báo \"Exceed plyer limit\"). Hãy bán/xóa bớt cầu thủ trong kho rồi chạy lại."); return; }
                if (buy.Raw.Contains("NOT_SIGNED")) { Stop("Phiên đăng nhập đã hết hạn."); return; }
                _log($"{tag} Mua thất bại ({GameApi.Trim(buy.Raw)}), thử lại...");
                if (++failures >= MaxFailures) { _log($"{tag} Mua thất bại {MaxFailures} lần liên tiếp, luồng này dừng."); return; }
                await Wait(4, 8, opt.Pace, tok);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"{tag} Lỗi: {ex.Message}"); }
        finally
        {
            if (Interlocked.Decrement(ref _alive) == 0)
            {
                lock (_lock) _reason ??= "Tất cả các luồng đã dừng.";
                _stop.Cancel();
            }
            Report();
        }
    }

    /// <summary>Manual purchase: the cheapest listing of one card within the limits. Returns what happened for the log.</summary>
    public static async Task<(bool Ok, string Message, long Price)> BuyCheapestAsync(GameApi api, long spid, string label, BuyOptions opt, CancellationToken ct)
    {
        var list = await api.SearchAsync(spid, ct);
        var offer = list.Where(l => (opt.AnyGrade || l.Grade == 1) && l.Price > 0 && (opt.MaxPrice <= 0 || l.Price <= opt.MaxPrice))
                        .OrderBy(l => l.Price).ThenBy(l => l.Grade).FirstOrDefault();
        if (offer == null) return (false, $"{label}: chưa có thẻ phù hợp trên chợ.", 0);
        long ep = api.LastWallet?.Ep ?? 0;
        if (ep > 0 && ep - offer.Price < opt.StopBelowEp) return (false, $"{label}: giá {offer.Price:N0} làm EP ({ep:N0}) xuống dưới mức dừng {opt.StopBelowEp:N0}.", 0);
        await Pacing.Wait(0.8, 1.6, opt.Pace, ct);
        var buy = await api.BuyAsync(spid, offer.Grade, offer.Price, ct);
        if (buy.Ok)
        {
            if (!buy.HadMoney) api.Spend(offer.Price);
            return (true, $"{label}: đã mua +{offer.Grade} giá {offer.Price:N0}.", offer.Price);
        }
        if (GameApi.IsStorageFull(buy.Raw)) return (false, $"{label}: Hết kho, kho cầu thủ đã đầy (\"Exceed plyer limit\").", 0);
        return (false, $"{label}: mua thất bại ({GameApi.Trim(buy.Raw)}), có thể giá vừa đổi hoặc thẻ đã bị mua.", 0);
    }
}
