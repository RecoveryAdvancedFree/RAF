using System.Text.Json;

namespace RAF.Core.Update;

/// <summary>קובץ אחד בשחרור: הכתובת, הגודל וטביעת האצבע שגיטהאב מפרסם לו.</summary>
public sealed record ReleaseAsset(string Url, long Size, byte[]? Sha256);

/// <summary>
/// הגרסה האחרונה שפורסמה בגיטהאב, מתוך התשובה של releases/latest.
/// גרסאות בטא (כמו גרסת המק) מסומנות שם כגרסה מקדימה, והכתובת הזו לא מחזירה אותן.
/// </summary>
public sealed record ReleaseInfo(Version Version, string PageUrl, ReleaseAsset? Setup, ReleaseAsset? Portable)
{
    public const string LatestUrl = "https://api.github.com/repos/RecoveryAdvancedFree/RAF/releases/latest";
    public const string SetupName = "RAF-Setup.exe";
    public const string PortableName = "RAF.exe";

    /// <summary>פענוח התשובה. null — אין בה גרסה שאפשר לקרוא.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;

        var version = ParseVersion(root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null);
        if (version is null) return null;

        string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        ReleaseAsset? setup = null, portable = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long v) ? v : 0;
                if (url is null || size <= 0) continue;
                var asset = new ReleaseAsset(url, size, ParseDigest(a.TryGetProperty("digest", out var d) ? d.GetString() : null));
                if (string.Equals(name, SetupName, StringComparison.OrdinalIgnoreCase)) setup = asset;
                else if (string.Equals(name, PortableName, StringComparison.OrdinalIgnoreCase)) portable = asset;
            }

        return new ReleaseInfo(version, page, setup, portable);
    }

    /// <summary>"v0.7.0" או "0.7.0" — שלושה חלקים. תגית כמו "v0.6.0-mac-beta" אינה גרסה רגילה.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string s = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(s, out var v) || v.Build < 0) return null;
        return new Version(v.Major, v.Minor, v.Build);
    }

    /// <summary>"sha256:ab12…" — טביעת האצבע שגיטהאב מחשב לכל קובץ שמועלה.</summary>
    private static byte[]? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            byte[] hash = Convert.FromHexString(digest[prefix.Length..]);
            return hash.Length == 32 ? hash : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>האם זו גרסה חדשה יותר מ-current (רק שלושת החלקים הראשונים נחשבים).</summary>
    public bool IsNewerThan(Version current) =>
        Version > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));
}
