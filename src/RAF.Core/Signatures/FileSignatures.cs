namespace RAF.Core.Signatures;

/// <summary>
/// חתימת קובץ — רצף בתים קבוע שמזהה את סוג הקובץ ללא תלות בסיומת.
/// זהו הבסיס הן לסריקה המתקדמת (carving) והן לזיהוי קבצים פגומים.
/// </summary>
public sealed class FileSignature
{
    /// <summary>שם הפורמט בעברית, להצגה למשתמש.</summary>
    public required string Name { get; init; }

    /// <summary>הסיומות המקובלות לפורמט זה.</summary>
    public required string[] Extensions { get; init; }

    public string MimeType { get; init; } = "application/octet-stream";

    /// <summary>רצף הבתים המזהה. ערך null בתוך המערך מסמן בית שאינו נבדק.</summary>
    public required byte?[] Header { get; init; }

    /// <summary>היסט החתימה מתחילת הקובץ. ברוב הפורמטים זהו אפס.</summary>
    public int HeaderOffset { get; init; }

    /// <summary>חתימת סיום, כשקיימת. משמשת לקביעת גבול הקובץ בסריקה מתקדמת.</summary>
    public byte[]? Footer { get; init; }

    /// <summary>הגודל המרבי הסביר לפורמט, לתיחום חילוץ בסריקה מתקדמת.</summary>
    public long MaxSize { get; init; } = 512L * 1024 * 1024;

    public bool IsImage { get; init; }

    /// <summary>האם הסיומת שנשמרה בשם הקובץ תואמת לתוכן שזוהה בפועל.</summary>
    public bool MatchesExtension(string extension)
        => string.IsNullOrEmpty(extension) ||
           Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>בדיקה האם התוכן פותח בחתימה זו.</summary>
    internal bool Matches(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderOffset + Header.Length) return false;

        for (int i = 0; i < Header.Length; i++)
        {
            byte? expected = Header[i];
            if (expected is not null && data[HeaderOffset + i] != expected.Value) return false;
        }

        return true;
    }
}

/// <summary>טבלת החתימות שהתוכנה מזהה.</summary>
public static class FileSignatures
{
    /// <summary>המרת מחרוזת ASCII למערך בתים לבדיקה.</summary>
    private static byte?[] Ascii(string text)
        => text.Select(c => (byte?)(byte)c).ToArray();

    private static byte?[] Bytes(params int[] values)
        => values.Select(v => v < 0 ? (byte?)null : (byte)v).ToArray();

