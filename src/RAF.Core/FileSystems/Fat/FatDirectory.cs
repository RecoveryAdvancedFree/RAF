using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.FileSystems.Fat;

/// <summary>ערך מפוענח מתוך ספריית FAT.</summary>
internal readonly record struct FatEntry(
    string Name,
    string ShortName,
    long FirstCluster,
    long Size,
    bool IsDirectory,
    bool IsDeleted,
    DateTime? Created,
    DateTime? Modified,
    DateTime? Accessed,
    int OffsetInDirectory,
    bool NameIsPartial)
{
    /// <summary>ערכי הנקודה של ספרייה עצמה ושל ההורה, שאינם קבצים.</summary>
    internal bool IsDotEntry => ShortName is "." or "..";
}

/// <summary>
/// פענוח ערכי ספרייה ב-FAT, כולל הרכבת שמות ארוכים.
///
/// כל ערך תופס 32 בתים. שם ארוך נשמר בערכים נוספים שקודמים לערך ה-8.3
/// ומסודרים בסדר הפוך. כאשר קובץ נמחק, הבית הראשון של כל ערכיו מוחלף
/// ב-0xE5 — ולכן האות הראשונה של השם הקצר אובדת, אך השם הארוך שורד.
/// </summary>
internal static class FatDirectory
{
    internal const int EntrySize = 32;

    private const byte FreeAndEnd = 0x00;
    private const byte Deleted = 0xE5;

    private const byte AttrReadOnly = 0x01;
    private const byte AttrHidden = 0x02;
    private const byte AttrSystem = 0x04;
    private const byte AttrVolumeId = 0x08;
    private const byte AttrDirectory = 0x10;
    private const byte AttrLongName = 0x0F;

    /// <summary>
    /// פענוח כל הערכים בגוש ספרייה.
    /// </summary>
    /// <param name="data">תוכן הספרייה, כפי שנקרא מהאשכולות שלה.</param>
    /// <param name="stopAtEnd">
    /// האם לעצור בערך ריק. בספרייה חיה זהו סוף הרשימה, אך בסריקה גולמית
    /// של אשכולות יש להמשיך ולסרוק את כל הגוש.
    /// </param>
    internal static List<FatEntry> Parse(ReadOnlySpan<byte> data, bool stopAtEnd = true)
    {
        var entries = new List<FatEntry>();
        var nameParts = new List<string>();

        for (int at = 0; at + EntrySize <= data.Length; at += EntrySize)
        {
            var entry = data.Slice(at, EntrySize);
            byte first = entry[0];

            if (first == FreeAndEnd)
            {
                nameParts.Clear();
                if (stopAtEnd) break;
                continue;
            }

            byte attributes = entry[11];

            // ערך שם ארוך: אוסף מקטע ומחכה לערך ה-8.3 שאחריו.
            if ((attributes & AttrLongName) == AttrLongName)
            {
                if (IsValidLongNameEntry(entry))
                {
                    string part = ReadLongNamePart(entry);
                    if (part.Length > 0) nameParts.Add(part);
                }
                else
                {
                    // ערך שאינו עומד במפרט אינו חלק משם אמיתי, ואוסף
                    // המקטעים שנצבר עד כה אינו אמין עוד.
                    nameParts.Clear();
                }

                continue;
            }

            // תווית אמצעי האחסון אינה קובץ.
            if ((attributes & AttrVolumeId) != 0)
            {
                nameParts.Clear();
                continue;
            }

            var parsed = ParseShortEntry(entry, nameParts, at, first == Deleted);
            nameParts.Clear();

            if (parsed is not null) entries.Add(parsed.Value);
        }

        return entries;
    }

