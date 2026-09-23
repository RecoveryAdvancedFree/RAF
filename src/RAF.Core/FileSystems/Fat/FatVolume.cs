using System.Buffers.Binary;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.Fat;

/// <summary>
/// מחיצת FAT פתוחה לקריאה. אחראית על טבלת ההקצאה, על מעקב אחר שרשראות
/// אשכולות ועל המרת מספר אשכול למיקום פיזי.
/// </summary>
internal sealed class FatVolume : IClusterVolume
{
    /// <summary>מספר האשכול הראשון באזור הנתונים. אשכולות 0 ו-1 שמורים.</summary>
    internal const long FirstCluster = 2;

    private readonly VolumeReader _reader;
    private readonly bool _ownsReader;
    private byte[]? _fat;

    internal FatBootSector Boot { get; }

    public int BytesPerCluster => Boot.BytesPerCluster;

    /// <summary>מספר האשכול הגבוה ביותר שקיים באזור הנתונים.</summary>
    internal long MaxCluster => FirstCluster + Boot.ClusterCount - 1;

    private FatVolume(VolumeReader reader, FatBootSector boot, bool ownsReader)
    {
        _reader = reader;
        Boot = boot;
        _ownsReader = ownsReader;
    }

    /// <summary>פתיחת מחיצת FAT. מחזיר null אם אינה FAT תקין.</summary>
    internal static FatVolume? Open(VolumeReader reader, bool ownsReader = false)
    {
        byte[] sector = reader.ReadBlock(0, 512);
        var boot = FatBootSector.Parse(sector);
        if (boot is null) return null;

        var volume = new FatVolume(reader, boot, ownsReader);
        volume.LoadFat();
        return volume;
    }

    /// <summary>
    /// טעינת טבלת ה-FAT לזיכרון. במחיצה גדולה הטבלה עשויה להיות עשרות
    /// מגה-בתים, ולכן היא מוגבלת בגודלה; ללא הטבלה עדיין ניתן לשחזר
    /// קבצים רציפים, אך לא לעקוב אחר שרשראות.
    /// </summary>
    private void LoadFat()
    {
        try
        {
            long size = Math.Min(Boot.FatBytes, 256L * 1024 * 1024);
            byte[] fat = _reader.ReadBlock(Boot.FatOffset, (int)size);
            _fat = fat.Length > 0 ? fat : null;
        }
        catch
        {
            _fat = null;
        }
    }

    /// <summary>המרת מספר אשכול להיסט בבתים מתחילת המחיצה.</summary>
    public long ClusterToOffset(long cluster)
        => (Boot.FirstDataSector + (cluster - FirstCluster) * Boot.SectorsPerCluster)
           * Boot.BytesPerSector;

    public int ReadRaw(long offset, Span<byte> destination) => _reader.Read(offset, destination);

    /// <summary>האם האשכול מסומן כתפוס בטבלת ההקצאה.</summary>
    public bool? IsClusterAllocated(long cluster)
    {
        long? entry = ReadFatEntry(cluster);
        return entry is null ? null : entry.Value != 0;
    }

    /// <summary>קריאת ערך מטבלת ה-FAT עבור אשכול נתון.</summary>
    internal long? ReadFatEntry(long cluster)
    {
        if (_fat is null || cluster < 0) return null;

        switch (Boot.Kind)
        {
            case FileSystemKind.Fat32:
            {
                long at = cluster * 4;
                if (at + 4 > _fat.Length) return null;
                // 4 הביטים העליונים שמורים ואינם חלק במספר האשכול.
                return BinaryPrimitives.ReadUInt32LittleEndian(_fat.AsSpan((int)at)) & 0x0FFFFFFF;
            }

            case FileSystemKind.Fat16:
            {
                long at = cluster * 2;
                if (at + 2 > _fat.Length) return null;
                return BinaryPrimitives.ReadUInt16LittleEndian(_fat.AsSpan((int)at));
            }

            case FileSystemKind.Fat12:
            {
                // ערכי FAT12 ארוזים ב-12 ביט: שני ערכים בכל שלושה בתים.
                long at = cluster + cluster / 2;
                if (at + 2 > _fat.Length) return null;

                int pair = BinaryPrimitives.ReadUInt16LittleEndian(_fat.AsSpan((int)at));
                return (cluster & 1) == 0 ? pair & 0x0FFF : pair >> 4;
            }

            default:
                return null;
        }
    }

