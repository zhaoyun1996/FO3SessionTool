namespace FO3SessionTool;

/// <summary>One idea for the auto mode: build this player up to +Target from cheap +1 cards and sell it to the system at the suggested price.</summary>
public sealed record AutoCandidate(long Spid, int Overall, int Target, int Cards, long Cost, long Ask, long TakeHome, long Fees)
{
    public long Profit => TakeHome - Cost - Fees;
    public double Pct => Cost > 0 ? (double)Profit / Cost : 0;
}

/// <summary>Settings of the auto mode (scan, pick the most profitable player at +6 / +7, run a window, scan again).</summary>
public sealed record AutoSettings(bool Use6, bool Use7, double MinPct, int IntervalMinutes, int PoolSize, IReadOnlySet<int>? Seasons, double Pace, long StopBelowEp,
    IReadOnlyDictionary<int, (int A, int B)> Rules, int MaxLossRounds);

public static class AutoPicker
{
    /// <summary>Estimated merge fees of one +target card: the captured fees are 5,000 / 10,000 / 20,000 for +2 / +3 / +4, assumed to keep doubling.</summary>
    public static long MergeFees(int target) => (target - 1) * 5000L * (1L << (target - 2));

    /// <summary>Scans the market: for the strongest cards (the pool) and each wanted target level, what it costs to buy the +1 cards (the cheapest listings, 2^(target-1) of them)
    /// against what the sale to the system pays (the suggested price minus the sale tax and the listing fee). Read only. Best first.</summary>
    public static async Task<List<AutoCandidate>> ScanAsync(GameApi api, AutoSettings s, Action<string> status, CancellationToken ct)
    {
        var targets = new List<int>(); if (s.Use6) targets.Add(6); if (s.Use7) targets.Add(7);
        var db = await LocalPlayerDb.GetAsync() ?? throw new InvalidOperationException("Không đọc được dữ liệu cầu thủ trong thư mục game.");
        var roster = await api.GetRosterSeasonsAsync();
        var pool = db.All.Where(r => r.Overall > 1)
            .Where(r => roster == null || roster.Contains((int)(r.Spid / 1_000_000)))
            .Where(r => s.Seasons == null || s.Seasons.Count == 0 || s.Seasons.Contains((int)(r.Spid / 1_000_000)))
            .OrderByDescending(r => r.Overall).Take(Math.Max(20, s.PoolSize)).ToList();
        var found = new List<AutoCandidate>();
        int done = 0;
        foreach (var r in pool)
        {
            ct.ThrowIfCancellationRequested();
            status($"Đang quét thị trường... {++done}/{pool.Count} cầu thủ, {found.Count} phương án.");
            List<Listing> market;
            try { market = await api.SearchAsync(r.Spid, ct); } catch (OperationCanceledException) { throw; } catch { continue; }
            var ask1 = new List<long>();
            foreach (var l in market.Where(l => l.Grade == 1 && l.Price > 0).OrderBy(l => l.Price))
                for (int i = 0; i < l.Count && ask1.Count < 200; i++) ask1.Add(l.Price);
            foreach (int t in targets)
            {
                int need = 1 << (t - 1);
                if (ask1.Count < need) continue;
                GameApi.SellPrice? p = null;
                try { p = await api.GetSellPriceAsync(r.Spid, t, ct); } catch (OperationCanceledException) { throw; } catch { }
                if (p == null) continue;
                long ask = p.Default > 0 ? p.Default : p.Lower;
                if (ask <= 0 || ask > CycleRunner.SystemBuyMax) continue;
                long cost = ask1.Take(need).Sum();
                found.Add(new AutoCandidate(r.Spid, r.Overall, t, need, cost, ask, ask - CycleRunner.SaleTax(ask) - p.Fee, MergeFees(t) + p.Fee));
                await Pacing.Wait(0.05, 0.12, 1.0, ct);
            }
            await Pacing.Wait(0.05, 0.14, 1.0, ct);
        }
        return found.OrderByDescending(c => c.Pct).ToList();
    }
}
