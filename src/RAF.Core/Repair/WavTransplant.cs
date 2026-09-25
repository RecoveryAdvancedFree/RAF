using System.Buffers.Binary;

namespace RAF.Core.Repair;

/// <summary>
/// בניית כותרת להקלטת WAV שנדרסה, בעזרת הקלטה תקינה מאותו מכשיר ובאותן הגדרות.
///
/// בכותרת כתוב איך לקרוא את השמע: מספר הערוצים, קצב הדגימה ועומק הדגימה. בלעדיה נגן
/// לא יודע אם 4 בתים הם דגימה אחת או שתיים. מכשיר מקליט כותב את אותה כותרת בכל הקלטה,
/// ולכן היא נלקחת מהדוגמה. השמע עצמו מתחיל אחרי הסימן "data" אם שרד, ואחרת — במקום
/// שבו הוא מתחיל בדוגמה.
///
/// בשמע לא דחוס, ההתחלה המדויקת (בתוך דגימה) נבחרת לפי "חלקות": בשמע אמיתי דגימה סמוכה
/// קרובה לקודמת, ובקריאה שמוזזת בבית אחד — או בפורמט אחר מזה של הדוגמה — הערכים
/// נראים כרעש. כך גם דוגמה בהגדרות אחרות (למשל 24 ביט מול 16) מתגלה ונדחית.
/// </summary>
public static class WavTransplant
{
    private sealed record Format(byte[] Chunk, int FormatTag, int Channels, int SampleRate, int BlockAlign, int Bits, long DataStart);

    // ================================================================ אבחון

    /// <summary>אין בתחילת הקובץ מקטע "fmt " תקין — ובלעדיו אי אפשר לקרוא את השמע.</summary>
    internal static bool LostHeader(ReadOnlySpan<byte> head, long size)
        => size > 4096 && ReadFormat(head, size) is null;

    public static string? DescribeReference(string path)
    {
        try
        {
            byte[] head = ReadHead(path);
            var f = ReadFormat(head, new FileInfo(path).Length);
            if (f is null || f.DataStart <= 0) return L.T("הקובץ שנבחר אינו הקלטת WAV תקינה.");
            return null;
        }
        catch (IOException) { return L.T("הקובץ שנבחר אינו הקלטת WAV תקינה."); }
    }

    private static byte[] ReadHead(string path)
    {
        using var s = File.OpenRead(path);
        byte[] head = new byte[(int)Math.Min(s.Length, 1 << 20)];
        s.ReadExactly(head);
        return head;
    }

