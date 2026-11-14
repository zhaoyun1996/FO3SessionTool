using System.Text;

namespace FO3SessionTool;

/// <summary>
/// Player data read straight from the game's own files: the table "players" of the t3db database "fifa_ng_db" stored (RefPack
/// compressed) inside data\data_0.big. It has every card of the client (all seasons) keyed by SPID: overall, positions, feet, skill
/// moves, height, weight, birth date. It has NO names, nation names or clubs (names live in the locale files). Used only as a fallback
/// when fifaaddict does not know a card. Read-only: the game files are never written.
/// </summary>
public sealed class LocalPlayerDb
{
    public sealed record Row(long Spid, int Overall, int Potential, int[] Positions, int Foot, int WeakFoot, int SkillMoves, int Height, int Weight, int BirthDays, int Nation,
        int Finishing = 0, int Sprint = 0, int Strength = 0, int Dribbling = 0, int Stamina = 0);

    private readonly Dictionary<long, Row> _rows;
    private readonly Dictionary<long, string> _club;   // spid -> club (or special team such as "Europe Legend") name
    private LocalPlayerDb(Dictionary<long, Row> rows, Dictionary<long, string> club) { _rows = rows; _club = club; }

    /// <summary>The club the game files give the card (many cards, mostly newer seasons, have none), or "".</summary>
    public string ClubOf(long spid) => _club.TryGetValue(spid, out var c) ? c : "";
    public int Count => _rows.Count;
    public IEnumerable<Row> All => _rows.Values;
    public Row? Find(long spid) => _rows.TryGetValue(spid, out var r) ? r : null;

    private Dictionary<long, List<Row>>? _byBase;

    /// <summary>The other season cards of the same player: same last six digits of the SPID and the same birth date (a reused id of another person is skipped).
    /// The base card (season 0) first, then the nearest seasons.</summary>
    public List<Row> Siblings(Row r)
    {
        _byBase ??= _rows.Values.GroupBy(x => x.Spid % 1_000_000).ToDictionary(g => g.Key, g => g.ToList());
        if (!_byBase.TryGetValue(r.Spid % 1_000_000, out var l)) return new();
        long season = r.Spid / 1_000_000;
        return l.Where(x => x.Spid != r.Spid && x.BirthDays == r.BirthDays)
                .OrderBy(x => x.Spid / 1_000_000 == 0 ? -1 : Math.Abs(x.Spid / 1_000_000 - season)).ToList();
    }

    // ---- loading (lazy, once, off the UI thread) -------------------------------------------------------------------------------
    private static Task<LocalPlayerDb?>? _task;
    private static string? _configuredDir;

    /// <summary>Shared instance; null when the game files cannot be found or read. Never throws.</summary>
    public static Task<LocalPlayerDb?> GetAsync(string? gameDir = null)
    {
        if (gameDir != null) _configuredDir = gameDir;
        return _task ??= Task.Run(() =>
        {
            try { var p = FindDataFile(_configuredDir); return p == null ? null : Load(p); }
            catch { return null; }
        });
    }

