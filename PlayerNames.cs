namespace FO3SessionTool;

/// <summary>Readable card names for the log: "Name - Mùa Europe Legend - Thẻ +5" instead of a bare SPID. Needs Init with the shared PlayerSearch (cached lookups).</summary>
public static class PlayerNames
{
    private static PlayerSearch? _search;
    public static void Init(PlayerSearch search) => _search = search;

    /// <summary>The season as the game names it ("Europe Legend", "2009"); "26" when the game has no name for it.</summary>
    public static string SeasonText(int season)
    {
        var l = _search?.SeasonLabelOf(season) ?? "";
        return l.Length > 0 ? l : season.ToString();
    }

    private static string Join(string name, int season, int grade) =>
        $"{name} - Mùa {SeasonText(season)}" + (grade > 0 ? $" - Thẻ +{grade}" : "");

    /// <summary>When the name is already known (a search result, a plan): just add the season (and the card level when given).</summary>
    public static string Format(string name, int season, int grade = 0) => Join(name, season, grade);

    /// <summary>"Name - Mùa X - Thẻ +N" for a card by SPID: the name from fifaaddict / the game's own files (cached), the season name from the game.
    /// Falls back to "cầu thủ SPID n" when the name is unknown.</summary>
    public static async Task<string> DescribeAsync(long spid, string? knownName = null, int grade = 0)
    {
        int season = (int)(spid / 1_000_000);
        string name = knownName ?? "";
        if (_search != null && name.Length == 0)
        {
            try
            {
                var d = await _search.GetDetailCachedAsync(spid).WaitAsync(TimeSpan.FromSeconds(8));
                if (d != null) { name = d.Name; season = d.Season; }
            }
            catch { }
        }
        return Join(name.Length > 0 ? name : $"cầu thủ SPID {spid}", season, grade);
    }
}
