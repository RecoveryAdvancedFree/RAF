using System.Text.Json;

namespace RAF.Core.Signatures;

/// <summary>סוג קובץ שהמשתמש הוסיף — כפי שהוא נשמר בקובץ ההגדרות.</summary>
public sealed record CustomType(string Name, string Extension, string Header, string? Footer, long MaxSize, int Samples);

/// <summary>תוצאת לימוד סוג קובץ מדוגמאות.</summary>
public sealed class LearnResult
{
    public bool Ok { get; init; }

    /// <summary>הסבר בעברית: מה נלמד, או למה אי אפשר.</summary>
    public string Message { get; init; } = "";

    public CustomType? Type { get; init; }

    /// <summary>כמה בתים בתחילת הקובץ זהים בכל הדוגמאות — ככל שיותר, הזיהוי בטוח יותר.</summary>
    public int FixedBytes { get; init; }
}

/// <summary>
/// סוגי קבצים שהמשתמש מלמד את התוכנה, לסריקה המתקדמת.
///
/// הסריקה המתקדמת מוצאת קבצים לפי הבתים שבתחילתם. לסוג שהתוכנה לא מכירה — קובץ של
/// תוכנת הנהלת חשבונות, של משחק, של מכשיר מסוים — אין לה במה לחפש. כאן בוחרים כמה
/// קבצים תקינים מאותו סוג, והבתים שזהים בתחילת כולם הופכים לחתימה. בתים שמשתנים
/// מקובץ לקובץ (גודל, תאריך, מספר גרסה) נשארים פתוחים, כמו בחתימות המובנות.
/// </summary>
public static class CustomSignatures
{
    /// <summary>כמה בתים מתחילת הקובץ נבדקים. הסורק מזהה לפי 64 הבתים הראשונים.</summary>
    private const int HeaderWindow = 32;

    /// <summary>מינימום בתים קבועים. פחות מזה — החתימה תופיע גם בנתונים אקראיים.</summary>
    private const int MinFixed = 4;

    private const int MaxFooter = 16;

    // ============================================================ לימוד

    /// <summary>לימוד סוג מתוך קבצים לדוגמה. הקבצים רק נקראים.</summary>
    public static LearnResult Learn(IReadOnlyList<string> paths)
    {
        if (paths.Count < 2)
            return Fail("צריך לפחות שני קבצים מאותו סוג — ועדיף שלושה ומעלה — כדי לדעת מה משותף לכולם.");

        var heads = new List<byte[]>();
        var tails = new List<byte[]>();
        long largest = 0;
        foreach (string path in paths)
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length < 16) return Fail($"הקובץ \"{Path.GetFileName(path)}\" קטן מדי כדי ללמוד ממנו.");
                largest = Math.Max(largest, stream.Length);

                byte[] head = new byte[Math.Min(HeaderWindow, stream.Length)];
                stream.ReadExactly(head);
                heads.Add(head);

