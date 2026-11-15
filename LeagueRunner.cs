using System.Text.Json;

namespace FO3SessionTool;

/// <summary>The three numbers league2/match/sim needs for a round (plus the round itself).</summary>
public sealed record LeagueFixture(int Round, long Date, int Home, int Away);

/// <summary>
/// Works out the parameters of a simulated round from league2/me only (the 20 team ids and the opening day):
/// the fixtures are the standard round-robin (circle method) over the team list, played backwards, second leg = first leg with home and away swapped
/// (checked against 6 played rounds of the new season and the whole old one); the date is one week per round after the opening day (an estimate,
/// the game's own dates wobble by a day).
/// </summary>
public static class LeagueFixtures
{
    private const long Day = 24L * 3600 * 1000;

    public static LeagueFixture? For(GameApi.LeagueMe me, int round)
    {
        int n = me.Teams.Length;
        if (n < 2 || n % 2 != 0 || round < 0 || round >= 2 * (n - 1)) return null;
        int legs = n - 1;
        int r = round % legs;
        int k = (legs - r) % legs;                  // the game walks the circle backwards
        var arr = me.Teams.ToList();
        for (int step = 0; step < k; step++) { var last = arr[^1]; arr.RemoveAt(arr.Count - 1); arr.Insert(1, last); }
        for (int i = 0; i < n / 2; i++)
        {
            int a = arr[i], b = arr[n - 1 - i];
            if (a != me.TeamId && b != me.TeamId) continue;
            bool home = a == me.TeamId;             // first leg: the list order decides (it also holds for the fixed team except one round, see note)
            if (i == 0) home = a == me.TeamId ^ (r % 4 is 2 or 3);   // observed A A B B A A over rounds 0..5
            if (round >= legs) home = !home;        // second leg: sides swapped
            long date = me.OpeningDay + 7 * Day + 7 * 3600 * 1000L + round * 7 * Day;
            return new LeagueFixture(round, date, home ? me.TeamId : (a == me.TeamId ? b : a), home ? (a == me.TeamId ? b : a) : me.TeamId);
        }
        return null;
    }
}

/// <summary>
/// The game's own league (mùa giải), simulated by the server: for every round the same calls the game client makes
/// (sponsor achievement, history, coach/me, league2/match/sim, league2/back, matchup). The server decides the score.
/// </summary>
public sealed class LeagueRunner
{
    private readonly GameApi _api;
    private readonly Action<string> _log;
    private readonly Action<string> _progress;

    public LeagueRunner(GameApi api, Action<string> log, Action<string> progress)
    {
        _api = api; _log = log; _progress = progress;
    }

    /// <param name="maxRounds">0 = until the season ends or something is missing.</param>
    public async Task<string> RunAsync(int maxRounds, double pace, CancellationToken ct)
    {
        int played = 0;
        try
        {
            var me = await _api.GetLeagueMeAsync(ct);
            if (me == null) return "Không đọc được trạng thái mùa giải (league2/me).";
            int? cards = await _api.GetItemCountAsync(GameApi.LeagueSimCardId, ct);
            if (cards == 0) return "Hết thẻ mô phỏng giải đấu: không còn thẻ nào trong túi để giả lập.";
            _log($"[Mùa giải] Bắt đầu: đội {me.TeamId}, vòng kế tiếp {me.NextRound}/{me.MaxRound}, thẻ mô phỏng: {(cards?.ToString() ?? "không đọc được")}.");

            int round = me.NextRound;
            while (!ct.IsCancellationRequested && round <= me.MaxRound && (maxRounds <= 0 || played < maxRounds))
            {
                var fx = LeagueFixtures.For(me, round);
                if (fx == null) return $"Dừng ở vòng {round}: không dựng được thông số vòng này (danh sách đội không đủ 20). Đã giả lập {played} vòng.";

                if (cards is <= 0) return $"Hết thẻ mô phỏng giải đấu sau {played} vòng.";
                _progress($"Vòng {round}/{me.MaxRound}" + (cards != null ? $" (còn {cards} thẻ mô phỏng)" : "") + "...");
                await _api.LeaguePrepareAsync(ct);
                await Pacing.Wait(2, 5, pace, ct);
                var sim = await _api.LeagueSimAsync(fx.Date, fx.Home, fx.Away, round, ct);
                if (!sim.Ok) return $"Dừng ở vòng {round}: server không chấp nhận ({GameApi.Trim(sim.Raw)}). Đã giả lập {played} vòng.";
                played++;
                if (cards != null) cards = await _api.GetItemCountAsync(GameApi.LeagueSimCardId, ct) ?? cards - 1;
                _log($"[Mùa giải] Vòng {round}: {fx.Home} {sim.HomeGoals}-{sim.AwayGoals} {fx.Away}.");
                await Pacing.Wait(3, 7, pace, ct);
                await _api.LeagueBackAsync(ct);
                await Pacing.Wait(1.5, 3.5, pace, ct);

                var after = await _api.GetLeagueMeAsync(ct);
                if (after == null) return $"Dừng sau vòng {round}: không đọc lại được trạng thái mùa giải.";
                if (after.NextRound <= round) return $"Dừng sau vòng {round}: server vẫn báo vòng kế tiếp là {after.NextRound}, không tiến lên.";
                round = after.NextRound;
                await Pacing.Think(pace, ct);
            }
            return round > me.MaxRound ? $"Đã hết mùa giải. Giả lập {played} vòng." : (ct.IsCancellationRequested ? $"Bạn đã dừng. Giả lập {played} vòng." : $"Đã chạy đủ {played} vòng.");
        }
        catch (OperationCanceledException) { return $"Bạn đã dừng. Giả lập {played} vòng."; }
        catch (Exception ex) { return $"Lỗi: {ex.Message}. Đã giả lập {played} vòng."; }
    }
}
