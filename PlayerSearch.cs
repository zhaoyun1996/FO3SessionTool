using System.Net;
using System.Text.RegularExpressions;

namespace FO3SessionTool;

/// <summary>A position the player can play with its rating. Group is att/mid/def/gk.</summary>
public sealed record PosRating(string Pos, string Group, int Rating, bool Active);

/// <summary>One row of the fifaaddict player list. Stats are Finishing, Sprint, Strength, Dribble, Stamina.</summary>
public sealed record PlayerInfo(
    long Spid, string Name, int Season, string Position, bool Limited,
    int Overall, int[] Stats, long PriceKr, long PriceVn, string ImageUrl,
    IReadOnlyList<PosRating> Positions, int SkillMoves, string Team = "", bool Approx = false);

/// <summary>The minimum profile shown on the upgrade tab (from fo3player.php?id=SPID).</summary>
public sealed record PlayerDetail(
    long Spid, string Name, int Season, string SeasonLabel, string Nation, string NationCode,
    string Club, string CrestUrl, string BirthDate, string Height, string Weight, string Body,
    int LeftFoot, int RightFoot, bool PrefersRight, IReadOnlyList<PosRating> Positions, int SkillMoves, string ImageUrl);

/// <summary>Scrapes https://en.fifaaddict.com/fo3db.php?q=player&amp;name=... (server-rendered HTML).</summary>
public sealed class PlayerSearch : IDisposable
{
    public static readonly string[] StatNames = { "Dứt điểm", "Tốc độ", "Sức mạnh", "Rê bóng", "Thể lực" };

    /// <summary>The site shows every stat with the default "+1, LV.1" card: base + 5 (playerEnchantList[1] in its script).</summary>
    public const int StatBonus = 5;

    public const string SpriteUrl = "https://fifaaddict.com/assets/img/badged.png?20171201";

