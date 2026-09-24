namespace RAF.Core.Repair;

/// <summary>
/// מדידת האורך של מסגרת קול AAC (raw_data_block, ISO/IEC 14496-3) בלי לפענח אותה.
///
/// בקובץ MP4 מסגרות הקול צמודות זו לזו בלי כותרת ובלי אורך — מה שמפריד ביניהן
/// הוא רק המבנה של כל אחת. לכן כדי לדעת היכן מסגרת נגמרת, עוברים על כל השדות
/// שלה כמו המפענח: הקטעים, מקדמי קנה המידה וערכי הספקטרום בקודי הופמן. נתונים
/// שאינם קול נכשלים מהר (קוד שאינו קיים, רצועה מחוץ לטווח), וכך המדידה היא גם בדיקה.
/// נתמך AAC-LC, כולל HE-AAC (התוספת שלו יושבת בשדות מילוי שאורכם ידוע).
/// </summary>
internal static class AacFrame
{
    private const int EightShortSequence = 2;
    private const int MaxElements = 64;

    /// <summary>אורך המסגרת בבתים, או ‎-1 כשהנתונים אינם מסגרת AAC תקינה.</summary>
    public static int Length(ReadOnlySpan<byte> data, int samplingIndex)
    {
        if (samplingIndex is < 0 or > 12 || data.Length < 2) return -1;
        var r = new BitReader(data);
        int audioElements = 0;

        for (int e = 0; e < MaxElements; e++)
        {
            int id = r.Bits(3);
            switch (id)
            {
                case 0:     // SCE — ערוץ אחד
                case 3:     // LFE — ערוץ בס
                    r.Bits(4);
                    if (!Channel(ref r, samplingIndex, commonWindow: false, default)) return -1;
                    audioElements++;
                    break;

                case 1:     // CPE — זוג ערוצים
                {
                    r.Bits(4);
                    bool common = r.Bits(1) == 1;
                    IcsInfo info = default;
                    if (common)
                    {
                        if (!ReadIcsInfo(ref r, samplingIndex, out info)) return -1;
                        int ms = r.Bits(2);
                        if (ms == 3) return -1;                              // ערך שמור
                        if (ms == 1) r.Skip(info.Groups * info.MaxSfb);
                    }
                    if (!Channel(ref r, samplingIndex, common, info)) return -1;
                    if (!Channel(ref r, samplingIndex, common, info)) return -1;
                    audioElements++;
                    break;
                }

                case 4:     // DSE — נתונים נלווים
                {
                    r.Bits(4);
                    bool align = r.Bits(1) == 1;
                    int count = r.Bits(8);
                    if (count == 255) count += r.Bits(8);
                    if (align) r.ByteAlign();
                    r.Skip(count * 8);
                    break;
                }

                case 6:     // FIL — מילוי, וגם התוספת של HE-AAC
                {
                    int count = r.Bits(4);
                    if (count == 15) count += r.Bits(8) - 1;
                    r.Skip(count * 8);
                    break;
                }

                case 7:     // END
                    if (r.Failed || audioElements == 0) return -1;
                    r.ByteAlign();
                    return r.Failed ? -1 : r.BytePosition;

                default:    // CCE ו-PCE — נדירים מאוד בתוך סרטון; לא נתמכים
                    return -1;
            }

            if (r.Failed) return -1;
        }

        return -1;
    }

    private struct IcsInfo
    {
        public bool Short;
        public int MaxSfb;
        public int Groups;
        public int[] GroupLength;
    }

    private static bool ReadIcsInfo(ref BitReader r, int sf, out IcsInfo info)
    {
        info = default;
        if (r.Bits(1) != 0) return false;                                    // ics_reserved_bit
        int sequence = r.Bits(2);
        r.Bits(1);                                                           // window_shape

        if (sequence == EightShortSequence)
        {
            info.Short = true;
            info.MaxSfb = r.Bits(4);
            int grouping = r.Bits(7);
            var lengths = new int[8];
            int groups = 1;
            lengths[0] = 1;
            for (int i = 6; i >= 0; i--)
            {
                if ((grouping >> i & 1) == 1) lengths[groups - 1]++;
                else lengths[groups++] = 1;
            }
            info.Groups = groups;
            info.GroupLength = lengths;
            if (info.MaxSfb > AacTables.NumSwbShort[sf]) return false;
        }
        else
        {
            info.MaxSfb = r.Bits(6);
            if (r.Bits(1) != 0) return false;                                // predictor — לא ב-AAC-LC
            info.Groups = 1;
            info.GroupLength = new[] { 1 };
            if (info.MaxSfb > AacTables.NumSwbLong[sf]) return false;
        }

        return !r.Failed;
    }

