using System.Buffers.Binary;

namespace RAF.Core.Repair;

/// <summary>תמונה אחת (access unit) שנמצאה בנתונים: אורכה, האם היא תמונת מפתח, וסדר התצוגה שלה.</summary>
internal readonly record struct VideoFrame(int Length, bool Key, bool Idr, int PocLsb, bool Reference);

/// <summary>
/// זיהוי תמונות H.264 / H.265 בתוך נתוני MP4. בקובץ כזה כל תמונה היא רצף של יחידות NAL,
/// כל אחת עם שדה אורך לפניה. תמונה נגמרת כשמתחילה הבאה — יחידת פרמטרים, או פרוסה
/// ראשונה של תמונה חדשה — או כשהנתונים שאחריה אינם NAL (קול, או אזור פגום).
///
/// מכאן נלקח גם סדר התצוגה (POC): בסרטון עם תמונות B התמונות שמורות בסדר הפענוח,
/// וסדר התצוגה כתוב בכותרת של כל תמונה. בלעדיו התנועה הייתה מקרטעת.
/// </summary>
internal sealed class VideoFrames
{
    private readonly bool _hevc;
    private readonly int _lengthSize;
    private readonly int _maxFrame;

    // פרמטרים מה-SPS/PPS שנדרשים לקריאת כותרת הפרוסה.
    private int _log2MaxFrameNum = 4, _pocType, _log2MaxPocLsb = 4;
    private bool _frameMbsOnly = true, _separateColourPlane, _bottomFieldPicOrder;
    private int _extraSliceHeaderBits;
    private bool _outputFlagPresent;

    public VideoFrames(bool hevc, int lengthSize, IEnumerable<byte[]> parameterSets, int maxFrame)
    {
        _hevc = hevc;
        _lengthSize = lengthSize is >= 1 and <= 4 ? lengthSize : 4;
        _maxFrame = maxFrame;
        foreach (var set in parameterSets) Learn(set);
    }

    /// <summary>סוג ה-POC של H.264: 2 — סדר התצוגה זהה לסדר הפענוח.</summary>
    public bool DisplayOrderIsDecodeOrder => !_hevc && _pocType == 2;
    public int MaxPocLsb => 1 << _log2MaxPocLsb;

    /// <summary>התמונה שמתחילה בתחילת הנתונים, או null כשאין שם תמונה תקינה.</summary>
    public VideoFrame? Read(ReadOnlySpan<byte> data)
    {
        int at = 0;
        bool sawSlice = false, key = false, idr = false, reference = false;
        int poc = 0;

        while (at + _lengthSize < data.Length)
        {
            long length = 0;
            for (int i = 0; i < _lengthSize; i++) length = length << 8 | data[at + i];
            int body = at + _lengthSize;
            int minimum = _hevc ? 3 : 2;

            bool fits = length >= minimum && length <= _maxFrame && body + length <= data.Length;
            if (!fits)
            {
                if (!sawSlice) return null;
                break;                                                       // מה שאחרי התמונה אינו NAL
            }

            var nal = data.Slice(body, (int)length);
            var kind = Classify(nal, out bool firstSlice, out bool nalReference);
            if (kind == NalKind.Invalid)
            {
                if (!sawSlice) return null;
                break;
            }

            // תחילת התמונה הבאה: יחידת פרמטרים או גבול, או פרוסה ראשונה של תמונה חדשה.
            if (sawSlice && (kind is NalKind.Boundary or NalKind.Parameters || (kind == NalKind.Slice && firstSlice)))
                break;

            if (kind == NalKind.Parameters) Learn(nal.ToArray());

            if (kind == NalKind.Slice)
            {
                if (!sawSlice)
                {
                    if (!firstSlice) return null;                            // תמונה מתחילה בפרוסה הראשונה שלה
                    if (!SliceHeader(nal, out key, out idr, out poc)) return null;
                    reference = nalReference;
                }
                sawSlice = true;
            }

            at = body + (int)length;
            if (at > _maxFrame) return null;
        }

        return sawSlice ? new VideoFrame(at, key, idr, poc, reference) : null;
    }

    private enum NalKind { Invalid, Slice, Parameters, Boundary, Trailing }

