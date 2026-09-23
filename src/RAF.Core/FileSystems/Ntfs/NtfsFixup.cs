using System.Buffers.Binary;

namespace RAF.Core.FileSystems.Ntfs;

/// <summary>
/// תיקון Update Sequence Array.
///
/// NTFS מחליף את שני הבתים האחרונים של כל סקטור במבנים רב-סקטוריים
/// (רשומות MFT, בלוקי אינדקס ודפי ‎$LogFile) במונה, כדי לזהות כתיבה
/// שנקטעה באמצע. לפני כל פענוח יש להחזיר את הערכים המקוריים ממערך המונים.
/// </summary>
internal static class NtfsFixup
{
    /// <summary>
    /// החזרת הבתים המקוריים במקום המונים.
    /// מחזיר false אם אחד הסקטורים אינו נושא את המונה הצפוי —
    /// סימן לכך שהמבנה נכתב חלקית ואינו אמין.
    /// </summary>
    internal static bool Apply(byte[] buffer, int bytesPerSector, int usOffset, int usCount)
    {
        if (usCount == 0) return true;
        if (usOffset < 0 || usOffset + usCount * 2 > buffer.Length) return false;

        // הערך הראשון במערך הוא המונה עצמו; אחריו ערך אחד לכל סקטור.
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(usOffset));

        for (int i = 1; i < usCount; i++)
        {
            int sectorEnd = i * bytesPerSector - 2;
            if (sectorEnd < 0 || sectorEnd + 2 > buffer.Length) break;

            ushort marker = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(sectorEnd));
            if (marker != usn) return false;

            ushort original = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(usOffset + i * 2));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(sectorEnd), original);
        }

        return true;
    }

    /// <summary>קריאת מיקום מערך המונים מכותרת מבנה סטנדרטית.</summary>
    internal static (int Offset, int Count) ReadHeader(ReadOnlySpan<byte> buffer)
        => buffer.Length < 8
            ? (0, 0)
            : (BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]),
               BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..]));
}
