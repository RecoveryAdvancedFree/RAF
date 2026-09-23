using System.Buffers.Binary;
using System.Text;
using RAF.Core.FileSystems.Ntfs;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// בדיקות פריסת LZNT1. שגיאה כאן מייצרת קובץ שנראה משוחזר אך תוכנו הרוס,
/// ולכן ההתנהגות נבדקת מול בלוקים שנבנו ידנית לפי מפרט הפורמט.
/// </summary>
public class Lznt1Tests
{
    /// <summary>עטיפת תוכן בלוק בכותרת LZNT1 מתאימה.</summary>
    private static byte[] Chunk(byte[] body, bool compressed)
    {
        ushort header = (ushort)((body.Length - 1) & 0x0FFF);
        header |= 0x3000;                       // חתימת הפורמט
        if (compressed) header |= 0x8000;

        byte[] result = new byte[2 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, header);
        body.CopyTo(result, 2);
        return result;
    }

    [Fact]
    public void Uncompressed_chunk_passes_through_unchanged()
    {
        byte[] body = Encoding.ASCII.GetBytes("RAW DATA BLOCK");

        byte[] output = Lznt1.Decompress(Chunk(body, compressed: false), body.Length);

        Assert.Equal(body, output);
    }

    [Fact]
    public void Back_reference_expands_a_repeating_sequence()
    {
        // דגלים: שלושה בתים מקוריים ואז פריט הפניה לאחור.
        // ההפניה: היסט 3 אחורה, אורך 6 → מייצרת "ABCABCABC".
        ushort token = (ushort)(((3 - 1) << 12) | (6 - 3));

        byte[] body =
        {
            0b0000_1000,                    // בית דגלים: רק הפריט הרביעי הוא הפניה
            (byte)'A', (byte)'B', (byte)'C',
            (byte)(token & 0xFF), (byte)(token >> 8),
        };

        byte[] output = Lznt1.Decompress(Chunk(body, compressed: true), 9);

        Assert.Equal("ABCABCABC", Encoding.ASCII.GetString(output));
    }

    [Fact]
    public void Overlapping_back_reference_is_copied_byte_by_byte()
    {
        // היסט 1 אחורה עם אורך 5: חייב לשכפל את הבית האחרון חמש פעמים.
        // העתקת בלוק במקום העתקה בית-אחר-בית הייתה מייצרת תוצאה שגויה.
        ushort token = (ushort)(((1 - 1) << 12) | (5 - 3));

        byte[] body =
        {
            0b0000_0010,                    // בית מקורי אחד ואז הפניה
            (byte)'X',
            (byte)(token & 0xFF), (byte)(token >> 8),
        };

        byte[] output = Lznt1.Decompress(Chunk(body, compressed: true), 6);

        Assert.Equal("XXXXXX", Encoding.ASCII.GetString(output));
    }

    [Fact]
    public void Zero_header_terminates_the_stream()
    {
        byte[] body = Encoding.ASCII.GetBytes("FIRST");
        byte[] input = new byte[2 + body.Length + 2];
        Chunk(body, compressed: false).CopyTo(input, 0);
        // שני בתי אפס בסוף מסמנים שאין בלוקים נוספים.

        byte[] output = Lznt1.Decompress(input, 32);

        Assert.Equal("FIRST", Encoding.ASCII.GetString(output));
    }

    [Fact]
    public void Multiple_chunks_are_concatenated()
    {
        byte[] first = Encoding.ASCII.GetBytes("HELLO ");
        byte[] second = Encoding.ASCII.GetBytes("WORLD");

        byte[] a = Chunk(first, compressed: false);
        byte[] b = Chunk(second, compressed: false);

        byte[] input = new byte[a.Length + b.Length];
        a.CopyTo(input, 0);
        b.CopyTo(input, a.Length);

        byte[] output = Lznt1.Decompress(input, first.Length + second.Length);

        Assert.Equal("HELLO WORLD", Encoding.ASCII.GetString(output));
    }

    [Fact]
    public void Truncated_input_returns_what_was_decoded_instead_of_throwing()
    {
        // בלוק שמצהיר על 100 בתים אך נקטע — מצב טיפוסי בשחזור מדיסק פגום.
        byte[] input = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(input, (ushort)(0xB000 | 99));

        byte[] output = Lznt1.Decompress(input, 4096);

        Assert.Empty(output);
    }

    [Fact]
    public void Back_reference_before_chunk_start_is_rejected()
    {
        // הפניה להיסט גדול ממה שנכתב עד כה מעידה על נתונים פגומים.
        ushort token = (ushort)(((5 - 1) << 12) | (5 - 3));

        byte[] body =
        {
            0b0000_0010,
            (byte)'A',
            (byte)(token & 0xFF), (byte)(token >> 8),
        };

        byte[] output = Lznt1.Decompress(Chunk(body, compressed: true), 16);

        // הבית התקין נשמר, והפענוח נעצר במקום לייצר זבל.
        Assert.Equal("A", Encoding.ASCII.GetString(output));
    }
}