    /// <summary>data\data_0.big of the game: the configured folder, or the tool's folder and its parents (the tool usually lives in the game folder).</summary>
    public static string? FindDataFile(string? configured)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) dirs.Add(configured);
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5 && d != null; i++, d = d.Parent) dirs.Add(d.FullName);
        foreach (var dir in dirs)
        {
            var p = Path.Combine(dir, "data", "data_0.big");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static LocalPlayerDb? Load(string bigFile)
    {
        var b = File.ReadAllBytes(bigFile);
        var db = FindDatabase(b);
        return db == null ? null : Parse(db);
    }

    // ---- RefPack (EA "QFS", header 10 FB) --------------------------------------------------------------------------------------
    /// <summary>The database is the RefPack stream that decodes to "DB\0\x08...". Only streams whose first literal run starts with "DB" are decoded.</summary>
    private static byte[]? FindDatabase(byte[] b)
    {
        for (int o = 0; o + 12 < b.Length; o++)
        {
            if (b[o] != 0x10 || b[o + 1] != 0xFB) continue;
            long size = (b[o + 2] << 16) | (b[o + 3] << 8) | b[o + 4];
            if (size < 1_000_000 || size > 200_000_000) continue;
            if (b[o + 5] < 0xE0 || b[o + 5] > 0xFB || b[o + 6] != (byte)'D' || b[o + 7] != (byte)'B') continue;
            var data = RefPack(b, o);
            if (data != null && data.Length > 24 && data[0] == 'D' && data[1] == 'B') return data;
        }
        return null;
    }

    private static byte[]? RefPack(byte[] b, int o)
    {
        try
        {
            int flags = b[o], p = o + 2;
            if ((flags & 0x01) != 0) p += (flags & 0x80) != 0 ? 4 : 3;   // compressed size present
            int usize;
            if ((flags & 0x80) != 0) { usize = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]; p += 4; }
            else { usize = (b[p] << 16) | (b[p + 1] << 8) | b[p + 2]; p += 3; }
            var outp = new byte[usize];
            int n = 0;
            while (n < usize)
            {
                int c = b[p++], lit, len, off;
                if (c < 0x80)
                {
                    int c1 = b[p++]; lit = c & 3; len = ((c & 0x1C) >> 2) + 3; off = ((c & 0x60) << 3) + c1 + 1;
                    Buffer.BlockCopy(b, p, outp, n, lit); p += lit; n += lit;
                }
                else if (c < 0xC0)
                {
                    int c1 = b[p++], c2 = b[p++]; lit = c1 >> 6; len = (c & 0x3F) + 4; off = ((c1 & 0x3F) << 8) + c2 + 1;
                    Buffer.BlockCopy(b, p, outp, n, lit); p += lit; n += lit;
                }
                else if (c < 0xE0)
                {
                    int c1 = b[p++], c2 = b[p++], c3 = b[p++]; lit = c & 3; len = ((c & 0x0C) << 6) + c3 + 5; off = ((c & 0x10) << 12) + (c1 << 8) + c2 + 1;
                    Buffer.BlockCopy(b, p, outp, n, lit); p += lit; n += lit;
                }
                else if (c < 0xFC)
                {
                    lit = ((c & 0x1F) << 2) + 4;
                    Buffer.BlockCopy(b, p, outp, n, lit); p += lit; n += lit;
                    continue;
                }
                else
                {
                    lit = c & 3;
                    Buffer.BlockCopy(b, p, outp, n, lit); p += lit; n += lit;
                    break;
                }
                if (off > n) return null;
                for (int i = 0; i < len && n < usize; i++, n++) outp[n] = outp[n - off];
            }
            return n == usize ? outp : null;
        }
        catch { return null; }
    }

    // ---- t3db (PC version 8; layout verified against the open-source fifa-t3db notes and the real file) -----------------------
    // Field short names of the "players" table (from the database's own meta xml): name -> (short name, rangelow).
    private static readonly (string Short, int Low)[] F =
    {
        ("voVf", 0),            // 0 spid
        ("UERs", 1),            // 1 overallrating
        ("mpuH", 1),            // 2 potential
        ("wZQU", 0), ("NgVS", -1), ("OblE", -1), ("YnYz", -1),   // 3..6 preferredposition1..4 (-1 = none)
        ("MDvm", 1),            // 7 preferredfoot (1 right, 2 left)
        ("aOBn", 1),            // 8 weakfootabilitytypecode
        ("BAPc", 0),            // 9 skillmoves (0..4 -> 1..5 stars)
        ("ypBQ", 150),          // 10 height
        ("sNZW", 50),           // 11 weight
        ("WVIU", int.MinValue), // 12 birthdate (days since 1582-10-14)
        ("enmm", 0),            // 13 nationality id
        ("xJZL", 1), ("NrcP", 1), ("nmgT", 1), ("nEbM", 1), ("XjDq", 1),   // 14..18 finishing, sprintspeed, strength, dribbling, stamina
    };

    private static uint U32(byte[] d, int o) => BitConverter.ToUInt32(d, o);

    private static LocalPlayerDb? Parse(byte[] d)
    {
        int tables = (int)U32(d, 16);
        int baseOff = 24 + 8 * tables + 4;
        int tableOff = -1;
        for (int i = 0; i < tables; i++)
            if (Encoding.ASCII.GetString(d, 24 + 8 * i, 4) == "CZUM") { tableOff = baseOff + (int)U32(d, 28 + 8 * i); break; }
        if (tableOff < 0) return null;

        int recSize = (int)U32(d, tableOff + 4);
        int count = BitConverter.ToUInt16(d, tableOff + 16);
        int fields = d[tableOff + 24];
        var desc = new Dictionary<string, (int Type, int Bit, int Depth)>();
        for (int i = 0; i < fields; i++)
        {
            int q = tableOff + 36 + 16 * i;
            desc[Encoding.ASCII.GetString(d, q + 8, 4)] = ((int)U32(d, q), (int)U32(d, q + 4), (int)U32(d, q + 12));
        }
        if (recSize <= 0 || recSize > 512) return null;
        foreach (var f in F) if (!desc.TryGetValue(f.Short, out var x) || x.Type != 3) return null;   // layout differs from what was verified: do not guess

        int recs = tableOff + 36 + 16 * fields;
        if (recs + (long)count * recSize > d.Length) return null;

        long Get(int rec, int idx)
        {
            var (_, bit, depth) = desc[F[idx].Short];
            int start = recs + rec * recSize;
            ulong v = 0;
            int firstByte = bit / 8, shift = bit % 8, bytes = (shift + depth + 7) / 8;
            for (int k = 0; k < bytes; k++) v |= (ulong)d[start + firstByte + k] << (8 * k);
            v >>= shift;
            if (depth < 64) v &= (1UL << depth) - 1;
            return (long)v + F[idx].Low;
        }

        var rows = new Dictionary<long, Row>(count);
        for (int r = 0; r < count; r++)
        {
            if ((d[recs + (r + 1) * recSize - 1] & 0x80) != 0) continue;   // cancelled record
            long spid = Get(r, 0);
            if (spid <= 0) continue;
            rows[spid] = new Row(spid, (int)Get(r, 1), (int)Get(r, 2),
                new[] { (int)Get(r, 3), (int)Get(r, 4), (int)Get(r, 5), (int)Get(r, 6) }.Where(p => p >= 0).ToArray(),
                (int)Get(r, 7), (int)Get(r, 8), (int)Get(r, 9), (int)Get(r, 10), (int)Get(r, 11), (int)Get(r, 12), (int)Get(r, 13),
                (int)Get(r, 14), (int)Get(r, 15), (int)Get(r, 16), (int)Get(r, 17), (int)Get(r, 18));
        }
        return rows.Count > 1000 ? new LocalPlayerDb(rows, ReadClubs(d, tables, baseOff)) : null;
    }

    /// <summary>teamplayerlinks (spid -> team) + teams (team -> name); national teams (listed in the nations table) are skipped. Best effort: empty on any surprise.</summary>
    private static Dictionary<long, string> ReadClubs(byte[] d, int tables, int baseOff)
    {
        var result = new Dictionary<long, string>();
        try
        {
            bool Open(string shortName, out int recs, out int recSize, out int count, out Dictionary<string, (int Type, int Bit, int Depth)> desc)
            {
                recs = recSize = count = 0; desc = new();
                int off = -1;
                for (int i = 0; i < tables; i++)
                    if (Encoding.ASCII.GetString(d, 24 + 8 * i, 4) == shortName) { off = baseOff + (int)U32(d, 28 + 8 * i); break; }
                if (off < 0) return false;
                recSize = (int)U32(d, off + 4); count = BitConverter.ToUInt16(d, off + 16);
                int fields = d[off + 24];
                for (int i = 0; i < fields; i++)
                {
                    int q = off + 36 + 16 * i;
                    desc[Encoding.ASCII.GetString(d, q + 8, 4)] = ((int)U32(d, q), (int)U32(d, q + 4), (int)U32(d, q + 12));
                }
                recs = off + 36 + 16 * fields;
                return recSize > 0 && recs + (long)count * recSize <= d.Length;
            }
            long Int(int recs, int recSize, int r, (int Type, int Bit, int Depth) f)
            {
                int start = recs + r * recSize, fb = f.Bit / 8, sh = f.Bit % 8, n = (sh + f.Depth + 7) / 8;
                ulong v = 0;
                for (int k = 0; k < n; k++) v |= (ulong)d[start + fb + k] << (8 * k);
                return (long)((v >> sh) & ((1UL << f.Depth) - 1));
            }
            // field short names (from the database's own meta xml): teamid mCXg, teamname AUsv, spid voVf
            var national = new HashSet<long>();
            if (Open("Crbb", out var nr, out var ns, out var nc, out var nd) && nd.TryGetValue("mCXg", out var nf))
                for (int r = 0; r < nc; r++) national.Add(Int(nr, ns, r, nf));
            var names = new Dictionary<long, string>();
            if (Open("lyxL", out var tr, out var ts, out var tc, out var td) && td.TryGetValue("mCXg", out var tid) && td.TryGetValue("AUsv", out var tname) && tname.Type == 0)
                for (int r = 0; r < tc; r++)
                {
                    int s0 = tr + r * ts + tname.Bit / 8, len = tname.Depth / 8;
                    int z = Array.IndexOf(d, (byte)0, s0, len); if (z < 0) z = s0 + len;
                    names[Int(tr, ts, r, tid)] = Encoding.UTF8.GetString(d, s0, z - s0);
                }
            if (Open("RrqT", out var lr, out var ls, out var lc, out var ld) && ld.TryGetValue("mCXg", out var lteam) && ld.TryGetValue("voVf", out var lspid))
                for (int r = 0; r < lc; r++)
                {
                    long team = Int(lr, ls, r, lteam), spid = Int(lr, ls, r, lspid);
                    if (!national.Contains(team) && names.TryGetValue(team, out var nm) && nm.Length > 0 && !result.ContainsKey(spid)) result[spid] = nm;
                }
        }
        catch { }
        return result;
    }

    // ---- to the tool's own PlayerDetail ----------------------------------------------------------------------------------------
    public static readonly string[] PosNames =
    {
        "GK", "SW", "RWB", "RB", "RCB", "CB", "LCB", "LB", "LWB", "RDM", "CDM", "LDM", "RM", "RCM", "CM", "LCM", "LM", "RAM", "CAM", "LAM",
        "RF", "CF", "LF", "RW", "RS", "ST", "LS", "LW",
    };

    public static string PosGroup(int id) => id == 0 ? "gk" : id <= 8 ? "def" : id <= 19 ? "mid" : "att";

    /// <summary>Same shape the fifaaddict page gives. Missing in the game files: name (use the card's jersey name), nation, club.</summary>
    public PlayerDetail ToDetail(Row r, string name = "", string club = "")
    {
        var birth = new DateTime(1582, 10, 14).AddDays(r.BirthDays);
        bool right = r.Foot != 2;
        var pos = r.Positions.Where(p => p < PosNames.Length)
            .Select((p, i) => new PosRating(PosNames[p], PosGroup(p), r.Overall + PlayerSearch.StatBonus, i == 0)).ToList();
        return new PlayerDetail(
            r.Spid, name, (int)(r.Spid / 1_000_000), $"Mùa {r.Spid / 1_000_000}", "", "", club, "",
            birth.ToString("dd/MM/yyyy"), r.Height.ToString(), r.Weight.ToString(), "",
            right ? r.WeakFoot : 5, right ? 5 : r.WeakFoot, right, pos, r.SkillMoves + 1,
            $"http://cdn.3sea.online/cdn/externalAssets/players/p{r.Spid}.png");
    }
}
