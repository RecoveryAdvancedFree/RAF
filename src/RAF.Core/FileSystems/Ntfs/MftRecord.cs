using System.Buffers.Binary;
using System.Text;
using RAF.Core.Model;

namespace RAF.Core.FileSystems.Ntfs;

/// <summary>סוגי התכונות ב-NTFS שהתוכנה מפענחת.</summary>
internal static class AttrType
{
    internal const uint StandardInformation = 0x10;
    internal const uint AttributeList = 0x20;
    internal const uint FileName = 0x30;
    internal const uint ObjectId = 0x40;
    internal const uint SecurityDescriptor = 0x50;
    internal const uint VolumeName = 0x60;
    internal const uint VolumeInformation = 0x70;
    internal const uint Data = 0x80;
    internal const uint IndexRoot = 0x90;
    internal const uint IndexAllocation = 0xA0;
    internal const uint Bitmap = 0xB0;
    internal const uint ReparsePoint = 0xC0;
    internal const uint End = 0xFFFFFFFF;
}

/// <summary>שם קובץ יחיד מתוך תכונת $FILE_NAME.</summary>
internal readonly record struct FileNameEntry(
    string Name, long ParentRecord, ushort ParentSequence, byte Namespace,
    DateTime? Created, DateTime? Modified, DateTime? Accessed, long RealSize);

/// <summary>תכונה מפוענחת מתוך רשומת MFT.</summary>
internal sealed class NtfsAttribute
{
    internal uint Type { get; init; }
    internal string Name { get; init; } = "";
    internal bool IsNonResident { get; init; }
    internal bool IsCompressed { get; init; }

    /// <summary>תוכן התכונה, כשהיא רזידנטית.</summary>
    internal byte[]? ResidentValue { get; init; }

    /// <summary>מיקום התוכן על המחיצה, כשהתכונה אינה רזידנטית.</summary>
    internal List<DataExtent> Extents { get; init; } = new();

    /// <summary>הגודל האמיתי של התוכן בבתים.</summary>
    internal long RealSize { get; init; }

    internal long StartVcn { get; init; }

    /// <summary>
    /// גודל יחידת הדחיסה באשכולות. ב-NTFS דחוס זהו כמעט תמיד 16.
    /// אפס פירושו שהתכונה אינה דחוסה.
    /// </summary>
    internal int CompressionUnitClusters { get; init; }
}

/// <summary>
/// רשומת MFT מפוענחת. זהו המבנה שמתאר קובץ או תיקייה אחת ב-NTFS,
/// כולל שמו, תאריכיו ומיקום תוכנו על הדיסק.
/// </summary>
internal sealed class MftRecord
{
    internal const uint SignatureFile = 0x454C4946; // "FILE"
    internal const uint SignatureBaad = 0x44414142; // "BAAD"

    internal long RecordNumber { get; private init; }
    internal ushort SequenceNumber { get; private init; }
    internal bool InUse { get; private init; }
    internal bool IsDirectory { get; private init; }

    /// <summary>הפניה לרשומת הבסיס, כשרשומה זו היא הרחבה של רשומה אחרת.</summary>
    internal long BaseRecord { get; private init; }

    internal List<NtfsAttribute> Attributes { get; } = new();
    internal List<FileNameEntry> FileNames { get; } = new();

    internal DateTime? Created { get; private set; }
    internal DateTime? Modified { get; private set; }
    internal DateTime? Accessed { get; private set; }

