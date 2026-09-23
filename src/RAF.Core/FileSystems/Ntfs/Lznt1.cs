using System.Buffers.Binary;

namespace RAF.Core.FileSystems.Ntfs;

/// <summary>
/// פריסת דחיסת LZNT1 — אלגוריתם הדחיסה המובנה של NTFS.
/// קובץ דחוס שנכתב ללא פריסה הוא קובץ הרוס, ולכן זהו חלק הכרחי בשחזור.
/// </summary>
internal static class Lznt1
{
    /// <summary>גודל בלוק פרוס קבוע ב-NTFS.</summary>
    internal const int ChunkSize = 4096;

    /// <summary>
    /// פריסת מאגר דחוס. הנתונים בנויים מרצף בלוקים, כל אחד עם כותרת בת שני בתים.
    /// </summary>
    /// <param name="compressed">הנתונים הדחוסים כפי שנקראו מהדיסק.</param>
    /// <param name="expectedSize">הגודל הפרוס הצפוי, לצורך הקצאה וחיתוך.</param>
    internal static byte[] Decompress(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        var output = new byte[expectedSize];
        int written = 0;
        int pos = 0;

        while (pos + 2 <= compressed.Length && written < expectedSize)
        {
            ushort header = BinaryPrimitives.ReadUInt16LittleEndian(compressed[pos..]);
            pos += 2;

            // כותרת אפס מסמנת את סוף הנתונים.
            if (header == 0) break;

            int chunkLength = (header & 0x0FFF) + 1;
            bool isCompressed = (header & 0x8000) != 0;

            if (pos + chunkLength > compressed.Length) break;

            var chunk = compressed.Slice(pos, chunkLength);
            pos += chunkLength;

            written += isCompressed
                ? DecompressChunk(chunk, output.AsSpan(written))
                : CopyLiteral(chunk, output.AsSpan(written));
        }

        return written == expectedSize ? output : output.AsSpan(0, written).ToArray();
    }

    private static int CopyLiteral(ReadOnlySpan<byte> chunk, Span<byte> destination)
    {
        int take = Math.Min(chunk.Length, destination.Length);
        chunk[..take].CopyTo(destination);
        return take;
    }

    /// <summary>
    /// פריסת בלוק דחוס יחיד. הבלוק בנוי מקבוצות של 8 פריטים,
    /// שכל אחת מקדימה בית דגלים הקובע לכל פריט אם הוא בית בודד או הפניה לאחור.
    /// </summary>
    private static int DecompressChunk(ReadOnlySpan<byte> chunk, Span<byte> destination)
    {
        int read = 0;
        int written = 0;

        while (read < chunk.Length && written < destination.Length)
        {
            byte flags = chunk[read++];

            for (int bit = 0; bit < 8; bit++)
            {
                if (read >= chunk.Length || written >= destination.Length) break;

                if ((flags & (1 << bit)) == 0)
                {
                    // דגל כבוי: בית מקורי המועתק כמות שהוא.
                    destination[written++] = chunk[read++];
                    continue;
                }

                if (read + 2 > chunk.Length) return written;

                ushort token = BinaryPrimitives.ReadUInt16LittleEndian(chunk[read..]);
                read += 2;

                // חלוקת הביטים בין אורך להיסט משתנה לפי כמות הפלט שנכתבה עד כה:
                // ככל שנכתב יותר, נדרשים יותר ביטים להיסט ופחות לאורך.
                int shift = 12;
                int threshold = 0x10;
                while (threshold < written)
                {
                    threshold <<= 1;
                    shift--;
                }

                int length = (token & ((1 << shift) - 1)) + 3;
                int displacement = (token >> shift) + 1;

                if (displacement > written) return written; // הפניה לפני תחילת הבלוק

                // ההעתקה חייבת להיות בית-אחר-בית: קטעים חופפים הם חלק מהאלגוריתם
                // ומשמשים לייצוג רצפים חוזרים.
                int source = written - displacement;
                for (int i = 0; i < length && written < destination.Length; i++)
                    destination[written++] = destination[source + i];
            }
        }

        return written;
    }
}
