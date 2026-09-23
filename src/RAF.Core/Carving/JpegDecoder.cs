using System.Runtime.CompilerServices;

namespace RAF.Core.Carving;

/// <summary>
/// הבתים שהמפענח קורא: קובץ רציף, או קובץ בשני מקטעים — הבתים עד <c>split</c>
/// ממקומם, ומשם והלאה מ-<c>gap</c> בתים אחרי המקום שבו היו אמורים להיות. כך
/// נבדק כל מועמד לחיבור בלי להעתיק נתונים.
///
/// מחלקה אחת סגורה ולא ממשק: הקריאה נעשית לכל בית בתמונה, וקריאה דרך ממשק
/// אינה ניתנת להטמעה — ההבדל ניכר בתמונות של מגה-פיקסלים רבים.
/// </summary>
internal sealed class JpegBytes
{
    private readonly byte[] _data;
    private readonly long _length, _split, _gap;

    private JpegBytes(byte[] data, long length, long split, long gap)
    {
        _data = data;
        _length = length;
        _split = split;
        _gap = gap;
    }

    public static JpegBytes Of(byte[] data, long length = -1)
        => new(data, length < 0 ? data.Length : Math.Min(length, data.Length), long.MaxValue, 0);

    public static JpegBytes Spliced(byte[] data, long split, long gap)
        => new(data, data.Length, split, gap);

    /// <summary>הבית במיקום נתון; -1 מעבר לסוף.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int At(long index)
    {
        long at = index < _split ? index : index + _gap;
        return (ulong)at < (ulong)_length ? _data[at] : -1;
    }
}

internal enum JpegVerdict
{
    /// <summary>כל יחידות ה-MCU פוענחו, ואחריהן סימן הסיום.</summary>
    Complete,

    /// <summary>הנתונים הדחוסים מפסיקים להיות JPEG תקין בנקודה מסוימת.</summary>
    Corrupt,

    /// <summary>פורמט שהמפענח אינו מכסה (פרוגרסיבי, אריתמטי, ללא טבלאות) — אין הכרעה.</summary>
    Unsupported,
}

/// <param name="Offset">Complete — אורך הקובץ (אחרי FF D9). Corrupt — היכן התגלתה השגיאה.</param>
internal readonly record struct JpegCheck(JpegVerdict Verdict, long Offset, int McusDecoded, int McusTotal)
{
    /// <summary>החלק מהתמונה שפוענח לפני השגיאה.</summary>
    public double Fraction => McusTotal > 0 ? Math.Min(1.0, (double)McusDecoded / McusTotal) : 0;
}

/// <summary>
/// מפענח JPEG בסיסי (baseline, הופמן) — לאימות בלבד, בלי לחשב פיקסלים.
///
/// אחרי שהקובץ חולץ, השאלה היא אם הנתונים הדחוסים שבו הם באמת של התמונה.
/// קובץ שהיה מפוצל נחתך בדיוק היכן שהמקטע הראשון נגמר, ומשם באים נתונים זרים:
/// קוד הופמן שאינו קיים בטבלה, מקדם שחורג מ-63, סימן FF באמצע יחידה. המפענח
/// עובר על כל יחידות ה-MCU ומדווח היכן — אם בכלל — הנתונים הפסיקו להיות תקינים.
///
/// המצב נשמר בתחילת כל יחידת MCU, כדי שאפשר יהיה להמשיך ממנה מול מקור אחר —
/// זה מה שמאפשר לחפש את המקטע השני של קובץ מפוצל בלי לפענח מחדש את הראשון.
/// </summary>
internal sealed class JpegDecoder
{
    private sealed class Huffman
    {
        private readonly int[] _maxCode = new int[17];
        private readonly int[] _minCode = new int[17];
        private readonly int[] _valPtr = new int[17];
        private readonly byte[] _values;

        private Huffman(byte[] values) => _values = values;

        /// <summary>טבלה קנונית ממספרי הקודים בכל אורך. null — טבלה שאינה חוקית.</summary>
        public static Huffman? Build(ReadOnlySpan<byte> counts, byte[] values)
        {
            var h = new Huffman(values);
            int code = 0, k = 0;
            for (int length = 1; length <= 16; length++)
            {
                int n = counts[length - 1];
                h._valPtr[length] = k;
                h._minCode[length] = code;
                code += n;
                k += n;
                h._maxCode[length] = n > 0 ? code - 1 : -1;
                if (code > 1 << length) return null;
                code <<= 1;
            }
            if (k != values.Length) return null;
            h.BuildLookup();
            return h;
        }

