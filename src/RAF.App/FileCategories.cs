namespace RAF.App;

/// <summary>
/// שיוך קובץ לקטגוריה לפי סיומתו, עבור שבבי הסינון במסך התוצאות.
/// השיוך הוא לפי הסיומת ולא לפי החתימה: זה מה שהמשתמש מכיר ומחפש,
/// ובסריקה מתקדמת הסיומת ממילא נקבעת לפי החתימה שזוהתה.
/// </summary>
internal static class FileCategories
{
    internal const string All = "all";
    internal const string Other = "other";

    /// <summary>הקטגוריות בסדר ההצגה, עם הסיומות של כל אחת.</summary>
    internal static readonly (string Id, string[] Extensions)[] Ordered =
    {
        ("images", new[] { "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif",
                           "ico", "svg", "psd", "raw", "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2" }),
        ("documents", new[] { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp",
                              "rtf", "txt", "csv", "md", "xml", "html", "htm", "json", "epub" }),
        ("video", new[] { "mp4", "mov", "avi", "mkv", "wmv", "flv", "webm", "m4v", "3gp", "mpg", "mpeg",
                          "mts", "m2ts" }),
        ("audio", new[] { "mp3", "wav", "flac", "aac", "m4a", "ogg", "wma", "opus", "amr", "mid", "midi" }),
        ("archives", new[] { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab" }),
    };

    private static readonly Dictionary<string, string> ByExtension = Ordered
        .SelectMany(c => c.Extensions.Select(e => (e, c.Id)))
        .ToDictionary(p => p.e, p => p.Id, StringComparer.OrdinalIgnoreCase);

    internal static string Of(string extension)
        => ByExtension.TryGetValue(extension, out var id) ? id : Other;

    /// <summary>סיומות שהמנוע יודע להקטין לתמונה ממוזערת.</summary>
    internal static readonly HashSet<string> Thumbnailable = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "ico",
    };
}
