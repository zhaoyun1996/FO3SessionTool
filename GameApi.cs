using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace FO3SessionTool;

public sealed record Listing(long Spid, int Grade, long Price, int Count);

/// <summary>REST client for the in-game API on :18090 (same calls fifazf.exe makes).</summary>
public sealed class GameApi
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly CookieContainer _cookies = new();

    public GameApi(Config cfg)
    {
        _base = cfg.GameBase.TrimEnd('/');
        var handler = new HttpClientHandler { CookieContainer = _cookies, UseCookies = true };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    /// <summary>Exchange the launcher token for the game session cookies (connect.sid + fo3sid).</summary>
    public async Task SignAsync(string launcherToken, CancellationToken ct = default)
    {
        var body = new JsonObject { ["token"] = launcherToken }.ToJsonString();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{_base}/publisher/garena/sign", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"sign failed: HTTP {(int)resp.StatusCode} {Trim(text)}");
        // Session cookies are kept by the handler's CookieContainer.
    }

    /// <summary>Character name from /coach/me (shown in the account bar).</summary>
    public async Task<string> GetCoachNameAsync(CancellationToken ct = default)
    {
        var text = await GetAsync("/coach/me", ct);
        ApplyMoney(text);
        return JsonNode.Parse(text)?["name"]?.GetValue<string>() ?? "";
    }

    /// <summary>Keep-alive the game itself sends every ~10 minutes.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(_base + "/sign/ping", ct);
        return resp.IsSuccessStatusCode;
    }

    public List<SavedCookie> ExportCookies() =>
        _cookies.GetCookies(new Uri(_base)).Cast<Cookie>()
            .Select(c => new SavedCookie(c.Name, c.Value, c.Path, c.Domain)).ToList();

    public void ImportCookies(IEnumerable<SavedCookie> cookies)
    {
        var uri = new Uri(_base);
        foreach (var c in cookies)
        {
            var ck = new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path);
            // Domain is left unset: the container binds the cookie to the game host; an IP "Domain" attribute is rejected as invalid.
            _cookies.Add(uri, ck);
        }
    }

    /// <summary>True when the server still accepts our cookies. /account answers NOT_SIGNED otherwise
    /// (unlike /transfer/search, which works without a session and so proves nothing).</summary>
    public async Task<(bool Valid, string Detail)> CheckSessionAsync(CancellationToken ct = default)
    {
        var text = await GetAsync("/account", ct);
        var node = JsonNode.Parse(text);
        var reason = node?["reason"]?.GetValue<string>();
        if (reason == "NOT_SIGNED") return (false, "NOT_SIGNED");
        if (node?["result"]?.GetValue<bool>() == false) return (false, reason ?? "result=false");
        return (true, "");
    }

    public async Task<List<Listing>> SearchAsync(long spid, CancellationToken ct = default)
    {
        var text = await GetAsync($"/transfer/search/{spid}", ct);
        var arr = JsonNode.Parse(text) as JsonArray ?? new JsonArray();
        return arr.Select(n => new Listing(
            n!["spid"]!.GetValue<long>(),
            n["grade"]?.GetValue<int>() ?? 1,
            n["price"]!.GetValue<long>(),
            n["count"]?.GetValue<int>() ?? 0)).ToList();
    }

    /// <summary>POST /transfer/purchase/{spid}/{grade}/{price}/purchase - price must equal the current listing price.</summary>
    public async Task<(bool Ok, string Raw)> PurchaseAsync(long spid, int grade, long price, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"{_base}/transfer/purchase/{spid}/{grade}/{price}/purchase", null, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        bool ok = false;
        try { ok = JsonNode.Parse(text)?["result"]?.GetValue<bool>() == true; } catch { }
        return (ok && resp.IsSuccessStatusCode, text);
    }

    // ---- trade (cards -> one player + fresh EP), endpoints observed in the game capture ----------

    /// <summary>One offer of a trade: a player plus cash. Value = Ep + the player's price.</summary>
    public sealed record TradeOption(int Index, long Spid, int Grade, long Price, long Ep)
    {
        public long Value => Ep + Price;
    }
    public sealed record TradeProposal(bool Ok, List<TradeOption> Options, string Raw);

    /// <summary>POST /players/trade2/proposal {"players":[ids]}: asks the server for the offers (3 in the capture). Nothing is traded yet.</summary>
    public async Task<TradeProposal> TradeProposalAsync(IEnumerable<string> cardIds, CancellationToken ct = default)
    {
        var arr = new JsonArray();
        foreach (var id in cardIds) arr.Add(id);
        using var content = new StringContent(new JsonObject { ["players"] = arr }.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{_base}/players/trade2/proposal", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var list = new List<TradeOption>();
        bool ok = false;
        try
        {
            var n = JsonNode.Parse(text);
            ok = n?["result"]?.GetValue<bool>() == true;
            if (n?["props"] is JsonArray props)
            {
                int i = 0;
                foreach (var p in props)
                    list.Add(new TradeOption(i++, p?["spid"]?.GetValue<long>() ?? 0, p?["grade"]?.GetValue<int>() ?? 0,
                        p?["price"]?.GetValue<long>() ?? 0, p?["ep"]?.GetValue<long>() ?? 0));
            }
        }
        catch { }
        return new TradeProposal(ok && resp.IsSuccessStatusCode && list.Count > 0, list,
            resp.IsSuccessStatusCode ? text : $"HTTP {(int)resp.StatusCode}: {text}");
    }

    /// <summary>POST /trade3/contract {"selectnum","slotnum"}: commits the chosen offer. IRREVERSIBLE: the cards are consumed.</summary>
    public async Task<(bool Ok, string Raw)> TradeContractAsync(int selectNum, int slotNum = 0, CancellationToken ct = default)
    {
        using var content = new StringContent(new JsonObject { ["selectnum"] = selectNum, ["slotnum"] = slotNum }.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{_base}/trade3/contract", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        bool ok = false;
        try { ok = JsonNode.Parse(text)?["result"]?.GetValue<bool>() == true; } catch { }
        if (ok) ApplyMoney(text);
        return (ok && resp.IsSuccessStatusCode, resp.IsSuccessStatusCode ? text : $"HTTP {(int)resp.StatusCode}: {text}");
    }

    // ---- sell on the transfer market (endpoints observed in the game capture, 2026-11-12) ----------

    /// <summary>Allowed price window for listing a card: Upper is the highest price the game accepts. Fee is the listing fee.</summary>
    public sealed record SellPrice(long Default, long Lower, long Upper, long Fee);

    /// <summary>GET /transfer/price/{spid}/{grade}.</summary>
    public async Task<SellPrice?> GetSellPriceAsync(long spid, int grade, CancellationToken ct = default)
    {
        var text = await GetAsync($"/transfer/price/{spid}/{grade}", ct);
        try
        {
            var n = JsonNode.Parse(text);
            long upper = n?["upper"]?.GetValue<long>() ?? 0;
            if (upper <= 0) return null;
            return new SellPrice(n?["default"]?.GetValue<long>() ?? 0, n?["lower"]?.GetValue<long>() ?? 0, upper, n?["registrationFee"]?.GetValue<long>() ?? 0);
        }
        catch { return null; }
    }

    /// <summary>A listing of ours on the market. Status is "waiting" while unsold; CompleteFee is what the game keeps once it sells.</summary>
    public sealed record MarketOffer(string OfferId, string CardId, long Spid, int Grade, long Price, string Status, long CompleteFee);

    /// <summary>POST /transfer/offer {"price","playerobjectid"}: lists the card. The card leaves our hands until it sells or expires.</summary>
    public async Task<(bool Ok, MarketOffer? Offer, string Raw)> SellAsync(string cardId, long price, CancellationToken ct = default)
    {
        using var content = new StringContent(new JsonObject { ["price"] = price, ["playerobjectid"] = cardId }.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{_base}/transfer/offer", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        bool ok = false; MarketOffer? mine = null;
        try
        {
            var n = JsonNode.Parse(text);
            ok = n?["result"]?.GetValue<bool>() == true;
            if (ok) { ApplyMoney(text); mine = ParseOffers(text).FirstOrDefault(o => o.CardId == cardId); }
        }
        catch { }
        return (ok && resp.IsSuccessStatusCode, mine, resp.IsSuccessStatusCode ? text : $"HTTP {(int)resp.StatusCode}: {text}");
    }

    /// <summary>GET /transfer/my/offer/list.</summary>
    public async Task<List<MarketOffer>> GetOffersAsync(CancellationToken ct = default)
        => ParseOffers(await GetAsync("/transfer/my/offer/list", ct));

    /// <summary>Offers from either the list answer or the "resources" of an offer answer.</summary>
    public static List<MarketOffer> ParseOffers(string text)
    {
        var list = new List<MarketOffer>();
        var root = JsonNode.Parse(text);
        JsonNode? offers = root?["offers"];
        if (offers == null)
            foreach (var r in root?["resources"]?.AsArray() ?? new JsonArray())
                if (r?["uri"]?.GetValue<string>() == "/transfer/my/offer/list") { offers = r["body"]?["offers"]; break; }
        foreach (var o in offers?.AsArray() ?? new JsonArray())
        {
            var offerId = o?["_id"]?.GetValue<string>();
            if (offerId == null) continue;
            list.Add(new MarketOffer(offerId, o!["playerobjectid"]?.GetValue<string>() ?? o["id"]?.GetValue<string>() ?? "",
                o["spid"]?.GetValue<long>() ?? 0, o["grade"]?.GetValue<int>() ?? 0, o["price"]?.GetValue<long>() ?? 0,
                o["status"]?.GetValue<string>() ?? "", o["completefee"]?["base"]?.GetValue<long>() ?? 0));
        }
        return list;
    }

    /// <summary>POST /transfer/my/offer/completed/{offerId} {"_method":"DELETE"}: receives the money of a sold card. Received = EP paid out.</summary>
    public async Task<(bool Ok, long Received, string Raw)> CollectOfferAsync(string offerId, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"_method\":\"DELETE\"}"));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var resp = await _http.PostAsync($"{_base}/transfer/my/offer/completed/{offerId}", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        bool ok = false; long got = 0;
        try
        {
            var n = JsonNode.Parse(text);
            ok = n?["result"]?.GetValue<bool>() == true;
            got = n?["ep"]?.GetValue<long>() ?? 0;
            if (ok) ApplyMoney(text);
        }
        catch { }
        return (ok && resp.IsSuccessStatusCode, got, resp.IsSuccessStatusCode ? text : $"HTTP {(int)resp.StatusCode}: {text}");
    }

    // ---- buy / enhance (endpoints observed in the game capture) ----------

    public sealed record Wallet(long Ep, long Es);

    /// <summary>Last known balance; raised on every change (server value, or an estimate after a purchase / merge).</summary>
    public Wallet? LastWallet { get; private set; }
    public event Action<Wallet>? WalletChanged;
    /// <summary>true when the last buy / enhance response carried the real balance (no need to estimate).</summary>
    public bool LastHadMoney { get; private set; }

    private void SetWallet(Wallet w) { LastWallet = w; WalletChanged?.Invoke(w); }

    /// <summary>Estimate after spending EP when the response did not include the balance; the next /coach/me corrects it.</summary>
    public void Spend(long ep) { if (LastWallet is { } w) SetWallet(w with { Ep = w.Ep - ep }); }

    private bool ApplyMoney(string text)
    {
        var w = ParseWallet(text);
        if (w != null) SetWallet(w);
        return w != null;
    }

    public static Wallet? ParseWallet(string text)
    {
        try
        {
            var root = JsonNode.Parse(text);
            var m = root?["money"];
            if (m?["ep"] == null)   // purchase / enhance / trade answers carry the fresh /coach/me in resources[]
                foreach (var r in root?["resources"]?.AsArray() ?? new JsonArray())
                    if (r?["uri"]?.GetValue<string>() == "/coach/me" && r["body"]?["money"]?["ep"] != null) { m = r["body"]!["money"]; break; }
            if (m?["ep"] == null) return null;
            return new Wallet(m["ep"]!.GetValue<long>(), m["es"]?.GetValue<long>() ?? 0);
        }
        catch { return null; }
    }

    public async Task<Wallet?> GetWalletAsync(CancellationToken ct = default)
    {
        var text = await GetAsync("/coach/me", ct);
        ApplyMoney(text);
        CountCards(text);
        return LastWallet;
    }

    /// <summary>Number of cards in the storage as of the last /coach/me read (the game's storage holds 200), null until read.</summary>
    public int? LastCardCount { get; private set; }

    private void CountCards(string text)
    {
        try { LastCardCount = JsonNode.Parse(text)?["team"]?["players"]?.AsArray().Count; } catch { }
    }

    /// <summary>RestoreGrade: the "restore" field the game puts on a card after a failed merge (grade it can be restored to), -1 when there is none.</summary>
    public sealed record OwnedCard(string Id, long Spid, int Grade, bool InSquad, string Name = "", int Group = 2, long Value = 0, int RestoreGrade = -1);
    /// <summary>Lineup group of an owned card: 0 = starting XI, 1 = substitutes, 2 = not registered.</summary>
    public const int GroupStarter = 0, GroupBench = 1, GroupNone = 2;
    private const int BenchSlot = 28;   // squad.positions[].pos of the substitutes
    public sealed record BuyResult(bool Ok, string? CardId, string Raw, bool HadMoney = false);

    /// <summary>The server's answer when the card storage has no free slot: {"result":false,"reason":"Exceed plyer limit"} (sic).</summary>
    public static bool IsStorageFull(string raw) =>
        raw.Contains("Exceed", StringComparison.OrdinalIgnoreCase) && raw.Contains("limit", StringComparison.OrdinalIgnoreCase);
    public sealed record EnhancePreview(int Grade, long Price, int Probability, long EnhancePay, int NeedEs, string Raw);
    public sealed record EnhanceResult(bool Ok, bool Success, int ResultGrade, string Raw);

    // The game always sent "0/-1/111" after the material id; replicated as observed.
    private const string EnhanceTail = "0/-1/111";

    /// <summary>Buy one card at exactly the listed price. CardId is the new card's _id, needed later to enhance it.</summary>
    public async Task<BuyResult> BuyAsync(long spid, int grade, long price, CancellationToken ct = default)
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{_base}/transfer/purchase/{spid}/{grade}/{price}/purchase", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var r = ParseBuy(text);
        LastHadMoney = r.Ok && ApplyMoney(text);
        return r with { Ok = r.Ok && resp.IsSuccessStatusCode, Raw = resp.IsSuccessStatusCode ? text : $"HTTP {(int)resp.StatusCode}: {text}", HadMoney = LastHadMoney };
    }

    public async Task<EnhancePreview> EnhancePreviewAsync(string mainId, string materialId, CancellationToken ct = default)
    {
        var text = await GetAsync($"/player/{mainId}/enhance2.5/{materialId}/preview/{EnhanceTail}", ct);
        return ParsePreview(text);
    }

    public async Task<EnhanceResult> EnhanceAsync(string mainId, string materialId, CancellationToken ct = default)
    {
        // Exactly what the game sends: plain "application/json" (no charset parameter).
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"_method\":\"PUT\"}"));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var resp = await _http.PostAsync($"{_base}/player/{mainId}/enhance2.5/{materialId}/{EnhanceTail}", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var r = ParseEnhance(text);
        LastHadMoney = r.Ok && ApplyMoney(text);
        return r with { Ok = r.Ok && resp.IsSuccessStatusCode, Raw = resp.IsSuccessStatusCode ? text : $"HTTP {(int)resp.StatusCode}: {text}" };
    }

    /// <summary>Restore the card of the last failed merge (the server remembers which one: no id in the request). The first restore is free,
    /// a second one costs EP. Success is judged by the card's grade afterwards, NOT by "success" (it is false even when the card came back).</summary>
    public async Task<(bool Ok, string Raw)> RestoreAsync(CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"_method\":\"PUT\"}"));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var resp = await _http.PostAsync($"{_base}/player/enhance2.5/restore", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        bool ok = resp.IsSuccessStatusCode && JsonNode.Parse(text)?["result"]?.GetValue<bool>() == true;
        if (ok) ApplyMoney(text);
        return (ok, resp.IsSuccessStatusCode ? text : $"HTTP {(int)resp.StatusCode}: {text}");
    }

    /// <summary>Delete a card from the storage for good (what the game's "discard player" does).</summary>
    public async Task<(bool Ok, string Raw)> DeleteCardAsync(string cardId, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"_method\":\"DELETE\"}"));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var resp = await _http.PostAsync($"{_base}/player/{cardId}", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        bool ok = resp.IsSuccessStatusCode && JsonNode.Parse(text)?["result"]?.GetValue<bool>() == true;
        return (ok, resp.IsSuccessStatusCode ? text : $"HTTP {(int)resp.StatusCode}: {text}");
    }

    /// <summary>Every card in the storage (coach/me -> team.players). InSquad marks cards that are in use (state set / lineup).</summary>
    public async Task<List<OwnedCard>> GetInventoryAsync(CancellationToken ct = default)
    {
        var text = await GetAsync("/coach/me", ct);
        ApplyMoney(text);
        CountCards(text);
        return ParseInventory(text);
    }

    public static List<OwnedCard> ParseInventory(string text)
    {
        var team = JsonNode.Parse(text)?["team"];
        var squad = new HashSet<string>();
        var slot = new Dictionary<string, int>();
        foreach (var pos in team?["squad"]?["positions"]?.AsArray() ?? new JsonArray())
            if (pos?["id"]?.GetValue<string>() is { } sid) { squad.Add(sid); slot[sid] = pos["pos"]?.GetValue<int>() ?? 0; }

        var list = new List<OwnedCard>();
        foreach (var p in team?["players"]?.AsArray() ?? new JsonArray())
        {
            var id = p?["id"]?.GetValue<string>() ?? p?["_id"]?.GetValue<string>();
            if (id == null) continue;
            list.Add(new OwnedCard(id, p!["spid"]?.GetValue<long>() ?? 0, p["grade"]?.GetValue<int>() ?? 0,
                squad.Contains(id) || p["state"] != null, p["jerseyname"]?.GetValue<string>() ?? "",
                slot.TryGetValue(id, out var sl) ? (sl == BenchSlot ? GroupBench : GroupStarter) : (p["state"] != null ? GroupBench : GroupNone),
                p["price"]?.GetValue<long>() ?? 0, p["restore"]?["grade"]?.GetValue<int>() ?? -1));
        }
        return list;
    }

    public static BuyResult ParseBuy(string text)
    {
        try
        {
            var n = JsonNode.Parse(text);
            var id = n?["player"]?["_id"]?.GetValue<string>();
            return new BuyResult(n?["result"]?.GetValue<bool>() == true && id != null, id, text);
        }
        catch { return new BuyResult(false, null, text); }
    }

    public static EnhancePreview ParsePreview(string text)
    {
        var n = JsonNode.Parse(text);
        return new EnhancePreview(
            n?["grade"]?.GetValue<int>() ?? 0,
            n?["price"]?.GetValue<long>() ?? 0,
            n?["probability"]?.GetValue<int>() ?? 0,
            n?["enhancepay"]?.GetValue<long>() ?? 0,
            n?["needes"]?["es"]?.GetValue<int>() ?? 0,
            text);
    }

    public static EnhanceResult ParseEnhance(string text)
    {
        try
        {
            var n = JsonNode.Parse(text);
            return new EnhanceResult(
                n?["result"]?.GetValue<bool>() == true,
                n?["success"]?.GetValue<bool>() == true,
                n?["resultgrade"]?.GetValue<int>() ?? 0,
                text);
        }
        catch { return new EnhanceResult(false, false, 0, text); }
    }

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(_base + path, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET {path}: HTTP {(int)resp.StatusCode} {Trim(text)}");
        return text;
    }

    public static string Trim(string s) => s.Length > 600 ? s[..600] + "..." : s;
}
