using System.Text.Json;
using System.Text.RegularExpressions;

namespace FO3SessionTool;

/// <summary>One fixture of the game's league mode (league2): the numbers the client sends with league2/match/sim.</summary>
public sealed record LeagueFixture(int Round, long Date, int Home, int Away);

/// <summary>
/// The league calendar (date, home, away per round) is not served by any endpoint seen so far, the game client works it out itself.
/// It is learned from a capture of one season played in the game and kept in %APPDATA%\FO3SessionTool\league_schedule.json.
/// </summary>
public sealed class LeagueSchedule
{
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

    public string Describe() => Rounds.Count == 0 ? "chưa có lịch"
        : $"{Rounds.Count} vòng (vòng {Rounds.Min(r => r.Round)} đến {Rounds.Max(r => r.Round)}), đội {TeamId}";

    /// <summary>Reads every league2/match/sim request of a game capture (the format capture\game_capture.py writes) and merges them in.
    /// Returns how many rounds were added or updated.</summary>
    public int MergeFromCapture(string captureFile)
    {
        var text = File.ReadAllText(captureFile);
        int added = 0;
        foreach (Match m in Regex.Matches(text, @"POST http://[^\s]+/league2/match/sim\r?\n.*?(\{[^{}]*""matchtype""[^{}]*\})", RegexOptions.Singleline))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                var r = doc.RootElement;
                if (r.GetProperty("matchtype").GetString() != "league") continue;
                var fx = new LeagueFixture(r.GetProperty("round").GetInt32(), r.GetProperty("date").GetInt64(), r.GetProperty("home").GetInt32(), r.GetProperty("away").GetInt32());
                Rounds.RemoveAll(x => x.Round == fx.Round);
                Rounds.Add(fx); added++;
            }
            catch (Exception) { }
        }
        Rounds = Rounds.OrderBy(x => x.Round).ToList();
        return added;
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
            _log($"[Mùa giải] Bắt đầu: đội {me.TeamId}, vòng kế tiếp {me.NextRound}/{me.MaxRound}.");

            int round = me.NextRound;
            while (!ct.IsCancellationRequested && round <= me.MaxRound && (maxRounds <= 0 || played < maxRounds))
            {
                var fx = _schedule.Get(round);
                if (fx == null) return $"Dừng ở vòng {round}: chưa có lịch vòng này (nạp thêm từ bản ghi một mùa đã chơi). Đã giả lập {played} vòng.";
                if (fx.Home != me.TeamId && fx.Away != me.TeamId) return $"Dừng ở vòng {round}: lịch không có đội {me.TeamId} (đội nhà {fx.Home}, đội khách {fx.Away}). Đã giả lập {played} vòng.";

                _progress($"Vòng {round}/{me.MaxRound}...");
                await _api.LeaguePrepareAsync(ct);
                await Pacing.Wait(2, 5, pace, ct);
                var sim = await _api.LeagueSimAsync(fx.Date, fx.Home, fx.Away, round, ct);
                if (!sim.Ok) return $"Dừng ở vòng {round}: server không chấp nhận ({GameApi.Trim(sim.Raw)}). Đã giả lập {played} vòng.";
                played++;
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