    /// <summary>Season id (the "yNN" css class minus 2000) -> x offset of its 18x18 icon in badged.png (from the site's CSS).</summary>
    public static readonly Dictionary<int, int> SeasonSpriteX = new()
    {
        [0] = 418, [6] = 441, [7] = 464, [8] = 487, [9] = 510, [10] = 533, [11] = 556, [13] = 579, [14] = 602, [15] = 625,
        [16] = 648, [17] = 671, [22] = 694, [23] = 717, [24] = 740, [26] = 763 /* not in the site's sprite: same gold star as season 27 */, [27] = 763, [28] = 786, [29] = 809, [30] = 832,
        [31] = 855, [32] = 878, [33] = 855 /* the site shows season 33 cards with the season 31 icon */, [35] = 901, [41] = 924, [44] = 947, [45] = 970, [47] = 993, [48] = 1016, [49] = 1039,
        [51] = 1062, [53] = 1085, [55] = 1108, [57] = 1131, [61] = 1154, [63] = 1177, [65] = 1200, [67] = 1223,
        [69] = 1246, [71] = 1269, [73] = 1292, [77] = 1315, [81] = 1338, [85] = 1361, [88] = 1384, [89] = 1407,
        [91] = 1430, [93] = 1453, [95] = 1476, [97] = 1499,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Seasons the server has in the game (from /static/roster, set after login). null = unknown, nothing is filtered by season.</summary>
    public HashSet<int>? AllowedSeasons { get; set; }
    private bool InGame(int season) => AllowedSeasons == null || AllowedSeasons.Contains(season);

    public PlayerSearch()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public async Task<List<PlayerInfo>> SearchAsync(string name, CancellationToken ct = default)
    {
        var url = "https://en.fifaaddict.com/fo3db.php?q=player&name=" + Uri.EscapeDataString(name);
        var html = await _http.GetStringAsync(url, ct);
        var found = Parse(html).Where(p => p.Season != 0 && p.Spid / 1_000_000 != 0).ToList();   // season 0 (base cards) is never offered
        return await GameFirstAsync(found);
    }

    /// <summary>
    /// The game's own files decide: overall, stats, positions and team come from data_0.big (fifaaddict only gives the name, which the files lack);
    /// cards the files do not have, or only as a placeholder (overall 1), are dropped; and the other seasons of the same player (same last six digits of
    /// the SPID and birth date) are added when fifaaddict does not list them. Without the game files the site's list is returned as it is.
    /// </summary>
    private async Task<List<PlayerInfo>> GameFirstAsync(List<PlayerInfo> found)
    {
        try
        {
            var db = await LocalPlayerDb.GetAsync();
            if (db == null || found.Count == 0) return found.Where(f => InGame(f.Season)).ToList();
            var result = new List<PlayerInfo>();
            var have = new HashSet<long>();
            foreach (var p in found)
            {
                var row = db.Find(p.Spid);
                if (row == null || row.Overall <= 1 || !InGame(p.Season)) continue;
                if (have.Add(p.Spid)) result.Add(p with { Team = db.ClubOf(p.Spid) });   // the site's overall/stats match the game screen; the game files give the team
            }
            foreach (var p in found)
            {
                var row = db.Find(p.Spid);
                if (row == null) continue;
                foreach (var sib in db.Siblings(row))
                {
                    if (sib.Spid / 1_000_000 == 0 || sib.Overall <= 1 || !InGame((int)(sib.Spid / 1_000_000)) || !have.Add(sib.Spid)) continue;
                    result.Add(FromGame(db, sib, p.Name, false, 0, 0));
                }
            }
            return result.OrderBy(r => r.Name).ThenBy(r => r.Season).ToList();
        }
        catch { return found; }
    }

    private static PlayerInfo FromGame(LocalPlayerDb db, LocalPlayerDb.Row row, string name, bool limited, long priceKr, long priceVn)
    {
        var d = db.ToDetail(row);
        var positions = d.Positions.ToList();
        return new PlayerInfo(row.Spid, name, (int)(row.Spid / 1_000_000), positions.FirstOrDefault()?.Pos ?? "", limited, row.Overall + StatBonus,
            new[] { row.Finishing, row.Sprint, row.Strength, row.Dribbling, row.Stamina }.Select(v => v + StatBonus).ToArray(),
            priceKr, priceVn, d.ImageUrl, positions, d.SkillMoves, db.ClubOf(row.Spid), Approx: true);   // overall estimated from the game files (can be several points off)
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<Image?>> _imageCache = new();

    /// <summary>Image with an in-memory cache per url: the same portrait / sprite is downloaded once, concurrent callers share the download
    /// (a failed download is retried next time). Cached images are shared, so callers must not dispose them.</summary>
    public Task<Image?> LoadImageCachedAsync(string url)
    {
        var t = _imageCache.GetOrAdd(url, u => LoadImageAsync(u));
        t.ContinueWith(x => { if (x.IsFaulted || x.IsCanceled || x.Result == null) _imageCache.TryRemove(url, out _); }, TaskScheduler.Default);
        return t;
    }

    /// <summary>Icons the site's badged.png lacks, taken from another sprite and appended to the right of it as extra 18x18 cells.
    /// Season 40 uses the "NHD" icon (14th of the first row, x = 390) of the 30x24 cell grid in this sprite.</summary>
    private const string ExtraSpriteUrl = "https://fifaaddict.com/ffaddv2/img/892f78a.png";

    private async Task<Image> AddExtraIconsAsync(Image sprite, CancellationToken ct)
    {
        var plain = new Bitmap(sprite);
        try
        {
            var bytes = await _http.GetByteArrayAsync(ExtraSpriteUrl, ct);
            using var ms = new MemoryStream(bytes);
            using var extra = Image.FromStream(ms);
            int x = plain.Width;
            var composed = new Bitmap(x + 18, plain.Height);
            using (var g = Graphics.FromImage(composed))
            {
                g.DrawImage(plain, 0, 0, plain.Width, plain.Height);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                // the sheet is a grid of 30x24 cells (18 columns): NHD is the 14th of the first row; keep its proportions inside the 18x18 cell
                using var ia = new System.Drawing.Imaging.ImageAttributes();
                ia.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                g.DrawImage(extra, new Rectangle(x, 2, 18, 14), 13 * 30, 0, 30, 24, GraphicsUnit.Pixel, ia);
            }
            SeasonSpriteX[40] = x;
            plain.Dispose();
            return composed;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return plain; }
    }

    public async Task<Image?> LoadImageAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(url, ct);
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            if (url == SpriteUrl) return await AddExtraIconsAsync(img, ct);
            return new Bitmap(img);   // detach from the stream
        }
        catch { return null; }
    }