    /// <summary>סוג יחידת ה-NAL, וכשזו פרוסה — האם היא הראשונה בתמונה.</summary>
    private NalKind Classify(ReadOnlySpan<byte> nal, out bool firstSlice, out bool reference)
    {
        firstSlice = false;
        reference = false;

        if (_hevc)
        {
            if ((nal[0] & 0x80) != 0) return NalKind.Invalid;
            int type = nal[0] >> 1 & 0x3F;
            int layer = (nal[0] & 1) << 5 | nal[1] >> 3;
            int temporal = nal[1] & 7;
            if (layer != 0 || temporal == 0) return NalKind.Invalid;

            if (type is <= 9 or (>= 16 and <= 21))
            {
                firstSlice = (nal[2] & 0x80) != 0;
                // תמונות שאינן משמשות לייחוס: הסוגים הזוגיים מתחת ל-16 (_N).
                reference = !(type <= 14 && type % 2 == 0);
                return NalKind.Slice;
            }
            return type switch
            {
                32 or 33 or 34 => NalKind.Parameters,
                35 or 39 or 41 or 42 or 43 or 44 => NalKind.Boundary,   // AUD, SEI קודם, שמורים לתחילה
                36 or 37 or 38 or 40 => NalKind.Trailing,               // סוף רצף/זרם, מילוי, SEI סופי
                >= 48 and <= 55 => NalKind.Boundary,
                _ => NalKind.Invalid,
            };
        }
        else
        {
            if ((nal[0] & 0x80) != 0) return NalKind.Invalid;
            int refIdc = nal[0] >> 5 & 3;
            int type = nal[0] & 0x1F;
            reference = refIdc != 0;

            switch (type)
            {
                case 1:
                case 5:
                    if (type == 5 && refIdc == 0) return NalKind.Invalid;
                    firstSlice = (nal[1] & 0x80) != 0;                       // first_mb_in_slice == 0
                    return NalKind.Slice;
                case 7:
                case 8:
                    return refIdc == 0 ? NalKind.Invalid : NalKind.Parameters;
                case 6:
                case 9:
                    return refIdc != 0 ? NalKind.Invalid : NalKind.Boundary;
                case 10:
                case 11:
                case 12:
                    return refIdc != 0 ? NalKind.Invalid : NalKind.Trailing;
                case 13:
                case 14:
                case 15:
                    return NalKind.Boundary;
                default:
                    return NalKind.Invalid;                                  // 2–4 (מחיצות נתונים) ו-16+ אינם בשימוש
            }
        }
    }

    // ------------------------------------------------------------ כותרות

    /// <summary>למידת הפרמטרים מ-SPS / PPS (מקובץ הייחוס, או שנמצאו בנתונים עצמם).</summary>
    private void Learn(byte[] nal)
    {
        try
        {
            if (_hevc) LearnHevc(nal);
            else LearnAvc(nal);
        }
        catch (IndexOutOfRangeException)
        {
            // פרמטרים קטועים — נשארים הקודמים.
        }
    }