    /// <summary>individual_channel_stream — ערוץ אחד: קטעים, מקדמים, ספקטרום.</summary>
    private static bool Channel(ref BitReader r, int sf, bool commonWindow, IcsInfo shared)
    {
        r.Bits(8);                                                           // global_gain
        IcsInfo info = shared;
        if (!commonWindow && !ReadIcsInfo(ref r, sf, out info)) return false;

        // --- section_data: איזה ספר קוד לכל רצועה
        int lengthBits = info.Short ? 3 : 5;
        int escape = (1 << lengthBits) - 1;
        var codebook = new byte[info.Groups * Math.Max(1, info.MaxSfb)];

        for (int g = 0; g < info.Groups; g++)
        {
            for (int k = 0; k < info.MaxSfb;)
            {
                int cb = r.Bits(4);
                if (cb == 12) return false;                                  // שמור
                int length = 0, increment;
                do
                {
                    increment = r.Bits(lengthBits);
                    length += increment;
                    if (r.Failed) return false;
                } while (increment == escape);

                if (length == 0 || k + length > info.MaxSfb) return false;
                for (int i = 0; i < length; i++) codebook[g * info.MaxSfb + k + i] = (byte)cb;
                k += length;
            }
        }

        // --- scale_factor_data
        bool firstNoise = true;
        for (int g = 0; g < info.Groups; g++)
            for (int b = 0; b < info.MaxSfb; b++)
            {
                int cb = codebook[g * info.MaxSfb + b];
                if (cb == 0) continue;
                if (cb == 13 && firstNoise)
                {
                    firstNoise = false;
                    r.Bits(9);
                    continue;
                }
                if (Huffman.Scalefactor.Decode(ref r) < 0) return false;
            }

        // --- pulse_data
        if (r.Bits(1) == 1)
        {
            if (info.Short) return false;
            int pulses = r.Bits(2) + 1;
            r.Bits(6);
            r.Skip(pulses * 9);
        }

        // --- tns_data
        if (r.Bits(1) == 1)
        {
            int windows = info.Short ? 8 : 1;
            for (int w = 0; w < windows; w++)
            {
                int filters = r.Bits(info.Short ? 1 : 2);
                if (filters == 0) continue;
                int resolution = r.Bits(1);
                for (int f = 0; f < filters; f++)
                {
                    r.Bits(info.Short ? 4 : 6);
                    int order = r.Bits(info.Short ? 3 : 5);
                    if (order == 0) continue;
                    r.Bits(1);
                    int compress = r.Bits(1);
                    r.Skip(order * (3 + resolution - compress));
                }
            }
        }

        if (r.Bits(1) == 1) return false;                                    // gain_control — לא ב-AAC-LC

        // --- spectral_data
        var offsets = info.Short ? AacTables.SwbOffsetShort[sf] : AacTables.SwbOffsetLong[sf];
        for (int g = 0; g < info.Groups; g++)
            for (int b = 0; b < info.MaxSfb; b++)
            {
                int cb = codebook[g * info.MaxSfb + b];
                if (cb is 0 or > 11) continue;                               // אפס, רעש, או סטריאו מרחבי

                int width = (offsets[b + 1] - offsets[b]) * info.GroupLength[g];
                int step = cb < 5 ? 4 : 2;
                var book = Huffman.Spectrum[cb - 1];
                for (int k = 0; k < width; k += step)
                {
                    int index = book.Decode(ref r);
                    if (index < 0) return false;
                    if (!SignsAndEscapes(ref r, cb, index)) return false;
                }
                if (r.Failed) return false;
            }

        return !r.Failed;
    }

