using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace FO3SessionTool;

/// <summary>Mimics 3seaLauncher.exe: POST /web/api/launcher/login, polling the queue ticket until a token is issued.</summary>
public sealed class LauncherClient
{
    private readonly HttpClient _http;
    private readonly Config _cfg;

    public LauncherClient(Config cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { BaseAddress = new Uri(cfg.LauncherBase), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(cfg.LauncherUserAgent);
    }

    public async Task<string> LoginAsync(string username, string password, Action<string> log, CancellationToken ct = default)
    {
        var body = new JsonObject { ["username"] = username, ["password"] = password, ["hwid"] = HwidNode() };
        while (true)
        {
            var res = await PostAsync(body, ct);
            if (res["result"]?.GetValue<bool>() == true)
                return res["token"]?.GetValue<string>() ?? throw new InvalidOperationException("login ok but no token");

            var code = res["code"]?.GetValue<string>() ?? "UNKNOWN";
            if (code != "QUEUE")
                throw new InvalidOperationException($"Login failed: {code} - {res["reason"]}");

            log($"Queue position {res["position"]} (waiting {res["waiting"]})");
            var ticket = res["ticket"]?.GetValue<string>() ?? throw new InvalidOperationException("QUEUE without ticket");
            // Subsequent polls send only the ticket, like the launcher does (password is not resent).
            body = new JsonObject { ["hwid"] = HwidNode(), ["ticket"] = ticket };
            await Task.Delay(TimeSpan.FromSeconds(_cfg.QueuePollSeconds), ct);
        }
    }

    private JsonObject HwidNode()
    {
        var o = new JsonObject();
        foreach (var kv in _cfg.Hwid.OrderBy(k => k.Key, StringComparer.Ordinal)) o[kv.Key] = kv.Value;
        return o;
    }

    private async Task<JsonNode> PostAsync(JsonObject body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("/web/api/launcher/login", body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Launcher HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {(text.Length > 300 ? text[..300] : text)}");
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty login response");
    }
}