        /// <summary>
        /// טבלת חיפוש ל-9 הביטים הבאים: רוב הקודים קצרים מזה, ומפוענחים בצעד אחד
        /// במקום ביט אחר ביט. ערך 0 — קוד ארוך יותר, שמפוענח בדרך האיטית.
        /// </summary>
        private const int LookupBits = 9;
        private readonly ushort[] _lookup = new ushort[1 << LookupBits];

        private void BuildLookup()
        {
            for (int length = 1; length <= LookupBits; length++)
            {
                for (int code = _minCode[length]; code <= _maxCode[length]; code++)
                {
                    int symbol = _values[_valPtr[length] + code - _minCode[length]];
                    int shift = LookupBits - length;
                    for (int fill = 0; fill < 1 << shift; fill++)
                        _lookup[(code << shift) | fill] = (ushort)(length << 8 | symbol);
                }
            }
        }

        /// <summary>פענוח סמל אחד; -1 אם אין קוד כזה בטבלה או שהנתונים נגמרו.</summary>
        public int Decode(JpegDecoder d)
        {
            if (d._accBits < 16) d.Fill();
            if (d._accBits >= 16)
            {
                int window = (int)(d._acc >> (d._accBits - 16)) & 0xFFFF;
                int entry = _lookup[window >> (16 - LookupBits)];
                if (entry != 0)
                {
                    d._accBits -= entry >> 8;
                    return entry & 0xFF;
                }

                // קוד ארוך מ-9 ביטים — נפוץ במקדמים גדולים של צילום מפורט. במקום
                // ביט אחר ביט, משווים את 16 הביטים הבאים מול כל אורך.
                for (int length = LookupBits + 1; length <= 16; length++)
                {
                    int c = window >> (16 - length);
                    if (c <= _maxCode[length])
                    {
                        d._accBits -= length;
                        return _values[_valPtr[length] + c - _minCode[length]];
                    }
                }
                return -1;
            }

            // סוף הנתונים או סימן קרוב: פחות מ-16 ביטים זמינים — ביט אחר ביט.
            int code = 0;
            for (int length = 1; length <= 16; length++)
            {
                int bit = d.ReadBit();
                if (bit < 0) return -1;
                code = (code << 1) | bit;
                if (code <= _maxCode[length])
                    return _values[_valPtr[length] + code - _minCode[length]];
            }
            return -1;
        }
    }

    private sealed record Component(int Id, int H, int V);

    private sealed record ScanComponent(Component Component, Huffman Dc, Huffman Ac);

    /// <summary>סריקה אחת: אילו רכיבים, ואיך נבנית כל יחידת MCU.</summary>
    private sealed record Scan(ScanComponent[] Components, int McusTotal, int RestartInterval);

    /// <summary>מצב המפענח בתחילת יחידת MCU.</summary>
    internal readonly record struct Checkpoint(long Pos, int Buffer, int Bits, int Mcu, int NextRestart);

    private readonly JpegBytes _src;

    // מצב הקורא
    private long _pos;
    private ulong _acc;              // ביטים שנקראו ועוד לא נצרכו — ה-_accBits התחתונים
    private int _accBits;
    private bool _markerAhead;
    private readonly long[] _origin = new long[8];   // מיקום המקור של שמונת הבתים האחרונים בצובר
    private int _appended;

    // טבלאות ומסגרת — נבנים מהכותרות
    private readonly Huffman?[] _dcTables = new Huffman?[4];
    private readonly Huffman?[] _acTables = new Huffman?[4];
    private readonly List<Component> _components = new();
    private int _width, _height, _hMax = 1, _vMax = 1, _restartInterval;
    private bool _frameSeen;

    // הסריקה הנוכחית
    private Scan? _scan;
    private int _mcu, _nextRestart;
    private int _mcusDoneAllScans, _mcusTotalFirstScan;

    /// <summary>נקודות שמירה בסריקה הנוכחית — לפי סדר, אחת לכל יחידת MCU.</summary>
    internal List<Checkpoint>? Checkpoints { get; }

    /// <summary>היכן מתחילים הנתונים הדחוסים של הסריקה הראשונה.</summary>
    internal long ScanStart { get; private set; } = -1;

    private JpegDecoder(JpegBytes source, bool recordCheckpoints)
    {
        _src = source;
        if (recordCheckpoints) Checkpoints = new List<Checkpoint>();
    }

    /// <summary>בדיקת קובץ שלם מתחילתו.</summary>
    internal static JpegCheck Check(JpegBytes source) => new JpegDecoder(source, false).Run();

