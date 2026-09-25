using System.Buffers.Binary;

namespace RAF.Core.Raid;

/// <summary>
/// הכותרת שמערך RAID של לינוקס (mdadm) כותב על כל אחד מהכוננים שלו — בשרתי אחסון
/// ביתיים (Synology, QNAP ודומיהם) ובשרתי לינוקס. היא אומרת בדיוק איך המערך בנוי:
/// סוג, מספר כוננים, גודל רצועה, סידור, ומה מקומו של הכונן הזה בתוכו. לכן אין
/// צורך לנחש — רק לאסוף את הכוננים ששייכים לאותו מערך.
///
/// שתי גרסאות: 0.90 הישנה (בסוף הכונן, 64KB מהסוף בעיגול) ו-1.x — 1.0 בסוף הכונן,
/// 1.1 בתחילתו, 1.2 (ברירת המחדל) 4KB מתחילתו.
/// </summary>
internal sealed class MdSuperblock
{
    internal const uint Magic = 0xA92B4EFC;

    /// <summary>"0.90", "1.0", "1.1" או "1.2".</summary>
    internal string Version { get; init; } = "";
    /// <summary>מזהה המערך — משותף לכל הכוננים שלו.</summary>
    internal Guid ArrayId { get; init; }
    internal string Name { get; init; } = "";
    /// <summary>-1 שרשור (linear), 0, 1, 4, 5, 6, 10.</summary>
    internal int Level { get; init; }
    internal int Layout { get; init; }
    /// <summary>גודל רצועה בבתים (0 — אין רצועות).</summary>
    internal long ChunkBytes { get; init; }
    internal int RaidDisks { get; init; }
    /// <summary>מקום הכונן במערך (0 והלאה); -1 — כונן רזרבי או כונן שסומן כתקול.</summary>
    internal int Role { get; init; }
    /// <summary>מונה שינויים: כונן שנפל מהמערך נשאר עם מונה ישן.</summary>
    internal ulong Events { get; init; }
    /// <summary>היכן מתחילים הנתונים בכונן, בבתים מתחילת הכונן (או המחיצה).</summary>
    internal long DataOffset { get; init; }
    /// <summary>כמה מהכונן משמש את המערך, בבתים.</summary>
    internal long DataSize { get; init; }
    /// <summary>הגודל שכל כונן תורם (במראה ובזוגיות) — לפעמים קטן מ-DataSize.</summary>
    internal long ComponentSize { get; init; }
    /// <summary>המערך באמצע שינוי צורה (הוספת כונן, שינוי סוג) — הסידור אינו אחיד.</summary>
    internal bool Reshaping { get; init; }
    internal DateTime? Updated { get; init; }

    /// <summary>
    /// חיפוש הכותרת בכונן או במחיצה. read — קריאה מהיסט בתוך הכונן; size — גודלו.
    /// </summary>
    internal static MdSuperblock? Find(Func<long, int, byte[]?> read, long size)
    {
        if (size < 128 * 1024) return null;
        // 1.2 ו-1.1 — בתחילת הכונן; 1.0 — 8KB מהסוף בעיגול ל-4KB; 0.90 — 64KB מהסוף בעיגול ל-64KB.
        foreach (var (at, version) in new[] { (4096L, "1.2"), (0L, "1.1"), ((((size >> 9) - 16) & ~7L) << 9, "1.0") })
            if (read(at, 4096) is { Length: 4096 } block && ParseV1(block, at, version, size) is { } sb)
                return sb;
        long old = (size & ~(65536L - 1)) - 65536;
        return old > 0 && read(old, 4096) is { Length: 4096 } legacy ? ParseV090(legacy, old) : null;
    }

    private static MdSuperblock? ParseV1(byte[] b, long at, string version, long deviceSize)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic || BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)) != 1)
            return null;
        // הכותרת רושמת איפה היא עצמה יושבת (בסקטורים) — כך 1.1 ו-1.2 לא מתבלבלות.
        if ((long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(144)) != at >> 9) return null;

        uint features = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
        int raidDisks = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(92));
        int devNumber = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(160));
        int maxDev = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(220));
        int role = -1;
        if (devNumber < maxDev && 256 + devNumber * 2 + 2 <= b.Length)
        {
            int r = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(256 + devNumber * 2));
            role = r < 0xFFFE ? r : -1;
        }
        long dataOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(128)) << 9;
        long dataSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(136)) << 9;
        if (raidDisks is < 1 or > 256 || dataOffset >= deviceSize) return null;

        ulong seconds = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(192)) & 0xFFFFFFFFFFUL;
        return new MdSuperblock
        {
            Version = version,
            ArrayId = new Guid(b.AsSpan(16, 16)),
            Name = System.Text.Encoding.UTF8.GetString(b.AsSpan(32, 32)).TrimEnd('\0'),
            Level = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(72)),
            Layout = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(76)),
            ComponentSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(80)) << 9,
            ChunkBytes = (long)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(88)) << 9,
            RaidDisks = raidDisks,
            Role = role,
            Events = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(200)),
            DataOffset = dataOffset,
            DataSize = dataSize > 0 ? dataSize : deviceSize - dataOffset,
            Reshaping = (features & 0x4) != 0,
            Updated = seconds == 0 ? null : DateTime.UnixEpoch.AddSeconds(seconds).ToLocalTime(),
        };
    }

    /// <summary>הגרסה הישנה: מילים של 32 סיביות. הנתונים מתחילים בתחילת הכונן ונגמרים בכותרת.</summary>
    private static MdSuperblock? ParseV090(byte[] b, long at)
    {
        uint Word(int i) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i * 4));
        if (Word(0) != Magic || Word(1) != 0 || Word(2) != 90) return null;

        int raidDisks = (int)Word(10);
        if (raidDisks is < 1 or > 27) return null;
        // this_disk: המתאר האחרון בכותרת; raid_disk בו הוא המקום במערך. מצב 1 — תקול.
        int thisRole = (int)Word(992 + 3);
        bool faulty = (Word(992 + 4) & 1) != 0;

        Span<byte> id = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(id, Word(5));
        BinaryPrimitives.WriteUInt32LittleEndian(id[4..], Word(13));
        BinaryPrimitives.WriteUInt32LittleEndian(id[8..], Word(14));
        BinaryPrimitives.WriteUInt32LittleEndian(id[12..], Word(15));
        uint utime = Word(32);

        return new MdSuperblock
        {
            Version = "0.90",
            ArrayId = new Guid(id),
            Level = (int)Word(7),
            Layout = (int)Word(64),
            ChunkBytes = Word(65),
            RaidDisks = raidDisks,
            Role = faulty || thisRole >= raidDisks ? -1 : thisRole,
            Events = ((ulong)Word(40) << 32) | Word(39),   // במחשב רגיל: קודם החצי הנמוך
            DataOffset = 0,
            DataSize = at,
            ComponentSize = (long)Word(8) * 1024,
            Updated = utime == 0 ? null : DateTime.UnixEpoch.AddSeconds(utime).ToLocalTime(),
        };
    }
}
