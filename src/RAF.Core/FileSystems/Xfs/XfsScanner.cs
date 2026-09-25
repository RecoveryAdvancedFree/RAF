using System.Diagnostics;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.Xfs;

/// <summary>
/// סורק XFS — מערכת הקבצים של הרבה שרתי אחסון ביתיים ושרתי לינוקס.
///
/// קבצים שנמחקו: השם נשאר באזור הפנוי שבבלוק התיקייה, עם החצי התחתון של מספר
/// האינוד; האינוד עצמו מאבד את הסוג והגודל, אבל רשומות המקטעים נשארות בו. הגודל
/// המדויק אבד — הקובץ משוחזר עד סוף הבלוק האחרון שלו, בלי האפסים שבסופו.
/// </summary>
public sealed class XfsScanner
{
    private readonly List<string> _warnings = new();
    private readonly HashSet<ulong> _visited = new();
    private readonly HashSet<ulong> _reported = new();
    private XfsVolume _volume = null!;
    private CancellationToken _token;
    private long _examined;
    private long _synthetic = 1L << 40;
    private int _verifyBudget = 100_000;
    private int _lost, _verifiedEmpty;

    public static Task<ScanResult> ScanAsync(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, TrimState trim,
        IProgress<ScanProgress>? progress, CancellationToken token)
        => Task.Run(() => new XfsScanner().Run(
            diskNumber, partitionOffset, partitionSize, sectorSize, mode, includeExisting, progress, token), token);

    private ScanResult Run(int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        _token = token;

        using var reader = VolumeReader.TryOpen(diskNumber, partitionOffset, partitionSize, sectorSize, sequential: false)
            ?? throw new IOException(RawDevice.OpenFailure());
        using var volume = XfsVolume.Open(reader)
            ?? throw new InvalidDataException(L.T("המחיצה אינה XFS תקינה, או שהכותרת שלה פגומה."));
        _volume = volume;

        var files = new List<RecoveredFile>();
        progress?.Report(new ScanProgress { Stage = L.T("קורא את ספריית השורש"), Elapsed = clock.Elapsed });

        var root = volume.ReadInode(volume.Super.RootInode)
            ?? throw new InvalidDataException(L.T("תיקיית השורש של המחיצה אינה נקראת."));
        _visited.Add(root.Number);
        Walk(root, "", false, files, includeExisting, progress, clock, 0);

        if (_lost > 0)
            _warnings.Add(L.T("ל-{0} קבצים שנמחקו נמצא השם, אבל המידע על מיקום התוכן כבר לא קיים. " +
                "סריקה מתקדמת עשויה למצוא את התוכן שלהם לפי סוג הקובץ, בלי השם.", _lost.ToString("N0")));
        if (_verifiedEmpty > 0)
            _warnings.Add(L.T("{0} קבצים נמצאו ברשומות הספרייה אך אזור הנתונים שלהם מכיל אפסים. " +
                "הם סומנו כלא ניתנים לשחזור.", _verifiedEmpty.ToString("N0")));

        return new ScanResult
        {
            Files = files,
            Mode = mode,
            Duration = clock.Elapsed,
            Cancelled = token.IsCancellationRequested,
            FileSystem = "XFS",
            RecordsExamined = _examined,
            Warnings = _warnings,
        };
    }

    /// <summary>בלוקי הנתונים של תיקייה מתחילים ב-0; מ-32GB (בבתים) מתחיל האינדקס שלה.</summary>
    private long LeafOffset => (32L << 30) / _volume.Super.BlockSize;

    private List<XfsEntry> Entries(XfsInode directory)
    {
        var sb = _volume.Super;
        if (directory.Format == 1)
            return XfsDirectory.ParseShort(directory.DataFork, directory.InUse ? directory.Size : directory.DataFork.Length, sb.FileTypeInEntries);

        var entries = new List<XfsEntry>();
        var runs = _volume.RunsOf(directory);
        if (runs is null) return entries;

        int perDirBlock = sb.DirBlockSize / sb.BlockSize;
        foreach (var run in runs.Where(r => r.Offset < LeafOffset && !r.Unwritten).OrderBy(r => r.Offset))
            for (long i = 0; i + perDirBlock <= run.Count; i += perDirBlock)
            {
                var block = _volume.ReadCached((run.Block + i) * sb.BlockSize, sb.DirBlockSize);
                if (block is not null) entries.AddRange(XfsDirectory.ParseBlock(block, sb.IsV5, sb.FileTypeInEntries));
            }
        return entries;
    }