    /// <summary>בדיקה עם שמירת מצב בכל יחידה — כדי לחפש אחר כך את המשך הקובץ.</summary>
    internal static (JpegCheck Check, JpegDecoder Decoder) CheckWithCheckpoints(JpegBytes source)
    {
        var d = new JpegDecoder(source, true);
        return (d.Run(), d);
    }

    /// <summary>
    /// המשך פענוח מנקודת שמירה של מפענח קודם, מול מקור אחר. הטבלאות והמסגרת
    /// נלקחות מהמפענח הקודם — הן בכותרת, שנמצאת במקטע הראשון.
    /// </summary>
    internal JpegCheck ResumeFrom(Checkpoint c, JpegBytes source)
    {
        var d = new JpegDecoder(source, false);
        Array.Copy(_dcTables, d._dcTables, 4);
        Array.Copy(_acTables, d._acTables, 4);
        d._components.AddRange(_components);
        (d._width, d._height, d._hMax, d._vMax, d._restartInterval, d._frameSeen) =
            (_width, _height, _hMax, _vMax, _restartInterval, _frameSeen);
        d._scan = _scan;
        d._mcusTotalFirstScan = _mcusTotalFirstScan;
        d.ScanStart = ScanStart;
        (d._pos, d._acc, d._accBits, d._mcu, d._nextRestart) = (c.Pos, (ulong)c.Buffer, c.Bits, c.Mcu, c.NextRestart);
        d._mcusDoneAllScans = c.Mcu;
        return d.DecodeScans();
    }

    private JpegCheck Result(JpegVerdict verdict, long offset)
        => new(verdict, offset, _mcusDoneAllScans, _mcusTotalFirstScan);

    private JpegCheck Run()
    {
        if (_src.At(0) != 0xFF || _src.At(1) != 0xD8) return Result(JpegVerdict.Corrupt, 0);
        _pos = 2;

        var header = ReadHeaders();
        if (header is not null) return header.Value;
        return DecodeScans();
    }

    // ------------------------------------------------------------ כותרות

    /// <summary>
    /// קריאת מקטעים עד SOS (אז null — להתחיל לפענח) או עד תוצאה סופית.
    /// </summary>
    private JpegCheck? ReadHeaders()
    {
        while (true)
        {
            // בתי FF מרובים לפני סימן הם ריפוד חוקי.
            if (_src.At(_pos) != 0xFF) return Result(JpegVerdict.Corrupt, _pos);
            while (_src.At(_pos + 1) == 0xFF) _pos++;
            int marker = _src.At(_pos + 1);
            if (marker < 0) return Result(JpegVerdict.Corrupt, _pos);
            _pos += 2;

            if (marker == 0xD9)
                return _scan is null
                    ? Result(JpegVerdict.Corrupt, _pos)     // סיום בלי אף סריקה
                    : Result(JpegVerdict.Complete, _pos);

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) continue;   // סימנים בלי אורך

            int hi = _src.At(_pos), lo = _src.At(_pos + 1);
            if (hi < 0 || lo < 0) return Result(JpegVerdict.Corrupt, _pos);
            int length = (hi << 8) | lo;
            if (length < 2) return Result(JpegVerdict.Corrupt, _pos);
            long body = _pos + 2, end = _pos + length;

            switch (marker)
            {
                case 0xC0 or 0xC1:
                    if (!ReadFrame(body)) return Result(JpegVerdict.Unsupported, _pos);
                    break;

                // פרוגרסיבי, ללא אובדן, היררכי ואריתמטי — מחוץ לתחום המפענח.
                case 0xC2 or 0xC3 or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF):
                    return Result(JpegVerdict.Unsupported, _pos);

                case 0xC4:
                    if (!ReadHuffman(body, end)) return Result(JpegVerdict.Corrupt, _pos);
                    break;

                case 0xDD:
                    if (length != 4) return Result(JpegVerdict.Corrupt, _pos);
                    _restartInterval = (_src.At(body) << 8) | _src.At(body + 1);
                    break;

                case 0xDA:
                    var scan = ReadScan(body);
                    if (scan is null) return Result(JpegVerdict.Unsupported, _pos);
                    _scan = scan;
                    _pos = end;
                    if (ScanStart < 0) { ScanStart = _pos; _mcusTotalFirstScan = scan.McusTotal; }
                    _mcu = 0;
                    _nextRestart = 0;
                    _acc = 0;
                    _accBits = 0;
                    _markerAhead = false;
                    return null;

                case 0xDC:
                    return Result(JpegVerdict.Unsupported, _pos);    // DNL — גובה שנקבע רק בסוף

                default:
                    break;   // APPn, COM, DQT ושאר המקטעים — לא נחוצים לאימות
            }