    /// <summary>כל החתימות המוכרות, מסודרות מהספציפית לכללית.</summary>
    public static readonly IReadOnlyList<FileSignature> All = new List<FileSignature>
    {
        // ---------- תמונות ----------
        new()
        {
            Name = "תמונת JPEG", Extensions = new[] { "jpg", "jpeg", "jpe", "jfif" },
            MimeType = "image/jpeg", IsImage = true,
            Header = Bytes(0xFF, 0xD8, 0xFF),
            Footer = new byte[] { 0xFF, 0xD9 },
            MaxSize = 64L * 1024 * 1024,
        },
        new()
        {
            Name = "תמונת PNG", Extensions = new[] { "png" },
            MimeType = "image/png", IsImage = true,
            Header = Bytes(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
            Footer = new byte[] { 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 },
            MaxSize = 128L * 1024 * 1024,
        },
        new()
        {
            Name = "תמונת GIF", Extensions = new[] { "gif" },
            MimeType = "image/gif", IsImage = true,
            Header = Ascii("GIF8"),
            Footer = new byte[] { 0x00, 0x3B },
            MaxSize = 32L * 1024 * 1024,
        },
        new()
        {
            Name = "תמונת BMP", Extensions = new[] { "bmp", "dib" },
            MimeType = "image/bmp", IsImage = true,
            Header = Ascii("BM"),
            MaxSize = 128L * 1024 * 1024,
        },
        new()
        {
            // WEBP: "RIFF" ואחריו גודל, ואז "WEBP" בהיסט 8.
            Name = "תמונת WebP", Extensions = new[] { "webp" },
            MimeType = "image/webp", IsImage = true,
            Header = Bytes(0x52, 0x49, 0x46, 0x46, -1, -1, -1, -1, 0x57, 0x45, 0x42, 0x50),
            MaxSize = 64L * 1024 * 1024,
        },
        new()
        {
            Name = "תמונת TIFF", Extensions = new[] { "tif", "tiff" },
            MimeType = "image/tiff", IsImage = true,
            Header = Bytes(0x49, 0x49, 0x2A, 0x00),
            MaxSize = 512L * 1024 * 1024,
        },
        new()
        {
            Name = "תמונת TIFF", Extensions = new[] { "tif", "tiff" },
            MimeType = "image/tiff", IsImage = true,
            Header = Bytes(0x4D, 0x4D, 0x00, 0x2A),
            MaxSize = 512L * 1024 * 1024,
        },
        new()
        {
            Name = "תמונת HEIC", Extensions = new[] { "heic", "heif" },
            MimeType = "image/heic", IsImage = true,
            Header = Bytes(-1, -1, -1, -1, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63),
            MaxSize = 64L * 1024 * 1024,
        },
        new()
        {
            Name = "סמל ICO", Extensions = new[] { "ico" },
            MimeType = "image/x-icon", IsImage = true,
            Header = Bytes(0x00, 0x00, 0x01, 0x00),
            MaxSize = 4L * 1024 * 1024,
        },
        new()
        {
            Name = "תמונת Photoshop", Extensions = new[] { "psd" },
            MimeType = "image/vnd.adobe.photoshop",
            Header = Ascii("8BPS"),
            MaxSize = 2L * 1024 * 1024 * 1024,
        },
        new()
        {
            Name = "תמונת RAW של Canon", Extensions = new[] { "cr2" },
            Header = Bytes(0x49, 0x49, 0x2A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x43, 0x52),
            MaxSize = 128L * 1024 * 1024,
        },

        // ---------- מסמכים ----------
        new()
        {
            Name = "מסמך PDF", Extensions = new[] { "pdf" },
            MimeType = "application/pdf",
            Header = Ascii("%PDF-"),
            Footer = new byte[] { 0x25, 0x25, 0x45, 0x4F, 0x46 }, // %%EOF
            MaxSize = 1024L * 1024 * 1024,
        },
        new()
        {
            // מסמכי Office משנת 2007 ואילך הם למעשה ארכיוני ZIP.
            Name = "מסמך Office או ארכיון ZIP",
            // כולל תבניות ומסמכים עם פקודות מאקרו — אחרת קובץ ‎.dotx תקין דווח כ"סיומת שגויה"
            // ותוקן ל-‎.zip.
            Extensions = new[]
            {
                "zip", "docx", "xlsx", "pptx", "docm", "xlsm", "pptm", "dotx", "xltx", "potx",
                "dotm", "xltm", "potm", "ppsx", "vsdx", "odt", "ods", "odp", "epub", "jar", "apk",
            },
            MimeType = "application/zip",
            Header = Bytes(0x50, 0x4B, 0x03, 0x04),
            MaxSize = 4L * 1024 * 1024 * 1024,
        },
        new()
        {
            // פורמט OLE2 הישן: doc, xls, ppt, msi.
            Name = "מסמך Office ישן", Extensions = new[] { "doc", "xls", "ppt", "msi", "msg" },
            MimeType = "application/x-ole-storage",
            Header = Bytes(0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1),
            MaxSize = 512L * 1024 * 1024,
        },
        new()
        {
            Name = "מסמך RTF", Extensions = new[] { "rtf" },
            MimeType = "application/rtf",
            Header = Ascii("{\\rtf"),
            MaxSize = 64L * 1024 * 1024,
        },

        // ---------- ארכיונים ----------
        new()
        {
            Name = "ארכיון RAR", Extensions = new[] { "rar" },
            MimeType = "application/vnd.rar",
            Header = Bytes(0x52, 0x61, 0x72, 0x21, 0x1A, 0x07),
            MaxSize = 8L * 1024 * 1024 * 1024,
        },
        new()
        {
            Name = "ארכיון 7-Zip", Extensions = new[] { "7z" },
            MimeType = "application/x-7z-compressed",
            Header = Bytes(0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C),
            MaxSize = 8L * 1024 * 1024 * 1024,
        },
        new()
        {
            Name = "ארכיון GZIP", Extensions = new[] { "gz", "tgz" },
            MimeType = "application/gzip",
            Header = Bytes(0x1F, 0x8B, 0x08),
            MaxSize = 4L * 1024 * 1024 * 1024,
        },

        // ---------- וידאו ואודיו ----------
        new()
        {
            // MP4 ומשפחתו: "ftyp" בהיסט 4.
            Name = "וידאו MP4", Extensions = new[] { "mp4", "m4v", "m4a", "mov" },
            MimeType = "video/mp4",
            Header = Ascii("ftyp"), HeaderOffset = 4,
            MaxSize = 16L * 1024 * 1024 * 1024,
        },
        new()
        {
            Name = "וידאו AVI", Extensions = new[] { "avi" },
            MimeType = "video/x-msvideo",
            Header = Bytes(0x52, 0x49, 0x46, 0x46, -1, -1, -1, -1, 0x41, 0x56, 0x49, 0x20),
            MaxSize = 16L * 1024 * 1024 * 1024,
        },
        new()
        {
            Name = "וידאו Matroska", Extensions = new[] { "mkv", "webm" },
            MimeType = "video/x-matroska",
            Header = Bytes(0x1A, 0x45, 0xDF, 0xA3),
            MaxSize = 32L * 1024 * 1024 * 1024,
        },
        new()
        {
            Name = "שמע MP3", Extensions = new[] { "mp3" },
            MimeType = "audio/mpeg",
            Header = Ascii("ID3"),
            MaxSize = 512L * 1024 * 1024,
        },
        new()
        {
            Name = "שמע WAV", Extensions = new[] { "wav" },
            MimeType = "audio/wav",
            Header = Bytes(0x52, 0x49, 0x46, 0x46, -1, -1, -1, -1, 0x57, 0x41, 0x56, 0x45),
            MaxSize = 4L * 1024 * 1024 * 1024,
        },
        new()
        {
            Name = "שמע FLAC", Extensions = new[] { "flac" },
            MimeType = "audio/flac",
            Header = Ascii("fLaC"),
            MaxSize = 2L * 1024 * 1024 * 1024,
        },

        // ---------- אחר ----------
        new()
        {
            Name = "מסד נתונים SQLite", Extensions = new[] { "db", "sqlite", "sqlite3" },
            MimeType = "application/vnd.sqlite3",
            Header = Ascii("SQLite format 3"),
            MaxSize = 8L * 1024 * 1024 * 1024,
        },
        new()
        {
            Name = "קובץ הרצה של Windows", Extensions = new[] { "exe", "dll", "sys", "ocx" },
            MimeType = "application/vnd.microsoft.portable-executable",
            Header = Ascii("MZ"),
            MaxSize = 1024L * 1024 * 1024,
        },
    };

    /// <summary>זיהוי סוג הקובץ לפי תחילת תוכנו. מחזיר null כשאין התאמה.</summary>
    public static FileSignature? Identify(ReadOnlySpan<byte> head)
    {
        foreach (var signature in All)
            if (signature.Matches(head)) return signature;

        return null;
    }

    /// <summary>החתימות הרלוונטיות לסיומת נתונה — לבדיקת תקינות קובץ.</summary>
    public static IEnumerable<FileSignature> ForExtension(string extension)
        => All.Where(s => s.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
}
