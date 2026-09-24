using System.Buffers.Binary;

namespace RAF.Core.Repair;

/// <summary>
/// מבנה קובץ WAV כפי שהוא בפועל — לא כפי שהכותרת מצהירה.
///
/// מקליט כותב את הכותרת בתחילת ההקלטה ומעדכן את שדות הגודל רק כשהיא נסגרת.
/// הקלטה שנקטעה (סוללה, כרטיס שנשלף, אפליקציה שקרסה) נשארת עם אפס — או עם
/// ערך מקסימלי, או עם הגודל שתוכנן — בעוד השמע עצמו נמצא בקובץ. נגנים סומכים
/// על הכותרת, ולכן מנגנים שום דבר או מסרבים לפתוח. כאן מחשבים את הגודל האמיתי.
/// </summary>
internal sealed record WavLayout(
    long RiffDeclared,        // הגודל שהכותרת הראשית מצהירה עליו (כולל שמונת בתי הכותרת)
    long DataSizeField,       // היסט שדה הגודל של מקטע השמע
    long DataDeclared,        // מה ששדה הגודל של מקטע השמע מצהיר
    long DataActual,          // כמה שמע יש בפועל
    long ProperEnd,           // היכן הקובץ נגמר באמת
    bool DataUnfinished,      // שדה הגודל של השמע לא עודכן — ההקלטה לא נסגרה
    long ByteRate)
{
    /// <summary>משך השמע בפועל, בשניות. 0 כשהכותרת לא מציינת קצב.</summary>
    internal double Seconds => ByteRate > 0 ? DataActual / (double)ByteRate : 0;

    /// <summary>שדה הגודל הראשי (אחרי התיקון) — הכול פחות שמונת בתי הכותרת.</summary>
    internal long RiffField => ProperEnd - 8;

    internal static WavLayout? Analyze(Func<long, int, byte[]> read, long size)
    {
        byte[] head = read(0, 12);
        if (head.Length < 12 || !head.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !head.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return null;

        long riff = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) + 8L;
        int blockAlign = 1;
        long byteRate = 0;
        long at = 12;

        for (int guard = 0; guard < 1000 && at + 8 <= size; guard++)
        {
            byte[] chunk = read(at, 8);
            if (chunk.Length < 8 || !IsChunkId(chunk.AsSpan(0, 4))) break;

            long length = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));

            if (chunk.AsSpan(0, 4).SequenceEqual("fmt "u8) && length >= 16 && at + 8 + 16 <= size)
            {
                byte[] fmt = read(at + 8, 16);
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(8));
                blockAlign = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12)));
            }

            if (chunk.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                long start = at + 8;
                long available = size - start;

                // ההקלטה לא נסגרה: אפס, ערך "לא ידוע", או יותר ממה שנכתב בפועל.
                if (length == 0 || length == 0xFFFFFFFF || length > available)
                {
                    long actual = available - available % blockAlign;
                    return new WavLayout(riff, at + 4, length, actual, start + actual, true, byteRate);
                }

                long dataEnd = start + length + (length & 1);
                return new WavLayout(riff, at + 4, length, length,
                    AfterData(read, size, Math.Min(dataEnd, size)), false, byteRate);
            }

            // מקטע שנחתך באמצע — מה שאחריו אינו חלק מהקובץ.
            if (at + 8 + length > size) break;
            at += 8 + length + (length & 1);
        }

        return null;
    }

    /// <summary>מקטעים שבאים אחרי השמע (למשל פרטי ההקלטה) עדיין שייכים לקובץ.</summary>
    private static long AfterData(Func<long, int, byte[]> read, long size, long at)
    {
        for (int guard = 0; guard < 100 && at + 8 <= size; guard++)
        {
            byte[] chunk = read(at, 8);
            if (chunk.Length < 8 || !IsChunkId(chunk.AsSpan(0, 4))) break;

            long length = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            if (at + 8 + length > size) break;
            at += 8 + length + (length & 1);
        }
        return Math.Min(at, size);
    }

    /// <summary>מזהה מקטע: ארבעה תווים מודפסים (כמו "fmt " או "LIST").</summary>
    private static bool IsChunkId(ReadOnlySpan<byte> id)
    {
        foreach (byte b in id)
            if (b is < 0x20 or > 0x7E) return false;
        return true;
    }
}

/// <summary>
/// מבנה קובץ MP3: תג הפרטים (ID3) בהתחלה, שרשרת מסגרות שמע, ותג קצר אופציונלי בסוף.
///
/// ל-MP3 אין חתימה קבועה — קובץ בלי תג פרטים מתחיל ישר במסגרת שמע. כל מסגרת
/// פותחת בארבעה בתים שמהם אפשר לחשב את אורכה, ולכן שרשרת של כמה מסגרות רצופות
/// היא הוכחה שזה שמע: בנתונים מקריים הסיכוי לכך אפסי. כך מוצאים היכן השמע מתחיל
/// גם כשלפניו נתונים זרים, והיכן הוא נגמר גם כשאחריו יש זבל.
/// </summary>
internal sealed record Mp3Layout(long Start, long AudioStart, long End, int Frames)
{
    /// <summary>מסגרות רצופות שנדרשות כדי לקבוע שזה שמע.</summary>
    private const int ChainToConfirm = 4;

    /// <summary>עד כמה עמוק מחפשים את תחילת השמע.</summary>
    private const int SearchLimit = 1024 * 1024;

