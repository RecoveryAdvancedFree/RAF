namespace RAF.Core.Model;

/// <summary>דיסק פיזי כפי שזוהה במערכת.</summary>
public sealed class PhysicalDiskInfo
{
    public int DiskNumber { get; init; }
    public string DevicePath => $@"\\.\PhysicalDrive{DiskNumber}";

    public string Model { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string BusType { get; init; } = "";

    public long SizeBytes { get; init; }
    public int LogicalSectorSize { get; init; } = 512;
    public int PhysicalSectorSize { get; init; } = 512;

    public MediaKind Media { get; init; }
    public TrimState Trim { get; init; }
    public bool IsRemovable { get; init; }
    public PartitionScheme Scheme { get; init; }

    /// <summary>האם הצלחנו לפתוח את ההתקן לקריאה גולמית (דורש הרשאות אדמין).</summary>
    public bool RawAccessible { get; init; }

    public List<PartitionInfo> Partitions { get; init; } = new();

    /// <summary>הכונן לא ענה בזמן — סימן לכונן פגום. המידע עליו חלקי.</summary>
    public bool Unresponsive { get; init; }

    /// <summary>הסבר בעברית על הבעיה, כשיש כזו.</summary>
    public string? Problem { get; init; }

    /// <summary>נתיב קובץ התמונה, אם זו תמונת דיסק ולא כונן.</summary>
    public string? ImagePath { get; init; }

    /// <summary>מצב התמונה לפי קובץ המפה שלה — להצגה למשתמש.</summary>
    public string? ImageNote { get; init; }

    /// <summary>בתמונה יש סקטורים שלא נקראו או אזורים שלא הועתקו.</summary>
    public bool ImageDamaged { get; init; }

    /// <summary>שם ידידותי להצגה, לדוגמה: "Samsung SSD 990 PRO 2TB".</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Model) ? $"דיסק {DiskNumber}" : Model.Trim();
}

/// <summary>
/// התקן ש-Windows מרגיש שהוא מחובר אך לא הצליח להפעיל, ולכן אינו מופיע כדיסק.
/// </summary>
public sealed class FailedDevice
{
    /// <summary>השם להצגה.</summary>
    public string Name { get; init; } = "";

    /// <summary>השם כפי שהוא מופיע במנהל ההתקנים, לזיהוי שם.</summary>
    public string WindowsName { get; init; } = "";

    public int ProblemCode { get; init; }
    public string Problem { get; init; } = "";
    public string Advice { get; init; } = "";
}

/// <summary>מחיצה על דיסק פיזי — בין אם ממופה לאות כונן ובין אם לא.</summary>
public sealed class PartitionInfo
{
    public int Index { get; init; }
    public int DiskNumber { get; init; }

    /// <summary>היסט תחילת המחיצה בבתים, מתחילת הדיסק הפיזי.</summary>
    public long OffsetBytes { get; init; }
    public long SizeBytes { get; init; }

    public FileSystemKind FileSystem { get; init; }
    public string TypeName { get; init; } = "";
    public Guid TypeGuid { get; init; }
    public Guid PartitionGuid { get; init; }

    /// <summary>תווית המחיצה — מתוך ה-GPT או מתוך מגזר האתחול.</summary>
    public string Label { get; init; } = "";

    /// <summary>אות הכונן אם קיימת, לדוגמה "C:". ריק אם המחיצה אינה ממופה.</summary>
    public string DriveLetter { get; init; } = "";

    public bool IsBootable { get; init; }
    public bool IsHidden { get; init; }

    /// <summary>נפח פנוי, זמין רק אם המחיצה מחוברת ונגישה למערכת.</summary>
    public long? FreeBytes { get; init; }

    /// <summary>
    /// המחיצה אינה מוכרת ל-Windows (נמחקה / פגומה) אך זוהתה בקריאה גולמית.
    /// מחיצות כאלה הן לרוב יעדי השחזור החשובים ביותר.
    /// </summary>
    public bool IsUnmounted { get; init; }
}

/// <summary>
/// אסטרטגיית הקריאה והשחזור שנבחרה עבור דיסק מסוים.
/// זהו המימוש של דרישה 5 — התאמת סוג השחזור לסוג הדיסק.
/// </summary>
public sealed class RecoveryProfile
{
    /// <summary>גודל בלוק הקריאה בבתים.</summary>
    public int ReadBlockSize { get; init; }

    /// <summary>מספר קריאות מקביליות. ב-HDD תמיד 1 — מקביליות רק מאיטה בגלל תנועת הראש.</summary>
    public int Parallelism { get; init; }

    /// <summary>האם לסרוק בסדר LBA עולה. קריטי ב-HDD, חסר משמעות ב-SSD.</summary>
    public bool SequentialOrder { get; init; }

    /// <summary>הסבר בעברית למשתמש מדוע נבחרה אסטרטגיה זו.</summary>
    public string Rationale { get; init; } = "";

    /// <summary>אזהרה למשתמש, אם סיכויי השחזור נמוכים (למשל TRIM פעיל).</summary>
    public string? Warning { get; init; }

    /// <summary>הערכה גסה של סיכויי ההצלחה, 0–100, להצגה כמחוון בממשק.</summary>
    public int SuccessOutlook { get; init; }

