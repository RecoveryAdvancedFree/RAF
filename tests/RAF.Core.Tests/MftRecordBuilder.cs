using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.Tests;

/// <summary>
/// בונה רשומות MFT סינתטיות בפורמט הבינארי המדויק של NTFS,
/// כולל Update Sequence Array, כדי לבדוק את המפענח מול קלט אמיתי במבנהו.
/// </summary>
internal sealed class MftRecordBuilder
{
    private readonly int _recordSize;
    private readonly int _sectorSize;
    private readonly List<byte[]> _attributes = new();

    internal long RecordNumber { get; set; } = 42;
    internal ushort SequenceNumber { get; set; } = 1;
    internal bool InUse { get; set; } = true;
    internal bool IsDirectory { get; set; }
    internal long BaseRecord { get; set; }

    internal MftRecordBuilder(int recordSize = 1024, int sectorSize = 512)
    {
        _recordSize = recordSize;
        _sectorSize = sectorSize;
    }

    /// <summary>הוספת תכונת $STANDARD_INFORMATION עם חותמות זמן.</summary>
    internal MftRecordBuilder WithStandardInformation(DateTime created, DateTime modified, DateTime accessed)
    {
        byte[] value = new byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(0), created.ToFileTimeUtc());
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(8), modified.ToFileTimeUtc());
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(16), modified.ToFileTimeUtc());
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(24), accessed.ToFileTimeUtc());

        _attributes.Add(BuildResident(0x10, "", value));
        return this;
    }

    /// <summary>הוספת תכונת $FILE_NAME עם שם, הורה ומרחב שמות.</summary>
    internal MftRecordBuilder WithFileName(
        string name, long parentRecord, ushort parentSequence = 1, byte nameSpace = 3, long realSize = 0)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        byte[] value = new byte[66 + nameBytes.Length];

        long parentRef = (parentRecord & 0x0000FFFFFFFFFFFF) | ((long)parentSequence << 48);
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(0), parentRef);
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(48), realSize);
        value[64] = (byte)name.Length;
        value[65] = nameSpace;
        nameBytes.CopyTo(value, 66);

        _attributes.Add(BuildResident(0x30, "", value));
        return this;
    }

    /// <summary>הוספת תכונת $DATA רזידנטית — תוכן השמור בתוך הרשומה עצמה.</summary>
    internal MftRecordBuilder WithResidentData(byte[] content, string streamName = "")
    {
        _attributes.Add(BuildResident(0x80, streamName, content));
        return this;
    }

    /// <summary>הוספת תכונת $DATA לא-רזידנטית עם רשימת ריצות נתונה.</summary>
    internal MftRecordBuilder WithNonResidentData(byte[] runList, long realSize, bool compressed = false)
    {
        // כותרת תכונה לא-רזידנטית: 64 בתים עד לרשימת הריצות.
        int headerSize = 64;
        int total = Align8(headerSize + runList.Length);
        byte[] attribute = new byte[total];

        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(0), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4), (uint)total);
        attribute[8] = 1;                                                    // לא רזידנטי
        attribute[9] = 0;                                                    // ללא שם
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(10), 64);  // היסט שם
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(12), (ushort)(compressed ? 1 : 0));

        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(16), 0);                 // VCN התחלתי
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(32), (ushort)headerSize); // היסט הריצות
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(48), realSize);           // גודל אמיתי

        runList.CopyTo(attribute, headerSize);
        _attributes.Add(attribute);
        return this;
    }

    /// <summary>הרכבת הרשומה המלאה בפורמט שבו היא יושבת על הדיסק.</summary>
    internal byte[] Build()
    {
        byte[] record = new byte[_recordSize];

        int usOffset = 48;
        int usCount = _recordSize / _sectorSize + 1;
        int attrOffset = Align8(usOffset + usCount * 2);

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0), 0x454C4946); // "FILE"
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), (ushort)usOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)usCount);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), SequenceNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), 1);         // מונה קישורים
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), (ushort)attrOffset);

        ushort flags = 0;
        if (InUse) flags |= 0x0001;
        if (IsDirectory) flags |= 0x0002;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), flags);

        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(32), BaseRecord);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44), (uint)RecordNumber);

        int pos = attrOffset;
        foreach (byte[] attribute in _attributes)
        {
            attribute.CopyTo(record, pos);
            pos += attribute.Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0xFFFFFFFF); // סוף התכונות
        pos += 8;

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)pos);          // גודל בשימוש
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), (uint)_recordSize);  // גודל מוקצה

        ApplyUpdateSequence(record, usOffset, usCount);
        return record;
    }

    /// <summary>
    /// החלפת שני הבתים האחרונים של כל סקטור במונה, ושמירת המקוריים במערך —
    /// בדיוק כפי ש-NTFS עושה בעת כתיבת רשומה לדיסק.
    /// </summary>
    private void ApplyUpdateSequence(byte[] record, int usOffset, int usCount)
    {
        const ushort usn = 0xBEEF;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usOffset), usn);

        for (int i = 1; i < usCount; i++)
        {
            int sectorEnd = i * _sectorSize - 2;

            ushort original = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(sectorEnd));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usOffset + i * 2), original);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(sectorEnd), usn);
        }
    }

    private static byte[] BuildResident(uint type, string name, byte[] value)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int headerSize = Align8(24 + nameBytes.Length);
        int total = Align8(headerSize + value.Length);

        byte[] attribute = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(0), type);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4), (uint)total);
        attribute[8] = 0;                                                     // רזידנטי
        attribute[9] = (byte)name.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(10), 24);   // היסט השם
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(16), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(20), (ushort)headerSize);

        nameBytes.CopyTo(attribute, 24);
        value.CopyTo(attribute, headerSize);
        return attribute;
    }

    private static int Align8(int value) => (value + 7) & ~7;
}
