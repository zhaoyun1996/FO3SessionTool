using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FO3SessionTool;

public sealed record SavedCookie(string Name, string Value, string Path, string Domain);
public sealed record SavedSession(string User, string Token, List<SavedCookie> Cookies, DateTime SavedAt);

/// <summary>Persists the game session cookies encrypted with Windows DPAPI (current user only).</summary>
public static class SessionStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FO3SessionTool", "session.dat");

    // Extra entropy so another app calling DPAPI as the same user cannot decrypt it by accident.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FO3SessionTool/session/v1");

    public static void Save(SavedSession s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var plain = JsonSerializer.SerializeToUtf8Bytes(s);
            File.WriteAllBytes(FilePath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
        catch { /* not being able to remember the session must never break login */ }
    }

    public static SavedSession? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SavedSession>(plain);
        }
        catch { Clear(); return null; }   // corrupt, or written by another Windows user
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    // ---- session handed over by the game capture (capture\game_capture.py) -------------------------

    private static string GameCookieFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FO3SessionTool", "game_cookie.dat");
    private static readonly byte[] GameEntropy = Encoding.UTF8.GetBytes("FO3SessionTool/gamecookie/v1");

    /// <summary>The cookies the running game last used (connect.sid, fo3sid) and when they were seen, or null.</summary>
    public static (List<SavedCookie> Cookies, DateTime At)? LoadGameCookie()
    {
        try
        {
            if (!File.Exists(GameCookieFile)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(GameCookieFile), GameEntropy, DataProtectionScope.CurrentUser);
            using var doc = JsonDocument.Parse(plain);
            var list = new List<SavedCookie>();
            foreach (var p in doc.RootElement.GetProperty("cookies").EnumerateObject())
                list.Add(new SavedCookie(p.Name, p.Value.GetString() ?? "", "/", ""));
            var at = DateTime.TryParse(doc.RootElement.GetProperty("at").GetString(), out var t) ? t : File.GetLastWriteTime(GameCookieFile);
            return list.Count == 0 ? null : (list, at);
        }
        catch { return null; }
    }
}