    /// <summary>בניית פרופיל השחזור המתאים לדיסק נתון ולמצב הסריקה שנבחר.</summary>
    public static RecoveryProfile For(PhysicalDiskInfo disk, ScanMode mode)
    {
        bool carving = mode == ScanMode.Advanced;

        return disk.Media switch
        {
            MediaKind.HardDisk => new RecoveryProfile
            {
                // בדיסק מגנטי זמן ה-seek הוא צוואר הבקבוק: בלוקים גדולים, זרם אחד, בסדר עולה.
                ReadBlockSize = 4 * 1024 * 1024,
                Parallelism = 1,
                SequentialOrder = true,
                SuccessOutlook = carving ? 85 : 90,
                Rationale = "דיסק מגנטי מסתובב: הסריקה תתבצע ברצף מתחילת הדיסק ועד סופו, " +
                            "בבלוקים של 4MB ובזרם קריאה יחיד, כדי למנוע תנועות ראש מיותרות. " +
                            "בדיסק מסוג זה נתונים שנמחקו נשארים על הצלחת עד לדריסה — סיכויי השחזור גבוהים.",
            },

            MediaKind.Ssd or MediaKind.NvmeSsd => new RecoveryProfile
            {
                // ב-SSD אין עלות seek, ולכן ריבוי קריאות מקביליות מנצל את התורים הפנימיים של הבקר.
                ReadBlockSize = 1 * 1024 * 1024,
                Parallelism = disk.Media == MediaKind.NvmeSsd ? 8 : 4,
                SequentialOrder = false,
                SuccessOutlook = disk.Trim == TrimState.Enabled ? (carving ? 25 : 40) : 80,
                Rationale = disk.Media == MediaKind.NvmeSsd
                    ? "כונן NVMe: אין עלות גישה אקראית, ולכן הסריקה תרוץ ב-8 ערוצים מקבילים " +
                      "כדי לנצל את התורים הפנימיים של הבקר ולהגיע למהירות מרבית."
                    : "כונן SSD: אין עלות גישה אקראית, ולכן הסריקה תרוץ ב-4 ערוצים מקבילים.",
                Warning = disk.Trim == TrimState.Enabled
                    ? "שימו לב: הכונן הזה מוחק מעצמו את התוכן של קבצים שנמחקו (TRIM), " +
                      "לרוב תוך דקות. שחזור אפשרי בעיקר לקבצים שנמחקו " +
                      "לאחרונה מאוד. מומלץ לכבות את המחשב ולסרוק בהקדם האפשרי."
                    : null,
            },

            MediaKind.UsbFlash or MediaKind.MemoryCard => new RecoveryProfile
            {
                // זיכרון פלאש נייד: בדרך כלל ללא TRIM, אך עם רוחב פס נמוך.
                ReadBlockSize = 512 * 1024,
                Parallelism = 1,
                SequentialOrder = true,
                SuccessOutlook = 88,
                Rationale = disk.Media == MediaKind.MemoryCard
                    ? "כרטיס זיכרון: רוב הכרטיסים אינם מוחקים מעצמם תוכן של קבצים שנמחקו (TRIM), ולכן הוא " +
                      "נשאר על הכרטיס. הסריקה תתבצע ברצף, בקצב שמתאים לכרטיס."
                    : "התקן USB נייד: רוב ההתקנים אינם מוחקים מעצמם תוכן של קבצים שנמחקו (TRIM), ולכן הוא " +
                      "נשאר על ההתקן. הסריקה תתבצע ברצף, בקצב שמתאים להתקן.",
            },

            MediaKind.Image => new RecoveryProfile
            {
                // קובץ על כונן אחר: קריאה רציפה בבלוקים גדולים, והכונן המקורי אינו נקרא כלל.
                ReadBlockSize = 4 * 1024 * 1024,
                Parallelism = 1,
                SequentialOrder = true,
                SuccessOutlook = carving ? 85 : 90,
                Rationale = "תמונת דיסק: הסריקה קוראת את קובץ התמונה בלבד, ברצף ובבלוקים של 4MB. " +
                            "הכונן המקורי אינו נקרא כלל — כך ניתן לסרוק שוב ושוב בלי לסכן כונן חלש.",
                Warning = disk.ImageDamaged
                    ? "בתמונה זו יש אזורים שלא נקראו מהכונן המקורי. קבצים שישבו בהם יחזרו פגומים חלקית."
                    : null,
            },

            MediaKind.Optical => new RecoveryProfile
            {
                ReadBlockSize = 256 * 1024,
                Parallelism = 1,
                SequentialOrder = true,
                SuccessOutlook = 50,
                Rationale = "תקליטור: קריאה רציפה איטית בבלוקים קטנים, עם סבלנות לשגיאות קריאה.",
            },

            _ => new RecoveryProfile
            {
                ReadBlockSize = 1 * 1024 * 1024,
                Parallelism = 2,
                SequentialOrder = true,
                SuccessOutlook = 60,
                Rationale = "סוג ההתקן לא זוהה בוודאות — נבחרה אסטרטגיית סריקה מאוזנת.",
            },
        };
    }
}