                byte[] tail = new byte[Math.Min(MaxFooter, stream.Length)];
                stream.Position = stream.Length - tail.Length;
                stream.ReadExactly(tail);
                tails.Add(tail);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Fail($"לא ניתן לקרוא את \"{Path.GetFileName(path)}\": {e.Message}");
            }
        }

        // סוג שהתוכנה כבר מכירה — אין מה ללמוד, והחתימה החדשה הייתה רק מתחרה בה.
        var known = heads.Select(h => FileSignatures.Identify(h)?.Name).Distinct().ToList();
        if (known.Count == 1 && known[0] is { } name)
            return Fail($"התוכנה כבר מכירה את הקבצים האלה — הם בנויים כמו {name}, והסריקה המתקדמת כבר מוצאת אותם. " +
                        "אין צורך להוסיף סוג חדש.");

        string extension = MostCommonExtension(paths);
        if (extension.Length == 0)
            return Fail("לקבצים לדוגמה אין סיומת. בחרו קבצים עם הסיומת שהם אמורים לקבל בשחזור.");

        // בתים שזהים בכל הדוגמאות — קבועים; השאר פתוחים.
        int window = heads.Min(h => h.Length);
        var header = new byte?[window];
        for (int i = 0; i < window; i++)
        {
            byte b = heads[0][i];
            header[i] = heads.All(h => h[i] == b) ? b : null;
        }
        int end = Array.FindLastIndex(header, b => b is not null) + 1;
        header = header[..end];

        int fixedCount = header.Count(b => b is not null);
        int meaningful = header.Count(b => b is not null and not 0);
        if (fixedCount < MinFixed || meaningful < 3)
            return Fail("לא נמצא מספיק משותף בתחילת הקבצים: " +
                        (fixedCount == 0 ? "כל קובץ מתחיל אחרת. " : $"רק {fixedCount} בתים זהים בכולם. ") +
                        "ייתכן שהם לא באמת מאותו סוג, או שלסוג הזה אין תחילה קבועה — ואז אי אפשר לחפש אותו כך.");

        string? footer = CommonFooter(tails);
        long maxSize = Math.Clamp(largest * 4, 1024 * 1024, 4L * 1024 * 1024 * 1024);

        var type = new CustomType(
            Name: $"קובץ {extension.ToUpperInvariant()}",
            Extension: extension,
            Header: ToHex(header),
            Footer: footer,
            MaxSize: maxSize,
            Samples: paths.Count);

        return new LearnResult
        {
            Ok = true,
            Type = type,
            FixedBytes = fixedCount,
            Message = $"בתחילת כל {paths.Count} הקבצים יש {fixedCount} בתים זהים — לפיהם הסריקה המתקדמת תמצא קבצים מהסוג הזה." +
                      (footer is not null ? " גם סוף הקבצים זהה, ולכן גם האורך של כל קובץ שיימצא ייקבע במדויק." :
                          $" לסוג הזה אין סוף קבוע, ולכן כל קובץ שיימצא ישוחזר באורך של עד {Size(maxSize)} — " +
                          "ייתכן שבסופו יהיו נתונים מיותרים. בדרך כלל התוכנה שפותחת אותו מתעלמת מהם.") +
                      (paths.Count == 2 ? " עם שלושה קבצים ומעלה הזיהוי מדויק יותר." : ""),
        };
    }

    /// <summary>סוף משותף לכל הדוגמאות — סימן סיום, כמו בחתימות המובנות. null — אין.</summary>
    private static string? CommonFooter(List<byte[]> tails)
    {
        int length = 0;
        int shortest = tails.Min(t => t.Length);
        while (length < shortest)
        {
            byte b = tails[0][^(length + 1)];
            if (!tails.All(t => t[^(length + 1)] == b)) break;
            length++;
        }

        byte[] footer = tails[0][^length..];
        // אפסים בסוף הם ריפוד, לא סימן — הם מופיעים אחרי כל קובץ.
        if (length < 2 || footer.Count(b => b != 0) < 2) return null;
        return ToHex(footer.Select(b => (byte?)b).ToArray());
    }

    private static string MostCommonExtension(IEnumerable<string> paths)
        => paths.Select(p => Path.GetExtension(p).TrimStart('.').ToLowerInvariant())
                .Where(e => e.Length > 0)
                .GroupBy(e => e)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault() ?? "";

    // ============================================================ שמירה והפעלה

    /// <summary>קובץ ההגדרות — ליד שאר נתוני התוכנה של המשתמש.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAF", "custom-types.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<CustomType> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<CustomType>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();                                                   // קובץ פגום לא מונע מהתוכנה לעבוד
        }
    }

    public static void Save(string path, IReadOnlyList<CustomType> types)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(types, Json));
    }

    /// <summary>טעינת הסוגים לסורק — הם נבדקים אחרי כל הסוגים המובנים.</summary>
    public static void Activate(IEnumerable<CustomType> types)
        => FileSignatures.Custom = types.Select(ToSignature).Where(s => s is not null).ToList()!;

    private static FileSignature? ToSignature(CustomType t)
    {
        byte?[]? header = FromHex(t.Header);
        if (header is null || header.Count(b => b is not null) < MinFixed) return null;

        byte[]? footer = t.Footer is null ? null : FromHex(t.Footer)?.Select(b => b ?? 0).ToArray();
        return new FileSignature
        {
            Name = t.Name,
            Extensions = new[] { t.Extension },
            Header = header,
            Footer = footer is { Length: > 0 } ? footer : null,
            MaxSize = Math.Clamp(t.MaxSize, 64 * 1024, 4L * 1024 * 1024 * 1024),
            Structure = "custom",
        };
    }

    /// <summary>"4D 5A ?? 00" — בתים בכתיב הקסדצימלי, ו-?? לבית פתוח.</summary>
    internal static string ToHex(byte?[] bytes)
        => string.Join(' ', bytes.Select(b => b is { } v ? v.ToString("X2") : "??"));

    internal static byte?[]? FromHex(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new byte?[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "??") continue;
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out byte b)) return null;
            result[i] = b;
        }
        return result;
    }

    private static LearnResult Fail(string message) => new() { Message = message };

    private static string Size(long bytes) => bytes >= 1024 * 1024 * 1024
        ? $"⁦{bytes / (1024.0 * 1024 * 1024):0.#} GB⁩"
        : $"⁦{bytes / (1024.0 * 1024):0.#} MB⁩";
}