    private void Walk(XfsInode directory, string path, bool deletedTree, List<RecoveredFile> files, bool includeExisting,
        IProgress<ScanProgress>? progress, Stopwatch clock, int depth)
    {
        if (_token.IsCancellationRequested || depth > 256) return;

        foreach (var entry in Entries(directory))
        {
            if (_token.IsCancellationRequested) return;
            _examined++;
            string childPath = path.Length == 0 ? entry.Name : path + "\\" + entry.Name;

            ulong number = entry.Inode;
            // מספר אינוד שחציו העליון נדרס: ברוב המחיצות הוא ממילא אפס; אחרת — כמו של התיקייה.
            if (entry.InodeLow) number |= directory.Number & 0xFFFFFFFF00000000UL;
            var inode = _volume.ReadInode(number);
            bool deleted = entry.Deleted || deletedTree;

            if (inode is null)
            {
                if (deleted && entry.FileType is 0 or 1) files.Add(Lost(entry.Name, path));
                continue;
            }
            if (deleted && inode.InUse) continue;   // שונה שם, או שהאינוד כבר של קובץ אחר

            if (!deleted)
            {
                if (inode.IsDirectory)
                {
                    if (_visited.Add(number)) Walk(inode, childPath, false, files, includeExisting, progress, clock, depth + 1);
                    continue;
                }
                if (!inode.IsRegular || !includeExisting) continue;
                files.Add(Existing(inode, entry.Name, path));
            }
            else
            {
                if (!_reported.Add(number)) continue;
                if (entry.FileType == 2 || (entry.FileType == 0 && LooksLikeDirectory(inode)))
                {
                    if (_visited.Add(number)) Walk(inode, childPath, true, files, includeExisting, progress, clock, depth + 1);
                    continue;
                }
                if (entry.FileType is not (0 or 1)) continue;
                files.Add(Deleted(inode, entry.Name, path));
            }

            if (files.Count % 500 == 0)
                progress?.Report(new ScanProgress { Stage = L.T("עובר על ספריות מערכת הקבצים"), FilesFound = files.Count, Elapsed = clock.Elapsed });
        }
    }

    /// <summary>אינוד שנמחק בלי סוג: תיקייה, אם הבלוק הראשון שלו נראה כבלוק תיקייה.</summary>
    private bool LooksLikeDirectory(XfsInode inode)
    {
        var first = _volume.RunsOf(inode)?.OrderBy(r => r.Offset).FirstOrDefault();
        if (first is not { Count: > 0 } run || run.Offset != 0) return false;
        var block = _volume.ReadBlock(run.Block);
        if (block is null) return false;
        var magic = block.AsSpan(0, 4);
        return magic.SequenceEqual("XD2B"u8) || magic.SequenceEqual("XDB3"u8) ||
               magic.SequenceEqual("XD2D"u8) || magic.SequenceEqual("XDD3"u8);
    }

    private RecoveredFile Existing(XfsInode inode, string name, string path)
    {
        var extents = _volume.ExtentsOf(inode, inode.Size);
        var file = new RecoveredFile
        {
            Id = (long)inode.Number,
            Name = name,
            Path = path,
            Size = inode.Size,
            Created = inode.Created,
            Modified = inode.Modified,
            Accessed = inode.Accessed,
            Source = DiscoverySource.MftActive,
            Extents = extents ?? new List<DataExtent>(),
        };
        if (extents is null && inode.Size > 0)
        {
            file.Quality = RecoveryQuality.Poor;
            file.QualityReason = L.T("הקובץ קיים, אבל המידע על מיקום התוכן שלו פגום.");
        }
        else
        {
            file.Quality = RecoveryQuality.Excellent;
            file.Content = ContentCheck.HasData;
            file.QualityReason = L.T("הקובץ קיים במערכת הקבצים ומיקומו ידוע במלואו.");
        }
        return file;
    }

