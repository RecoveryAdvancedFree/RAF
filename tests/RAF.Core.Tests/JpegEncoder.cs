namespace RAF.Core.Tests;

/// <summary>
/// מקודד JPEG בסיסי לבדיקות: כותב נתונים דחוסים אמיתיים — קודי הופמן, ריפוד
/// בביטי 1, בתי 00 אחרי FF, וסימני RST לפי הסדר — מתוך מקדמים אקראיים.
///
/// התמונה עצמה חסרת משמעות, אבל המבנה מדויק. זה מה שהמפענח בודק, ומה
/// שקובע אם תמונה אמיתית תסומן "מצוין" או "חלש" — ולכן הוא חייב להיבדק
/// מול קבצים שנכתבו בדיוק לפי התקן, כולל RST ודגימת צבע מופחתת.
/// </summary>
internal static class JpegEncoder
{
    /// <summary>טבלת DC התקנית (נספח K) — סמלים 0 עד 11.</summary>
    private static readonly byte[] DcCounts = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcValues = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();

    /// <summary>
    /// טבלת AC עם כל 162 הסמלים החוקיים: EOB, ZRL, וכל צירוף של רצף 0–15 וגודל 1–10.
    /// 64 קודים באורך 7 ועוד 98 באורך 8 — וקוד של כל-אחדות נשאר פנוי, כנדרש.
    /// </summary>
    private static readonly byte[] AcCounts = { 0, 0, 0, 0, 0, 0, 64, 98, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] AcValues = new byte[] { 0x00, 0xF0 }
        .Concat(from run in Enumerable.Range(0, 16) from size in Enumerable.Range(1, 10) select (byte)(run << 4 | size))
        .ToArray();

    private static Dictionary<byte, (int Code, int Length)> Codes(byte[] counts, byte[] values)
    {
        var codes = new Dictionary<byte, (int, int)>();
        int code = 0, k = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < counts[length - 1]; i++) codes[values[k++]] = (code++, length);
            code <<= 1;
        }
        return codes;
    }

    private sealed class BitWriter
    {
        private readonly MemoryStream _out;
        private int _buffer, _bits;

        public BitWriter(MemoryStream output) => _out = output;

        public void Write(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((value >> i) & 1);
                if (++_bits == 8) Flush();
            }
        }

        private void Flush()
        {
            _out.WriteByte((byte)_buffer);
            if (_buffer == 0xFF) _out.WriteByte(0x00);   // byte stuffing
            _buffer = _bits = 0;
        }

        /// <summary>ריפוד בביטי 1 עד סוף הבית — לפני כל סימן.</summary>
        public void Align()
        {
            while (_bits != 0) Write(1, 1);
        }
    }

    /// <param name="sampling">דגימת הבהירות: (2,2) ל-4:2:0, (1,1) ל-4:4:4. רכיב אחד — גווני אפור.</param>
    internal static byte[] Encode(int width, int height, int seed,
        (int H, int V) sampling = default, int components = 3, int restartInterval = 0)
    {
        if (sampling == default) sampling = (2, 2);
        var random = new Random(seed);
        var dc = Codes(DcCounts, DcValues);
        var ac = Codes(AcCounts, AcValues);

        using var s = new MemoryStream();
        s.Write([0xFF, 0xD8]);
        Segment(s, 0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        Segment(s, 0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]);

        var frame = new List<byte> { 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components };
        for (int c = 0; c < components; c++)
            frame.AddRange(new[] { (byte)(c + 1), (byte)(c == 0 ? sampling.H << 4 | sampling.V : 0x11), (byte)0 });
        Segment(s, 0xC0, frame.ToArray());

        Segment(s, 0xC4, [0x00, .. DcCounts, .. DcValues, 0x10, .. AcCounts, .. AcValues]);
        if (restartInterval > 0) Segment(s, 0xDD, [(byte)(restartInterval >> 8), (byte)restartInterval]);

        var scan = new List<byte> { (byte)components };
        for (int c = 0; c < components; c++) scan.AddRange(new[] { (byte)(c + 1), (byte)0x00 });
        scan.AddRange(new byte[] { 0, 63, 0 });
        Segment(s, 0xDA, scan.ToArray());

        var bits = new BitWriter(s);
        int hMax = components == 1 ? 1 : sampling.H, vMax = components == 1 ? 1 : sampling.V;
        int mcus = ((width + 8 * hMax - 1) / (8 * hMax)) * ((height + 8 * vMax - 1) / (8 * vMax));
        int blocksPerMcu = components == 1 ? 1 : sampling.H * sampling.V + (components - 1);

        void Emit((int Code, int Length) code) => bits.Write(code.Code, code.Length);

        for (int m = 0; m < mcus; m++)
        {
            if (restartInterval > 0 && m > 0 && m % restartInterval == 0)
            {
                bits.Align();
                s.Write([0xFF, (byte)(0xD0 + (m / restartInterval - 1) % 8)]);
            }

            for (int b = 0; b < blocksPerMcu; b++)
            {
                int dcSize = random.Next(0, 7);
                Emit(dc[(byte)dcSize]);
                bits.Write(random.Next(1 << dcSize), dcSize);

                int k = 1;
                for (int n = random.Next(0, 7); n > 0 && k < 64; n--)
                {
                    int run = random.Next(0, 24);
                    if (k + run > 63) break;
                    while (run > 15) { Emit(ac[0xF0]); run -= 16; k += 16; }
                    int size = random.Next(1, 7);
                    Emit(ac[(byte)(run << 4 | size)]);
                    bits.Write(random.Next(1 << size), size);
                    k += run + 1;
                }
                if (k < 64) Emit(ac[0x00]);   // EOB
            }
        }

        bits.Align();
        s.Write([0xFF, 0xD9]);
        return s.ToArray();
    }

    private static void Segment(Stream s, byte marker, byte[] payload)
    {
        s.Write([0xFF, marker]);
        s.Write([(byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
        s.Write(payload);
    }
}
