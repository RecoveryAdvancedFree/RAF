using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.FileSystems.Xfs;

/// <summary>רשומה בתיקייה. InodeLow — ברשומה שנמחקה נשארות רק 32 הסיביות התחתונות של מספר האינוד.</summary>
internal readonly record struct XfsEntry(ulong Inode, string Name, int FileType, bool Deleted, bool InodeLow);

/// <summary>
/// תיקיות XFS בשלוש צורות: קטנה — בתוך האינוד; ובלוקים של נתונים — רשומות באורך
/// משתנה, כל אחת מסתיימת ב"תג" שהוא ההיסט של תחילתה.
///
/// מחיקה הופכת רשומה לאזור פנוי: 4 הבתים הראשונים (מתוך 8 של מספר האינוד) נדרסים
/// בסימן "פנוי" ובאורך — אבל 4 הבתים האחרים, השם והתג נשארים. התג הוא מה שמאמת
/// שאכן זו רשומה ולא בתים מקריים.
/// </summary>
internal static class XfsDirectory
{
    /// <summary>
    /// תיקייה קטנה שבתוך האינוד. מחיקה מזיזה את הרשומות שאחריה אחורה ומקצרת את התיקייה,
    /// אבל לינוקס כותב לדיסק רק את החלק שבשימוש — מה שהיה אחריו נשאר כמו שהיה. לכן אחרי
    /// סוף התיקייה נמצאות לרוב הרשומות שנמחקו, שלמות, עם מספר האינוד.
    /// </summary>
    internal static List<XfsEntry> ParseShort(ReadOnlySpan<byte> fork, long size, bool fileTypes)
    {
        var entries = new List<XfsEntry>();
        if (fork.Length < 6) return entries;
        int count = fork[0];
        bool wide = fork[1] > 0;
        int inodeBytes = wide ? 8 : 4;
        int at = 2 + inodeBytes;
        int end = (int)Math.Min(fork.Length, size);

        for (int i = 0; i < count && at + 3 <= end; i++)
        {
            int nameLen = fork[at];
            int nameAt = at + 3;
            int after = nameAt + nameLen + (fileTypes ? 1 : 0);
            if (nameLen == 0 || after + inodeBytes > end) break;
            string? name = Name(fork.Slice(nameAt, nameLen));
            int type = fileTypes ? fork[nameAt + nameLen] : 0;
            ulong inode = wide ? BinaryPrimitives.ReadUInt64BigEndian(fork[after..]) : BinaryPrimitives.ReadUInt32BigEndian(fork[after..]);
            if (name is not null) entries.Add(new XfsEntry(inode, name, type, false, false));
            at = after + inodeBytes;
        }

        // שרידים אחרי הסוף. אחרי מחיקה מהאמצע השארית מתחילה באמצע רשומה — לכן מתקדמים
        // בית-בית, ומקבלים רק מה שנראה כרשומה: היסט בכפולות של 8, שם תקין, סוג קובץ ואינוד.
        at = Math.Max(at, end);
        while (at + 3 < fork.Length)
        {
            int nameLen = fork[at];
            int offset = BinaryPrimitives.ReadUInt16BigEndian(fork[(at + 1)..]);
            int after = at + 3 + nameLen + (fileTypes ? 1 : 0);
            if (nameLen == 0 || (offset & 7) != 0 || offset < 16 || after + inodeBytes > fork.Length) { at++; continue; }
            int type = fileTypes ? fork[at + 3 + nameLen] : 0;
            ulong inode = wide ? BinaryPrimitives.ReadUInt64BigEndian(fork[after..]) : BinaryPrimitives.ReadUInt32BigEndian(fork[after..]);
            if ((fileTypes && type is < 1 or > 7) || inode == 0 || Name(fork.Slice(at + 3, nameLen)) is not { } name
                || name is "." or "..") { at++; continue; }
            entries.Add(new XfsEntry(inode, name, type, true, false));
            at = after + inodeBytes;
        }
        return entries;
    }

    /// <summary>
    /// בלוק נתונים של תיקייה. headerSize — אורך הכותרת (16, או 64 בגרסה 5). בתיקייה
    /// "בלוק יחיד" סוף הבלוק הוא אינדקס, ולא רשומות.
    /// </summary>
    internal static List<XfsEntry> ParseBlock(byte[] block, bool v5, bool fileTypes)
    {
        var entries = new List<XfsEntry>();
        var magic = block.AsSpan(0, 4);
        bool single = magic.SequenceEqual("XD2B"u8) || magic.SequenceEqual("XDB3"u8);
        bool data = magic.SequenceEqual("XD2D"u8) || magic.SequenceEqual("XDD3"u8);
        if (!single && !data) return entries;

        int at = v5 ? 64 : 16;
        int end = block.Length;
        if (single)
        {
            int leafCount = (int)BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(block.Length - 8));
            end = block.Length - 8 - leafCount * 8;
            if (end < at) return entries;
        }

        while (at + 16 <= end)
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(at)) == 0xFFFF)
            {
                int length = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(at + 2));
                if (length < 8 || (length & 7) != 0 || at + length > end) break;
                Freed(block, at, at + length, fileTypes, entries);
                at += length;
                continue;
            }

            int nameLen = block[at + 8];
            int size = EntrySize(nameLen, fileTypes);
            if (nameLen == 0 || at + size > end) break;
            ulong inode = BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(at));
            string? name = Name(block.AsSpan(at + 9, nameLen));
            if (name is not null && name is not ("." or ".."))
                entries.Add(new XfsEntry(inode, name, fileTypes ? block[at + 9 + nameLen] : 0, false, false));
            at += size;
        }
        return entries;
    }

    private static int EntrySize(int nameLen, bool fileTypes) => (8 + 1 + nameLen + (fileTypes ? 1 : 0) + 2 + 7) & ~7;

    /// <summary>שרידי רשומות באזור פנוי: כל מקום בכפולות של 8 שהתג בסופו מצביע עליו.</summary>
    private static void Freed(byte[] block, int from, int to, bool fileTypes, List<XfsEntry> entries)
    {
        for (int at = from; at + 16 <= to; at += 8)
        {
            int nameLen = block[at + 8];
            if (nameLen == 0) continue;
            int size = EntrySize(nameLen, fileTypes);
            if (at + size > to) continue;
            // הרשומה האחרונה באזור: התג שלה נדרס בתג של האזור כולו — שמצביע על תחילתו.
            int tag = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(at + size - 2));
            if (tag != at && !(at + size == to && tag == from)) continue;
            string? name = Name(block.AsSpan(at + 9, nameLen));
            if (name is null || name is "." or "..") continue;

            bool overwritten = at == from;   // הרשומה הראשונה באזור — חציו העליון של מספר האינוד נדרס
            ulong inode = overwritten
                ? BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at + 4))
                : BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(at));
            entries.Add(new XfsEntry(inode, name, fileTypes ? block[at + 9 + nameLen] : 0, true, overwritten));
            at += size - 8;
        }
    }

    private static string? Name(ReadOnlySpan<byte> span)
    {
        foreach (byte b in span) if (b is 0 or (byte)'/') return null;
        try
        {
            string name = new UTF8Encoding(false, true).GetString(span);
            foreach (char c in name) if (char.IsControl(c)) return null;
            return name;
        }
        catch (DecoderFallbackException) { return null; }
    }
}
