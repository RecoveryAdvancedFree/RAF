using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RAF.Core.Model;

namespace RAF.Core.Recovery;

/// <summary>זיהוי הכונן שנסרק — כדי לשחזר מהכונן הנכון גם אחרי שמספרו השתנה.</summary>
public sealed record DiskIdentity(string Model, string SerialNumber, long SizeBytes, string? ImagePath)
{
    public static DiskIdentity Of(PhysicalDiskInfo disk)
        => new(disk.Model, disk.SerialNumber, disk.SizeBytes, disk.ImagePath);

    /// <summary>
    /// האם זה אותו כונן. מספר הדיסק ב-Windows משתנה בין חיבורים, ולכן
    /// ההשוואה היא לפי מספר סידורי וגודל; תמונה — לפי הנתיב שלה.
    /// כונן בלי מספר סידורי (קורא כרטיסים זול, למשל) מזוהה לפי דגם וגודל.
    /// </summary>
    public bool Matches(PhysicalDiskInfo disk)
    {
        if (ImagePath is not null || disk.ImagePath is not null)
            return ImagePath is not null && disk.ImagePath is not null &&
                   string.Equals(Path.GetFullPath(ImagePath), Path.GetFullPath(disk.ImagePath),
                                 StringComparison.OrdinalIgnoreCase);

        if (disk.SizeBytes != SizeBytes) return false;
        return string.IsNullOrWhiteSpace(SerialNumber)
            ? string.Equals(disk.Model.Trim(), Model.Trim(), StringComparison.OrdinalIgnoreCase)
            : string.Equals(disk.SerialNumber.Trim(), SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>מה שמוצג ברשימת "סריקות אחרונות" — נקרא בלי לפענח את כל הקבצים.</summary>
public sealed record ScanArchiveHeader
{
    public int FormatVersion { get; init; } = ScanArchive.CurrentFormat;
    public DateTime SavedAt { get; init; }
    public string AppVersion { get; init; } = "";

    public DiskIdentity Disk { get; init; } = new("", "", 0, null);
    public string PartitionTitle { get; init; } = "";
    public ScanMode Mode { get; init; }
    public int FileCount { get; init; }
    public int RecoverableCount { get; init; }

    /// <summary>נקודת ביניים שנשמרה באמצע סריקה — הסריקה עצמה לא הסתיימה.</summary>
    public bool Partial { get; init; }
}

/// <summary>
/// תוצאות סריקה שמורות בקובץ, כדי שסריקה של שעות לא תאבד כשסוגרים את
/// התוכנה או כשהיא קורסת — ולא תצטרך לקרוא שוב מכונן שאולי גוסס.
///
/// המבנה: קובץ gzip ששורתו הראשונה היא הכותרת (JSON), ואחריה גוף הסריקה.
/// רשימת "סריקות אחרונות" קוראת רק את השורה הראשונה.
/// </summary>
public sealed class ScanArchive
{
    public const int CurrentFormat = 1;
    public const string Extension = ".rafscan";

    public ScanArchiveHeader Header { get; init; } = new();

    // מה שהשחזור צריך כדי לחזור לאותה מחיצה
    public long PartitionOffset { get; init; }
    public long PartitionSize { get; init; }
    public int SectorSize { get; init; } = 512;
    public FileSystemKind FileSystem { get; init; }

    /// <summary>המחיצה נקראה דרך עותק הגיבוי של מגזר האתחול — יש להפעיל זאת שוב בפתיחה.</summary>
    public bool ReadThrough { get; init; }

    public ScanResult Result { get; init; } = new();

    /// <summary>הקבצים שסומנו לשחזור ברגע השמירה.</summary>
    public List<long> Selected { get; init; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            // מאפיינים מחושבים (Extension, HasContent, IsWorthRecovering, ספירות) אינם נשמרים:
            // הם נגזרים מהשאר, ושמירתם הייתה מנפחת את הקובץ ועלולה לסתור את הנתונים.
            Modifiers =
            {
                info =>
                {
                    if (info.Kind != JsonTypeInfoKind.Object) return;
                    for (int i = info.Properties.Count - 1; i >= 0; i--)
                    {
                        var p = info.Properties[i];
                        if (p.Set is null && !IsConstructorParameter(info, p)) info.Properties.RemoveAt(i);
                    }
                },
            },
        },
    };

    /// <summary>מאפיין של רשומה פוזיציונית (כמו DataExtent) מאותחל דרך הבנאי, ולא דרך setter.</summary>
    private static bool IsConstructorParameter(JsonTypeInfo info, JsonPropertyInfo p)
        => info.Type.GetConstructors().Any(c => c.GetParameters()
            .Any(a => string.Equals(a.Name, p.Name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// שמירה אטומית: הכתיבה לקובץ זמני, והחלפה בסיום. קריסה באמצע אינה
    /// משאירה קובץ פגום בשם האמיתי — לכל היותר נשאר הקובץ הקודם.
    /// </summary>
    public static void Save(ScanArchive archive, string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp";

        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            JsonSerializer.Serialize(gzip, archive.Header, Options);
            gzip.WriteByte((byte)'\n');
            JsonSerializer.Serialize(gzip, archive, Options);
        }

        File.Move(temp, full, overwrite: true);
    }

    public static ScanArchive Load(string path)
    {
        using var reader = Open(path);

        var header = ReadHeader(reader);
        if (header.FormatVersion > CurrentFormat)
            throw new InvalidDataException("הקובץ נשמר בגרסה חדשה יותר של התוכנה. עדכנו את RAF כדי לפתוח אותו.");

        return Guard(() => JsonSerializer.Deserialize<ScanArchive>(reader.ReadToEnd(), Options));
    }

    /// <summary>קריאת הכותרת בלבד — לרשימת הסריקות האחרונות.</summary>
    public static ScanArchiveHeader ReadHeader(string path)
    {
        using var reader = Open(path);
        return ReadHeader(reader);
    }

    private static StreamReader Open(string path)
        => new(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), Encoding.UTF8);

    private static ScanArchiveHeader ReadHeader(StreamReader reader)
        => Guard(() => JsonSerializer.Deserialize<ScanArchiveHeader>(reader.ReadLine() ?? "", Options));

    /// <summary>
    /// כל כשל בפענוח — קובץ שאינו gzip, JSON שבור, קובץ שנקטע — הופך להודעה אחת ברורה,
    /// ולא לשגיאה טכנית באנגלית מתוך ספריית הדחיסה.
    /// </summary>
    private static T Guard<T>(Func<T?> read) where T : class
    {
        try
        {
            return read() ?? throw new InvalidDataException();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException("זה אינו קובץ סריקה של RAF, או שהקובץ פגום.");
        }
    }
}
