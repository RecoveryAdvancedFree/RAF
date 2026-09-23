using System.Buffers.Binary;
using System.Text;
using RAF.Core.Model;

namespace RAF.Core.FileSystems.Ntfs;

/// <summary>רשומה בודדת מתוך יומן השינויים של NTFS.</summary>
internal readonly record struct UsnEntry(
    long FileRecord,
    ushort FileSequence,
    long ParentRecord,
    ushort ParentSequence,
    long Usn,
    DateTime? Timestamp,
    uint Reason,
    uint Attributes,
    string FileName)
{
    /// <summary>הרשומה מתעדת מחיקה של הקובץ.</summary>
    internal bool IsDelete => (Reason & UsnReason.FileDelete) != 0;

    /// <summary>הרשומה מתארת תיקייה ולא קובץ.</summary>
    internal bool IsDirectory => (Attributes & 0x10) != 0; // FILE_ATTRIBUTE_DIRECTORY
}

/// <summary>דגלי הסיבה ברשומת USN.</summary>
internal static class UsnReason
{
    internal const uint DataOverwrite = 0x00000001;
    internal const uint DataExtend = 0x00000002;
    internal const uint FileCreate = 0x00000100;
    internal const uint FileDelete = 0x00000200;
    internal const uint RenameOldName = 0x00001000;
    internal const uint RenameNewName = 0x00002000;
    internal const uint Close = 0x80000000;
}

/// <summary>
/// קריאת ‎$UsnJrnl — יומן השינויים של NTFS.
///
/// היומן מתעד כל יצירה, שינוי, שינוי-שם ומחיקה של קובץ, ושומר את שמו ואת
/// מזהה תיקיית האב שלו. ערכו בשחזור: הוא מאתר קבצים שרשומת ה-MFT שלהם
/// כבר נדרסה לחלוטין — מצב שבו הסריקה העמוקה אינה מוצאת דבר. היומן אינו
/// מכיל את תוכן הקבצים, אלא את העובדה שהם היו קיימים ואת שמם המדויק.
/// </summary>
internal static class UsnJournalReader
{
    /// <summary>שם הזרם החלופי שבו שמור גוף היומן.</summary>
    internal const string JournalStreamName = "$J";

    /// <summary>מספר הרשומה של תיקיית ‎$Extend, שתחתיה יושב היומן.</summary>
    internal const long ExtendRecord = 11;

    /// <summary>תקרת קריאה מהיומן. יומן גדול מכך ייקרא חלקית.</summary>
    private const long MaxBytes = 1024L * 1024 * 1024;

    private const int BlockSize = 4 * 1024 * 1024;

    /// <summary>
    /// מעבר על רשומות היומן וקריאה לפעולה עבור כל אחת.
    /// מחזיר את מספר הרשומות שנקראו.
    /// </summary>
    internal static long Read(
        NtfsVolume volume, NtfsAttribute journal,
        Action<UsnEntry> onEntry,
        Action<long>? onProgress,
        CancellationToken token)
    {
        if (!journal.IsNonResident || journal.Extents.Count == 0) return 0;

        long entries = 0;
        long logicalOffset = 0;
        int clusterSize = volume.Boot.BytesPerCluster;

        // מאגר עם שוליים: רשומה עלולה להיחתך בגבול הבלוק, ולכן השארית
        // מהבלוק הקודם נגררת קדימה.
        byte[] buffer = new byte[BlockSize];
        int carry = 0;

        foreach (var extent in journal.Extents)
        {
            if (token.IsCancellationRequested) break;

            // תחילת היומן דלילה: רשומות ישנות משוחררות והאזור אינו מוקצה.
            if (extent.IsSparse)
            {
                logicalOffset += extent.ClusterCount * clusterSize;
                carry = 0;
                continue;
            }

            long extentBytes = extent.ClusterCount * clusterSize;

            for (long done = 0; done < extentBytes; done += BlockSize - carry)
            {
                if (token.IsCancellationRequested) break;
                if (logicalOffset > MaxBytes) return entries;

                int want = (int)Math.Min(BlockSize - carry, extentBytes - done);
                if (want <= 0) break;

                long diskOffset = volume.ClusterToOffset(extent.StartCluster) + done;
                int read = volume.ReadRaw(diskOffset, buffer.AsSpan(carry, want));
                if (read <= 0) break;

                int available = carry + read;
                int consumed = Parse(buffer.AsSpan(0, available), onEntry, ref entries);

                // גרירת השארית לתחילת המאגר לקראת הבלוק הבא.
                carry = available - consumed;
                if (carry > 0 && carry < available)
                    Array.Copy(buffer, consumed, buffer, 0, carry);
                else
                    carry = 0;

                logicalOffset += read;
                onProgress?.Invoke(logicalOffset);
            }
        }

        return entries;
    }

