using System.Text.Json;
using System.Text.RegularExpressions;

namespace FO3SessionTool;

/// <summary>One fixture of the game's league mode (league2): the numbers the client sends with league2/match/sim.
/// Estimated: the date was not seen in a capture (guessed as one week after the previous known round).</summary>
public sealed record LeagueFixture(int Round, long Date, int Home, int Away, bool Estimated = false);

/// <summary>
/// The league calendar (date, home, away per round) is not served by any endpoint seen so far, the game client works it out itself.
/// Observed in captures: opponents and home / away are the same every season, and round r + 19 repeats round r with home and away swapped
/// (rounds 0..37 = two legs of 20 teams); the dates differ every season (weekend days, 6 to 8 days apart, no rule found).
/// So the fixtures come from any captured season (completed by that mirror rule) and the dates from the captured rounds of the current season;
/// the rest of the dates are estimated and marked as such.
/// </summary>
public sealed class LeagueSchedule
{
    public const int LastRound = 37;
    private const long Week = 7L * 24 * 3600 * 1000;

    public int TeamId { get; set; }
    public List<LeagueFixture> Rounds { get; set; } = new();

    private static string PathOf => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FO3SessionTool", "league_schedule.json");

    public static LeagueSchedule Load()
    {
        try { return JsonSerializer.Deserialize<LeagueSchedule>(File.ReadAllText(PathOf)) ?? new(); } catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOf)!);
        File.WriteAllText(PathOf, JsonSerializer.Serialize(this));
    }

    public LeagueFixture? Get(int round) => Rounds.FirstOrDefault(r => r.Round == round);

    public string Describe()
    {
        if (Rounds.Count == 0) return "chưa có lịch";
        int est = Rounds.Count(r => r.Estimated);
        return $"{Rounds.Count} vòng (vòng {Rounds.Min(r => r.Round)} đến {Rounds.Max(r => r.Round)}), đội {TeamId}" + (est > 0 ? $", trong đó {est} vòng có ngày ước tính" : "");
    }

    private sealed record Seen(int Pos, int Round, long Date, int Home, int Away);

    /// <summary>Reads the league2/match/sim and league2/match requests of a game capture (the format capture\game_capture.py writes) and
    /// builds the whole season from them. Returns the number of rounds in the resulting schedule.</summary>
    public int MergeFromCapture(string captureFile)
    {
        // the capture may still be open for writing by the capture tool: read it with shared access
        string text;
        using (var fs = new FileStream(captureFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, System.Text.Encoding.UTF8))
            text = sr.ReadToEnd();
        var seen = new List<Seen>();
        foreach (Match m in Regex.Matches(text, @"POST http://[^\s]+/league2/match(?:/sim)?\r?\n.*?(\{[^{}]*""matchtype""[^{}]*\})", RegexOptions.Singleline))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                var r = doc.RootElement;
                if (r.GetProperty("matchtype").GetString() != "league") continue;
                seen.Add(new Seen(m.Index, r.GetProperty("round").GetInt32(), r.GetProperty("date").GetInt64(), r.GetProperty("home").GetInt32(), r.GetProperty("away").GetInt32()));
            }
            catch (Exception) { }
        }
        if (seen.Count == 0) return 0;

        // our team: the one in (nearly) every fixture
        int team = seen.SelectMany(x => new[] { x.Home, x.Away }).GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
        // the current season starts at the last league2/calendar call; before it the dates belong to an older season
        int lastCal = text.LastIndexOf("/league2/calendar", StringComparison.Ordinal);
        var current = lastCal >= 0 && seen.Any(x => x.Pos > lastCal) ? seen.Where(x => x.Pos > lastCal).ToList() : seen;

        // fixtures (opponent, are we at home) per round from any season, then the mirror rule for the rest
        var fix = new Dictionary<int, (int Opp, bool Home)>();
        foreach (var x in seen) fix[x.Round] = x.Home == team ? (x.Away, true) : (x.Home, false);
        for (int r = 0; r <= LastRound; r++)
        {
            if (fix.ContainsKey(r)) continue;
            int mirror = r < 19 ? r + 19 : r - 19;
            if (fix.TryGetValue(mirror, out var f)) fix[r] = (f.Opp, !f.Home);
        }

        var known = current.GroupBy(x => x.Round).ToDictionary(g => g.Key, g => g.Last().Date);
        var rounds = new List<LeagueFixture>();
        long lastDate = 0; int lastDateRound = 0;
        for (int r = 0; r <= LastRound; r++)
        {
            if (!fix.TryGetValue(r, out var f)) continue;
            long date; bool est = false;
            if (known.TryGetValue(r, out var d)) { date = d; lastDate = d; lastDateRound = r; }
            else if (lastDate > 0) { date = lastDate + Week * (r - lastDateRound); est = true; }
            else continue;   // before the first known date of the season: not needed (those rounds are already played)
            rounds.Add(new LeagueFixture(r, date, f.Home ? team : f.Opp, f.Home ? f.Opp : team, est));
        }
        TeamId = team;
        Rounds = rounds;
        return rounds.Count;
    }
}

