namespace RAF.Core.Model;

/// <summary>מקור הגילוי של קובץ — קובע את מידת האמון בנתונים שלו.</summary>
public enum DiscoverySource
{
    /// <summary>רשומת MFT פעילה בטבלה — המקור האמין ביותר.</summary>
    MftActive = 0,

    /// <summary>רשומת MFT יתומה שנמצאה בסריקת הדיסק — שמות ונתיבים חלקיים.</summary>
    MftOrphan,

    /// <summary>יומן השינויים של NTFS — מעיד שהקובץ היה קיים, לרוב ללא תוכן.</summary>
    UsnJournal,

    /// <summary>יומן הטרנזקציות $LogFile — שרידי רשומות שנדרסו.</summary>
    LogFile,

    /// <summary>זוהה לפי חתימת HEX בסריקה מתקדמת — ללא שם ונתיב מקוריים.</summary>
    Carving,
}

/// <summary>הערכת סיכויי השחזור של קובץ מסוים.</summary>
public enum RecoveryQuality
{
    /// <summary>כל האשכולות פנויים — הקובץ צפוי להשתחזר במלואו.</summary>
    Excellent = 0,

    /// <summary>חלק מהאשכולות עלולים להיות תפוסים — ייתכן נזק חלקי.</summary>
    Good,

    /// <summary>חלק ניכר מהנתונים נדרס — הקובץ ישוחזר פגום.</summary>
    Poor,

    /// <summary>אין מידע על מיקום התוכן — לא ניתן לשחזר את הקובץ עצמו.</summary>
    Unrecoverable,
}

/// <summary>תוצאת אימות התוכן מול הדיסק בפועל.</summary>
public enum ContentCheck
{
    /// <summary>התוכן לא נבדק.</summary>
    NotChecked = 0,

    /// <summary>נדגם תוכן אמיתי — הנתונים קיימים על הדיסק.</summary>
    HasData,

    /// <summary>כל הדגימות החזירו אפסים — הנתונים אינם קיימים עוד.</summary>
    Empty,

    /// <summary>לא ניתן היה לקרוא את האזור לצורך הדגימה.</summary>
    Unreadable,
}

/// <summary>רצף אשכולות רציף על המחיצה — מרכיב את תוכן הקובץ.</summary>
public readonly record struct DataExtent(long StartCluster, long ClusterCount, bool IsSparse)
{
    public override string ToString() =>
        IsSparse ? $"[דליל × {ClusterCount}]" : $"[{StartCluster}+{ClusterCount}]";
}

/// <summary>קובץ שנמצא בסריקה ומועמד לשחזור.</summary>
public sealed class RecoveredFile
{
    /// <summary>מזהה ייחודי בתוך הסריקה. ב-NTFS זהו מספר רשומת ה-MFT.</summary>
    public long Id { get; init; }

    /// <summary>מזהה רשומת ההורה, לשחזור עץ התיקיות.</summary>
    public long ParentId { get; init; } = -1;

    public string Name { get; set; } = "";

    /// <summary>הנתיב המלא כפי ששוחזר. ריק כשלא ניתן היה לשחזר נתיב.</summary>
    public string Path { get; set; } = "";

    public long Size { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsDeleted { get; init; }

    public DateTime? Created { get; init; }
    public DateTime? Modified { get; init; }
    public DateTime? Accessed { get; init; }

    /// <summary>
    /// מתי הקובץ נשלח לסל המחזור, כשהשם המקורי הוחזר מתוך הסל (ראו RecycleBinNames).
    /// null — הקובץ לא נמחק דרך הסל, או שהפרטים לא שרדו.
    /// </summary>
    public DateTime? RecycledAt { get; set; }

    public DiscoverySource Source { get; init; }
    public RecoveryQuality Quality { get; set; } = RecoveryQuality.Good;

    /// <summary>הסבר בעברית מדוע זהו הדירוג. מוצג למשתמש לצד הקובץ.</summary>
    public string QualityReason { get; set; } = "";

    /// <summary>
    /// האם נדגם תוכן אמיתי מהדיסק. דירוג שאינו מאומת אינו יכול להבטיח שחזור:
    /// בכונן SSD עם TRIM רשומת המטא-דאטה שורדת גם אחרי שהנתונים נמחקו פיזית.
    /// </summary>
    public ContentCheck Content { get; set; } = ContentCheck.NotChecked;

    /// <summary>מיקום תוכן הקובץ על המחיצה. ריק כשהתוכן שמור בתוך הרשומה עצמה.</summary>
    public List<DataExtent> Extents { get; init; } = new();

    /// <summary>תוכן הקובץ כשהוא קטן מספיק להישמר בתוך רשומת ה-MFT עצמה.</summary>
    public byte[]? ResidentData { get; init; }

    /// <summary>האם התוכן דחוס ב-NTFS ודורש פריסה בעת השחזור.</summary>
    public bool IsCompressed { get; init; }

    /// <summary>גודל יחידת הדחיסה באשכולות, כשהתוכן דחוס.</summary>
    public int CompressionUnitClusters { get; init; }

    /// <summary>
    /// השם שוחזר חלקית. ב-FAT, מחיקת קובץ דורסת את האות הראשונה של שם 8.3
    /// ואינה ניתנת לשחזור — התוכן שלם, אך אות אחת בשם אבדה.
    /// </summary>
    public bool NameIsPartial { get; init; }

    /// <summary>סיומת הקובץ באותיות קטנות, ללא נקודה.</summary>
    public string Extension
    {
        get
        {
            int dot = Name.LastIndexOf('.');
            return dot >= 0 && dot < Name.Length - 1
                ? Name[(dot + 1)..].ToLowerInvariant()
                : "";
        }
    }

    /// <summary>האם יש בידינו מספיק מידע כדי להוציא את תוכן הקובץ בפועל.</summary>
    public bool HasContent => ResidentData is not null || Extents.Count > 0;

    /// <summary>
    /// האם יש טעם להציע את הקובץ לשחזור.
    /// קובץ שנבדק ונמצא ריק לא יישוחזר — הוא רק ייצור קובץ אפסים מטעה.
    /// </summary>
    public bool IsWorthRecovering =>
        HasContent && Content != ContentCheck.Empty && Quality != RecoveryQuality.Unrecoverable;
}

/// <summary>דיווח התקדמות שוטף מהסורק לממשק.</summary>
public sealed class ScanProgress
{
    /// <summary>תיאור השלב הנוכחי בעברית.</summary>
    public string Stage { get; init; } = "";

    /// <summary>אחוז התקדמות 0–100, או null כשלא ניתן להעריך.</summary>
    public double? Percent { get; init; }

    public long FilesFound { get; init; }
    public long BytesProcessed { get; init; }
    public long BytesTotal { get; init; }

    /// <summary>קצב קריאה נוכחי בבתים לשנייה.</summary>
    public double BytesPerSecond { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>מפת הסקטורים של מעבר שעובר סקטור אחרי סקטור; null — בשלבים אחרים.</summary>
    public SectorMap? Map { get; init; }
}