    /// <summary>
    /// פענוח רשומות מתוך מאגר. מחזיר כמה בתים נצרכו,
    /// כדי שהשארית תיגרר לבלוק הבא.
    /// </summary>
    internal static int Parse(ReadOnlySpan<byte> data, Action<UsnEntry> onEntry, ref long counter)
    {
        int pos = 0;

        while (pos + 4 <= data.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);

            // אפסים הם ריפוד בין דפי היומן. מדלגים קדימה ביישור של 8 בתים.
            if (length == 0)
            {
                pos += 8;
                continue;
            }

            // אורך בלתי סביר מעיד על אזור פגום; מוותרים על שארית המאגר.
            if (length < 56 || length > 64 * 1024) return data.Length;

            // הרשומה נחתכה בגבול הבלוק — תיקרא בסיבוב הבא.
            if (pos + length > data.Length) return pos;

            var entry = ParseOne(data.Slice(pos, (int)length));
            if (entry is not null)
            {
                counter++;
                onEntry(entry.Value);
            }

            pos += (int)length;
        }

        return pos;
    }

    private static UsnEntry? ParseOne(ReadOnlySpan<byte> record)
    {
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);

        return major switch
        {
            2 => ParseV2(record),
            3 or 4 => ParseV3(record),
            _ => null,
        };
    }

    /// <summary>גרסה 2 — מזהי קבצים בני 8 בתים. הנפוצה ביותר.</summary>
    private static UsnEntry? ParseV2(ReadOnlySpan<byte> r)
    {
        if (r.Length < 60) return null;

        long fileRef = BinaryPrimitives.ReadInt64LittleEndian(r[8..]);
        long parentRef = BinaryPrimitives.ReadInt64LittleEndian(r[16..]);
        long usn = BinaryPrimitives.ReadInt64LittleEndian(r[24..]);
        long time = BinaryPrimitives.ReadInt64LittleEndian(r[32..]);
        uint reason = BinaryPrimitives.ReadUInt32LittleEndian(r[40..]);
        uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(r[52..]);
        ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(r[56..]);
        ushort nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(r[58..]);

        string name = ReadName(r, nameOffset, nameLength);
        if (name.Length == 0) return null;

        return new UsnEntry(
            fileRef & 0x0000FFFFFFFFFFFF, (ushort)((fileRef >> 48) & 0xFFFF),
            parentRef & 0x0000FFFFFFFFFFFF, (ushort)((parentRef >> 48) & 0xFFFF),
            usn, ToDateTime(time), reason, attributes, name);
    }

    /// <summary>גרסה 3 — מזהי קבצים בני 16 בתים, במחיצות גדולות.</summary>
    private static UsnEntry? ParseV3(ReadOnlySpan<byte> r)
    {
        if (r.Length < 80) return null;

        // ב-128 ביט, 64 הביטים הנמוכים נושאים את מספר הרשומה ואת מונה הגרסה.
        long fileRef = BinaryPrimitives.ReadInt64LittleEndian(r[8..]);
        long parentRef = BinaryPrimitives.ReadInt64LittleEndian(r[24..]);
        long usn = BinaryPrimitives.ReadInt64LittleEndian(r[40..]);
        long time = BinaryPrimitives.ReadInt64LittleEndian(r[48..]);
        uint reason = BinaryPrimitives.ReadUInt32LittleEndian(r[56..]);
        uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(r[68..]);
        ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(r[72..]);
        ushort nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(r[74..]);

        string name = ReadName(r, nameOffset, nameLength);
        if (name.Length == 0) return null;

        return new UsnEntry(
            fileRef & 0x0000FFFFFFFFFFFF, (ushort)((fileRef >> 48) & 0xFFFF),
            parentRef & 0x0000FFFFFFFFFFFF, (ushort)((parentRef >> 48) & 0xFFFF),
            usn, ToDateTime(time), reason, attributes, name);
    }

    private static string ReadName(ReadOnlySpan<byte> record, int offset, int byteLength)
    {
        if (byteLength <= 0 || byteLength > 512) return "";
        if (offset < 0 || offset + byteLength > record.Length) return "";

        string name = Encoding.Unicode.GetString(record.Slice(offset, byteLength));

        // שמות מאזור פגום מכילים לעיתים תווי בקרה; אלה נפסלים.
        foreach (char c in name)
            if (char.IsControl(c)) return "";

        return name;
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