    private static FatEntry? ParseShortEntry(
        ReadOnlySpan<byte> entry, List<string> nameParts, int offset, bool deleted)
    {
        string shortName = ReadShortName(entry, deleted);
        if (shortName.Length == 0) return null;

        // שדות שהמפרט מחייב את ערכם. הם מסננים נתונים אקראיים
        // שבמקרה נראים כמו ערך ספרייה.
        if ((entry[11] & 0xC0) != 0) return null;   // ביטי תכונות שאינם מוגדרים
        if ((entry[12] & ~0x18) != 0) return null;  // שדה שמור, פרט לדגלי אותיות קטנות

        // מקטעי השם הארוך נשמרים בסדר הפוך על הדיסק.
        string longName = "";
        if (nameParts.Count > 0)
        {
            var builder = new StringBuilder();
            for (int i = nameParts.Count - 1; i >= 0; i--) builder.Append(nameParts[i]);
            longName = builder.ToString().TrimEnd('￿', '\0');
        }

        byte attributes = entry[11];
        byte caseFlags = entry[12];

        long clusterHigh = BinaryPrimitives.ReadUInt16LittleEndian(entry[20..]);
        long clusterLow = BinaryPrimitives.ReadUInt16LittleEndian(entry[26..]);
        long firstCluster = (clusterHigh << 16) | clusterLow;

        long size = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]);

        string name = longName.Length > 0 ? longName : ApplyCaseFlags(shortName, caseFlags);
        if (!IsPlausibleName(name)) return null;

        // אשכול התחלה הוא אפס (קובץ ריק) או 2 ומעלה; 1 אינו חוקי.
        if (firstCluster == 1) return null;

        // ספרייה אינה נושאת גודל.
        bool isDirectory = (attributes & AttrDirectory) != 0;
        if (isDirectory && size != 0) return null;

        var modified = ToDateTime(BinaryPrimitives.ReadUInt16LittleEndian(entry[24..]),
                                  BinaryPrimitives.ReadUInt16LittleEndian(entry[22..]));

        // חותמת זמן תקינה היא אינדיקציה חזקה לכך שזהו ערך אמיתי.
        if (modified is null) return null;

        // שם 8.3 של קובץ שנמחק איבד את אות ההתחלה שלו לבלתי-הפיך.
        // שם ארוך נשמר בערכים נפרדים ולכן שורד במלואו.
        bool namePartial = deleted && longName.Length == 0;

        return new FatEntry(
            name,
            shortName,
            firstCluster,
            size,
            isDirectory,
            deleted,
            ToDateTime(BinaryPrimitives.ReadUInt16LittleEndian(entry[16..]),
                       BinaryPrimitives.ReadUInt16LittleEndian(entry[14..])),
            modified,
            ToDateTime(BinaryPrimitives.ReadUInt16LittleEndian(entry[18..]), 0),
            offset,
            namePartial);
    }

    /// <summary>
    /// בדיקת תקינות ערך שם ארוך לפי המפרט.
    ///
    /// בלי הבדיקה הזו כל 32 בתים אקראיים שהניבל שלהם במקרה 0x0F מתפרשים
    /// כמקטע שם, ובסריקה גולמית הדבר מייצר שמות קבצים שמעולם לא היו.
    /// שני השדות האלה חייבים להיות אפס במפרט, וזה מסנן כמעט הכול.
    /// </summary>
    private static bool IsValidLongNameEntry(ReadOnlySpan<byte> entry)
    {
        // שדה הסוג חייב להיות אפס.
        if (entry[12] != 0) return false;

        // שדה אשכול ההתחלה אינו בשימוש בערך שם ואמור להיות אפס.
        if (BinaryPrimitives.ReadUInt16LittleEndian(entry[26..]) != 0) return false;

        // מספר סידורי: 1 עד 20, עם ביט 0x40 בערך האחרון ברצף.
        int ordinal = entry[0] & 0x3F;
        if (entry[0] != Deleted && ordinal is < 1 or > 20) return false;

        return true;
    }

    /// <summary>חילוץ 13 תווי השם מערך שם ארוך.</summary>
    private static string ReadLongNamePart(ReadOnlySpan<byte> entry)
    {
        Span<char> chars = stackalloc char[13];
        int count = 0;

        // התווים מפוזרים בשלושה מקטעים בתוך הערך.
        count += CopyChars(entry[1..11], chars[count..]);
        count += CopyChars(entry[14..26], chars[count..]);
        count += CopyChars(entry[28..32], chars[count..]);

        return new string(chars[..count]);
    }

    private static int CopyChars(ReadOnlySpan<byte> source, Span<char> destination)
    {
        int written = 0;

        for (int i = 0; i + 1 < source.Length && written < destination.Length; i += 2)
        {
            char c = (char)BinaryPrimitives.ReadUInt16LittleEndian(source[i..]);

            // אפס מסיים את השם, ו-0xFFFF הוא ריפוד.
            if (c == '\0' || c == '￿') break;
            destination[written++] = c;
        }

        return written;
    }

    /// <summary>
    /// קריאת שם 8.3. בקובץ שנמחק האות הראשונה הוחלפה בסימון המחיקה
    /// ואינה ניתנת לשחזור, ולכן מוצג במקומה קו תחתון.
    /// </summary>
    private static string ReadShortName(ReadOnlySpan<byte> entry, bool deleted)
    {
        Span<byte> raw = stackalloc byte[11];
        entry[..11].CopyTo(raw);

        if (deleted) raw[0] = (byte)'_';

        string basePart = Encoding.ASCII.GetString(raw[..8]).TrimEnd();
        string extension = Encoding.ASCII.GetString(raw[8..11]).TrimEnd();

        if (basePart.Length == 0) return "";
        if (basePart is "." or "..") return basePart;

        return extension.Length > 0 ? basePart + "." + extension : basePart;
    }

    /// <summary>Windows מסמן בשדה שמור שהשם נשמר באותיות קטנות.</summary>
    private static string ApplyCaseFlags(string shortName, byte flags)
    {
        int dot = shortName.IndexOf('.');

        // ערכי "." ו-".." מתחילים בנקודה ואינם שם עם סיומת.
        // פיצול שלהם היה מוחק אותם לחלוטין.
        if (dot <= 0) return shortName;

        string basePart = dot < 0 ? shortName : shortName[..dot];
        string extension = dot < 0 ? "" : shortName[(dot + 1)..];

        if ((flags & 0x08) != 0) basePart = basePart.ToLowerInvariant();
        if ((flags & 0x10) != 0) extension = extension.ToLowerInvariant();

        return extension.Length > 0 ? basePart + "." + extension : basePart;
    }

    private static bool IsPlausibleName(string name)
    {
        if (name.Length is 0 or > 255) return false;

        foreach (char c in name)
        {
            if (char.IsControl(c)) return false;
            if (c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*') return false;
        }

        return name.Trim().Length > 0;
    }

    /// <summary>
    /// המרת תאריך ושעה בפורמט FAT. התאריך נספר משנת 1980,
    /// והשניות נשמרות ברזולוציה של שתי שניות.
    /// </summary>
    private static DateTime? ToDateTime(int date, int time)
    {
        if (date == 0) return null;

        int day = date & 0x1F;
        int month = (date >> 5) & 0x0F;
        int year = 1980 + ((date >> 9) & 0x7F);

        int seconds = (time & 0x1F) * 2;
        int minutes = (time >> 5) & 0x3F;
        int hours = (time >> 11) & 0x1F;

        if (day is < 1 or > 31 || month is < 1 or > 12) return null;
        if (hours > 23 || minutes > 59 || seconds > 59) return null;

        try
        {
            return new DateTime(year, month, day, hours, minutes, seconds, DateTimeKind.Local);
        }
        catch
        {
            return null;
        }
    }
}
