using System.Reflection;

namespace RAF.App;

/// <summary>
/// הגשת נכסי הממשק ישירות מתוך קובץ ההרצה.
/// שום קובץ אינו נכתב לדיסק — זו הדרישה לניידות מלאה.
/// </summary>
internal static class WebAssets
{
    /// <summary>שם המארח הווירטואלי שממנו נטען הממשק.</summary>
    internal const string VirtualHost = "raf.local";
    internal const string BaseUrl = "https://" + VirtualHost + "/";

    private const string ResourcePrefix = "RAF.Web.";
    private static readonly Assembly Assembly = typeof(WebAssets).Assembly;

    /// <summary>קריאת נכס לפי שם הקובץ. מחזיר null אם אינו קיים.</summary>
    internal static byte[]? Read(string fileName)
    {
        string resourceName = ResourcePrefix + fileName.Replace('/', '.');

        using Stream? stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>סוג התוכן לפי סיומת הקובץ.</summary>
    internal static string ContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };

    /// <summary>כל שמות הנכסים המוטמעים — לאבחון.</summary>
    internal static IEnumerable<string> List() =>
        Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                .Select(n => n[ResourcePrefix.Length..]);
}
