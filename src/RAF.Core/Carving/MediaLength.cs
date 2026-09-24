namespace RAF.Core.Carving;

/// <summary>
/// אורך של פורמטי וידאו ושמע שאינם נושאים את אורכם בכותרת: מצלמות וידאו
/// ביתיות (AVCHD, ‏‎.mts), סרטי DVD ו-MPEG, הקלטות AMR מטלפונים ו-OGG.
/// האורך נקבע לפי המבנה עצמו — חבילה אחרי חבילה — עד המקום שבו הוא נשבר.
/// </summary>
internal static class MediaLength
{
    /// <summary>דגימה כל מגה-בייט בזרם חבילות קבועות, לפני סריקה צמודה של הקטע האחרון.</summary>
    private const long SampleEvery = 1 << 20;

    // ------------------------------------------------------------ MPEG-TS

    /// <summary>
    /// זרם MPEG-TS: חבילות של 188 בתים (או 192 ב-AVCHD — ארבעה בתי זמן לפני כל
    /// חבילה), שכל אחת מתחילה בבית 0x47. הקובץ נגמר בחבילה האחרונה שבה הבית
    /// הזה במקומו. כדי לא לקרוא סרט של 2GB פעמיים, בודקים חבילה אחת בכל מגה-בייט,
    /// ורק את המגה-בייט האחרון — חבילה אחרי חבילה.
    /// </summary>
    internal static long ReadTransportStream(WindowReader r, int packet)
    {
        int sync = packet - 188;
        bool Sync(long k) => r.Byte(k * packet + sync) == 0x47;

        for (int k = 0; k < 8; k++)
            if (!Sync(k)) return 0;

        long step = SampleEvery / packet;
        long good = 7;
        while ((good + step + 2) * packet <= r.Limit && Sync(good + step) && Sync(good + step + 1))
            good += step;

        long end = good + 1;
        while ((end + 1) * packet <= r.Limit && Sync(end)) end++;
        return end * packet;
    }

    // ---------------------------------------------------------- MPEG-PS

    /// <summary>
    /// זרם MPEG-PS (‏‎.mpg, ‏‎.vob): רצף של "חבילות" (00 00 01 BA) ובתוכן מנות
    /// שכל אחת נושאת את אורכה. הקובץ נגמר בקוד הסיום 00 00 01 B9, או במקום
    /// שבו הרצף נשבר. אין כאן דגימה כמו ב-TS: בבדיקה על קבצים אמיתיים, דגימה
    /// בגבולות 2048 נחתה על חבילה של סרט אחר שישב אחרי הקובץ, ובלעה אותו.
    /// </summary>
    internal static long ReadProgramStream(WindowReader r)
    {
        long pos = 0;
        long last = 0;
        for (int guard = 0; guard < 50_000_000 && pos + 4 <= r.Limit; guard++)
        {
            if (r.Byte(pos) != 0 || r.Byte(pos + 1) != 0 || r.Byte(pos + 2) != 1) break;
            int code = r.Byte(pos + 3);

            if (code == 0xBA)
            {
                int flags = r.Byte(pos + 4);
                if ((flags & 0xC0) == 0x40)                                   // MPEG-2
                {
                    int stuffing = r.Byte(pos + 13);
                    if (stuffing < 0) break;
                    pos += 14 + (stuffing & 7);
                }
                else if ((flags & 0xF0) == 0x20) pos += 12;                    // MPEG-1
                else break;
            }
            else if (code == 0xB9)
            {
                return pos + 4;                                               // קוד הסיום
            }
            else if (code >= 0xBB)
            {
                int length = r.BigEndian16(pos + 4);
                if (length < 0) break;
                pos += 6 + length;
            }
            else break;

            if (pos <= r.Limit) last = pos;
        }

        return last;
    }

    // --------------------------------------------------------------- ASF

    /// <summary>ה-GUID של אובייקט מאפייני הקובץ ב-ASF, שבו שדה גודל הקובץ.</summary>
    private static readonly byte[] AsfFileProperties =
        { 0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65 };