    /// <summary>המקטע "fmt " מהכותרת, ותחילת השמע (-1 כשהסימן "data" לא נמצא).</summary>
    private static Format? ReadFormat(ReadOnlySpan<byte> head, long size)
    {
        if (head.Length < 12 || !head[..4].SequenceEqual("RIFF"u8) || !head.Slice(8, 4).SequenceEqual("WAVE"u8)) return null;
        Format? format = null;
        int at = 12;
        while (at + 8 <= head.Length)
        {
            var id = head.Slice(at, 4);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(head[(at + 4)..]);
            if (id.SequenceEqual("fmt "u8) && length is >= 16 and < 4096 && at + 8 + length <= head.Length)
            {
                var b = head.Slice(at + 8, (int)length);
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(b), channels = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);
                int rate = BinaryPrimitives.ReadInt32LittleEndian(b[4..]), align = BinaryPrimitives.ReadUInt16LittleEndian(b[12..]);
                int bits = BinaryPrimitives.ReadUInt16LittleEndian(b[14..]);
                if (channels is < 1 or > 32 || rate is < 1000 or > 768000 || align == 0) return null;
                format = new Format(head.Slice(at, 8 + (int)length).ToArray(), tag, channels, rate, align, bits, -1);
            }
            else if (id.SequenceEqual("data"u8))
                return format is null ? null : format with { DataStart = at + 8 };
            else if (!IsChunkId(id)) return format;
            at = (int)Math.Min(at + 8L + length + (length & 1), int.MaxValue);
        }
        return format;
    }

    private static bool IsChunkId(ReadOnlySpan<byte> id)
    {
        foreach (byte c in id) if (c is < 0x20 or > 0x7E) return false;
        return true;
    }

    // ================================================================ בנייה

    public static PhotoRebuildResult Rebuild(string brokenPath, string referencePath, string outputPath)
    {
        if (string.Equals(Path.GetFullPath(brokenPath), Path.GetFullPath(referencePath), StringComparison.OrdinalIgnoreCase))
            return new PhotoRebuildResult { Message = L.T("הקלטת הדוגמה היא ההקלטה הפגומה עצמה. בחרו הקלטה תקינה אחרת מאותו מכשיר.") };
        if (DescribeReference(referencePath) is { } problem) return new PhotoRebuildResult { Message = problem };

        var donor = ReadFormat(ReadHead(referencePath), new FileInfo(referencePath).Length)!;
        long size = new FileInfo(brokenPath).Length;
        using var input = File.OpenRead(brokenPath);
        byte[] head = new byte[(int)Math.Min(size, 1 << 20)];
        input.ReadExactly(head);

        // תחילת השמע: אחרי הסימן "data" אם שרד, ואחרת — כמו בדוגמה.
        long start = FindData(head) ?? donor.DataStart;
        bool exact = FindData(head) is not null;
        if (start >= size) return new PhotoRebuildResult { Message = L.T("אין בהקלטה הפגומה שמע אחרי מקום הכותרת.") };

        // סוף השמע: לפני מקטעי מידע שבסוף הקובץ (שם האמן, הערות), שאחרת היו מושמעים כרעש.
        long end = TrailingChunks(input, size);

        // בשמע לא דחוס: היישור בתוך הדגימה. אורך השמע הוא תמיד מספר שלם של מסגרות, ולכן
        // ההתחלה מיושרת לסוף — כך גם הערוצים לא מתחלפים (בקריאה שמוזזת בדגימה אחת השמאלי
        // נקרא כימני, ו"חלקות" לא מבחינה בזה). אם היישור הזה נראה כרעש (הקובץ נקטע באמצע
        // מסגרת) — היישור החלק ביותר.
        int shift = 0;
        if (Pcm(donor) && !exact)
        {
            var scores = Enumerable.Range(0, donor.BlockAlign).Select(s => Roughness(input, size, start + s, donor)).ToList();
            int aligned = (int)(((end - start) % donor.BlockAlign + donor.BlockAlign) % donor.BlockAlign);
            shift = scores[aligned] <= scores.Min() * 1.2 + 1e-9 ? aligned : scores.IndexOf(scores.Min());
        }
        if (Pcm(donor) && !Plausible(input, size, start + shift, donor))
            return new PhotoRebuildResult
            {
                Message = L.T("השמע בהקלטה הפגומה אינו נקרא נכון בפורמט של הדוגמה (ערוצים, קצב או עומק דגימה שונים). " +
                    "בחרו הקלטה מאותו מכשיר ובאותן הגדרות."),
            };

        start += shift;
        // כשהסימן "data" שרד — הגודל שבו, אם הוא סביר.
        if (exact && start >= 4)
        {
            long declared = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan((int)start - 4));
            if (declared > 0 && start + declared <= size) end = start + declared;
        }
        long audio = end - start;
        audio -= audio % donor.BlockAlign;
        if (audio > uint.MaxValue - 1024) audio = (uint.MaxValue - 1024) / donor.BlockAlign * donor.BlockAlign;

        using (var output = File.Create(outputPath))
        {
            byte[] header = new byte[12 + donor.Chunk.Length + 8];
            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(header.Length - 8 + audio + (audio & 1)));
            "WAVE"u8.CopyTo(header.AsSpan(8));
            donor.Chunk.CopyTo(header, 12);
            "data"u8.CopyTo(header.AsSpan(12 + donor.Chunk.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(header.Length - 4), (uint)audio);
            output.Write(header);
            input.Position = start;
            byte[] buffer = new byte[1 << 20];
            for (long left = audio; left > 0;)
            {
                int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0) break;
                output.Write(buffer, 0, n);
                left -= n;
            }
            if ((audio & 1) == 1) output.WriteByte(0);
        }

        double seconds = audio / (double)(donor.SampleRate * (long)donor.BlockAlign);
        var applied = new List<string>
        {
            L.T("מהקלטת הדוגמה נלקחו הגדרות השמע: {0} ערוצים, {1} הרץ, {2} ביט. השמע עצמו — מההקלטה, ⁦{3}⁩.",
                donor.Channels, donor.SampleRate.ToString("N0"), donor.Bits, TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss")),
        };
        if (!exact) applied.Add(L.T("הסימן של תחילת השמע נדרס, ולכן השמע נקרא מהמקום שבו הוא מתחיל בדוגמה. " +
            "אם יש רעש קצר בהתחלה — זה מה שנשאר מהכותרת."));

        return new PhotoRebuildResult
        {
            Written = true,
            Complete = true,
            Fraction = 1,
            Applied = applied,
            Message = L.T("הכותרת נבנתה מחדש. אם ההקלטה מתנגנת מהר או לאט מדי, הדוגמה הוקלטה בקצב דגימה אחר — נסו הקלטה אחרת."),
        };
    }

    /// <summary>
    /// תחילת המקטעים שאחרי השמע, בסוף הקובץ: מזהה של ארבע אותיות ואורך שמסתיים בדיוק
    /// בסוף הקובץ (או במקטע הבא). size — כשאין כאלה.
    /// </summary>
    private static long TrailingChunks(FileStream input, long size)
    {
        int window = (int)Math.Min(size, 256 * 1024);
        byte[] tail = new byte[window];
        input.Position = size - window;
        input.ReadExactly(tail);
        // המקטע החיצוני — המוקדם ביותר שנגמר בסוף; גם מקטע פנימי (למשל ICMT בתוך LIST) נגמר שם.
        long end = size;
        for (bool found = true; found;)
        {
            found = false;
            long earliest = end;
            for (int i = (int)(end - (size - window)) - 8; i >= 0; i--)
            {
                if (!IsChunkId(tail.AsSpan(i, 4)) || !tail.AsSpan(i, 4).ToArray().Any(b => char.IsAsciiLetter((char)b))) continue;
                long length = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 4));
                long chunkEnd = size - window + i + 8 + length;
                if (chunkEnd == end || chunkEnd + 1 == end) earliest = size - window + i;
            }
            if (earliest < end) { end = earliest; found = true; }
        }
        return end;
    }

    /// <summary>תחילת השמע: אחרי "data" עם גודל סביר, ב-1MB הראשון. null — לא נמצא.</summary>
    private static long? FindData(byte[] head)
    {
        int at = head.AsSpan().IndexOf("data"u8);
        while (at >= 0 && at + 8 <= head.Length)
        {
            if (at % 2 == 0) return at + 8;
            int next = head.AsSpan(at + 1).IndexOf("data"u8);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return null;
    }

    /// <summary>
    /// האם הפורמט של הדוגמה הוא הפורמט של השמע: אין עומק דגימה אחר שבו הנתונים נראים
    /// חלקים בהרבה. סף מוחלט לא עובד — יש הקלטות "מחוספסות" מטבען.
    /// </summary>
    private static bool Plausible(FileStream input, long size, long start, Format f)
    {
        double own = Roughness(input, size, start, f);
        // רק עומק הדגימה: ערוצים דומים זה לזה (סטריאו כמעט מונו) נראים "חלקים" גם בקריאה שגויה.
        foreach (int bits in new[] { 8, 16, 24, 32 })
        {
            if (bits == f.Bits) continue;
            var other = f with { Bits = bits, BlockAlign = bits / 8 * f.Channels };
            double best = Enumerable.Range(0, other.BlockAlign).Min(s => Roughness(input, size, start + s, other));
            if (best < own * 0.6) return false;
        }
        return true;
    }

    private static bool Pcm(Format f) => f.FormatTag is 1 or 0xFFFE && f.Bits is 8 or 16 or 24 or 32 && f.BlockAlign == f.Channels * f.Bits / 8;

    /// <summary>
    /// מדד "חוספס": ממוצע השינוי בין דגימות סמוכות באותו ערוץ, ביחס לממוצע הגודל שלהן.
    /// בשמע אמיתי הוא קטן (בדרך כלל מתחת ל-0.5); ברעש — כלומר בקריאה שגויה — סביב 1.4.
    /// שקט מוחלט נחשב חלק.
    /// </summary>
    private static double Roughness(FileStream input, long size, long start, Format f)
    {
        int bytes = f.Bits / 8;
        double change = 0, level = 0;
        // שלושה קטעים לאורך ההקלטה — לא רק תחילתה, שעלולה להיות שקטה.
        foreach (double where in new[] { 0.2, 0.5, 0.8 })
        {
            long at = start + (long)((size - start) * where);
            at -= (at - start) % f.BlockAlign;
            byte[] block = new byte[f.BlockAlign * 4096];
            input.Position = at;
            int n = input.Read(block, 0, block.Length) / f.BlockAlign;
            for (int c = 0; c < f.Channels; c++)
            {
                double previous = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = Sample(block, i * f.BlockAlign + c * bytes, bytes);
                    if (i > 0) change += Math.Abs(x - previous);
                    level += Math.Abs(x);
                    previous = x;
                }
            }
        }
        return level < 1e-9 ? 0 : change / level;
    }

    private static double Sample(byte[] b, int at, int bytes) => bytes switch
    {
        1 => b[at] - 128,
        2 => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(at)),
        3 => (b[at] | b[at + 1] << 8 | (sbyte)b[at + 2] << 16),
        _ => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at)),
    };
}
