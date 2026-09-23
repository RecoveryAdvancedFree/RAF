namespace RAF.Core.Carving;

/// <summary>
/// קריאת תחילת קובץ Matroska (MKV / WebM): הכותרת, ואחריה אלמנט ה-Segment —
/// שמכיל את כל הסרטון ומצהיר על אורכו.
///
/// הקידוד הוא EBML: כל מזהה וכל אורך הם מספר באורך משתנה, שהבית הראשון שלו
/// מגלה כמה בתים יש בו. אורך שכל הביטים שלו דולקים פירושו "לא ידוע" — כך נכתב
/// סרטון בשידור חי, ונגנים מנגנים אותו עד שהנתונים נגמרים.
/// </summary>
internal static class Matroska
{
    private static readonly byte[] EbmlId = { 0x1A, 0x45, 0xDF, 0xA3 };
    private static readonly byte[] SegmentId = { 0x18, 0x53, 0x80, 0x67 };

    /// <summary>
    /// שדה האורך של ה-Segment: היכן הוא, כמה בתים, ואיזה אורך הוא מצהיר
    /// (null — "לא ידוע"). null כשתחילת הקובץ אינה במבנה הזה.
    /// </summary>
    internal readonly record struct SegmentSize(int FieldOffset, int FieldLength, long DataStart, long? Size)
    {
        /// <summary>אורך הקובץ כולו לפי ההצהרה, או null כשהאורך "לא ידוע".</summary>
        public long? DeclaredFileLength => Size is { } s ? DataStart + s : null;
    }

    internal static SegmentSize? ReadSegment(ReadOnlySpan<byte> head)
    {
        if (head.Length < 12 || !head[..4].SequenceEqual(EbmlId)) return null;

        var header = ReadVint(head, 4);
        if (header is null || header.Value.Value is not { } headerSize || headerSize > 4096) return null;

        long segment = 4 + header.Value.Length + headerSize;
        if (segment + 5 > head.Length || !head.Slice((int)segment, 4).SequenceEqual(SegmentId)) return null;

        int fieldOffset = (int)segment + 4;
        var size = ReadVint(head, fieldOffset);
        if (size is null) return null;

        return new SegmentSize(fieldOffset, size.Value.Length, fieldOffset + size.Value.Length, size.Value.Value);
    }

    /// <summary>מספר EBML: אורכו בבתים, וערכו — null כשכל ביטי הערך דולקים ("לא ידוע").</summary>
    private static (int Length, long? Value)? ReadVint(ReadOnlySpan<byte> data, int at)
    {
        if (at >= data.Length || data[at] == 0) return null;

        byte first = data[at];
        int length = 1;
        while ((first & (0x80 >> (length - 1))) == 0) length++;
        if (at + length > data.Length) return null;

        long value = first & (0xFF >> length);
        bool allOnes = value == (0xFF >> length);
        for (int i = 1; i < length; i++)
        {
            value = (value << 8) | data[at + i];
            allOnes &= data[at + i] == 0xFF;
        }

        return (length, allOnes ? null : value);
    }
}