    /// <summary>
    /// אחרי מילת הקוד: סיבית סימן לכל ערך שאינו אפס בספרים שאינם מסומנים,
    /// ובספר 11 — רצף בריחה לכל ערך 16.
    /// </summary>
    private static bool SignsAndEscapes(ref BitReader r, int cb, int index)
    {
        switch (cb)
        {
            case 1: case 2: case 5: case 6:
                return true;                                                 // הערכים מסומנים בקוד עצמו

            case 3: case 4:
                for (int i = 0, v = index; i < 4; i++, v /= 3)
                    if (v % 3 != 0) r.Skip(1);
                return true;

            default:
            {
                int mod = cb switch { 7 or 8 => 8, 9 or 10 => 13, _ => 17 };
                int first = index / mod, second = index % mod;
                if (first != 0) r.Skip(1);
                if (second != 0) r.Skip(1);
                if (cb == 11)
                {
                    if (first == 16 && !Escape(ref r)) return false;
                    if (second == 16 && !Escape(ref r)) return false;
                }
                return true;
            }
        }
    }

    /// <summary>escape_sequence: N סיביות 1, אפס, ואז N+4 סיביות של הערך.</summary>
    private static bool Escape(ref BitReader r)
    {
        int n = 0;
        while (r.Bits(1) == 1)
            if (++n > 8 || r.Failed) return false;
        r.Skip(n + 4);
        return !r.Failed;
    }

    // ------------------------------------------------------------ קודי הופמן

    /// <summary>עץ הופמן במערכים: מעבר סיבית אחר סיבית מהשורש עד עלה.</summary>
    private sealed class Huffman
    {
        private readonly int[] _next;      // לכל צומת: ילד 0 ב-2n, ילד 1 ב-2n+1. ערך שלילי — עלה (‎-1-אינדקס).

        private Huffman(IReadOnlyList<uint> codes, IReadOnlyList<byte> bits)
        {
            var next = new List<int> { 0, 0 };
            for (int symbol = 0; symbol < codes.Count; symbol++)
            {
                int node = 0;
                for (int i = bits[symbol] - 1; i >= 0; i--)
                {
                    int slot = node * 2 + (int)(codes[symbol] >> i & 1);
                    if (i == 0)
                    {
                        next[slot] = -1 - symbol;
                        break;
                    }
                    if (next[slot] <= 0)
                    {
                        next[slot] = next.Count / 2;
                        next.Add(0);
                        next.Add(0);
                    }
                    node = next[slot];
                }
            }
            _next = next.ToArray();
        }

        public int Decode(ref BitReader r)
        {
            int node = 0;
            for (int depth = 0; depth < 20; depth++)
            {
                int value = _next[node * 2 + r.Bits(1)];
                if (r.Failed) return -1;
                if (value < 0) return -1 - value;
                if (value == 0) return -1;                                   // קוד שאינו קיים
                node = value;
            }
            return -1;
        }

        public static readonly Huffman Scalefactor = new(AacTables.ScalefactorCodes, AacTables.ScalefactorBits);

        public static readonly Huffman[] Spectrum = Enumerable.Range(0, 11)
            .Select(i => new Huffman(AacTables.SpectrumCodes[i].Select(c => (uint)c).ToArray(), AacTables.SpectrumBits[i]))
            .ToArray();
    }

    /// <summary>קריאת סיביות מהמשמעותית ביותר. קריאה מעבר לסוף מסמנת כישלון ומחזירה אפסים.</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private long _bit;

        public BitReader(ReadOnlySpan<byte> data) { _data = data; _bit = 0; Failed = false; }

        public bool Failed { get; private set; }
        public int BytePosition => (int)((_bit + 7) >> 3);

        public int Bits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                long at = _bit >> 3;
                if (at >= _data.Length) { Failed = true; return 0; }
                value = value << 1 | (_data[(int)at] >> (7 - (int)(_bit & 7)) & 1);
                _bit++;
            }
            return value;
        }

        public void Skip(long count)
        {
            _bit += count;
            if (_bit > (long)_data.Length * 8) Failed = true;
        }

        public void ByteAlign() => _bit = (_bit + 7) & ~7L;
    }
}