/// <summary>
/// The game's own league (mùa giải), simulated by the server: for every round the same calls the game client makes
/// (sponsor achievement, history, coach/me, league2/match/sim, league2/back, matchup). The server decides the score.
/// </summary>
public sealed class LeagueRunner
{
    private readonly GameApi _api;
    private readonly LeagueSchedule _schedule;
    private readonly Action<string> _log;
    private readonly Action<string> _progress;

    public LeagueRunner(GameApi api, LeagueSchedule schedule, Action<string> log, Action<string> progress)
    {
        _api = api; _schedule = schedule; _log = log; _progress = progress;
    }

    /// <param name="maxRounds">0 = until the season ends or something is missing.</param>
    public async Task<string> RunAsync(int maxRounds, double pace, CancellationToken ct)
    {
        int played = 0;
        try
        {
            var me = await _api.GetLeagueMeAsync(ct);
            if (me == null) return "Không đọc được trạng thái mùa giải (league2/me).";
            if (_schedule.TeamId != 0 && _schedule.TeamId != me.TeamId)
                return $"Lịch đã nạp là của đội {_schedule.TeamId} nhưng tài khoản đang chơi đội {me.TeamId}: hãy nạp lịch từ bản ghi của đúng mùa này.";
            int? cards = await _api.GetItemCountAsync(GameApi.LeagueSimCardId, ct);
            if (cards == 0) return "Hết thẻ mô phỏng giải đấu: không còn thẻ nào trong túi để giả lập.";
            _log($"[Mùa giải] Bắt đầu: đội {me.TeamId}, vòng kế tiếp {me.NextRound}/{me.MaxRound}, thẻ mô phỏng: {(cards?.ToString() ?? "không đọc được")}.");

            int round = me.NextRound;
            while (!ct.IsCancellationRequested && round <= me.MaxRound && (maxRounds <= 0 || played < maxRounds))
            {
                var fx = _schedule.Get(round);
                if (fx == null) return $"Dừng ở vòng {round}: chưa có lịch vòng này (nạp thêm từ bản ghi một mùa đã chơi). Đã giả lập {played} vòng.";
                if (fx.Home != me.TeamId && fx.Away != me.TeamId) return $"Dừng ở vòng {round}: lịch không có đội {me.TeamId} (đội nhà {fx.Home}, đội khách {fx.Away}). Đã giả lập {played} vòng.";

                if (cards is <= 0) return $"Hết thẻ mô phỏng giải đấu sau {played} vòng.";
                _progress($"Vòng {round}/{me.MaxRound}" + (cards != null ? $" (còn {cards} thẻ mô phỏng)" : "") + "...");
                await _api.LeaguePrepareAsync(ct);
                await Pacing.Wait(2, 5, pace, ct);
                if (fx.Estimated) _log($"[Mùa giải] Vòng {round}: ngày thi đấu là ước tính ({DateTimeOffset.FromUnixTimeMilliseconds(fx.Date):yyyy-MM-dd}).");
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