    /// <summary>
    /// ASF (‏‎.wmv, ‏‎.wma): הקובץ בנוי מאובייקטים — GUID ואורך. סכום האובייקטים
    /// ברמה העליונה הוא אורך הקובץ; שדה הגודל שבכותרת משמש לאימות.
    /// </summary>
    internal static long ReadAsf(WindowReader r)
    {
        long declared = 0;
        long headerSize = LittleEndian64(r, 16);
        if (headerSize is < 30 or > 16 * 1024 * 1024) return 0;

        // מאפייני הקובץ, בתוך אובייקט הכותרת.
        for (long at = 30; at + 24 <= headerSize; )
        {
            long size = LittleEndian64(r, at + 16);
            if (size < 24) break;
            if (Matches(r, at, AsfFileProperties)) declared = LittleEndian64(r, at + 40);
            at += size;
        }

        long total = 0;
        for (int guard = 0; guard < 64 && total + 24 <= r.Limit; guard++)
        {
            long size = LittleEndian64(r, total + 16);
            if (size < 24 || total + size > r.Limit) break;
            total += size;
            if (total == declared) break;
        }

        return declared > 0 && declared <= r.Limit ? declared : total;
    }

    // --------------------------------------------------------------- AMR

    private static readonly int[] AmrNarrow = { 12, 13, 15, 17, 19, 20, 26, 31, 5, -1, -1, -1, -1, -1, -1, 0 };
    private static readonly int[] AmrWide = { 17, 23, 32, 36, 40, 46, 50, 58, 60, 5, -1, -1, -1, -1, 0, 0 };

    /// <summary>
    /// הקלטת AMR: אחרי הכותרת, מסגרות שהבית הראשון שלהן קובע את אורכן. הקובץ
    /// נגמר במסגרת הראשונה שהבית הזה בה אינו חוקי.
    /// </summary>
    internal static long ReadAmr(WindowReader r)
    {
        bool wide = r.Byte(5) == '-';
        var sizes = wide ? AmrWide : AmrNarrow;
        long pos = wide ? 9 : 6;
        int frames = 0;

        while (pos < r.Limit)
        {
            int header = r.Byte(pos);
            // ביטי הריפוד כבויים, ודגל האיכות (Q) דולק — אחרת גם אפסים שאחרי הקובץ נראים כמסגרות.
            if (header < 0 || (header & 0x83) != 0 || (header & 0x04) == 0) break;
            int size = sizes[(header >> 3) & 0x0F];
            if (size < 0 || pos + 1 + size > r.Limit) break;
            pos += 1 + size;
            frames++;
        }

        return frames >= 5 ? pos : 0;
    }

    // --------------------------------------------------------------- OGG

    /// <summary>
    /// OGG (שמע Vorbis ו-Opus, וידאו Theora): רצף "דפים", שכל אחד פותח ב-OggS
    /// ונושא את אורכו. הקובץ נגמר בדף שמסומן כאחרון, או כשהרצף נשבר.
    /// </summary>
    internal static long ReadOgg(WindowReader r)
    {
        long pos = 0;
        long serial = r.LittleEndian32(14);

        for (int guard = 0; guard < 10_000_000 && pos + 27 <= r.Limit; guard++)
        {
            if (r.Byte(pos) != 'O' || r.Byte(pos + 1) != 'g' || r.Byte(pos + 2) != 'g' || r.Byte(pos + 3) != 'S'
                || r.Byte(pos + 4) != 0)
                break;

            int flags = r.Byte(pos + 5);
            int segments = r.Byte(pos + 26);
            if (segments < 0) break;

            long body = 0;
            for (int i = 0; i < segments; i++)
            {
                int lace = r.Byte(pos + 27 + i);
                if (lace < 0) return pos;
                body += lace;
            }

            long next = pos + 27 + segments + body;
            if (next > r.Limit) break;
            pos = next;

            // סוף הזרם הראשי. זרמים נוספים (וידאו עם קול) נגמרים באותו אזור.
            if ((flags & 0x04) != 0 && r.LittleEndian32(pos - 27 - segments - body + 14) == serial) return pos;
        }

        return pos;
    }

    // ------------------------------------------------------------ עזרים

    private static long LittleEndian64(WindowReader r, long pos)
    {
        long low = r.LittleEndian32(pos), high = r.LittleEndian32(pos + 4);
        return low < 0 || high < 0 || high > int.MaxValue ? -1 : (high << 32) | low;
    }

    private static bool Matches(WindowReader r, long pos, byte[] pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
            if (r.Byte(pos + i) != pattern[i]) return false;
        return true;
    }
}
