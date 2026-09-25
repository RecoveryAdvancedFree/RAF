using System.Diagnostics;
using RAF.Core.Model;
using RAF.Core.Native;
using RAF.Core.Signatures;

namespace RAF.Core.FileSystems.Ext;

/// <summary>
/// סורק ext2/3/4 — כוננים של לינוקס, משרתי אחסון ביתיים, מקלטי טלוויזיה ומצלמות אבטחה.
///
/// קבצים קיימים: הליכה על עץ התיקיות מתיקיית השורש (אינוד 2).
///
/// קבצים שנמחקו: השם נשאר ברווח שהמחיקה השאירה בתיקייה, יחד עם מספר האינוד.
/// ב-ext2 האינוד עצמו שומר עדיין את מיקום התוכן. ב-ext3/4 המחיקה מאפסת אותו — ואז
/// מחפשים ביומן עותק ישן של אותו אינוד, מלפני המחיקה. אין עותק — הקובץ מוצג
/// כעדות בלבד, והסריקה המתקדמת עשויה למצוא את התוכן לפי סוג.
/// </summary>
public sealed class ExtScanner
{
    private readonly List<string> _warnings = new();
    private readonly HashSet<long> _visited = new();
    private readonly HashSet<long> _reported = new();

    private ExtVolume _volume = null!;
    private Lazy<ExtJournal?> _journal = null!;
    private CancellationToken _token;
    private long _examined;
    private int _verifyBudget = 100_000;
    private long _synthetic = 1L << 40;
    private int _fromJournal, _lost, _verifiedEmpty;

    public static Task<ScanResult> ScanAsync(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, TrimState trim,
        IProgress<ScanProgress>? progress, CancellationToken token)
        => Task.Run(() => new ExtScanner().Run(
            diskNumber, partitionOffset, partitionSize, sectorSize, mode, includeExisting, progress, token), token);

