using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.FileSystems.Ntfs;

/// <summary>שם קובץ שחולץ מתוך שריד רשומה ב-‎$LogFile.</summary>
internal readonly record struct LogNameEntry(
    string Name,
    long ParentRecord,
    ushort ParentSequence,
    DateTime? Created,
    DateTime? Modified,
    long RealSize,
    byte Namespace,
    bool IsDirectory);

/// <summary>
/// חילוץ שמות קבצים מתוך ‎$LogFile — יומן הטרנזקציות של NTFS.
///
/// היומן שומר עותקים של רשומות מטא-דאטה לפני ואחרי כל שינוי, כדי לאפשר
/// שיחזור עקביות לאחר קריסה. פענוח מלא של מבנה הטרנזקציות הוא פרויקט
/// בפני עצמו; הגישה כאן היא זו שנהוגה בכלי פורנזיקה — סריקת דפי היומן
/// אחר מבני ‎$FILE_NAME שנותרו בהם.
///
/// הערך: היומן מכיל שמות ותיקיות אב של קבצים שרשומת ה-MFT שלהם נדרסה
/// מזמן, ולכן הוא מוצא מה שהסריקה העמוקה כבר אינה יכולה למצוא.
/// </summary>
internal static class LogFileReader
{
    /// <summary>מספר הרשומה של ‎$LogFile ב-MFT.</summary>
    internal const long LogFileRecord = 2;

    /// <summary>גודל דף ביומן. קבוע בכל גרסאות NTFS.</summary>
    private const int PageSize = 4096;

    private const uint SignatureRcrd = 0x44524352; // "RCRD"
    private const uint SignatureRstr = 0x52545352; // "RSTR"
    private const uint SignatureChkd = 0x444B4843; // "CHKD"

    /// <summary>גודל גוף ‎$FILE_NAME ללא השם עצמו.</summary>
    private const int FileNameHeaderSize = 66;

    /// <summary>
    /// מעבר על היומן וחילוץ כל שמות הקבצים שנמצאו בו.
    /// מחזיר את מספר השמות שחולצו.
    /// </summary>
    internal static long Read(
        NtfsVolume volume, NtfsAttribute logData,
        Action<LogNameEntry> onEntry,
        Action<long>? onProgress,
        CancellationToken token)
    {
        if (!logData.IsNonResident || logData.Extents.Count == 0) return 0;

        long found = 0;
        long processed = 0;
        int clusterSize = volume.Boot.BytesPerCluster;
        int sectorSize = volume.Boot.BytesPerSector;

        byte[] page = new byte[PageSize];

        foreach (var extent in logData.Extents)
        {
            if (token.IsCancellationRequested) break;
            if (extent.IsSparse)
            {
                processed += extent.ClusterCount * clusterSize;
                continue;
            }

            long extentBytes = extent.ClusterCount * clusterSize;
            long baseOffset = volume.ClusterToOffset(extent.StartCluster);

            for (long at = 0; at + PageSize <= extentBytes; at += PageSize)
            {
                if (token.IsCancellationRequested) break;

                int read = volume.ReadRaw(baseOffset + at, page);
                processed += PageSize;

                if (read < PageSize) continue;

                uint signature = BinaryPrimitives.ReadUInt32LittleEndian(page);

                // דפי נתונים ביומן נושאים חתימת RCRD; השאר הם אזורי אתחול.
                if (signature is not (SignatureRcrd or SignatureRstr or SignatureChkd))
                    continue;

                // גם דפי היומן מוגנים במונה סקטורים, שיש להחזיר לפני הפענוח.
                var (usOffset, usCount) = NtfsFixup.ReadHeader(page);
                if (usCount > 0) NtfsFixup.Apply(page, sectorSize, usOffset, usCount);

                found += ScanPage(page, onEntry);
                onProgress?.Invoke(processed);
            }
        }

        return found;
    }

    /// <summary>
    /// סריקת דף אחד אחר מבני ‎$FILE_NAME.
    /// הזיהוי מסתמך על צירוף של מספר שדות, כדי לצמצם התאמות שווא.
    /// </summary>
    internal static int ScanPage(ReadOnlySpan<byte> page, Action<LogNameEntry> onEntry)
    {
        int found = 0;

        // מבני התכונות ביומן מיושרים ל-8 בתים.
        for (int at = 0; at + FileNameHeaderSize + 2 <= page.Length; at += 8)
        {
            var entry = TryParseFileName(page[at..]);
            if (entry is null) continue;

            onEntry(entry.Value);
            found++;
        }

        return found;
    }

    /// <summary>
    /// ניסיון לפענח מבנה ‎$FILE_NAME בהיסט נתון.
    /// מחזיר null כשהנתונים אינם עומדים בכל תנאי הסבירות.
    /// </summary>
    internal static LogNameEntry? TryParseFileName(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileNameHeaderSize) return null;

        byte nameChars = data[64];
        byte nameSpace = data[65];

        // מרחב שמות חוקי הוא 0–3, ואורך שם סביר.
        if (nameSpace > 3) return null;
        if (nameChars is 0 or > 128) return null;

        int nameBytes = nameChars * 2;
        if (FileNameHeaderSize + nameBytes > data.Length) return null;

        long rawParent = BinaryPrimitives.ReadInt64LittleEndian(data);
        long parentRecord = rawParent & 0x0000FFFFFFFFFFFF;
        ushort parentSequence = (ushort)((rawParent >> 48) & 0xFFFF);

        // הפניית הורה חייבת להיות סבירה: לא אפס, ולא ערך אסטרונומי.
        if (parentRecord is <= 0 or > 0x0000FFFFFFFF) return null;
        if (parentSequence == 0) return null;

        // שתי חותמות זמן תקינות הן האינדיקציה החזקה ביותר שזהו מבנה אמיתי.
        DateTime? created = ToDateTime(BinaryPrimitives.ReadInt64LittleEndian(data[8..]));
        DateTime? modified = ToDateTime(BinaryPrimitives.ReadInt64LittleEndian(data[16..]));
        if (created is null || modified is null) return null;

        long allocated = BinaryPrimitives.ReadInt64LittleEndian(data[40..]);
        long realSize = BinaryPrimitives.ReadInt64LittleEndian(data[48..]);
        if (realSize < 0 || allocated < 0) return null;
        if (realSize > allocated && allocated > 0) return null;

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[56..]);

        string name = Encoding.Unicode.GetString(data.Slice(FileNameHeaderSize, nameBytes));
        if (!IsPlausibleName(name)) return null;

        return new LogNameEntry(
            name, parentRecord, parentSequence, created, modified, realSize, nameSpace,
            IsDirectory: (flags & 0x10000000) != 0);
    }

    /// <summary>שם קובץ אמיתי אינו מכיל תווי בקרה או תווים אסורים ב-Windows.</summary>
    private static bool IsPlausibleName(string name)
    {
        if (name.Length == 0) return false;

        foreach (char c in name)
        {
            if (char.IsControl(c)) return false;
            if (c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*') return false;
            if (c == '�') return false; // תו החלפה — קידוד שבור
        }

        // שם שכולו רווחים או נקודות אינו שם חוקי.
        return name.Trim().Trim('.').Length > 0;
    }

    private static DateTime? ToDateTime(long fileTime)
    {
        if (fileTime <= 0) return null;
        try
        {
            var value = DateTime.FromFileTimeUtc(fileTime);
            return value.Year is >= 1980 and <= 2200 ? value.ToLocalTime() : null;
        }
        catch
        {
            return null;
        }
    }
}
