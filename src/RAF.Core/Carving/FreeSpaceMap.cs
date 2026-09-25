using RAF.Core.FileSystems;
using RAF.Core.FileSystems.ExFat;
using RAF.Core.FileSystems.Fat;
using RAF.Core.FileSystems.Ntfs;

namespace RAF.Core.Carving;

/// <summary>
/// המקום הפנוי במחיצה, לפי מפת ההקצאה של מערכת הקבצים — רעיון מהתוכנה "משיב".
///
/// קובץ שנמחק יושב במקום שמערכת הקבצים סימנה כפנוי. המקום התפוס מכיל את
/// הקבצים הקיימים, ולכן הסריקה המתקדמת יכולה לדלג עליו בלי לקרוא אותו — בכונן
/// מלא ברובו זה מהיר פי כמה. אחרי פירמוט מהיר מערכת הקבצים החדשה מסמנת כמעט
/// הכול כפנוי, כך שהסריקה נשארת כמעט מלאה; וכשמערכת הקבצים אינה נקראת אין מפה,
/// והמחיצה נסרקת כולה.
///
/// כל מה שאינו אשכול נתונים (מגזר האתחול, טבלאות ה-FAT) נחשב תפוס: אין בו קבצים.
/// </summary>
internal sealed class FreeSpaceMap
{
    /// <summary>טווחי הבתים הפנויים, ממוינים ולא חופפים, יחסית לתחילת המחיצה.</summary>
    private readonly List<(long From, long To)> _free;

    public long FreeBytes { get; }
    public long TotalBytes { get; }

    private FreeSpaceMap(List<(long From, long To)> free, long total)
    {
        _free = free;
        FreeBytes = free.Sum(r => r.To - r.From);
        TotalBytes = total;
    }

    /// <summary>מפה מטווחים ידועים — לבדיקות.</summary>
    internal static FreeSpaceMap FromRanges(IEnumerable<(long From, long To)> free, long total)
        => new(free.OrderBy(r => r.From).ToList(), total);

    /// <summary>המפה, או null כשמפת ההקצאה אינה זמינה — ואז סורקים הכול.</summary>
    internal static FreeSpaceMap? Build(IClusterVolume volume, long partitionSize)
    {
        (long first, long last) = volume switch
        {
            NtfsVolume ntfs => Ntfs(ntfs),
            ExFatVolume exfat => (ExFatVolume.FirstCluster, exfat.MaxCluster),
            FatVolume fat => (FatVolume.FirstCluster, fat.MaxCluster),
            FileSystems.Ext.ExtVolume ext => (ext.Super.FirstDataBlock, ext.Super.BlocksCount - 1),
            FileSystems.Xfs.XfsVolume xfs => (0, xfs.TotalBlocks - 1),
            FileSystems.Btrfs.BtrfsVolume btrfs => (0, btrfs.Length / btrfs.SectorSize - 1),
            FileSystems.Hfs.HfsVolume hfs => (0, hfs.TotalBlocks - 1),
            FileSystems.Apfs.ApfsVolume apfs => (0, apfs.BlockCount - 1),
            _ => (-1, -1),
        };
        if (first < 0 || last < first) return null;

        int cluster = volume.BytesPerCluster;
        var free = new List<(long, long)>();
        long runStart = -1, runEnd = -1, unknown = 0;

        for (long c = first; c <= last; c++)
        {
            bool? allocated = volume.IsClusterAllocated(c);
            if (allocated is null) unknown++;
            if (allocated != false) continue;

            long from = volume.ClusterToOffset(c);
            long to = Math.Min(from + cluster, partitionSize);
            if (from >= partitionSize) break;

            if (from == runEnd) runEnd = to;
            else
            {
                if (runStart >= 0) free.Add((runStart, runEnd));
                runStart = from;
                runEnd = to;
            }
        }
        if (runStart >= 0) free.Add((runStart, runEnd));

        // מפה שחלק ניכר ממנה אינו ידוע אינה מפה — עדיף לסרוק הכול.
        if (unknown > (last - first + 1) / 100) return null;
        return new FreeSpaceMap(free, partitionSize);

        static (long, long) Ntfs(NtfsVolume ntfs)
        {
            ntfs.LoadClusterBitmap();
            return ntfs.IsClusterAllocated(0) is null ? (-1, -1) : (0, ntfs.Boot.TotalClusters - 1);
        }
    }

    /// <summary>האם הבית בהיסט נתון נמצא במקום פנוי.</summary>
    internal bool IsFree(long offset)
    {
        int i = Find(offset);
        return i < _free.Count && _free[i].From <= offset;
    }

    /// <summary>תחילת המקום הפנוי הראשון מהיסט נתון והלאה, או long.MaxValue.</summary>
    internal long NextFree(long offset)
    {
        int i = Find(offset);
        if (i >= _free.Count) return long.MaxValue;
        return Math.Max(offset, _free[i].From);
    }

    /// <summary>הטווח הראשון שמסתיים אחרי ההיסט (חיפוש בינארי).</summary>
    private int Find(long offset)
    {
        int lo = 0, hi = _free.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_free[mid].To <= offset) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