    private ScanResult Run(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        _token = token;

        using var reader = VolumeReader.TryOpen(diskNumber, partitionOffset, partitionSize, sectorSize, sequential: false)
            ?? throw new IOException(RawDevice.OpenFailure());
        using var volume = ExtVolume.Open(reader)
            ?? throw new InvalidDataException(L.T("המחיצה אינה ext2/3/4 תקינה, או שהכותרת שלה פגומה."));
        _volume = volume;
        _journal = new Lazy<ExtJournal?>(() =>
        {
            progress?.Report(new ScanProgress { Stage = L.T("קורא את היומן של מערכת הקבצים"), Elapsed = clock.Elapsed });
            return ExtJournal.Open(volume, token);
        });

        if (volume.Super.Encrypted)
            _warnings.Add(L.T("בחלק מהתיקיות במחיצה הזו הופעלה הצפנה של לינוקס. שמות ותוכן בתיקיות כאלה מוצפנים ולא ייקראו."));

        var files = new List<RecoveredFile>();
        progress?.Report(new ScanProgress { Stage = L.T("קורא את ספריית השורש"), Elapsed = clock.Elapsed });

        _visited.Add(2);
        if (volume.ReadInode(2) is { } root)
            Walk(root, "", deletedTree: false, files, includeExisting, progress, clock, depth: 0);
        else
            throw new InvalidDataException(L.T("תיקיית השורש של המחיצה אינה נקראת."));

        if (mode is ScanMode.Deep && !token.IsCancellationRequested)
            Orphans(files, progress, clock);

        if (_fromJournal > 0)
            _warnings.Add(L.T("{0} קבצים שנמחקו שוחזרו בעזרת עותק ישן של הרשומה שלהם, שנשמר ביומן של מערכת הקבצים.",
                _fromJournal.ToString("N0")));
        if (_lost > 0)
            _warnings.Add(L.T("ל-{0} קבצים שנמחקו נמצא השם, אבל לינוקס מחק את המידע על מיקום התוכן ולא נשאר ממנו עותק ביומן. " +
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
            FileSystem = volume.Super.Version,
            RecordsExamined = _examined,
            Warnings = _warnings,
        };
    }

    // ------------------------------------------------------------ עץ התיקיות

    private void Walk(ExtInode directory, string path, bool deletedTree, List<RecoveredFile> files, bool includeExisting,
        IProgress<ScanProgress>? progress, Stopwatch clock, int depth)
    {
        if (_token.IsCancellationRequested || depth > 256) return;

        var data = _volume.ReadContent(directory, 64 * 1024 * 1024);
        if (data is null) return;
        var entries = directory.HasInlineData
            ? ExtDirectory.ParseInline(data, _volume.Super.FileTypeInEntries, _volume.Super.InodesCount)
            : ExtDirectory.Parse(data, _volume.Super.BlockSize, _volume.Super.FileTypeInEntries,
                _volume.Super.InodesCount, indexed: (directory.Flags & 0x1000) != 0);
        if (!directory.HasInlineData && _volume.Super.HasJournal)
            entries.AddRange(JournalEntries(directory, entries));

        var live = entries.Where(e => !e.Deleted).Select(e => e.Name).ToHashSet();
        foreach (var entry in entries)
        {
            if (_token.IsCancellationRequested) return;
            if (entry.Name is "." or "..") continue;
            _examined++;

            string childPath = path.Length == 0 ? entry.Name : path + "\\" + entry.Name;

            if (entry.Inode == 0)
            {
                // שם בלי מספר אינוד (רשומה ראשונה בבלוק, או ext2): עדות בלבד. שם שקיים היום
                // באותה תיקייה — כנראה שמירה של עורך (קובץ חדש במקום הישן), לא קובץ שאבד.
                if (entry.FileType is 0 or 1 && live.Add(entry.Name)) files.Add(Lost(entry.Name, path, null));
                continue;
            }

            var inode = _volume.ReadInode(entry.Inode);
            if (inode is null) continue;

            bool deleted = entry.Deleted || deletedTree;
            // האינוד בשימוש: הקובץ שונה שם או הועבר (השם הישן נשאר ברווח), או שהאינוד
            // כבר של קובץ אחר. בשני המקרים — אין כאן קובץ שנמחק.
            if (deleted && inode.InUse) continue;

            if (!deleted)
            {
                if (inode.IsDirectory)
                {
                    if (_visited.Add(entry.Inode))
                        Walk(inode, childPath, false, files, includeExisting, progress, clock, depth + 1);
                    continue;
                }
                if (!inode.IsRegular || !includeExisting) continue;
                files.Add(Existing(inode, entry.Name, path));
            }
            else
            {
                if (!_reported.Add(entry.Inode)) continue;
                var (source, fromJournal) = Resolve(inode, entry.FileType);
                bool isDirectory = source?.IsDirectory ?? entry.FileType == 2;

                if (isDirectory)
                {
                    // תיקייה שנמחקה: הרשומות שבה נשארות, וכל מה שבתוכה נמחק גם הוא.
                    if (source is not null && _visited.Add(entry.Inode))
                        Walk(source, childPath, true, files, includeExisting, progress, clock, depth + 1);
                    continue;
                }
                if (entry.FileType is not (0 or 1) || (source is not null && !source.IsRegular)) continue;
                files.Add(source is null ? Lost(entry.Name, path, inode) : Deleted(source, entry.Name, path, fromJournal));
            }

            if (files.Count % 500 == 0)
                progress?.Report(new ScanProgress
                {
                    Stage = L.T("עובר על ספריות מערכת הקבצים"),
                    FilesFound = files.Count,
                    Elapsed = clock.Elapsed,
                });
        }
    }

    /// <summary>
    /// רשומות שנמחקו מהתיקייה, מעותקים ישנים של הבלוקים שלה ביומן. מלינוקס 6.4 המחיקה
    /// מאפסת את השם בתיקייה עצמה — אבל היומן שומר את הבלוק כפי שהיה לפני כן. רשומה
    /// שמופיעה בעותק ישן ולא בתיקייה היום — נמחקה (או שונה שמה; את זה מסננים לפי האינוד).
    /// </summary>
    private IEnumerable<ExtEntry> JournalEntries(ExtInode directory, List<ExtEntry> current)
    {
        if (_journal.Value is not { } journal || _volume.ExtentsOf(directory) is not { } extents) yield break;
        var seen = current.Select(e => (e.Inode, e.Name)).ToHashSet();
        var sb = _volume.Super;
        bool indexed = (directory.Flags & 0x1000) != 0;

        foreach (var extent in extents.Where(e => !e.IsSparse))
            for (long i = 0; i < extent.ClusterCount && i < 16384; i++)
                foreach (var copy in journal.BlockCopies(extent.StartCluster + i))
                    foreach (var entry in ExtDirectory.Parse(copy, sb.BlockSize, sb.FileTypeInEntries, sb.InodesCount, indexed))
                        if (entry.Inode != 0 && entry.Name is not ("." or "..") && seen.Add((entry.Inode, entry.Name)))
                            yield return entry with { Deleted = true };
    }

    /// <summary>
    /// האינוד של קובץ שנמחק, עם מיקום התוכן: כמו שהוא (ext2), או העותק החדש ביותר ביומן
    /// שעוד היה בשימוש. null — לא נשאר מידע על המיקום.
    /// </summary>
    private (ExtInode? Inode, bool FromJournal) Resolve(ExtInode current, int fileType)
    {
        if (current.HasBlockMap && current.Size > 0) return (current, false);
        if (_journal.Value is not { } journal) return (null, false);

        foreach (var copy in journal.InodeCopies(current.Number))
        {
            if (!copy.InUse || !copy.HasBlockMap) continue;
            if (fileType == 1 && !copy.IsRegular) continue;
            if (fileType == 2 && !copy.IsDirectory) continue;
            if (current.Mode != 0 && (current.Mode & 0xF000) != copy.Type) continue;
            return (copy, true);
        }
        return (null, false);
    }

    // ------------------------------------------------------------ קבצים

    private RecoveredFile Existing(ExtInode inode, string name, string path)
    {
        var file = Build(inode, name, path, deleted: false, DiscoverySource.MftActive);
        if (file.Extents.Count == 0 && file.ResidentData is null && file.Size > 0)
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

    private RecoveredFile Deleted(ExtInode inode, string name, string path, bool fromJournal)
    {
        var file = Build(inode, name, path, deleted: true, fromJournal ? DiscoverySource.MftOrphan : DiscoverySource.MftActive);
        if (fromJournal) _fromJournal++;
        Assess(file, fromJournal
            ? L.T("מיקום התוכן נלקח מעותק של הרשומה שנשמר ביומן של מערכת הקבצים לפני המחיקה.")
            : L.T("הרשומה של הקובץ שמרה את מיקום התוכן גם אחרי המחיקה."));
        return file;
    }

    /// <summary>שם בלי מיקום תוכן — עדות לכך שהקובץ היה, בלי אפשרות לשחזר אותו מכאן.</summary>
    private RecoveredFile Lost(string name, string path, ExtInode? inode)
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
            QualityReason = L.T("השם נשאר, אבל לינוקס מחק את המידע על מיקום התוכן, ולא נשאר ממנו עותק ביומן. " +
                                "סריקה מתקדמת עשויה למצוא את התוכן לפי סוג הקובץ."),
        };
    }