    internal static Mp3Layout? Analyze(Func<long, int, byte[]> read, long size)
    {
        var window = new Window(read, size);

        long searchFrom = 0;
        long tagStart = -1;
        if (Id3Length(window, 0) is { } tag)
        {
            tagStart = 0;
            searchFrom = tag;
        }

        long audio = -1;
        long limit = Math.Min(size, searchFrom + SearchLimit);
        for (long at = searchFrom; at + 4 <= limit; at++)
        {
            if (window[at] != 0xFF || (window[at + 1] & 0xE0) != 0xE0) continue;
            if (Chain(window, at, ChainToConfirm) >= ChainToConfirm) { audio = at; break; }
        }
        if (audio < 0) return null;

        // נתונים זרים ואחריהם תג פרטים שלם — השיר מתחיל בתג, לא בשמע.
        if (tagStart < 0)
        {
            for (long at = 0; at + 10 <= audio; at++)
            {
                if (window[at] != (byte)'I' || window[at + 1] != (byte)'D' || window[at + 2] != (byte)'3') continue;
                if (Id3Length(window, at) is { } length && at + length <= audio) { tagStart = at; break; }
            }
        }

        // מעבר על כל המסגרות עד שהשרשרת נשברת.
        long position = audio;
        int frames = 0;
        var first = Header.Parse(window, audio)!.Value;
        while (position + 4 <= size && Header.Parse(window, position) is { } h && h.SameStream(first))
        {
            if (position + h.Length > size) { position = size; break; }       // המסגרת האחרונה נקטעה — היא עדיין שמע
            position += h.Length;
            frames++;
        }

        // תג קצר בסוף (128 בתים שמתחילים ב-"TAG").
        if (position + 128 <= size && window[position] == (byte)'T' && window[position + 1] == (byte)'A' && window[position + 2] == (byte)'G')
            position += 128;

        return new Mp3Layout(tagStart >= 0 ? tagStart : audio, audio, position, frames);
    }

    private static int Chain(Window window, long at, int wanted)
    {
        if (Header.Parse(window, at) is not { } first) return 0;
        int count = 0;
        while (count < wanted && Header.Parse(window, at) is { } h && h.SameStream(first))
        {
            at += h.Length;
            count++;
        }
        return count;
    }

    /// <summary>אורך תג הפרטים בתחילת הקובץ (כולל כותרתו), או null כשאין תג.</summary>
    private static long? Id3Length(Window w, long at)
    {
        if (at + 10 > w.Size || w[at] != (byte)'I' || w[at + 1] != (byte)'D' || w[at + 2] != (byte)'3') return null;
        if (w[at + 3] is < 2 or > 4) return null;

        long size = 0;
        for (int i = 6; i < 10; i++)
        {
            if (w[at + i] >= 0x80) return null;
            size = (size << 7) | w[at + i];
        }
        bool footer = (w[at + 5] & 0x10) != 0;
        return 10 + size + (footer ? 10 : 0);
    }

    /// <summary>כותרת מסגרת שמע: גרסה, שכבה, קצב דגימה ואורך המסגרת.</summary>
    private readonly record struct Header(int Version, int Layer, int SampleRate, int Length)
    {
        private static readonly int[,] Bitrates =
        {
            // MPEG-1: שכבה 1, 2, 3
            { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },
            { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },
            { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },
            // MPEG-2 ו-2.5: שכבה 1, ושכבות 2 ו-3
            { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },
            { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
            { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
        };

        private static readonly int[] Rates = { 44100, 48000, 32000 };

        internal bool SameStream(Header other)
            => Version == other.Version && Layer == other.Layer && SampleRate == other.SampleRate;

        internal static Header? Parse(Window w, long at)
        {
            if (at + 4 > w.Size || w[at] != 0xFF || (w[at + 1] & 0xE0) != 0xE0) return null;

            int version = (w[at + 1] >> 3) & 3;          // 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5
            int layerBits = (w[at + 1] >> 1) & 3;        // 3 = שכבה 1, 2 = שכבה 2, 1 = שכבה 3
            int bitrateIndex = w[at + 2] >> 4;
            int rateIndex = (w[at + 2] >> 2) & 3;
            int padding = (w[at + 2] >> 1) & 1;

            if (version == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || rateIndex == 3) return null;

            int layer = 4 - layerBits;
            bool mpeg1 = version == 3;
            int row = mpeg1 ? layer - 1 : (layer == 1 ? 3 : 4);
            int bitrate = Bitrates[row, bitrateIndex] * 1000;
            int rate = Rates[rateIndex] >> (version switch { 3 => 0, 2 => 1, _ => 2 });

            int length = layer == 1
                ? (12 * bitrate / rate + padding) * 4
                : (layer == 3 && !mpeg1 ? 72 : 144) * bitrate / rate + padding;

            return length >= 24 ? new Header(version, layer, rate, length) : null;
        }
    }

    /// <summary>קריאה מהקובץ בחלונות של מגה-בית — בלי קריאה נפרדת לכל בית.</summary>
    private sealed class Window(Func<long, int, byte[]> read, long size)
    {
        private const int Span = 1024 * 1024;
        private byte[] _data = [];
        private long _start = -1;

        internal long Size => size;

        internal byte this[long at]
        {
            get
            {
                if (at < _start || at >= _start + _data.Length)
                {
                    _start = at;
                    _data = read(at, (int)Math.Min(Span, size - at));
                    if (_data.Length == 0) return 0;
                }
                return _data[at - _start];
            }
        }
    }
}
