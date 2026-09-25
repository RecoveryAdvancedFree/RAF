namespace RAF.Core.Raid;

/// <summary>
/// הגאומטריה של מערך RAID: לכל בית במערך — באיזה כונן ובאיזה מקום בו הוא יושב.
///
/// שרשור (linear) — כונן אחרי כונן. RAID 0 — רצועות לסירוגין. RAID 1 — כל הכוננים זהים.
/// RAID 10 — רצועות, וכל רצועה בכמה עותקים על כוננים סמוכים. RAID 4/5/6 — רצועות, ובכל
/// שורה רצועת זוגיות (XOR של השאר; ב-6 עוד אחת); מקומה מתחלף משורה לשורה לפי ה"סידור".
///
/// כונן חסר: במראה ובעותקים — קוראים עותק אחר. ב-4/5/6 — מחשבים את הרצועה החסרה
/// מ-XOR של כל שאר הרצועות בשורה (כולל הזוגיות). ב-RAID 6 עם שני כוננים חסרים צריך את
/// רצועת ה-Q (חשבון בשדה גלואה) — זה עוד לא נתמך.
/// </summary>
internal sealed class RaidArray : IComposedVolume
{
    internal int Level { get; }
    internal int Layout { get; }
    internal long Chunk { get; }
    internal int Disks { get; }
    public long Size { get; }
    /// <summary>לכל מקום במערך: היכן מתחילים הנתונים בכונן, וכמה ממנו בשימוש (לשרשור).</summary>
    private readonly long[] _dataOffset, _memberSize;
    private readonly bool[] _present;

    internal RaidArray(int level, int layout, long chunk, int disks, long componentSize,
        long[] dataOffset, long[] memberSize, bool[] present)
    {
        Level = level;
        Layout = level == 4 ? 5 : layout;   // RAID 4: הזוגיות תמיד בכונן האחרון
        Chunk = chunk;
        Disks = disks;
        _dataOffset = dataOffset;
        _memberSize = memberSize;
        _present = present;
        Size = ComputeSize(componentSize);
    }

    internal int DataDisks => Level switch { 4 or 5 => Disks - 1, 6 => Disks - 2, _ => Disks };
    internal int NearCopies => Level == 10 ? Layout & 0xFF : 1;
    internal int Missing => _present.Count(p => !p);

    /// <summary>למה אי אפשר לקרוא את המערך, או null — אפשר.</summary>
    internal string? Unsupported()
    {
        if (Level is not (-1 or 0 or 1 or 4 or 5 or 6 or 10))
            return L.T("סוג המערך (RAID {0}) אינו נתמך.", Level);
        if (Level is 0 or 4 or 5 or 6 or 10 && Chunk <= 0) return L.T("גודל הרצועה של המערך אינו ידוע.");
        if (Level is 5 or 6 && Layout > 5)
            return L.T("הסידור של המערך ({0}) אינו נתמך. נתמכים ארבעת הסידורים הרגילים וזוגיות בכונן הראשון או האחרון.", Layout);
        if (Level == 10 && (Layout >> 8 & 0xFF) != 1)
            return L.T("מערך RAID 10 בסידור \"רחוק\" או \"היסט\" אינו נתמך עדיין — רק הסידור הרגיל (\"קרוב\").");
        if (Level == 10 && (NearCopies < 1 || NearCopies > Disks)) return L.T("מספר העותקים במערך אינו תקין.");
        return MissingProblem();
    }

    private string? MissingProblem()
    {
        int missing = Missing;
        if (missing == 0) return null;
        bool ok = Level switch
        {
            1 => missing < Disks,
            4 or 5 => missing == 1,
            6 => missing == 1,
            10 => Enumerable.Range(0, Disks).All(d => Enumerable.Range(0, NearCopies).Any(j => _present[(d + j) % Disks])),
            _ => false,
        };
        if (ok) return null;
        return Level switch
        {
            -1 or 0 => L.T("חסר כונן במערך. במערך מסוג זה כל כונן מחזיק חלק מהנתונים ואין עותק — בלי כל הכוננים הקבצים יחזרו חלקיים. סריקה מתקדמת של הכוננים שנמצאו עדיין יכולה למצוא קבצים קטנים."),
            6 when missing == 2 => L.T("חסרים שני כוננים במערך RAID 6. שחזור שני כוננים חסרים עוד לא נתמך — חברו לפחות אחד מהם."),
            _ => L.T("חסרים {0} כוננים במערך — יותר ממה שהוא יכול לאבד.", missing),
        };
    }

    private long ComputeSize(long component)
    {
        switch (Level)
        {
            case -1: return _memberSize.Sum();
            case 0:
            {
                long per = _memberSize.Min() / Chunk * Chunk;
                return per * Disks;
            }
            case 1: return component > 0 ? component : _memberSize.Min();
            case 10:
            {
                long per = (component > 0 ? component : _memberSize.Min()) / Chunk;
                return per * Disks / NearCopies * Chunk;
            }
            default:
            {
                long per = (component > 0 ? component : _memberSize.Min()) / Chunk * Chunk;
                return per * DataDisks;
            }
        }
    }

