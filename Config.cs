using System.Text.Json;

namespace FO3SessionTool;

public sealed class Config
{
    public string LauncherBase { get; set; } = "https://3sea.online";
    public string GameBase { get; set; } = "http://216.236.2.193:18090/lang-vi";
    public string LauncherUserAgent { get; set; } = "3seaLauncher/1.0.5";
    public int QueuePollSeconds { get; set; } = 5;
    /// <summary>Optional: the game folder (the one holding data\data_0.big). Empty = look in the tool's folder and its parents.</summary>
    public string GameDir { get; set; } = "";
    public Dictionary<string, string> Hwid { get; set; } = new();

    public static Config Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("config.json is empty");
    }
}
