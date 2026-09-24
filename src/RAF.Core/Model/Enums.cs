namespace RAF.Core.Model;

/// <summary>סוג אמצעי האחסון הפיזי. קובע את אסטרטגיית השחזור.</summary>
public enum MediaKind
{
    Unknown = 0,
    HardDisk,       // דיסק מגנטי מסתובב
    Ssd,            // SSD בחיבור SATA
    NvmeSsd,        // SSD בחיבור NVMe
    UsbFlash,       // התקן USB נייד
    MemoryCard,     // כרטיס SD / MMC
    Optical,        // תקליטור
    Virtual,        // דיסק וירטואלי
    NetworkOrRam,   // כונן רשת או RAM Disk
    Image,          // קובץ תמונת דיסק שנפתח בתוכנה
}

/// <summary>מערכת הקבצים שזוהתה במחיצה.</summary>
public enum FileSystemKind
{
    Unknown = 0,
    Raw,            // ללא מערכת קבצים מזוהה / פגומה
    Ntfs,
    ExFat,
    Fat32,
    Fat16,
    Fat12,
    ReFS,
    Ext,            // ext2/3/4 — זוהה אך אינו נתמך בגרסה זו
    Apfs,           // זוהה אך אינו נתמך בגרסה זו
    Hfs,            // זוהה אך אינו נתמך בגרסה זו
    BitLocker,      // מוצפנת — נקראת רק דרך Windows אחרי פתיחת הנעילה
}

/// <summary>סכמת טבלת המחיצות של הדיסק.</summary>
public enum PartitionScheme
{
    Unknown = 0,
    Mbr,
    Gpt,
    SuperFloppy,    // ללא טבלת מחיצות — מערכת קבצים ישירות על ההתקן
}

/// <summary>שלוש רמות הסריקה שהמשתמש בוחר מהן.</summary>
public enum ScanMode
{
    /// <summary>סריקה מהירה — קריאת רשומות מטא-דאטה בלבד (MFT / טבלת FAT). שניות עד דקות.</summary>
    Quick = 1,

    /// <summary>סריקה עמוקה — סריקת כל רשומות המטא-דאטה בדיסק כולו, כולל יתומות ושרידי ספריות.</summary>
    Deep = 2,

    /// <summary>
    /// סריקה מתקדמת — carving גולמי של כל הדיסק לפי חתימות HEX, ללא תלות במערכת הקבצים.
    /// הקבצים משוחזרים ללא נתיבי תיקייה וללא שמות מקוריים, אך זו הדרך העמוקה ביותר.
    /// </summary>
    Advanced = 3,
}

/// <summary>מצב תמיכת TRIM — משפיע קריטית על סיכויי השחזור ב-SSD.</summary>
public enum TrimState
{
    Unknown = 0,
    NotSupported,   // אין TRIM — נתונים מחוקים בד"כ נשארים על ההתקן
    Enabled,        // TRIM פעיל — נתונים מחוקים עלולים להימחק פיזית
}
