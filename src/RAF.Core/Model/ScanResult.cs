namespace RAF.Core.Model;

/// <summary>תוצאת סריקה שהושלמה או בוטלה.</summary>
public sealed class ScanResult
{
    public List<RecoveredFile> Files { get; init; } = new();

    public ScanMode Mode { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Cancelled { get; init; }

    /// <summary>שם מערכת הקבצים שנסרקה בפועל.</summary>
    public string FileSystem { get; init; } = "";

    /// <summary>מספר רשומות המטא-דאטה שנבדקו.</summary>
    public long RecordsExamined { get; init; }

    /// <summary>סך הבתים שנקראו מהדיסק.</summary>
    public long BytesRead { get; init; }

    /// <summary>אזהרות שנאספו במהלך הסריקה, בעברית.</summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>
    /// סריקה מתקדמת שנעצרה באמצע (השהיה, או נקודת ביניים): מאיפה להמשיך, ועם אילו
    /// אפשרויות. null — הסריקה הושלמה, או שאינה ניתנת להמשך.
    /// </summary>
    public ScanResume? Resume { get; init; }

    /// <summary>הסריקה נעצרה כי הכונן נותק — לא בגלל המשתמש.</summary>
    public bool Disconnected { get; init; }

    internal ScanResult WithDisconnected() => new()
    {
        Files = Files, Mode = Mode, Duration = Duration, Cancelled = Cancelled, FileSystem = FileSystem,
        RecordsExamined = RecordsExamined, BytesRead = BytesRead, Warnings = Warnings, Resume = Resume,
        Disconnected = true,
    };

    public int DeletedCount => Files.Count(f => f.IsDeleted);
    public int RecoverableCount => Files.Count(f => f.HasContent && !f.IsDirectory);
}

/// <summary>
/// נקודת המשך של סריקה מתקדמת — רעיון מהתוכנה "משיב". נשמרת עם הסריקה, כך
/// שאפשר להמשיך גם אחרי סגירת התוכנה, מאותו סקטור ובאותן אפשרויות.
/// </summary>
public sealed class ScanResume
{
    /// <summary>הסקטור הבא שטרם נבדק, בבתים מתחילת המחיצה.</summary>
    public long Offset { get; init; }

    /// <summary>גבול הקובץ האחרון שנמצא: עד כאן גוף של קובץ, ואין לחפש בו קבצים חדשים.</summary>
    public long NextAllowedStart { get; init; }

    /// <summary>כמה מהמחיצה כבר נסרק, באחוזים — להצגה.</summary>
    public double Percent { get; init; }

    public bool FreeSpaceOnly { get; init; }

    /// <summary>סוגי הקבצים שנבחרו (קטגוריות). null — כל הסוגים.</summary>
    public List<string>? Types { get; init; }
}
