using System.Buffers.Binary;

namespace RAF.Core.FileSystems.Btrfs;

/// <summary>
/// פריסת LZO1X — שיטת דחיסה שבה btrfs יכול לדחוס קבצים. לפי המימוש של ליבת לינוקס
/// (lib/lzo/lzo1x_decompress_safe.c), עם בדיקת גבולות בכל צעד: נתונים פגומים זורקים
/// חריגה ולא קוראים מחוץ למערך.
/// </summary>
internal static class Lzo
{
    /// <summary>
    /// מבנה של btrfs: 4 בתים — אורך הכול, ואז מקטעים: 4 בתים אורך ומיד הדחוס, שכל אחד נפרס
    /// לעמוד של 4KB לכל היותר. כותרת מקטע לא חוצה גבול של עמוד — אם נשארו פחות מ-4 בתים בעמוד, מדלגים.
    /// </summary>
    internal static byte[] Btrfs(byte[] data, int ramBytes)
    {
        int total = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data), data.Length);
        var output = new byte[ramBytes];
        int at = 4, written = 0;
        while (at + 4 <= total && written < ramBytes)
        {
            int inPage = 4096 - at % 4096;
            if (inPage < 4) { at += inPage; continue; }
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            at += 4;
            if (length <= 0 || at + length > data.Length) throw new InvalidDataException();
            var page = new byte[Math.Min(4096, ramBytes - written)];
            int got = Decompress(data.AsSpan(at, length).ToArray(), page);
            page.AsSpan(0, got).CopyTo(output.AsSpan(written));
            written += got;
            at += length;
        }
        return output;
    }

    /// <summary>פריסת זרם LZO1X אחד. מחזיר כמה בתים נכתבו.</summary>
    internal static int Decompress(byte[] input, byte[] output)
    {
        int ip = 0, op = 0, state = 0, t, next;
        int mPos;

        byte In() => ip < input.Length ? input[ip++] : throw new InvalidDataException();
        void Literals(int count)
        {
            if (count < 0 || ip + count > input.Length || op + count > output.Length) throw new InvalidDataException();
            Array.Copy(input, ip, output, op, count);
            ip += count;
            op += count;
        }
        int ZeroRun(int bias)
        {
            int zeros = 0;
            while (ip < input.Length && input[ip] == 0) { ip++; zeros++; }
            return zeros * 255 + bias + In();
        }

        if (input.Length > 0 && input[0] > 17)
        {
            t = In() - 17;
            Literals(t);
            state = t < 4 ? t : 4;
        }

        while (true)
        {
            t = In();
            if (t < 16)
            {
                if (state == 0)
                {
                    if (t == 0) t = ZeroRun(15);
                    t += 3;
                    Literals(t);
                    state = 4;
                    continue;
                }
                if (state != 4)
                {
                    next = t & 3;
                    mPos = op - 1 - (t >> 2) - (In() << 2);
                    Copy(output, ref op, mPos, 2);
                    goto MatchNext;
                }
                next = t & 3;
                mPos = op - (1 + 0x0800) - (t >> 2) - (In() << 2);
                t = 3;
            }
            else if (t >= 64)
            {
                next = t & 3;
                mPos = op - 1 - ((t >> 2) & 7) - (In() << 3);
                t = (t >> 5) - 1 + 2;
            }
            else if (t >= 32)
            {
                t = (t & 31) + 2;
                if (t == 2) t += ZeroRun(31);
                int word = In() | (In() << 8);
                mPos = op - 1 - (word >> 2);
                next = word & 3;
            }
            else
            {
                mPos = op - ((t & 8) << 11);
                t = (t & 7) + 2;
                if (t == 2) t += ZeroRun(7);
                int word = In() | (In() << 8);
                mPos -= word >> 2;
                next = word & 3;
                if (mPos == op) return op;   // סוף הזרם
                mPos -= 0x4000;
            }
            Copy(output, ref op, mPos, t);

        MatchNext:
            state = next;
            Literals(next);
        }
    }

    /// <summary>העתקה מתוך מה שכבר נפרס — בית-בית, כי המקור והיעד עשויים לחפוף.</summary>
    private static void Copy(byte[] output, ref int op, int from, int count)
    {
        if (from < 0 || op + count > output.Length) throw new InvalidDataException();
        for (int i = 0; i < count; i++) output[op++] = output[from + i];
    }
}