    private RecoveredFile Build(ExtInode inode, string name, string path, bool deleted, DiscoverySource source)
    {
        byte[]? inline = inode.InlineContent();
        var extents = inline is null ? _volume.ExtentsOf(inode) ?? new List<DataExtent>() : new List<DataExtent>();
        return new RecoveredFile
        {
            Id = inode.Number,
            Name = name,
            Path = path,
            Size = inode.Size,
            IsDeleted = deleted,
            Created = inode.Created,
            Modified = inode.Modified,
            Accessed = inode.Accessed,
            Source = source,
            Extents = extents,
            ResidentData = inline,
        };
    }

    private void Assess(RecoveredFile file, string basis)
    {
        if (file.Size == 0)
        {
            file.Quality = RecoveryQuality.Excellent;
            file.QualityReason = L.T("הקובץ ריק ואין לו תוכן לשחזר.");
            return;
        }
        if (file.ResidentData is not null)
        {
            file.Quality = RecoveryQuality.Excellent;
            file.Content = ContentCheck.HasData;
            file.QualityReason = basis;
            return;
        }
        if (file.Extents.Count == 0)
        {
            file.Quality = RecoveryQuality.Unrecoverable;
            file.QualityReason = L.T("המידע על מיקום התוכן פגום.");
            return;
        }

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
            0 => (RecoveryQuality.Excellent, L.T("נמצאו נתונים, וכל המקום שהקובץ תפס בכונן עדיין פנוי. {0}", basis)),
            < 0.15 => (RecoveryQuality.Good, L.T("נמצאו נתונים. כ-{0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים. {1}", ratio.ToString("P0"), basis)),
            < 0.85 => (RecoveryQuality.Poor, L.T("כ-{0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים. הקובץ ישוחזר פגום.", ratio.ToString("P0"))),
            _ => (RecoveryQuality.Unrecoverable, L.T("כמעט כל המקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים.")),
        };
    }

    // ------------------------------------------------------------ סריקה עמוקה

    /// <summary>
    /// קבצים שנמחקו ושמם כבר לא נמצא באף תיקייה: אינודים מחוקים שעוד יודעים איפה
    /// התוכן — ישירות (ext2) או מעותק ביומן. הם מוצגים בלי שם, בתיקייה "?".
    /// </summary>
    private void Orphans(List<RecoveredFile> files, IProgress<ScanProgress>? progress, Stopwatch clock)
    {
        var candidates = new SortedSet<long>();
        int perBlock = _volume.Super.BlockSize / _volume.Super.InodeSize;

        if (_journal.Value is { } journal)
        {
            // כל בלוק של טבלת אינודים שיש לו עותק ביומן — האינודים שבו.
            foreach (long block in journal.JournaledBlocks)
                for (long g = 0; g < _volume.GroupCount; g++)
                {
                    long table = _volume.InodeTableOf(g);
                    long tableBlocks = (_volume.Super.InodesPerGroup * _volume.Super.InodeSize + _volume.Super.BlockSize - 1) / _volume.Super.BlockSize;
                    if (block < table || block >= table + tableBlocks) continue;
                    long first = g * _volume.Super.InodesPerGroup + (block - table) * perBlock + 1;
                    for (int i = 0; i < perBlock; i++) candidates.Add(first + i);
                    break;
                }
        }
        else if (_volume.Super.InodesCount <= 5_000_000)
        {
            // בלי יומן (ext2) האינוד עצמו שומר את המיקום — עוברים על כולם.
            for (long n = 1; n <= _volume.Super.InodesCount; n++) candidates.Add(n);
        }

        long done = 0;
        foreach (long n in candidates)
        {
            if (_token.IsCancellationRequested) return;
            if (++done % 4096 == 0)
                progress?.Report(new ScanProgress
                {
                    Stage = L.T("מחפש קבצים שנמחקו בלי שם"),
                    Percent = done * 100.0 / candidates.Count,
                    FilesFound = files.Count,
                    Elapsed = clock.Elapsed,
                });

            if (n < 11 || _reported.Contains(n) || _visited.Contains(n)) continue;
            var inode = _volume.ReadInode(n);
            if (inode is null || inode.InUse) continue;
            _examined++;

            var (source, fromJournal) = Resolve(inode, 1);
            if (source is null || !source.IsRegular || source.Size == 0) continue;
            _reported.Add(n);

            var file = Deleted(source, $"inode-{n}", "?", fromJournal);
            file.Name += Extension(file);
            files.Add(file);
        }
    }

    /// <summary>סיומת לקובץ בלי שם, לפי תחילת התוכן.</summary>
    private string Extension(RecoveredFile file)
    {
        var first = file.Extents.FirstOrDefault(e => !e.IsSparse);
        if (first.ClusterCount == 0) return "";
        byte[] head = new byte[Math.Min(_volume.BytesPerCluster, 8192)];
        if (_volume.ReadRaw(_volume.ClusterToOffset(first.StartCluster), head) <= 0) return "";
        var signature = FileSignatures.Identify(head);
        return signature is { Extensions.Length: > 0 } ? "." + signature.Extensions[0] : "";
    }
}