    /// <summary>האם הערך מסמן את סוף השרשרת.</summary>
    internal bool IsEndOfChain(long entry) => Boot.Kind switch
    {
        FileSystemKind.Fat12 => entry >= 0x0FF8,
        FileSystemKind.Fat16 => entry >= 0xFFF8,
        FileSystemKind.Fat32 => entry >= 0x0FFFFFF8,
        _ => true,
    };

    /// <summary>האם הערך מסמן אשכול פגום.</summary>
    internal bool IsBadCluster(long entry) => Boot.Kind switch
    {
        FileSystemKind.Fat12 => entry == 0x0FF7,
        FileSystemKind.Fat16 => entry == 0xFFF7,
        FileSystemKind.Fat32 => entry == 0x0FFFFFF7,
        _ => false,
    };

    internal bool IsValidCluster(long cluster)
        => cluster >= FirstCluster && cluster <= MaxCluster;

    /// <summary>
    /// מעקב אחר שרשרת אשכולות מלאה, והמרתה לרשימת מקטעים רציפים.
    /// משמש לקבצים קיימים ולספריות.
    /// </summary>
    internal List<DataExtent> FollowChain(long startCluster, long maxClusters = 1 << 22)
    {
        var extents = new List<DataExtent>();
        if (!IsValidCluster(startCluster)) return extents;

        var visited = new HashSet<long>();
        long runStart = startCluster;
        long runLength = 0;
        long current = startCluster;

        while (IsValidCluster(current) && runLength < maxClusters)
        {
            // שרשרת מעגלית מעידה על טבלה פגומה.
            if (!visited.Add(current)) break;

            if (runLength == 0 || current == runStart + runLength)
            {
                runLength++;
            }
            else
            {
                extents.Add(new DataExtent(runStart, runLength, false));
                runStart = current;
                runLength = 1;
            }

            long? next = ReadFatEntry(current);
            if (next is null) break;
            if (IsEndOfChain(next.Value) || IsBadCluster(next.Value)) break;
            if (next.Value == 0) break; // אשכול פנוי — השרשרת נקטעה

            current = next.Value;
        }

        if (runLength > 0) extents.Add(new DataExtent(runStart, runLength, false));
        return extents;
    }

    /// <summary>
    /// בניית מקטע רציף מאשכול התחלה ומגודל.
    ///
    /// כאשר קובץ נמחק ב-FAT, המערכת מאפסת את כל ערכי ה-FAT של השרשרת שלו,
    /// ולכן לא נותר מידע על סדר האשכולות. ההנחה המקובלת בשחזור היא הקצאה
    /// רציפה מנקודת ההתחלה — נכונה ברוב הקבצים, ושגויה בקובץ שהיה מפוצל.
    /// </summary>
    internal List<DataExtent> ContiguousExtent(long startCluster, long sizeBytes)
    {
        var extents = new List<DataExtent>();
        if (!IsValidCluster(startCluster) || sizeBytes <= 0) return extents;

        long clusters = (sizeBytes + BytesPerCluster - 1) / BytesPerCluster;
        clusters = Math.Min(clusters, MaxCluster - startCluster + 1);
        if (clusters <= 0) return extents;

        extents.Add(new DataExtent(startCluster, clusters, false));
        return extents;
    }

    /// <summary>קריאת תוכן אשכולות רצופים לוגית למאגר אחד.</summary>
    internal byte[] ReadChain(IReadOnlyList<DataExtent> extents, int maxBytes)
    {
        var buffer = new List<byte>(Math.Min(maxBytes, 1 << 20));

        foreach (var extent in extents)
        {
            for (long i = 0; i < extent.ClusterCount && buffer.Count < maxBytes; i++)
            {
                int take = Math.Min(BytesPerCluster, maxBytes - buffer.Count);
                byte[] chunk = new byte[take];

                int read = _reader.Read(ClusterToOffset(extent.StartCluster + i), chunk);
                if (read <= 0) return buffer.ToArray();

                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            }
        }

        return buffer.ToArray();
    }

    /// <summary>קריאת בלוק מהיסט מוחלט במחיצה — לאזורים שאינם אשכולות.</summary>
    internal byte[] ReadBlockAt(long offset, int length) => _reader.ReadBlock(offset, length);

    internal long VolumeLength => _reader.Length;

    public void Dispose()
    {
        if (_ownsReader) _reader.Dispose();
    }
}