    private RecoveredFile Deleted(XfsInode inode, string name, string path)
    {
        var runs = _volume.RunsOf(inode);
        if (runs is null || runs.Count == 0) return Lost(name, path, inode);

        // הגודל אופס: עד סוף הבלוק האחרון, בלי האפסים שבסופו.
        long blocks = runs.Max(r => r.Offset + r.Count);
        var extents = XfsInode.ToExtents(runs, blocks);
        long size = blocks * _volume.Super.BlockSize - TrailingZeros(extents);

        var file = new RecoveredFile
        {
            Id = (long)inode.Number,
            Name = name,
            Path = path,
            Size = size,
            IsDeleted = true,
            Created = inode.Created,
            Modified = inode.Modified,
            Accessed = inode.Accessed,
            Source = DiscoverySource.MftActive,
            Extents = extents,
        };
        Assess(file);
        return file;
    }

    /// <summary>כמה אפסים בסוף הבלוק האחרון של הקובץ.</summary>
    private int TrailingZeros(List<DataExtent> extents)
    {
        if (extents.Count == 0 || extents[^1].IsSparse) return 0;
        var last = extents[^1];
        var block = _volume.ReadBlock(last.StartCluster + last.ClusterCount - 1);
        if (block is null) return 0;
        int zeros = 0;
        for (int i = block.Length - 1; i >= 0 && block[i] == 0; i--) zeros++;
        return zeros == block.Length ? 0 : zeros;
    }

    private RecoveredFile Lost(string name, string path, XfsInode? inode = null)
    {
        _lost++;
        return new RecoveredFile
        {
            Id = _synthetic++,
            Name = name,
            Path = path,
            IsDeleted = true,
            Modified = inode?.Modified,
            Accessed = inode?.Accessed,
            Created = inode?.Created,
            Source = DiscoverySource.MftActive,
            Quality = RecoveryQuality.Unrecoverable,
            QualityReason = L.T("השם נשאר, אבל המידע על מיקום התוכן כבר לא קיים. סריקה מתקדמת עשויה למצוא את התוכן לפי סוג הקובץ."),
        };
    }

    private void Assess(RecoveredFile file)
    {
        string basis = L.T("המיקום נשאר ברשומת הקובץ. הגודל המקורי נמחק, ולכן הקובץ משוחזר עד סוף הבלוק האחרון שלו.");
        if (_verifyBudget-- > 0)
            file.Content = new ClusterStream(_volume, file.Extents, file.Size, 0).SampleContent();
        if (file.Content == ContentCheck.Empty)
        {
            _verifiedEmpty++;
            file.Quality = RecoveryQuality.Unrecoverable;
            file.QualityReason = L.T("אזור הנתונים של הקובץ מכיל אפסים בלבד — התוכן נמחק. לא ניתן לשחזר.");
            return;
        }
        if (file.Content == ContentCheck.Unreadable)
        {
            file.Quality = RecoveryQuality.Poor;
            file.QualityReason = L.T("לא ניתן היה לקרוא את אזור הנתונים של הקובץ.");
            return;
        }

        long total = 0, taken = 0;
        foreach (var extent in file.Extents.Where(e => !e.IsSparse))
        {
            long step = Math.Max(1, extent.ClusterCount / 64);
            for (long i = 0; i < extent.ClusterCount; i += step)
            {
                if (_volume.IsClusterAllocated(extent.StartCluster + i) is not { } allocated) continue;
                total++;
                if (allocated) taken++;
            }
        }
        double ratio = total == 0 ? 0 : (double)taken / total;
        (file.Quality, file.QualityReason) = ratio switch
        {
            0 => (RecoveryQuality.Good, L.T("נמצאו נתונים, וכל המקום שהקובץ תפס בכונן עדיין פנוי. {0}", basis)),
            < 0.15 => (RecoveryQuality.Good, L.T("נמצאו נתונים. כ-{0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים. {1}", ratio.ToString("P0"), basis)),
            < 0.85 => (RecoveryQuality.Poor, L.T("כ-{0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים. הקובץ ישוחזר פגום.", ratio.ToString("P0"))),
            _ => (RecoveryQuality.Unrecoverable, L.T("כמעט כל המקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים.")),
        };
    }
}
