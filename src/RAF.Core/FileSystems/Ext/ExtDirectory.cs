using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.FileSystems.Ext;

/// <summary>רשומה בתיקייה: שם ומספר אינוד. Deleted — נמצאה ברווח שמחיקה השאירה.</summary>
internal readonly record struct ExtEntry(long Inode, string Name, int FileType, bool Deleted);

/// <summary>
/// תיקייה ב-ext2/3/4 היא רשימה של רשומות באורך משתנה: אינוד, אורך הרשומה, אורך השם,
/// סוג, ושם. מחיקה אינה מוחקת את הרשומה — היא מאריכה את הרשומה שלפניה כך שתבלע
/// אותה. השם ומספר האינוד נשארים ברווח שבסוף הרשומה הקודמת, ומשם הם נקראים.
/// (ברשומה הראשונה בבלוק אין "לפניה" — שם רק מספר האינוד מתאפס, והשם נשאר.)
/// </summary>
internal static class ExtDirectory
{
    /// <param name="indexed">תיקייה עם אינדקס (עץ גיבובים): הרווח אחרי ".." ובבלוקים עם רשומה ריקה הוא האינדקס, לא שרידים.</param>
    internal static List<ExtEntry> Parse(byte[] data, int blockSize, bool fileTypes, long maxInode, bool indexed = false)
    {
        var entries = new List<ExtEntry>();
        for (int block = 0; block + 12 <= data.Length; block += blockSize)
        {
            int end = Math.Min(data.Length, block + blockSize);
            int at = block;
            while (at + 8 <= end)
            {
                long inode = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                int recLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4));
                if (recLen == 0 && blockSize >= 65536) recLen = 65536;
                int nameLen = fileTypes ? data[at + 6] : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 6));
                if (recLen < 8 || at + recLen > end || (recLen & 3) != 0) break;   // בלוק פגום — לבא

                int used = (8 + nameLen + 3) & ~3;
                if (nameLen > 0 && used <= recLen && Name(data, at + 8, nameLen) is { } name)
                {
                    if (inode != 0 && inode <= maxInode)
                        entries.Add(new ExtEntry(inode, name, fileTypes ? data[at + 7] : 0, Deleted: false));
                    else if (inode == 0 && name is not ("." or ".."))
                        entries.Add(new ExtEntry(0, name, fileTypes ? data[at + 7] : 0, Deleted: true));
                }

                // הרווח שבין סוף השם לסוף הרשומה: שרידי רשומות שנמחקו.
                bool index = indexed && ((nameLen == 2 && data[at + 8] == (byte)'.' && data[at + 9] == (byte)'.') || (inode == 0 && nameLen == 0));
                if (recLen - used >= 12 && !index) Slack(data, at + used, at + recLen, fileTypes, maxInode, entries);
                at += recLen;
            }
        }
        return entries;
    }

    private static void Slack(byte[] data, int from, int to, bool fileTypes, long maxInode, List<ExtEntry> entries)
    {
        for (int at = from; at + 12 <= to; at += 4)
        {
            long inode = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            int nameLen = fileTypes ? data[at + 6] : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 6));
            int recLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4));
            if (inode == 0 || inode > maxInode || nameLen == 0 || at + 8 + nameLen > to) continue;
            if (recLen < 8 + nameLen || (recLen & 3) != 0) continue;
            if (fileTypes && data[at + 7] > 7) continue;
            if (Name(data, at + 8, nameLen) is not { } name || name is "." or "..") continue;

            entries.Add(new ExtEntry(inode, name, fileTypes ? data[at + 7] : 0, Deleted: true));
            at += ((8 + nameLen + 3) & ~3) - 4;
        }
    }

    /// <summary>שם: UTF-8 בלי אפסים ובלי "/". בתים שאינם UTF-8 תקין — כנראה לא שם.</summary>
    private static string? Name(byte[] data, int at, int length)
    {
        var span = data.AsSpan(at, length);
        foreach (byte b in span) if (b is 0 or (byte)'/') return null;
        try
        {
            string name = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(span);
            foreach (char c in name) if (char.IsControl(c)) return null;
            return name;
        }
        catch (DecoderFallbackException) { return null; }
    }

    /// <summary>תיקייה קטנה מוטבעת באינוד: 4 בתים של אינוד ההורה, ואז רשומות רגילות.</summary>
    internal static List<ExtEntry> ParseInline(byte[] data, bool fileTypes, long maxInode)
        => data.Length <= 4 ? new() : Parse(data[4..], data.Length - 4, fileTypes, maxInode);
}