            if (_src.At(end - 1) < 0) return Result(JpegVerdict.Corrupt, _pos);
            _pos = end;
        }
    }

    private bool ReadFrame(long at)
    {
        if (_src.At(at) != 8) return false;          // דיוק 12 ביט — נדיר, מחוץ לתחום
        _height = (_src.At(at + 1) << 8) | _src.At(at + 2);
        _width = (_src.At(at + 3) << 8) | _src.At(at + 4);
        int count = _src.At(at + 5);
        if (_width <= 0 || _height <= 0 || count is < 1 or > 4) return false;

        _components.Clear();
        for (int i = 0; i < count; i++)
        {
            long c = at + 6 + i * 3;
            int sampling = _src.At(c + 1);
            int h = sampling >> 4, v = sampling & 15;
            if (h is < 1 or > 4 || v is < 1 or > 4) return false;
            _components.Add(new Component(_src.At(c), h, v));
        }

        _hMax = _components.Max(c => c.H);
        _vMax = _components.Max(c => c.V);
        _frameSeen = true;
        return true;
    }

    private bool ReadHuffman(long at, long end)
    {
        Span<byte> counts = stackalloc byte[16];
        while (at < end)
        {
            int info = _src.At(at);
            if (info < 0) return false;
            int tableClass = info >> 4, id = info & 15;
            if (tableClass > 1 || id > 3) return false;

            int total = 0;
            for (int i = 0; i < 16; i++)
            {
                int n = _src.At(at + 1 + i);
                if (n < 0) return false;
                counts[i] = (byte)n;
                total += n;
            }
            if (total > 256) return false;

            var values = new byte[total];
            for (int i = 0; i < total; i++)
            {
                int v = _src.At(at + 17 + i);
                if (v < 0) return false;
                values[i] = (byte)v;
            }

            var table = Huffman.Build(counts, values);
            if (table is null) return false;
            (tableClass == 0 ? _dcTables : _acTables)[id] = table;
            at += 17 + total;
        }
        return at == end;
    }

    private Scan? ReadScan(long at)
    {
        if (!_frameSeen) return null;
        int count = _src.At(at);
        if (count is < 1 or > 4) return null;

        var components = new ScanComponent[count];
        for (int i = 0; i < count; i++)
        {
            int id = _src.At(at + 1 + i * 2), tables = _src.At(at + 2 + i * 2);
            var component = _components.FirstOrDefault(c => c.Id == id);
            if (component is null || tables < 0) return null;

            // בלי טבלת הופמן אין מה לאמת (כך למשל בפריימים של MJPEG).
            var dc = _dcTables[(tables >> 4) & 3];
            var ac = _acTables[tables & 3];
            if (dc is null || ac is null) return null;
            components[i] = new ScanComponent(component, dc, ac);
        }

        long p = at + 1 + count * 2;
        int ss = _src.At(p), se = _src.At(p + 1);
        if (ss != 0 || se != 63) return null;        // לא בסיסי

        int mcus;
        if (count == 1)
        {
            // סריקה של רכיב יחיד: כל יחידה היא בלוק אחד, לפי ממדי הרכיב עצמו.
            var c = components[0].Component;
            int w = (int)Math.Ceiling(_width * c.H / (double)_hMax);
            int h = (int)Math.Ceiling(_height * c.V / (double)_vMax);
            mcus = ((w + 7) / 8) * ((h + 7) / 8);
        }
        else
        {
            mcus = ((_width + 8 * _hMax - 1) / (8 * _hMax)) * ((_height + 8 * _vMax - 1) / (8 * _vMax));
        }

        return new Scan(components, mcus, _restartInterval);
    }

    // ------------------------------------------------------------ נתונים דחוסים

    /// <summary>
    /// מילוי מראש של הצובר עד 56 ביטים. בית FF 00 הוא FF אחד בנתונים;
    /// FF עם כל ערך אחר הוא סימן — והמילוי נעצר לפניו. באמצע יחידה, סימן
    /// פירושו שהנתונים אינם של התמונה; במקומו (RST, סוף) הוא נקרא בנפרד.
    /// המיקום של כל בית נשמר, כדי שאפשר יהיה לחזור לבית הראשון שלא נצרך.
    /// </summary>
    private void Fill()
    {
        while (_accBits <= 56 && !_markerAhead)
        {
            int b = _src.At(_pos);
            if (b < 0) return;

            long at = _pos;
            if (b == 0xFF)
            {
                int next = _src.At(_pos + 1);
                if (next != 0x00)
                {
                    if (next >= 0) _markerAhead = true;
                    return;
                }
                _pos += 2;
            }
            else
            {
                _pos++;
            }

            _acc = (_acc << 8) | (uint)b;
            _accBits += 8;
            _origin[_appended++ & 7] = at;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadBit()
    {
        if (_accBits == 0) Fill();
        if (_accBits == 0) return -1;
        _accBits--;
        return (int)(_acc >> _accBits) & 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Skip(int count)
    {
        if (_accBits < count) Fill();
        if (_accBits < count) return false;
        _accBits -= count;
        return true;
    }

    /// <summary>
    /// חזרה לגבול בית: ביטי הריפוד של הבית הנוכחי נזרקים, ובתים שנקראו מראש
    /// ועוד לא נצרכו "מוחזרים" — הקריאה הבאה תתחיל מהראשון שבהם.
    /// </summary>
    private void Align()
    {
        int whole = _accBits >> 3;
        if (whole > 0) _pos = _origin[(_appended - whole) & 7];
        _acc = 0;
        _accBits = 0;
        _markerAhead = false;
    }

    /// <summary>
    /// המצב לנקודת שמירה: בלי בתים שנקראו מראש. כך כל הבתים שהמצב נשען עליהם
    /// יושבים לפני Pos — ואפשר להמשיך מהמצב הזה מול מקור שמשתנה מ-Pos והלאה.
    /// </summary>
    private Checkpoint Snapshot()
    {
        int whole = _accBits >> 3;
        if (whole > 0)
        {
            _pos = _origin[(_appended - whole) & 7];
            _accBits &= 7;
            _acc >>= whole * 8;
            _markerAhead = false;
        }
        return new Checkpoint(_pos, (int)(_acc & ((1UL << _accBits) - 1)), _accBits, _mcu, _nextRestart);
    }


    /// <summary>
    /// פענוח בלוק 8×8 אחד. הערכים עצמם אינם נשמרים — רק נבדק שהרצף חוקי:
    /// קודים שקיימים בטבלה, גדלים עד 11 ביט, ומקדמים שאינם חורגים מ-64.
    /// </summary>
    private bool DecodeBlock(ScanComponent sc)
    {
        int t = sc.Dc.Decode(this);
        if (t is < 0 or > 11 || !Skip(t)) return false;

        for (int k = 1; k < 64;)
        {
            int rs = sc.Ac.Decode(this);
            if (rs < 0) return false;
            int run = rs >> 4, size = rs & 15;

            if (size == 0)
            {
                if (run != 15) break;       // EOB
                k += 16;                    // ZRL — שישה-עשר אפסים
                if (k > 64) return false;
                continue;
            }

            k += run;
            if (k > 63 || size > 10 || !Skip(size)) return false;
            k++;
        }
        return true;
    }

    private bool DecodeMcu()
    {
        var scan = _scan!;
        if (scan.Components.Length == 1) return DecodeBlock(scan.Components[0]);

        foreach (var sc in scan.Components)
            for (int b = 0; b < sc.Component.H * sc.Component.V; b++)
                if (!DecodeBlock(sc)) return false;
        return true;
    }

    /// <summary>
    /// סימן RST במקומו: יישור לבית, ואז FF D0–D7 לפי הסדר. גם סימן נכון במקום
    /// הנכון הוא אימות חזק — נתונים זרים כמעט לעולם לא ייראו כך.
    /// </summary>
    private bool ReadRestart()
    {
        Align();
        if (_src.At(_pos) != 0xFF) return false;
        while (_src.At(_pos + 1) == 0xFF) _pos++;
        if (_src.At(_pos + 1) != 0xD0 + _nextRestart) return false;
        _pos += 2;
        _nextRestart = (_nextRestart + 1) & 7;
        return true;
    }

    private JpegCheck DecodeScans()
    {
        while (true)
        {
            var scan = _scan!;
            while (_mcu < scan.McusTotal)
            {
                if (scan.RestartInterval > 0 && _mcu > 0 && _mcu % scan.RestartInterval == 0 && !ReadRestart())
                    return Result(JpegVerdict.Corrupt, _pos);

                if (Checkpoints is not null) Checkpoints.Add(Snapshot());

                if (!DecodeMcu())
                    return Result(JpegVerdict.Corrupt, _pos);

                _mcu++;
                _mcusDoneAllScans++;
            }

            // סוף הסריקה: ביטים של ריפוד עד סוף הבית, ואז הסימן הבא.
            Align();
            Checkpoints?.Clear();

            var next = ReadHeaders();
            if (next is not null) return next.Value;
        }
    }
}
