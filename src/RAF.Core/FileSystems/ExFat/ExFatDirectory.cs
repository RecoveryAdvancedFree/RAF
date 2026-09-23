using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.FileSystems.ExFat;

/// <summary>ערך מפוענח מתוך ספריית exFAT.</summary>
internal readonly record struct ExFatEntry(
    string Name,
    long FirstCluster,
    long Size,
    bool IsDirectory,
    bool IsDeleted,
    bool NoFatChain,
    DateTime? Created,
    DateTime? Modified,
    DateTime? Accessed);

/// <summary>
/// פענוח ערכי ספרייה ב-exFAT.
///
/// כל קובץ מתואר בקבוצת ערכים רצופים: ערך קובץ, ערך זרם וערכי שם.
/// מחיקה מכבה את ביט 7 בסוג הערך ומשאירה את שאר התוכן שלם — ולכן,
/// בניגוד ל-FAT הקלאסי, השם המלא של קובץ שנמחק נשמר במלואו.
/// </summary>
internal static class ExFatDirectory
{
    internal const int EntrySize = 32;

    private const byte TypeFile = 0x85;
    private const byte TypeStream = 0xC0;
    private const byte TypeFileName = 0xC1;
    private const byte TypeAllocationBitmap = 0x81;
    private const byte TypeVolumeLabel = 0x83;

    /// <summary>ביט התקפות. כיבויו מסמן ערך שנמחק.</summary>
    private const byte InUseFlag = 0x80;

    private const ushort AttributeDirectory = 0x0010;

    /// <summary>פענוח כל קבוצות הערכים בגוש ספרייה.</summary>
    internal static List<ExFatEntry> Parse(ReadOnlySpan<byte> data)
    {
        var entries = new List<ExFatEntry>();

        for (int at = 0; at + EntrySize <= data.Length; at += EntrySize)
        {
            byte type = data[at];
            if (type == 0x00) continue; // ערך שלא היה בשימוש מעולם

            byte baseType = (byte)(type | InUseFlag);
            if (baseType != TypeFile) continue;

            bool deleted = (type & InUseFlag) == 0;

            var parsed = ParseSet(data, at, deleted, out int consumed);
            if (parsed is not null)
            {
                entries.Add(parsed.Value);
                at += consumed - EntrySize; // הלולאה תוסיף ערך אחד
            }
        }

        return entries;
    }

    /// <summary>איתור ערך מפת ההקצאה בספריית השורש.</summary>
    internal static (long FirstCluster, long Length)? FindAllocationBitmap(ReadOnlySpan<byte> root)
    {
        for (int at = 0; at + EntrySize <= root.Length; at += EntrySize)
        {
            if (root[at] != TypeAllocationBitmap) continue;

            long cluster = BinaryPrimitives.ReadUInt32LittleEndian(root[(at + 20)..]);
            long length = BinaryPrimitives.ReadInt64LittleEndian(root[(at + 24)..]);

            if (cluster >= 2 && length > 0) return (cluster, length);
        }

        return null;
    }

    /// <summary>קריאת תווית אמצעי האחסון מספריית השורש.</summary>
    internal static string FindVolumeLabel(ReadOnlySpan<byte> root)
    {
        for (int at = 0; at + EntrySize <= root.Length; at += EntrySize)
        {
            if (root[at] != TypeVolumeLabel) continue;

            int chars = root[at + 1];
            if (chars is 0 or > 11) continue;

            return Encoding.Unicode.GetString(root.Slice(at + 2, chars * 2));
        }

        return "";
    }

    /// <summary>
    /// פענוח קבוצת ערכים אחת: ערך הקובץ, ערך הזרם שאחריו וערכי השם.
    /// </summary>
    private static ExFatEntry? ParseSet(ReadOnlySpan<byte> data, int at, bool deleted, out int consumed)
    {
        consumed = EntrySize;

        int secondaryCount = data[at + 1];
        if (secondaryCount is < 2 or > 18) return null;

        int total = (secondaryCount + 1) * EntrySize;
        if (at + total > data.Length) return null;
        consumed = total;

        ushort attributes = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 4)..]);
        uint created = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 8)..]);
        uint modified = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 12)..]);
        uint accessed = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 16)..]);

        // ערך הזרם חייב לבוא מיד אחרי ערך הקובץ.
        int streamAt = at + EntrySize;
        byte streamType = (byte)(data[streamAt] | InUseFlag);
        if (streamType != TypeStream) return null;

        byte flags = data[streamAt + 1];
        int nameLength = data[streamAt + 3];
        long firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(data[(streamAt + 20)..]);
        long dataLength = BinaryPrimitives.ReadInt64LittleEndian(data[(streamAt + 24)..]);

        if (nameLength is 0 or > 255) return null;
        if (dataLength < 0) return null;

        // ביט 1 בדגלים מציין שהקובץ רציף ואינו משתמש בטבלת ה-FAT.
        bool noFatChain = (flags & 0x02) != 0;

        // ערכי השם מגיעים אחרי ערך הזרם, 15 תווים בכל אחד.
        var name = new StringBuilder(nameLength);

        for (int i = 2; i <= secondaryCount && name.Length < nameLength; i++)
        {
            int nameAt = at + i * EntrySize;
            byte nameType = (byte)(data[nameAt] | InUseFlag);
            if (nameType != TypeFileName) break;

            int take = Math.Min(15, nameLength - name.Length);
            name.Append(Encoding.Unicode.GetString(data.Slice(nameAt + 2, take * 2)));
        }

        string fileName = name.ToString();
        if (fileName.Length != nameLength) return null;
        if (!IsPlausibleName(fileName)) return null;

        return new ExFatEntry(
            fileName,
            firstCluster,
            dataLength,
            (attributes & AttributeDirectory) != 0,
            deleted,
            noFatChain,
            ToDateTime(created),
            ToDateTime(modified),
            ToDateTime(accessed));
    }

    private static bool IsPlausibleName(string name)
    {
        foreach (char c in name)
        {
            if (char.IsControl(c)) return false;
            if (c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*') return false;
        }

        return name.Trim().Length > 0;
    }

    /// <summary>
    /// המרת חותמת זמן של exFAT. הפורמט זהה ל-FAT הקלאסי, ארוז במילה אחת:
    /// שניות חלקי שתיים, דקות, שעות, יום, חודש ושנה מ-1980.
    /// </summary>
    private static DateTime? ToDateTime(uint stamp)
    {
        if (stamp == 0) return null;

        int seconds = (int)(stamp & 0x1F) * 2;
        int minutes = (int)((stamp >> 5) & 0x3F);
        int hours = (int)((stamp >> 11) & 0x1F);
        int day = (int)((stamp >> 16) & 0x1F);
        int month = (int)((stamp >> 21) & 0x0F);
        int year = 1980 + (int)((stamp >> 25) & 0x7F);

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
