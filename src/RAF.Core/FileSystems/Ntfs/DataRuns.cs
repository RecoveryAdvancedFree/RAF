using RAF.Core.Model;

namespace RAF.Core.FileSystems.Ntfs;

/// <summary>
/// פענוח רשימת ה-data runs של תכונה לא-רזידנטית.
/// זהו המנגנון שבו NTFS מתאר היכן תוכן הקובץ יושב פיזית על המחיצה.
/// </summary>
internal static class DataRuns
{
    /// <summary>
    /// פענוח רשימת ריצות לרשימת מקטעי אשכולות.
    /// כל ריצה מקודדת כאורך ואחריו היסט יחסי לריצה הקודמת.
    /// </summary>
    internal static List<DataExtent> Decode(ReadOnlySpan<byte> runList)
    {
        var extents = new List<DataExtent>();
        long currentLcn = 0;
        int pos = 0;

        while (pos < runList.Length)
        {
            byte header = runList[pos++];
            if (header == 0) break; // סוף הרשימה

            int lengthBytes = header & 0x0F;
            int offsetBytes = (header >> 4) & 0x0F;

            // אורך אפס אינו חוקי, ומעל 8 בתים חורג מטווח long.
            if (lengthBytes is 0 or > 8 || offsetBytes > 8) break;
            if (pos + lengthBytes + offsetBytes > runList.Length) break;

            long length = ReadUnsigned(runList.Slice(pos, lengthBytes));
            pos += lengthBytes;

            if (offsetBytes == 0)
            {
                // ללא היסט: זהו מקטע דליל (sparse) שאינו תופס מקום על הדיסק.
                extents.Add(new DataExtent(0, length, IsSparse: true));
                continue;
            }

            long offset = ReadSigned(runList.Slice(pos, offsetBytes));
            pos += offsetBytes;

            currentLcn += offset;

            // היסט שלילי מצטבר מתחת לאפס מעיד על רשימה פגומה.
            if (currentLcn < 0) break;
            if (length <= 0) break;

            extents.Add(new DataExtent(currentLcn, length, IsSparse: false));
        }

        return extents;
    }

    /// <summary>קריאת מספר חסר סימן באורך משתנה, little-endian.</summary>
    private static long ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int i = bytes.Length - 1; i >= 0; i--)
            value = (value << 8) | bytes[i];
        return value;
    }

    /// <summary>קריאת מספר עם סימן באורך משתנה, little-endian, בהשלמה ל-2.</summary>
    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int i = bytes.Length - 1; i >= 0; i--)
            value = (value << 8) | bytes[i];

        // הרחבת הסימן מהבית העליון של הערך המקודד.
        int bits = bytes.Length * 8;
        if (bits < 64 && (value & (1L << (bits - 1))) != 0)
            value |= -1L << bits;

        return value;
    }

    /// <summary>סך האשכולות שמקטעים אלה תופסים בפועל, ללא מקטעים דלילים.</summary>
    internal static long AllocatedClusters(IEnumerable<DataExtent> extents)
        => extents.Where(e => !e.IsSparse).Sum(e => e.ClusterCount);
}
