namespace RAF.Core.Model;

/// <summary>מצב של אזור במפת הסקטורים. הסדר קובע מה גובר כשבאותו ריבוע יש כמה מצבים.</summary>
public enum SectorState : byte
{
    Pending = 0,   // עוד לא הגענו
    Skipped = 1,   // דולג בכוונה (מקום תפוס, בסריקת המקום הפנוי בלבד)
    Read = 2,      // נקרא ונבדק
    Found = 3,     // נמצא כאן משהו — קובץ, או מחיצה
    Retry = 4,     // נכשל בקריאה המהירה, וממתין לניסיון חוזר
    Bad = 5,       // לא נקרא
}

/// <summary>
/// מפת סקטורים — רעיון של המשתמש: האזור הנסרק מחולק לריבועים, וכל ריבוע נצבע
/// לפי מה שקרה בו. לכל ריבוע נספרים הבתים שבכל מצב, כך שאפשר גם להוריד מצב
/// (ניסיון חוזר שהצליח) בלי לאבד את מה שקרה בשאר הריבוע. בטוחה לשימוש מכמה חוטים.
/// </summary>
public sealed class SectorMap
{
    public const int DefaultCells = 1200;
    private const int States = 6;

    private readonly long[] _bytes;
    private readonly object _gate = new();
    private long _cursor = -1;

    public long Length { get; }
    public int Cells { get; }

    /// <summary>המקום שהמעבר נמצא בו עכשיו, בבתים; ‎-1 — אין.</summary>
    public long Cursor
    {
        get => Interlocked.Read(ref _cursor);
        set => Interlocked.Exchange(ref _cursor, value);
    }

    public SectorMap(long length, int cells = DefaultCells)
    {
        Length = Math.Max(1, length);
        Cells = (int)Math.Clamp(Length, 1, Math.Max(1, cells));
        _bytes = new long[States * Cells];
    }

    /// <summary>הריבוע שבו נמצא ההיסט.</summary>
    public int CellOf(long offset) => (int)Math.Clamp(offset * Cells / Length, 0, Cells - 1);

    /// <summary>הבית הראשון של הריבוע: הקטן ביותר שהריבוע שלו הוא c.</summary>
    public long CellStart(int cell) => cell >= Cells ? Length : ((long)cell * Length + Cells - 1) / Cells;

    public void Add(long offset, long length, SectorState state) => Change(offset, length, state, +1);

    public void Remove(long offset, long length, SectorState state) => Change(offset, length, state, -1);

    /// <summary>סימון נקודה — קובץ או מחיצה שנמצאו שם.</summary>
    public void Mark(long offset, SectorState state) => Change(offset, 1, state, +1);

    private void Change(long offset, long length, SectorState state, int sign)
    {
        if (state == SectorState.Pending) return;
        long start = Math.Clamp(offset, 0, Length);
        long end = Math.Clamp(offset + length, 0, Length);
        if (end <= start) return;

        lock (_gate)
        {
            for (int c = CellOf(start), last = CellOf(end - 1); c <= last; c++)
            {
                long overlap = Math.Min(end, CellStart(c + 1)) - Math.Max(start, CellStart(c));
                if (overlap <= 0) continue;
                ref long slot = ref _bytes[(int)state * Cells + c];
                slot = Math.Max(0, slot + sign * overlap);
            }
        }
    }

    public SectorState StateOf(int cell)
    {
        lock (_gate) return StateUnlocked(cell);
    }

    private SectorState StateUnlocked(int cell)
    {
        for (int s = States - 1; s > 0; s--)
            if (_bytes[s * Cells + cell] > 0) return (SectorState)s;
        return SectorState.Pending;
    }

    /// <summary>תמונת מצב לממשק: תו אחד לכל ריבוע, '0' עד '5'.</summary>
    public string Snapshot()
    {
        var chars = new char[Cells];
        lock (_gate)
            for (int c = 0; c < Cells; c++)
                chars[c] = (char)('0' + (int)StateUnlocked(c));
        return new string(chars);
    }
}