    /// <summary>
    /// קריאה מהמערך. כונן שחסר או שהקריאה ממנו נכשלה — מעותק או מהזוגיות. אזור שאי אפשר
    /// לשחזר מוחזר כאפסים, כדי שקריאה אחת פגומה לא תעצור סריקה של מערך שלם.
    /// </summary>
    public int Read(long offset, Span<byte> destination, MemberRead read)
    {
        if (offset >= Size) return 0;
        int total = (int)Math.Min(destination.Length, Size - offset);
        for (int done = 0; done < total; )
        {
            long at = offset + done;
            int part = total - done;
            var target = destination.Slice(done, Math.Min(part, Piece(at)));
            ReadPiece(at, target, read);
            done += target.Length;
        }
        return total;
    }

    /// <summary>כמה אפשר לקרוא מ-at בלי לעבור לכונן אחר.</summary>
    private int Piece(long at)
    {
        if (Level == -1)
        {
            long start = 0;
            foreach (long size in _memberSize)
            {
                if (at < start + size) return (int)Math.Min(int.MaxValue, start + size - at);
                start += size;
            }
            return int.MaxValue;
        }
        if (Level == 1) return int.MaxValue;
        return (int)(Chunk - at % Chunk);
    }

    private void ReadPiece(long at, Span<byte> target, MemberRead read)
    {
        switch (Level)
        {
            case -1:
            {
                long start = 0;
                for (int role = 0; role < Disks; role++)
                {
                    if (at < start + _memberSize[role])
                    {
                        Direct(role, _dataOffset[role] + at - start, target, read);
                        return;
                    }
                    start += _memberSize[role];
                }
                target.Clear();
                return;
            }
            case 1:
                for (int role = 0; role < Disks; role++)
                    if (_present[role] && read(role, _dataOffset[role] + at, target) == target.Length) return;
                target.Clear();
                return;
            case 0:
            {
                long chunk = at / Chunk;
                int role = (int)(chunk % Disks);
                Direct(role, _dataOffset[role] + chunk / Disks * Chunk + at % Chunk, target, read);
                return;
            }
            case 10:
            {
                long first = at / Chunk * NearCopies;
                for (int j = 0; j < NearCopies; j++)
                {
                    int role = (int)((first + j) % Disks);
                    long row = (first + j) / Disks;
                    if (_present[role] && read(role, _dataOffset[role] + row * Chunk + at % Chunk, target) == target.Length) return;
                }
                target.Clear();
                return;
            }
            default:
                Parity(at, target, read);
                return;
        }
    }

    private void Direct(int role, long offset, Span<byte> target, MemberRead read)
    {
        int got = _present[role] ? read(role, offset, target) : 0;
        if (got < target.Length) target[Math.Max(0, got)..].Clear();
    }

    /// <summary>RAID 4/5/6: מיקום הרצועה בשורה לפי הסידור, או חישוב שלה מהזוגיות.</summary>
    private void Parity(long at, Span<byte> target, MemberRead read)
    {
        long chunk = at / Chunk;
        long stripe = chunk / DataDisks;
        var (disk, _, q) = Locate(stripe, (int)(chunk % DataDisks));
        long offsetIn = stripe * Chunk + at % Chunk;

        if (_present[disk] && read(disk, _dataOffset[disk] + offsetIn, target) == target.Length) return;

        // הרצועה חסרה: XOR של כל האחרות בשורה, כולל P ובלי Q.
        target.Clear();
        byte[] buffer = new byte[target.Length];
        for (int role = 0; role < Disks; role++)
        {
            if (role == disk || role == q) continue;
            if (!_present[role] || read(role, _dataOffset[role] + offsetIn, buffer) != buffer.Length)
            {
                target.Clear();   // שני חסרים באותה שורה — אין מה לחשב
                return;
            }
            for (int i = 0; i < buffer.Length; i++) target[i] ^= buffer[i];
        }
    }

    /// <summary>
    /// לפי raid5.c של לינוקס: הכונן של רצועת הנתונים dd בשורה stripe, וכונני P ו-Q (Q=-1 ב-4/5).
    /// </summary>
    internal (int Data, int P, int Q) Locate(long stripe, int dd)
    {
        int n = Disks;
        int rot = (int)(stripe % n);
        if (Level != 6)
        {
            int data = DataDisks;
            switch (Layout)
            {
                case 0: { int pd = data - rot; return (dd >= pd ? dd + 1 : dd, pd, -1); }      // left-asymmetric
                case 1: { int pd = rot; return (dd >= pd ? dd + 1 : dd, pd, -1); }             // right-asymmetric
                case 2: { int pd = data - rot; return ((pd + 1 + dd) % n, pd, -1); }           // left-symmetric
                case 3: { int pd = rot; return ((pd + 1 + dd) % n, pd, -1); }                  // right-symmetric
                case 4: return (dd + 1, 0, -1);                                               // parity-first
                default: return (dd, data, -1);                                               // parity-last
            }
        }
        switch (Layout)
        {
            case 0:
            case 1:
            {
                int pd = Layout == 0 ? n - 1 - rot : rot;
                if (pd == n - 1) return (dd + 1, pd, 0);
                return (dd >= pd ? dd + 2 : dd, pd, pd + 1);
            }
            case 2: { int pd = n - 1 - rot; return ((pd + 2 + dd) % n, pd, (pd + 1) % n); }
            case 3: { int pd = rot; return ((pd + 2 + dd) % n, pd, (pd + 1) % n); }
            case 4: return (dd + 2, 0, 1);
            default: return (dd, n - 2, n - 1);
        }
    }

    /// <summary>שם הסוג להצגה.</summary>
    internal static string LevelName(int level) => level switch
    {
        -1 => L.T("שרשור (JBOD)"),
        _ => $"RAID {level}",
    };
}
