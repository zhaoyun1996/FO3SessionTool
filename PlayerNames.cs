namespace FO3SessionTool;

/// <summary>Readable card names for the log: "Name (season label)" instead of a bare SPID. Needs Init with the shared PlayerSearch (cached lookups).</summary>
public static class PlayerNames
{
    private static PlayerSearch? _search;
    public static void Init(PlayerSearch search) => _search = search;

    public static string SeasonText(int season)
    {
        var l = _search?.SeasonLabelOf(season) ?? "";
        return l.Length > 0 ? l : $"Mùa {season}";
    }

    /// <summary>When the name is already known (a search result, a plan): just add the season.</summary>
    public static string Format(string name, int season) => $"{name} ({SeasonText(season)})";

    /// <summary>"Name (season)" for a card by SPID: fifaaddict / the game's own files, cached. Falls back to the season and SPID when the name is unknown.</summary>
    public static async Task<string> DescribeAsync(long spid, string? knownName = null)
    {
        int season = (int)(spid / 1_000_000);
        string label = "", club = "";
        string name = knownName ?? "";
        if (_search != null)
        {
            try
            {
                var d = await _search.GetDetailCachedAsync(spid).WaitAsync(TimeSpan.FromSeconds(8));
                if (d != null)
                {
                    if (name.Length == 0) name = d.Name;
                    label = d.SeasonLabel;
                    season = d.Season;
                    club = d.Club;
                }
            }
            catch { }
            if (label.Length == 0) label = _search.SeasonLabelOf(season);
        }
        if (label.Length == 0) label = $"Mùa {season}";
        if (club.Length > 0) label = $"Mùa {season} · {club}";   // the game's own data decides: its team name, not the website's season label
        return name.Length > 0 ? $"{name} ({label})" : $"cầu thủ {label} (SPID {spid})";
    }
}