    /// <summary>
    /// פענוח רשומת MFT ממאגר בתים. המאגר משתנה במקום בעת תיקון ה-fixup.
    /// מחזיר null אם הרשומה אינה רשומת FILE תקינה.
    /// </summary>
    internal static MftRecord? Parse(byte[] buffer, int bytesPerSector, long fallbackRecordNumber = -1)
    {
        if (buffer.Length < 48) return null;

        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (signature != SignatureFile) return null;

        if (!ApplyFixup(buffer, bytesPerSector)) return null;

        ushort attrOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(20));
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(22));
        uint usedSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(24));
        long baseRef = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(32)) & 0x0000FFFFFFFFFFFF;

        long recordNumber = buffer.Length >= 48
            ? BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(44))
            : fallbackRecordNumber;

        // רשומות ישנות אינן מכילות את מספרן; נופלים חזרה למספר שחושב מההיסט.
        if (recordNumber == 0 && fallbackRecordNumber > 0) recordNumber = fallbackRecordNumber;

        if (attrOffset < 42 || attrOffset >= buffer.Length) return null;
        if (usedSize > buffer.Length) usedSize = (uint)buffer.Length;

        var record = new MftRecord
        {
            RecordNumber = recordNumber,
            SequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(16)),
            InUse = (flags & 0x0001) != 0,
            IsDirectory = (flags & 0x0002) != 0,
            BaseRecord = baseRef,
        };

        record.ParseAttributes(buffer, attrOffset, (int)usedSize);
        return record;
    }

    /// <summary>
    /// תיקון Update Sequence לרשומה. המנגנון משותף לרשומות MFT,
    /// לבלוקי אינדקס ולדפי ‎$LogFile, ולכן ממומש במקום אחד.
    /// </summary>
    private static bool ApplyFixup(byte[] buffer, int bytesPerSector)
    {
        var (usOffset, usCount) = NtfsFixup.ReadHeader(buffer);
        return NtfsFixup.Apply(buffer, bytesPerSector, usOffset, usCount);
    }

    private void ParseAttributes(byte[] buffer, int offset, int limit)
    {
        int pos = offset;

        // תקרת איטרציות כהגנה מפני רשומה פגומה עם שרשרת תכונות מעגלית.
        for (int guard = 0; guard < 256 && pos + 8 <= limit; guard++)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos));
            if (type == AttrType.End) break;

            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos + 4));
            if (length <= 0 || pos + length > limit) break;

            var attribute = ParseAttribute(buffer, pos, length);
            if (attribute is not null)
            {
                Attributes.Add(attribute);
                Absorb(attribute);
            }

            pos += length;
        }
    }

    private static NtfsAttribute? ParseAttribute(byte[] buffer, int pos, int length)
    {
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos));
        bool nonResident = buffer[pos + 8] != 0;
        byte nameLength = buffer[pos + 9];
        ushort nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos + 10));
        ushort attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos + 12));

        string name = "";
        if (nameLength > 0 && pos + nameOffset + nameLength * 2 <= pos + length)
            name = Encoding.Unicode.GetString(buffer, pos + nameOffset, nameLength * 2);

        bool compressed = (attrFlags & 0x0001) != 0;

        if (!nonResident)
        {
            if (pos + 24 > buffer.Length) return null;

            int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos + 16));
            ushort valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos + 20));

            if (valueLength < 0 || pos + valueOffset + valueLength > pos + length) return null;

            byte[] value = new byte[valueLength];
            Buffer.BlockCopy(buffer, pos + valueOffset, value, 0, valueLength);

            return new NtfsAttribute
            {
                Type = type,
                Name = name,
                IsNonResident = false,
                IsCompressed = compressed,
                ResidentValue = value,
                RealSize = valueLength,
            };
        }

        if (pos + 64 > buffer.Length) return null;

        long startVcn = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(pos + 16));
        ushort runOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos + 32));
        // השדה מחזיק חזקה של 2: הערך 4 פירושו יחידת דחיסה של 16 אשכולות.
        ushort compressionUnitExponent = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos + 34));
        long realSize = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(pos + 48));

        if (runOffset <= 0 || pos + runOffset > pos + length) return null;

        var runs = DataRuns.Decode(buffer.AsSpan(pos + runOffset, length - runOffset));

        return new NtfsAttribute
        {
            Type = type,
            Name = name,
            IsNonResident = true,
            IsCompressed = compressed,
            Extents = runs,
            RealSize = realSize,
            StartVcn = startVcn,
            CompressionUnitClusters = compressionUnitExponent is > 0 and <= 16
                ? 1 << compressionUnitExponent
                : 0,
        };
    }

    /// <summary>שליפת המידע השימושי מתוך תכונה שזה עתה פוענחה.</summary>
    private void Absorb(NtfsAttribute attribute)
    {
        switch (attribute.Type)
        {
            case AttrType.StandardInformation when attribute.ResidentValue is { Length: >= 32 }:
            {
                var v = attribute.ResidentValue;
                Created = ToDateTime(BinaryPrimitives.ReadInt64LittleEndian(v.AsSpan(0)));
                Modified = ToDateTime(BinaryPrimitives.ReadInt64LittleEndian(v.AsSpan(8)));
                Accessed = ToDateTime(BinaryPrimitives.ReadInt64LittleEndian(v.AsSpan(24)));
                break;
            }

            case AttrType.FileName when attribute.ResidentValue is { Length: >= 66 }:
            {
                var v = attribute.ResidentValue;
                long rawParentRef = BinaryPrimitives.ReadInt64LittleEndian(v.AsSpan(0));
                long parentRef = rawParentRef & 0x0000FFFFFFFFFFFF;
                // 16 הביטים העליונים הם מונה הגרסה של רשומת ההורה.
                // אי-התאמה מעידה שהרשומה מוחזרה לשימוש ושהנתיב כבר אינו אמין.
                ushort parentSeq = (ushort)((rawParentRef >> 48) & 0xFFFF);
                byte nameChars = v[64];
                byte nameSpace = v[65];

                if (66 + nameChars * 2 > v.Length) break;

                FileNames.Add(new FileNameEntry(
                    Encoding.Unicode.GetString(v, 66, nameChars * 2),
                    parentRef,
                    parentSeq,
                    nameSpace,
                    ToDateTime(BinaryPrimitives.ReadInt64LittleEndian(v.AsSpan(8))),
                    ToDateTime(BinaryPrimitives.ReadInt64LittleEndian(v.AsSpan(16))),
                    ToDateTime(BinaryPrimitives.ReadInt64LittleEndian(v.AsSpan(32))),
                    BinaryPrimitives.ReadInt64LittleEndian(v.AsSpan(48))));
                break;
            }
        }
    }

    /// <summary>
    /// השם המועדף להצגה. NTFS שומר לעיתים כמה שמות לאותו קובץ,
    /// ויש להעדיף את השם הארוך על פני שם 8.3 הישן.
    /// </summary>
    internal FileNameEntry? PreferredName()
    {
        if (FileNames.Count == 0) return null;

        // מרחב שמות 1 = Win32, 3 = Win32 ו-DOS יחד, 2 = DOS בלבד (8.3).
        foreach (byte preferred in new byte[] { 3, 1, 0, 2 })
        {
            foreach (var entry in FileNames)
                if (entry.Namespace == preferred) return entry;
        }

        return FileNames[0];
    }

    /// <summary>תכונת $DATA הראשית — הזרם ללא שם, שהוא תוכן הקובץ עצמו.</summary>
    internal NtfsAttribute? PrimaryData()
        => Attributes.FirstOrDefault(a => a.Type == AttrType.Data && a.Name.Length == 0);

    /// <summary>כל תכונות $DATA הנוספות — זרמים חלופיים בעלי שם.</summary>
    internal IEnumerable<NtfsAttribute> AlternateStreams()
        => Attributes.Where(a => a.Type == AttrType.Data && a.Name.Length > 0);

    /// <summary>המרת חותמת זמן של Windows לתאריך, עם סינון ערכים בלתי אפשריים.</summary>
    private static DateTime? ToDateTime(long fileTime)
    {
        if (fileTime <= 0) return null;
        try
        {
            var value = DateTime.FromFileTimeUtc(fileTime);
            // תאריכים מחוץ לטווח סביר מעידים על רשומה פגומה.
            return value.Year is >= 1980 and <= 2200 ? value.ToLocalTime() : null;
        }
        catch
        {
            return null;
        }
    }
}
