using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.Tests;

/// <summary>
/// בוני קבצים במבנה אמיתי.
///
/// הלקח מהסריקה על כונן אמיתי: קבצי בדיקה שרק פותחים בחתימה נכונה אינם
/// קבצים. BMP בלי כותרת DIB או JPEG בלי מבנה מקטעים עוברים בדיקות שבודקות
/// חתימה בלבד — ונכשלים ברגע שהסורק מתחיל לאמת מבנה, כמו שהוא חייב.
/// כל בונה כאן מייצר קובץ שתוכנה אמיתית הייתה פותחת.
/// </summary>
internal static class RealFormats
{
    internal static byte[] Random(int size, int seed)
    {
        byte[] data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>BMP של 24 ביט עם כותרת BITMAPINFOHEADER תקינה.</summary>
    internal static byte[] Bmp(int width, int height, int seed)
    {
        int stride = (width * 3 + 3) & ~3;
        int pixels = stride * height;
        int size = 54 + pixels;

        byte[] b = new byte[size];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10), 54);       // היסט הפיקסלים
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(14), 40);       // גודל כותרת DIB
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 1);        // מישורים
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), 24);       // עומק צבע
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(34), (uint)pixels);

        Random(pixels, seed).CopyTo(b, 54);
        return b;
    }

    /// <summary>
    /// JPEG במבנה מקטעים מלא: JFIF, טבלת כימות, מסגרת, סריקה ונתונים
    /// דחוסים עם ריפוד בתי FF כנדרש. אפשר לשלב תמונה ממוזערת בתוך Exif —
    /// היא מכילה FF D9 משלה, שהסורק חייב לדלג עליו.
    /// </summary>
    internal static byte[] Jpeg(int entropyBytes, int seed, bool withExifThumbnail = false)
    {
        using var s = new MemoryStream();
        s.Write([0xFF, 0xD8]);

        if (withExifThumbnail)
        {
            byte[] thumbnail = [0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xD9];
            byte[] payload = [.. "Exif\0\0"u8, .. thumbnail];
            Segment(s, 0xE1, payload);
        }

        Segment(s, 0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        Segment(s, 0xDB, new byte[65]);
        Segment(s, 0xC0, [8, 0, 16, 0, 16, 3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1]);
        Segment(s, 0xDA, [3, 1, 0, 2, 0x11, 3, 0x11, 0, 0x3F, 0]);

        // נתונים דחוסים: כל FF מלווה ב-00, כמו ב-JPEG אמיתי.
        foreach (byte b in Random(entropyBytes, seed))
        {
            s.WriteByte(b);
            if (b == 0xFF) s.WriteByte(0x00);
        }

        s.Write([0xFF, 0xD9]);
        return s.ToArray();
    }

    private static void Segment(Stream s, byte marker, byte[] payload)
    {
        s.Write([0xFF, marker]);
        s.Write([(byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
        s.Write(payload);
    }

    /// <summary>PNG עם שרשרת מקטעים תקינה.</summary>
    internal static byte[] Png(int dataBytes, int seed)
    {
        using var s = new MemoryStream();
        s.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        void Chunk(string type, byte[] data)
        {
            byte[] length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            s.Write(length);
            s.Write(Encoding.ASCII.GetBytes(type));
            s.Write(data);
            s.Write(new byte[4]);
        }

        Chunk("IHDR", new byte[13]);
        Chunk("IDAT", Random(dataBytes, seed));
        Chunk("IEND", []);
        return s.ToArray();
    }

    /// <summary>קובץ WAV: כותרת RIFF עם גודל מדויק.</summary>
    internal static byte[] Wav(int size, int seed)
    {
        byte[] b = Random(size, seed);
        Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(size - 8));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(b, 8);
        return b;
    }

    /// <summary>מסד SQLite: גודל דף, גרסאות קריאה וכתיבה, ומספר דפים.</summary>
    internal static byte[] Sqlite(int pageSize, int pages, int seed)
    {
        byte[] db = Random(pageSize * pages, seed);
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(db, 0);
        BinaryPrimitives.WriteUInt16BigEndian(db.AsSpan(16), (ushort)pageSize);
        db[18] = 1; db[19] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(db.AsSpan(28), (uint)pages);
        return db;
    }

    /// <summary>MP4: תיבת ftyp עם מותג, תיבת moov ותיבת mdat.</summary>
    internal static byte[] Mp4(int mediaBytes, int seed)
    {
        using var s = new MemoryStream();

        void Box(string type, byte[] payload)
        {
            byte[] size = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(payload.Length + 8));
            s.Write(size);
            s.Write(Encoding.ASCII.GetBytes(type));
            s.Write(payload);
        }

        Box("ftyp", [.. "isom"u8, 0, 0, 2, 0, .. "isomiso2"u8]);
        Box("moov", Random(512, seed));
        Box("mdat", Random(mediaBytes, seed + 1));
        return s.ToArray();
    }

    /// <summary>
    /// קובץ הרצה PE32 עם שני מקטעים. האורך הנכון הוא סוף המקטע האחרון,
    /// ולא מה שבא אחריו.
    /// </summary>
    internal static byte[] Pe(int seed)
    {
        const int headers = 0x400;
        const int end = 0x1400 + 0x800;

        byte[] b = new byte[end];
        Random(end - headers, seed).CopyTo(b, headers);

        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x3C), 0x80);

        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(b, 0x80);
        int coff = 0x84;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(coff), 0x14C);          // i386
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(coff + 2), 2);          // מקטעים
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(coff + 16), 0xE0);      // כותרת אופציונלית

        int optional = coff + 20;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(optional), 0x10B);      // PE32
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(optional + 60), headers);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(optional + 92), 16);    // מספר טבלאות

        int table = optional + 0xE0;
        Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(b, table);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(table + 16), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(table + 20), 0x400);

        Encoding.ASCII.GetBytes(".data\0\0\0").CopyTo(b, table + 40);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(table + 56), 0x800);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(table + 60), 0x1400);

        return b;
    }

    /// <summary>ICO עם שתי תמונות. האורך הוא סוף התמונה הרחוקה.</summary>
    internal static byte[] Ico(int seed)
    {
        const int first = 1000, second = 500;
        const int offset1 = 6 + 32, offset2 = offset1 + first;

        byte[] b = new byte[offset2 + second];
        Random(first + second, seed).CopyTo(b, offset1);

        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 1);   // סוג: סמל
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 2);   // מספר תמונות

        void Entry(int at, int size, int offset)
        {
            b[at] = 16; b[at + 1] = 16; b[at + 2] = 0; b[at + 3] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 6), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 8), (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 12), (uint)offset);
        }

        Entry(6, first, offset1);
        Entry(22, second, offset2);

        // כל תמונה בסמל פותחת בכותרת BITMAPINFOHEADER: גודל 40, רוחב, גובה כפול
        // (תמונה + מסכה), מישור אחד ועומק צבע.
        void Dib(int at)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), 40);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at + 4), 16);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at + 8), 32);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 14), 32);
        }

        Dib(offset1);
        Dib(offset2);
        return b;
    }

    /// <summary>
    /// GIF עם טבלת צבעים, הרחבה ותמונה. נתוני התמונה מכילים את הבית 3B
    /// שוב ושוב — הסורק חייב לעבור על הבלוקים ולא לחפש אותו.
    /// </summary>
    internal static byte[] Gif()
    {
        using var s = new MemoryStream();
        s.Write("GIF89a"u8);
        s.Write([10, 0, 10, 0, 0x80, 0, 0]);                 // מסך לוגי, טבלה של 2 צבעים
        s.Write(new byte[6]);                               // טבלת הצבעים
        s.Write([0x21, 0xF9, 4, 0, 0, 0, 0, 0]);            // הרחבת בקרה
        s.Write([0x2C, 0, 0, 0, 0, 10, 0, 10, 0, 0]);       // מתאר תמונה
        s.WriteByte(2);                                     // גודל קוד LZW

        byte[] block = new byte[200];
        Array.Fill(block, (byte)0x3B);
        s.WriteByte(200);
        s.Write(block);
        s.WriteByte(0);                                     // סוף הבלוקים

        s.WriteByte(0x3B);                                  // סיום הקובץ
        return s.ToArray();
    }

    /// <summary>ZIP שמסתיים ברשומת סוף ספרייה עם הערה.</summary>
    internal static byte[] Zip(int bodyBytes, int seed)
    {
        byte[] body = Random(bodyBytes, seed);

        // גוף שאינו מכיל בטעות את חתימת סוף הספרייה.
        for (int i = 0; i + 3 < body.Length; i++)
            if (body[i] == 0x50 && body[i + 1] == 0x4B) body[i + 1] = 0x4C;

        using var s = new MemoryStream();
        s.Write([0x50, 0x4B, 0x03, 0x04]);
        s.Write(body);
        s.Write([0x50, 0x4B, 0x05, 0x06]);
        s.Write(new byte[16]);
        s.Write([5, 0]);
        s.Write("hello"u8);
        return s.ToArray();
    }
}