    private void LearnAvc(byte[] nal)
    {
        int type = nal[0] & 0x1F;
        var r = new Rbsp(nal, 1);
        if (type == 7)
        {
            int profile = r.Bits(8);
            r.Bits(16);
            r.Ue();
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                int chroma = r.Ue();
                if (chroma == 3) _separateColourPlane = r.Bits(1) == 1;
                r.Ue();
                r.Ue();
                r.Bits(1);
                if (r.Bits(1) == 1)
                    for (int i = 0; i < (chroma == 3 ? 12 : 8); i++)
                        if (r.Bits(1) == 1) SkipScalingList(ref r, i < 6 ? 16 : 64);
            }
            _log2MaxFrameNum = r.Ue() + 4;
            _pocType = r.Ue();
            if (_pocType == 0) _log2MaxPocLsb = r.Ue() + 4;
            else if (_pocType == 1)
            {
                r.Bits(1);
                r.Se();
                r.Se();
                int cycle = r.Ue();
                for (int i = 0; i < cycle; i++) r.Se();
            }
            r.Ue();
            r.Bits(1);
            r.Ue();
            r.Ue();
            _frameMbsOnly = r.Bits(1) == 1;
        }
        else if (type == 8)
        {
            r.Ue();
            r.Ue();
            r.Bits(1);
            _bottomFieldPicOrder = r.Bits(1) == 1;
        }
    }

    private static void SkipScalingList(ref Rbsp r, int size)
    {
        int last = 8, next = 8;
        for (int j = 0; j < size; j++)
        {
            if (next != 0) next = (last + r.Se() + 256) % 256;
            last = next == 0 ? last : next;
        }
    }

    private void LearnHevc(byte[] nal)
    {
        int type = nal[0] >> 1 & 0x3F;
        var r = new Rbsp(nal, 2);
        if (type == 33)
        {
            r.Bits(4);
            int subLayers = r.Bits(3);
            r.Bits(1);
            // profile_tier_level: 88 סיביות פרופיל ו-8 של רמה, ואחריהם תת-שכבות.
            r.Bits(32); r.Bits(32); r.Bits(32);
            var profilePresent = new bool[8];
            var levelPresent = new bool[8];
            for (int i = 0; i < subLayers; i++)
            {
                profilePresent[i] = r.Bits(1) == 1;
                levelPresent[i] = r.Bits(1) == 1;
            }
            if (subLayers > 0)
                for (int i = subLayers; i < 8; i++) r.Bits(2);
            for (int i = 0; i < subLayers; i++)
            {
                if (profilePresent[i]) { r.Bits(32); r.Bits(32); r.Bits(24); }
                if (levelPresent[i]) r.Bits(8);
            }
            r.Ue();
            int chroma = r.Ue();
            if (chroma == 3) _separateColourPlane = r.Bits(1) == 1;
            r.Ue();
            r.Ue();
            if (r.Bits(1) == 1) { r.Ue(); r.Ue(); r.Ue(); r.Ue(); }
            r.Ue();
            r.Ue();
            _log2MaxPocLsb = r.Ue() + 4;
        }
        else if (type == 34)
        {
            r.Ue();
            r.Ue();
            r.Bits(1);                                                       // dependent_slice_segments_enabled
            _outputFlagPresent = r.Bits(1) == 1;
            _extraSliceHeaderBits = r.Bits(3);
        }
    }

    /// <summary>כותרת הפרוסה הראשונה: סוג התמונה, ו-pic_order_cnt_lsb.</summary>
    private bool SliceHeader(ReadOnlySpan<byte> nal, out bool key, out bool idr, out int pocLsb)
    {
        key = idr = false;
        pocLsb = 0;
        try
        {
            if (_hevc)
            {
                int type = nal[0] >> 1 & 0x3F;
                key = type is >= 16 and <= 21;
                idr = type is 19 or 20;
                var r = new Rbsp(nal.ToArray(), 2);
                r.Bits(1);                                                   // first_slice_segment_in_pic_flag
                if (key) r.Bits(1);                                          // no_output_of_prior_pics_flag
                if (r.Ue() > 63) return false;                               // pps_id
                r.Bits(_extraSliceHeaderBits);
                if (r.Ue() > 2) return false;                                // slice_type: B / P / I
                if (_outputFlagPresent) r.Bits(1);
                if (_separateColourPlane) r.Bits(2);
                if (!idr) pocLsb = r.Bits(_log2MaxPocLsb);
                return true;
            }
            else
            {
                int type = nal[0] & 0x1F;
                idr = key = type == 5;
                var r = new Rbsp(nal.ToArray(), 1);
                r.Ue();                                                      // first_mb_in_slice
                if (r.Ue() > 9) return false;                                // slice_type
                if (r.Ue() > 255) return false;                              // pps_id
                if (_separateColourPlane) r.Bits(2);
                r.Bits(_log2MaxFrameNum);
                if (!_frameMbsOnly && r.Bits(1) == 1) r.Bits(1);
                if (idr) r.Ue();
                if (_pocType == 0) pocLsb = r.Bits(_log2MaxPocLsb);
                return true;
            }
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>קריאת סיביות מתוכן NAL, בדילוג על בתי המניעה (00 00 03).</summary>
    private ref struct Rbsp
    {
        private readonly byte[] _data;
        private int _byte, _bit, _zeros;

        public Rbsp(byte[] data, int start) { _data = data; _byte = start; _bit = 0; _zeros = 0; }

        private int Bit()
        {
            if (_bit == 0)
            {
                // בית 03 אחרי שני אפסים הוא בית מניעה — לא חלק מהנתונים.
                if (_zeros >= 2 && _data[_byte] == 3) { _byte++; _zeros = 0; }
                _zeros = _data[_byte] == 0 ? _zeros + 1 : 0;
            }
            int value = _data[_byte] >> (7 - _bit) & 1;
            if (++_bit == 8) { _bit = 0; _byte++; }
            return value;
        }

        public int Bits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++) v = v << 1 | Bit();
            return v;
        }

        public int Ue()
        {
            int zeros = 0;
            while (Bit() == 0)
                if (++zeros > 31) throw new IndexOutOfRangeException();
            return (int)((1L << zeros) - 1 + Bits(zeros));
        }

        public int Se()
        {
            int k = Ue();
            return (k & 1) == 1 ? (k + 1) / 2 : -(k / 2);
        }
    }

    /// <summary>
    /// סדר התצוגה של רצף תמונות בסדר הפענוח — חישוב ה-POC המלא מה-lsb (כמו בתקן),
    /// ודירוג בתוך כל קטע שמתחיל בתמונת IDR.
    /// </summary>
    public int[] DisplayOrder(IReadOnlyList<VideoFrame> frames)
    {
        var order = new int[frames.Count];
        if (DisplayOrderIsDecodeOrder || frames.Count == 0)
        {
            for (int i = 0; i < order.Length; i++) order[i] = i;
            return order;
        }

        int max = MaxPocLsb;
        long prevMsb = 0;
        int prevLsb = 0;
        var poc = new long[frames.Count];
        var segment = new int[frames.Count];
        int seg = 0;

        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (f.Idr)
            {
                prevMsb = 0;
                prevLsb = 0;
                if (i > 0) seg++;
            }
            long msb = f.PocLsb < prevLsb && prevLsb - f.PocLsb >= max / 2 ? prevMsb + max
                     : f.PocLsb > prevLsb && f.PocLsb - prevLsb > max / 2 ? prevMsb - max
                     : prevMsb;
            poc[i] = msb + f.PocLsb;
            segment[i] = seg;
            if (f.Reference || f.Idr)
            {
                prevMsb = msb;
                prevLsb = f.PocLsb;
            }
        }

        int start = 0;
        while (start < frames.Count)
        {
            int end = start;
            while (end < frames.Count && segment[end] == segment[start]) end++;
            var ranked = Enumerable.Range(start, end - start).OrderBy(i => poc[i]).ThenBy(i => i).ToList();
            for (int k = 0; k < ranked.Count; k++) order[ranked[k]] = start + k;
            start = end;
        }
        return order;
    }
}