    public static List<PlayerInfo> Parse(string html)
    {
        var result = new List<PlayerInfo>();
        foreach (var row in Regex.Split(html, "<tr id=\"player_id").Skip(1))
        {
            var idM = Regex.Match(row, @"^(\d+)""");
            var nameM = Regex.Match(row, @"class=""label_lg"">([^<]*)<");
            if (!idM.Success || !nameM.Success) continue;

            long spid = long.Parse(idM.Groups[1].Value);
            var stats = new Dictionary<int, int>();
            foreach (Match m in Regex.Matches(row, @"stat_cell(\d) stat_value"" rel=""(\d+)"""))
                stats[int.Parse(m.Groups[1].Value)] = int.Parse(m.Groups[2].Value);

            var img = Regex.Match(row, @"class=""player_img[^""]*"" src=""([^""]+)""").Groups[1].Value;
            if (img.StartsWith("//")) img = "https:" + img;

            var seasonM = Regex.Match(row, @"badged y(\d+)");
            var positions = Regex.Matches(row,
                    @"<span class=""player_position_list([^""]*)"" rel=""(\w+)""><i class=""badge_position badge_position_(\w+)"">\w+</i><b class=""stat_value"" rel=""(\d+)""")
                .Select(m => new PosRating(m.Groups[2].Value.ToUpperInvariant(), m.Groups[3].Value,
                    int.Parse(m.Groups[4].Value) + StatBonus, m.Groups[1].Value.Contains("active")))
                .ToList();
            var skill = Regex.Match(row, @"player_skillmoves"">(.*?)</div>", RegexOptions.Singleline);
            int stars = skill.Success ? Regex.Matches(skill.Groups[1].Value, "fa-star").Count : 0;

            result.Add(new PlayerInfo(
                spid,
                WebUtility.HtmlDecode(nameM.Groups[1].Value.Trim()),
                seasonM.Success ? int.Parse(seasonM.Groups[1].Value) - 2000 : (int)(spid / 1_000_000),
                Regex.Match(row, @"badge_position_recomment"">(\w+)<").Groups[1].Value.ToUpperInvariant(),
                row.Contains("title=\"Limited Player\""),
                stats.GetValueOrDefault(2) + StatBonus,
                Enumerable.Range(3, 5).Select(i => stats.GetValueOrDefault(i) + StatBonus).ToArray(),
                Price(row, "kr"), Price(row, "vn"), img, positions, stars));
        }
        return result;
    }

