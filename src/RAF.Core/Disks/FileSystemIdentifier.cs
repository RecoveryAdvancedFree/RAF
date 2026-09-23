using System.Text;
using RAF.Core.Model;

namespace RAF.Core.Disks;

/// <summary>
/// זיהוי מערכת הקבצים של מחיצה מתוך מגזר האתחול הגולמי שלה.
/// הזיהוי אינו מסתמך על Windows — כך ניתן לזהות גם מחיצות פגומות או לא מחוברות.
/// </summary>
internal static class FileSystemIdentifier
{
    /// <summary>תוצאת הזיהוי: סוג מערכת הקבצים והתווית אם ניתן היה לחלץ אותה.</summary>
    internal readonly record struct Result(FileSystemKind Kind, string Label);

    /// <summary>
    /// מזהה מערכת קבצים מתוך הבתים הראשונים של המחיצה.
    /// <paramref name="head"/> צריך להכיל לפחות 2048 בתים מתחילת המחיצה.
    /// </summary>
    internal static Result Identify(ReadOnlySpan<byte> head)
    {
        if (head.Length < 512) return new Result(FileSystemKind.Unknown, "");

        // --- NTFS: מזהה OEM בהיסט 3 ---
        if (Matches(head, 3, "NTFS    "))
            return new Result(FileSystemKind.Ntfs, "");

        // --- exFAT: מזהה OEM בהיסט 3 ---
        if (Matches(head, 3, "EXFAT   "))
            return new Result(FileSystemKind.ExFat, "");

        // --- ReFS ---
        if (Matches(head, 3, "ReFS"))
            return new Result(FileSystemKind.ReFS, "");

        // --- FAT32: מזהה בהיסט 82, תווית בהיסט 71 ---
        if (Matches(head, 82, "FAT32   "))
            return new Result(FileSystemKind.Fat32, ReadLabel(head, 71));

        // --- FAT16 / FAT12: מזהה בהיסט 54, תווית בהיסט 43 ---
        if (Matches(head, 54, "FAT16   "))
            return new Result(FileSystemKind.Fat16, ReadLabel(head, 43));
        if (Matches(head, 54, "FAT12   "))
            return new Result(FileSystemKind.Fat12, ReadLabel(head, 43));
        if (Matches(head, 54, "FAT     "))
            return new Result(InferFatWidth(head), ReadLabel(head, 43));

        // --- ext2/3/4: סופר-בלוק בהיסט 1024, חתימה 0xEF53 בהיסט 56 בתוכו ---
        if (head.Length >= 1082 && head[1080] == 0x53 && head[1081] == 0xEF)
            return new Result(FileSystemKind.Ext, "");

        // --- APFS: חתימת NXSB בהיסט 32 ---
        if (Matches(head, 32, "NXSB"))
            return new Result(FileSystemKind.Apfs, "");

        // --- HFS+ / HFSX: חתימה בהיסט 1024 ---
        if (head.Length >= 1026 &&
            ((head[1024] == (byte)'H' && head[1025] == (byte)'+') ||
             (head[1024] == (byte)'H' && head[1025] == (byte)'X')))
            return new Result(FileSystemKind.Hfs, "");

        // מגזר אתחול תקין אך ללא חתימה מוכרת — כנראה מערכת קבצים פגומה.
        bool hasBootSignature = head[510] == 0x55 && head[511] == 0xAA;
        return new Result(hasBootSignature ? FileSystemKind.Raw : FileSystemKind.Unknown, "");
    }

    /// <summary>
    /// כשמגזר האתחול מצהיר רק "FAT", רוחב הטבלה נגזר ממספר האשכולות.
    /// מתחת ל-4085 אשכולות זה FAT12, ומתחת ל-65525 זה FAT16.
    /// </summary>
    private static FileSystemKind InferFatWidth(ReadOnlySpan<byte> bs)
    {
        int bytesPerSector = bs[11] | (bs[12] << 8);
        int sectorsPerCluster = bs[13];
        int reservedSectors = bs[14] | (bs[15] << 8);
        int fatCount = bs[16];
        int rootEntries = bs[17] | (bs[18] << 8);
        int totalSectors16 = bs[19] | (bs[20] << 8);
        int sectorsPerFat = bs[22] | (bs[23] << 8);
        uint totalSectors32 = (uint)(bs[32] | (bs[33] << 8) | (bs[34] << 16) | (bs[35] << 24));

        if (bytesPerSector == 0 || sectorsPerCluster == 0) return FileSystemKind.Fat16;

        long totalSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;
        long rootDirSectors = (rootEntries * 32 + bytesPerSector - 1) / bytesPerSector;
        long dataSectors = totalSectors - (reservedSectors + fatCount * (long)sectorsPerFat + rootDirSectors);
        long clusters = dataSectors / sectorsPerCluster;

        return clusters < 4085 ? FileSystemKind.Fat12 : FileSystemKind.Fat16;
    }

    /// <summary>קריאת תווית FAT בת 11 תווים מהיסט נתון.</summary>
    private static string ReadLabel(ReadOnlySpan<byte> bs, int offset)
    {
        if (offset + 11 > bs.Length) return "";
        string label = Encoding.ASCII.GetString(bs.Slice(offset, 11)).Trim();
        return label is "NO NAME" ? "" : label;
    }

    private static bool Matches(ReadOnlySpan<byte> data, int offset, string ascii)
    {
        if (offset + ascii.Length > data.Length) return false;
        for (int i = 0; i < ascii.Length; i++)
            if (data[offset + i] != (byte)ascii[i]) return false;
        return true;
    }

    /// <summary>שם מערכת הקבצים להצגה בממשק.</summary>
    internal static string DisplayName(FileSystemKind kind) => kind switch
    {
        FileSystemKind.Ntfs => "NTFS",
        FileSystemKind.ExFat => "exFAT",
        FileSystemKind.Fat32 => "FAT32",
        FileSystemKind.Fat16 => "FAT16",
        FileSystemKind.Fat12 => "FAT12",
        FileSystemKind.ReFS => "ReFS",
        FileSystemKind.Ext => "ext2/3/4",
        FileSystemKind.Apfs => "APFS",
        FileSystemKind.Hfs => "HFS+",
        FileSystemKind.Raw => "לא מזוהה",
        _ => "לא ידוע",
    };

    /// <summary>האם מערכת הקבצים נתמכת לשיחזור מבוסס מטא-דאטה בגרסה זו.</summary>
    internal static bool IsSupported(FileSystemKind kind) => kind
        is FileSystemKind.Ntfs or FileSystemKind.ExFat
        or FileSystemKind.Fat32 or FileSystemKind.Fat16 or FileSystemKind.Fat12;
}