    public static PlayerDetail? ParseDetail(long spid, string html)
    {
        int a = html.IndexOf("f3player_topinfo", StringComparison.Ordinal);
        if (a < 0) return null;
        int b = html.IndexOf("player_position_recomment", a, StringComparison.Ordinal);
        var blk = b > a ? html[a..b] : html[a..];

        string G(string pattern, int group = 1) =>
            WebUtility.HtmlDecode(Regex.Match(blk, pattern, RegexOptions.Singleline).Groups[group].Value.Trim());

        var nameM = Regex.Match(blk, @"player_name"">\s*<a[^>]*>\s*<span class=""badged y(\d+)""></span>([^<]+)</a>", RegexOptions.Singleline);
        if (!nameM.Success) return null;

        // "WORLD" + "BEST" are split over two tags; the text/xs duplicate must not be repeated.
        var parts = Regex.Matches(Regex.Match(blk, @"label_season"">(.*?)</a>", RegexOptions.Singleline).Groups[1].Value, @"<b[^>]*>([^<]*)</b>")
            .Select(m => m.Groups[1].Value).ToList();
        var label = parts.Count >= 3 ? $"{parts[0]} {parts[^1]}" : string.Join(" ", parts.Distinct());

        int left = 0, right = 0; bool prefersRight = true;
        foreach (Match m in Regex.Matches(blk, @"class=""stat_foot_bg (leftfoot|rightfoot)( footok)?"">\s*(\d)"))
        {
            int v = int.Parse(m.Groups[3].Value);
            if (m.Groups[1].Value == "leftfoot") { left = v; if (m.Groups[2].Success) prefersRight = false; }
            else { right = v; if (m.Groups[2].Success) prefersRight = true; }
        }

        var positions = Regex.Matches(blk,
                @"<span class=""player_position_list( player_position_active)?"" rel=""(\w+)""><i class=""badge_position badge_position_(\w+)"">\w+</i><b class=""\s*stat_value"" rel=""(\d+)""")
            .Select(m => new PosRating(m.Groups[2].Value.ToUpperInvariant(), m.Groups[3].Value, int.Parse(m.Groups[4].Value) + StatBonus, m.Groups[1].Success))
            .ToList();

        var sk = Regex.Match(blk, @"player_skillmoves_wrapper"">(.*?)clearfix", RegexOptions.Singleline);
        var body = Regex.Matches(Regex.Match(blk, @"Body Type</span>(.*?)</div>", RegexOptions.Singleline).Groups[1].Value, @"<b>([^<]*)</b>")
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value));

        static string Abs(string u) => u.StartsWith("//") ? "https:" + u : u.Length == 0 || u.StartsWith("http") ? u : "https://en.fifaaddict.com/" + u.TrimStart('/');

        return new PlayerDetail(
            spid, WebUtility.HtmlDecode(nameM.Groups[2].Value.Trim()), int.Parse(nameM.Groups[1].Value) - 2000, label,
            G(@"<div class=""player_nation"">.*?<b>([^<]*)</b>"), G(@"<div class=""player_nation"">.*?<i class=""flag (\w+)"""),
            G(@"player_club"">.*?<b>([^<]*)</b>"), Abs(G(@"<img src=""([^""]*)""\s+class=""club_crest""")),
            G(@"Date of Birth</span>\s*<b>([^<]*)</b>"), G(@"Height</span>\s*<b>(\d+)</b>"), G(@"Weight</span>.*?<b>(\d+)</b>"),
            string.Join(", ", body), left, right, prefersRight, positions, sk.Success ? Regex.Matches(sk.Groups[1].Value, "fa-star").Count : 0,
            Abs(G(@"class=""player_img[^""]*"" src=""([^""]+)""")));
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Task<PlayerDetail?>> _detailCache = new();

    /// <summary>Detail page with an in-memory cache per SPID (a failed lookup is retried next time).</summary>
    public Task<PlayerDetail?> GetDetailCachedAsync(long spid)
    {
        var t = _detailCache.GetOrAdd(spid, id => GetDetailAsync(id));
        if (t.IsFaulted || t.IsCanceled) { _detailCache.TryRemove(spid, out _); return GetDetailCachedAsync(spid); }
        t.ContinueWith(x => { if (x.IsFaulted || x.IsCanceled || x.Result == null) _detailCache.TryRemove(spid, out _); }, TaskScheduler.Default);
        return t;
    }

    public async Task<PlayerDetail?> GetDetailAsync(long spid, CancellationToken ct = default)
    {
        // the site's numbers are the ones the game screen shows (the game files' overall column is not); the files are the fallback for cards the site lacks
        var d = await FetchSiteAsync(spid, ct);
        if (d != null)
        {
            await LearnAsync(d);
            var gl = SeasonLabelOf(d.Season);   // season name as the game words it
            return gl.Length > 0 ? d with { SeasonLabel = gl } : d;
        }
        return await LocalDetailAsync(spid, ct);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Task<PlayerDetail?>> _siteCache = new();

    /// <summary>The fifaaddict page only, cached (null when the site does not have the card). Its active-position rating is the card's overall
    /// as the site (and the player list) shows it; the game's own overallrating field is not the same number.</summary>
    public Task<PlayerDetail?> GetSiteDetailCachedAsync(long spid)
    {
        var t = _siteCache.GetOrAdd(spid, async id => { var d = await FetchSiteAsync(id, CancellationToken.None); if (d != null) await LearnAsync(d); return d; });
        t.ContinueWith(x => { if (x.IsFaulted || x.IsCanceled) _siteCache.TryRemove(spid, out _); }, TaskScheduler.Default);
        return t;
    }

    /// <summary>The fifaaddict page only (null when the site does not have the card or is unreachable).</summary>
    private async Task<PlayerDetail?> FetchSiteAsync(long spid, CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync($"https://en.fifaaddict.com/fo3player.php?id={spid}", ct);
            return ParseDetail(spid, html);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    // What the game files lack (nation names, season labels) is learned from every card fifaaddict does give us and kept between runs.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string[]> _nations = new();   // local nationality id -> [name, flag code]
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _seasonLabels = new();
    private static string LearnedPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FO3SessionTool", "learned.json");
    private sealed class Learned { public Dictionary<int, string[]> Nations { get; set; } = new(); public Dictionary<int, string> Seasons { get; set; } = new(); }
    private bool _learnedLoaded;
    // Season names as the game itself shows them: data\ui\external\LANGUAGESTRING_VIE.json (TRANSFERMARKET_SEASON_n, else CHATTING_SEASON_HEADER_n).
    private Dictionary<int, string>? _gameSeasons;
    private Dictionary<int, string> GameSeasons()
    {
        if (_gameSeasons != null) return _gameSeasons;
        var result = new Dictionary<int, string>();
        try
        {
            var data = LocalPlayerDb.FindDataFile(LocalPlayerDb.ConfiguredDir);   // ...\data\data_0.big
            var file = data == null ? null : Path.Combine(Path.GetDirectoryName(data)!, "ui", "external", "LANGUAGESTRING_VIE.json");
            if (file != null && File.Exists(file))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                var full = new Dictionary<int, string>(); var head = new Dictionary<int, string>();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                    var m = Regex.Match(p.Name, @"^(TRANSFERMARKET_SEASON|CHATTING_SEASON_HEADER)_(\d+)$");
                    if (!m.Success) continue;
                    int id = int.Parse(m.Groups[2].Value);
                    var v = (p.Value.GetString() ?? "").Trim();
                    if (v.Length <= 1 || v.StartsWith("Chọn")) continue;   // placeholders ("." etc.)
                    (m.Groups[1].Value == "TRANSFERMARKET_SEASON" ? full : head)[id] = v;
                }
                foreach (var kv in head) result[kv.Key] = kv.Value;
                foreach (var kv in full) result[kv.Key] = kv.Value;   // the full name wins over the short header
            }
        }
        catch { }
        return _gameSeasons = result;
    }

    public string SeasonLabelOf(int season)
    {
        if (GameSeasons().TryGetValue(season, out var g)) return g;   // the game decides
        LoadLearned();
        if (_seasonLabels.TryGetValue(season, out var l)) return l;
        return season == 33 && _seasonLabels.TryGetValue(31, out var l31) ? l31 : "";   // the site shows season 33 as season 31 (EUROLEAGUE LEGEND)
    }
    private readonly object _learnLock = new();

    private void LoadLearned()
    {
        lock (_learnLock)
        {
            if (_learnedLoaded) return;
            _learnedLoaded = true;
            try
            {
                var l = System.Text.Json.JsonSerializer.Deserialize<Learned>(File.ReadAllText(LearnedPath));
                if (l != null) { foreach (var kv in l.Nations) _nations[kv.Key] = kv.Value; foreach (var kv in l.Seasons) _seasonLabels[kv.Key] = kv.Value; }
            }
            catch { }
        }
    }

    private async Task LearnAsync(PlayerDetail d)
    {
        LoadLearned();
        var db = await LocalPlayerDb.GetAsync();
        var row = db?.Find(d.Spid);
        bool changed = false;
        if (row != null && d.Nation.Length > 0 && d.NationCode.Length > 0 && !_nations.ContainsKey(row.Nation)) { _nations[row.Nation] = new[] { d.Nation, d.NationCode }; changed = true; }
        if (d.SeasonLabel.Length > 0 && !_seasonLabels.ContainsKey(d.Season)) { _seasonLabels[d.Season] = d.SeasonLabel; changed = true; }
        if (!changed) return;
        lock (_learnLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LearnedPath)!);
                File.WriteAllText(LearnedPath, System.Text.Json.JsonSerializer.Serialize(new Learned { Nations = _nations.ToDictionary(k => k.Key, k => k.Value), Seasons = _seasonLabels.ToDictionary(k => k.Key, k => k.Value) }));
            }
            catch { }
        }
    }

    /// <summary>
    /// For a card fifaaddict does not have: the stats come from the game's own files, and what those lack is filled in from fifaaddict: the name (and the
    /// nation) from another season card of the same player, the nation name and season label from what was learned on other cards. The club stays empty.
    /// </summary>
    public async Task<PlayerDetail?> LocalDetailAsync(long spid, CancellationToken ct = default)
    {
        var db = await LocalPlayerDb.GetAsync();
        var row = db?.Find(spid);
        if (db == null || row == null) return null;
        LoadLearned();
        var d = db.ToDetail(row, "", db.ClubOf(spid));
        var gl = SeasonLabelOf(d.Season);
        if (gl.Length > 0) d = d with { SeasonLabel = gl };
        if (_nations.TryGetValue(row.Nation, out var nat)) d = d with { Nation = nat[0], NationCode = nat[1] };

        var sibs = db.Siblings(row).Take(4).ToList();
        if (sibs.Count > 0)
        {
            var found = await Task.WhenAll(sibs.Select(s => FetchSiteAsync(s.Spid, ct)));
            for (int i = 0; i < sibs.Count; i++)
            {
                var s = found[i];
                if (s == null || s.Name.Length == 0) continue;
                await LearnAsync(s);
                d = d with { Name = s.Name };
                if (sibs[i].Nation == row.Nation && s.Nation.Length > 0) d = d with { Nation = s.Nation, NationCode = s.NationCode };
                break;
            }
        }
        return d;
    }

    private static long Price(string row, string region)
    {
        var m = Regex.Match(row, "stat_cell_price_" + region + @"""> *(\d+)");
        return m.Success ? long.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>185600000 -> "185.6M" (same short style as the website).</summary>
    public static string FormatPrice(long v)
    {
        if (v <= 0) return "—";
        static string T(double d, string s) => d.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + s;
        return v >= 1_000_000_000 ? T(v / 1e9, "B") : v >= 1_000_000 ? T(v / 1e6, "M") : v >= 1_000 ? T(v / 1e3, "K") : v.ToString();
    }

    public void Dispose() => _http.Dispose();
}
