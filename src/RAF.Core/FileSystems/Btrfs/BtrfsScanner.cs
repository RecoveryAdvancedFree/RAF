using System.Buffers.Binary;
using System.Diagnostics;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.Btrfs;

/// <summary>
/// סורק btrfs — מערכת הקבצים של שרתי Synology ושל הרבה הפצות לינוקס.
///
/// קבצים קיימים: עץ השורשים מפרט את כל העצים; כל "תיקייה משותפת" (תת-כרך) היא עץ
/// נפרד של קבצים. הולכים על התיקיות מהשורש, וכשרשומה בתיקייה מצביעה על עץ אחר —
/// ממשיכים בתוכו.
/// </summary>
public sealed class BtrfsScanner
{
    private const ulong FsTree = 5, ExtentTree = 2, FirstDir = 256;
    private const byte InodeItem = 1, DirIndex = 96, ExtentData = 108, RootItem = 132;

    private readonly List<string> _warnings = new();
    private BtrfsVolume _volume = null!;
    private CancellationToken _token;
    private long _examined, _memoryBudget = 512L * 1024 * 1024;
    private int _unreadable;

    /// <summary>לכל עץ (תיקייה משותפת): כתובת השורש שלו.</summary>
    private readonly Dictionary<ulong, ulong> _roots = new();
    /// <summary>לכל עץ: הקבצים והתיקיות שקיימים היום.</summary>
    private readonly Dictionary<ulong, HashSet<ulong>> _live = new();
    /// <summary>הנתיב של כל תיקייה קיימת.</summary>
    private readonly Dictionary<(ulong Tree, ulong Dir), string> _dirPaths = new();
    /// <summary>הטווחים הלוגיים שתפוסים היום (מעץ ההקצאה), ממוינים.</summary>
    private List<(ulong Start, ulong Length)> _allocated = new();
    private int _fromOldBlocks, _overwritten;

    /// <summary>עץ קבצים שנטען: רשומות הקבצים, התיקיות והקטעים.</summary>
    private sealed class Tree
    {
        internal readonly Dictionary<ulong, BtrfsInode> Inodes = new();
        internal readonly Dictionary<ulong, List<BtrfsDirEntry>> Dirs = new();
        internal readonly Dictionary<ulong, List<BtrfsExtent>> Extents = new();
    }

    public static Task<ScanResult> ScanAsync(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, TrimState trim,
        IProgress<ScanProgress>? progress, CancellationToken token)
        => Task.Run(() => new BtrfsScanner().Run(diskNumber, partitionOffset, partitionSize, sectorSize, mode, includeExisting, progress, token), token);

    private ScanResult Run(int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        _token = token;
        using var reader = VolumeReader.TryOpen(diskNumber, partitionOffset, partitionSize, sectorSize, sequential: false)
            ?? throw new IOException(RawDevice.OpenFailure());
        using var volume = BtrfsVolume.Open(reader)
            ?? throw new InvalidDataException(L.T("המחיצה אינה btrfs תקינה, או שהכותרת שלה פגומה."));
        _volume = volume;
        if (volume.NumDevices > 1 || volume.HasStripedProfiles)
            _warnings.Add(L.T("מערכת הקבצים מפוזרת על כמה כוננים בעצמה (בלי מערך מתחת). זה עוד לא נתמך — חלק מהקבצים עלולים לחזור פגומים."));

        progress?.Report(new ScanProgress { Stage = L.T("קורא את עץ השורשים"), Elapsed = clock.Elapsed });
        foreach (var item in volume.Items(volume.RootTree, token))
            if (item.Key.Type == RootItem && item.DataSize >= 239)
                _roots[item.Key.ObjectId] = BinaryPrimitives.ReadUInt64LittleEndian(item.Data[176..]);
        if (!_roots.ContainsKey(FsTree))
            throw new InvalidDataException(L.T("עץ הקבצים הראשי של המחיצה אינו נקרא."));

        var files = new List<RecoveredFile>();
        var visitedTrees = new HashSet<ulong>();
        WalkTree(FsTree, "", files, includeExisting, visitedTrees, progress, clock);

        LoadAllocation();
        if (!token.IsCancellationRequested) Deleted(files, progress, clock);

        if (_unreadable > 0)
            _warnings.Add(L.T("{0} קבצים דחוסים לא נפרסו — הסיבה ליד כל אחד מהם.", _unreadable.ToString("N0")));

        return new ScanResult
        {
            Files = files,
            Mode = mode,
            Duration = clock.Elapsed,
            Cancelled = token.IsCancellationRequested,
            FileSystem = "btrfs",
            RecordsExamined = _examined,
            Warnings = _warnings,
        };
    }

    private Tree Load(ulong treeId)
    {
        var tree = new Tree();
        if (!_roots.TryGetValue(treeId, out ulong root)) return tree;
        foreach (var item in _volume.Items(root, _token))
        {
            _examined++;
            switch (item.Key.Type)
            {
                case InodeItem when BtrfsInode.Parse(item.Key.ObjectId, item.Data) is { } inode:
                    tree.Inodes[item.Key.ObjectId] = inode;
                    break;
                case DirIndex when BtrfsDirEntry.Parse(item.Data) is { } entry:
                    if (!tree.Dirs.TryGetValue(item.Key.ObjectId, out var list)) tree.Dirs[item.Key.ObjectId] = list = new();
                    list.Add(entry);
                    break;
                case ExtentData when BtrfsExtent.Parse((long)item.Key.Offset, item.Data) is { } extent:
                    if (!tree.Extents.TryGetValue(item.Key.ObjectId, out var extents)) tree.Extents[item.Key.ObjectId] = extents = new();
                    extents.Add(extent);
                    break;
            }
        }
        return tree;
    }

    private void WalkTree(ulong treeId, string path, List<RecoveredFile> files, bool includeExisting,
        HashSet<ulong> visitedTrees, IProgress<ScanProgress>? progress, Stopwatch clock)
    {
        if (!visitedTrees.Add(treeId) || _token.IsCancellationRequested) return;
        progress?.Report(new ScanProgress { Stage = L.T("קורא את התיקיות"), FilesFound = files.Count, Elapsed = clock.Elapsed });
        var tree = Load(treeId);
        _live[treeId] = tree.Inodes.Keys.ToHashSet();
        _dirPaths[(treeId, FirstDir)] = path;
        var visited = new HashSet<ulong>();
        Walk(tree, treeId, FirstDir, path, files, includeExisting, visited, visitedTrees, progress, clock, 0);
    }

    private void Walk(Tree tree, ulong treeId, ulong dir, string path, List<RecoveredFile> files, bool includeExisting,
        HashSet<ulong> visited, HashSet<ulong> visitedTrees, IProgress<ScanProgress>? progress, Stopwatch clock, int depth)
    {
        if (depth > 256 || !visited.Add(dir) || !tree.Dirs.TryGetValue(dir, out var entries)) return;
        foreach (var entry in entries)
        {
            if (_token.IsCancellationRequested) return;
            string child = path.Length == 0 ? entry.Name : path + "\\" + entry.Name;

            // "תיקייה משותפת" — עץ נפרד.
            if (entry.TargetType == RootItem)
            {
                WalkTree(entry.Target, child, files, includeExisting, visitedTrees, progress, clock);
                continue;
            }
            if (!tree.Inodes.TryGetValue(entry.Target, out var inode)) continue;
            if (inode.IsDirectory)
            {
                _dirPaths[(treeId, entry.Target)] = child;
                Walk(tree, treeId, entry.Target, child, files, includeExisting, visited, visitedTrees, progress, clock, depth + 1);
                continue;
            }
            if (!inode.IsRegular || !includeExisting) continue;
            files.Add(Build(treeId, inode, entry.Name, path, tree.Extents.GetValueOrDefault(entry.Target) ?? new(), deleted: false));
            if (files.Count % 500 == 0)
                progress?.Report(new ScanProgress { Stage = L.T("קורא את התיקיות"), FilesFound = files.Count, Elapsed = clock.Elapsed });
        }
    }

    private RecoveredFile Build(ulong treeId, BtrfsInode inode, string name, string path, List<BtrfsExtent> extents, bool deleted)
    {
        var file = new RecoveredFile
        {
            Id = (long)(treeId << 40 | inode.Number),
            Name = name,
            Path = path,
            Size = inode.Size,
            IsDeleted = deleted,
            Created = inode.Created,
            Modified = inode.Modified,
            Accessed = inode.Accessed,
            Source = DiscoverySource.MftActive,
            Extents = BtrfsContent.Runs(_volume, extents, inode.Size) ?? new List<DataExtent>(),
            ResidentData = Resident(extents, inode.Size, out string? problem),
            Quality = RecoveryQuality.Excellent,
            Content = ContentCheck.HasData,
            QualityReason = L.T("הקובץ קיים במערכת הקבצים ומיקומו ידוע במלואו."),
        };
        if (problem is not null)
        {
            file.Quality = RecoveryQuality.Unrecoverable;
            file.Content = ContentCheck.NotChecked;
            file.QualityReason = problem;
        }
        return file;
    }

    // ------------------------------------------------------------ קבצים שנמחקו

    /// <summary>
    /// עץ ההקצאה: כל טווח לוגי שתפוס היום — נתונים (168) או בלוק של עץ (169). משמש לבדיקה
    /// אם המקום של קובץ שנמחק כבר נתפס, ולמפת המקום הפנוי של הסריקה המתקדמת.
    /// </summary>
    private void LoadAllocation()
    {
        if (!_roots.TryGetValue(ExtentTree, out ulong root)) return;
        var list = new List<(ulong, ulong)>();
        foreach (var item in _volume.Items(root, _token))
        {
            if (item.Key.Type == 168) list.Add((item.Key.ObjectId, item.Key.Offset));
            else if (item.Key.Type == 169) list.Add((item.Key.ObjectId, (ulong)_volume.NodeSize));
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        _allocated = list;
        int sector = _volume.SectorSize;
        _volume.Allocation = cluster => _volume.Unmap(cluster * sector) is { } logical ? Allocated(logical, 1) > 0 : null;
    }

    /// <summary>כמה בתים מהטווח תפוסים היום.</summary>
    private long Allocated(ulong start, ulong length)
    {
        int lo = 0, hi = _allocated.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_allocated[mid].Start + _allocated[mid].Length <= start) lo = mid + 1; else hi = mid;
        }
        long taken = 0;
        for (int i = lo; i < _allocated.Count && _allocated[i].Start < start + length; i++)
        {
            ulong from = Math.Max(start, _allocated[i].Start), to = Math.Min(start + length, _allocated[i].Start + _allocated[i].Length);
            if (to > from) taken += (long)(to - from);
        }
        return taken;
    }

    /// <summary>
    /// btrfs לא כותב במקום: כל שינוי בעץ נכתב לבלוק חדש, והבלוק הישן נשאר באזור המטא-דאטה עד
    /// שנדרס. עוברים על כל האזור, לוקחים כל עלה ישן של עץ קבצים שטביעת האצבע שלו תקינה, ואוספים
    /// ממנו קבצים שכבר אינם קיימים: הרשומה, השם והתיקייה, והקטעים — מהעותק החדש ביותר של כל אחד.
    /// </summary>
    private void Deleted(List<RecoveredFile> files, IProgress<ScanProgress>? progress, Stopwatch clock)
    {
        var inodes = new Dictionary<(ulong, ulong), (ulong Gen, BtrfsInode Inode)>();
        var refs = new Dictionary<(ulong, ulong), (ulong Gen, ulong Parent, string Name)>();
        var extents = new Dictionary<(ulong, ulong, long), (ulong Gen, BtrfsExtent Extent)>();
        bool Live(ulong tree, ulong inode) => _live.TryGetValue(tree, out var set) && set.Contains(inode);

        var metadata = _volume.Chunks.Where(c => (c.Type & 4) != 0).ToList();
        ulong total = metadata.Aggregate(0UL, (s, c) => s + c.Length), done = 0;
        int node = _volume.NodeSize, batch = Math.Max(1, (1 << 20) / node);

        foreach (var chunk in metadata)
            for (ulong at = chunk.Logical; at < chunk.Logical + chunk.Length && !_token.IsCancellationRequested; at += (ulong)(batch * node))
            {
                int count = (int)Math.Min((ulong)batch, (chunk.Logical + chunk.Length - at) / (ulong)node);
                if (count <= 0) break;
                var data = _volume.ReadLogical(at, count * node);
                done += (ulong)(count * node);
                if (done % (64UL << 20) < (ulong)(count * node))
                    progress?.Report(new ScanProgress
                    {
                        Stage = L.T("מחפש קבצים שנמחקו בעותקים הישנים של הרשומות"),
                        Percent = total == 0 ? null : done * 100.0 / total,
                        FilesFound = files.Count,
                        Elapsed = clock.Elapsed,
                    });
                if (data is null) continue;

                for (int i = 0; i < count; i++)
                {
                    var leaf = data.AsSpan(i * node, node).ToArray();
                    if (!_volume.IsNode(leaf, at + (ulong)(i * node)) || BtrfsVolume.NodeLevel(leaf) != 0 || !_volume.ChecksumOk(leaf)) continue;
                    ulong tree = BtrfsVolume.NodeOwner(leaf), gen = BtrfsVolume.NodeGeneration(leaf);
                    if (tree != FsTree && tree is < 256 or > (1UL << 48)) continue;

                    foreach (var item in BtrfsVolume.LeafItems(leaf))
                    {
                        ulong ino = item.Key.ObjectId;
                        switch (item.Key.Type)
                        {
                            case InodeItem when !Live(tree, ino) && BtrfsInode.Parse(ino, item.Data) is { } inode:
                                // הגרסה החדשה ביותר מלפני המחיקה: בגרסה שנכתבה ברגע המחיקה אין קישורים והגודל 0.
                                static int Rank(BtrfsInode i) => (i.Links > 0 ? 2 : 0) + (i.Size > 0 ? 1 : 0);
                                if (!inodes.TryGetValue((tree, ino), out var had)
                                    || Rank(had.Inode) < Rank(inode)
                                    || (Rank(had.Inode) == Rank(inode) && had.Gen < gen))
                                    inodes[(tree, ino)] = (gen, inode);
                                break;
                            case 12 when !Live(tree, ino) && item.DataSize >= 10:
                            {
                                int len = BinaryPrimitives.ReadUInt16LittleEndian(item.Data[8..]);
                                if (10 + len > item.DataSize) break;
                                string name = System.Text.Encoding.UTF8.GetString(item.Data.Slice(10, len));
                                if (!refs.TryGetValue((tree, ino), out var r) || r.Gen < gen) refs[(tree, ino)] = (gen, item.Key.Offset, name);
                                break;
                            }
                            case DirIndex when BtrfsDirEntry.Parse(item.Data) is { TargetType: InodeItem } entry && !Live(tree, entry.Target):
                                if (!refs.TryGetValue((tree, entry.Target), out var d) || d.Gen < gen)
                                    refs[(tree, entry.Target)] = (gen, ino, entry.Name);
                                break;
                            case ExtentData when !Live(tree, ino) && BtrfsExtent.Parse((long)item.Key.Offset, item.Data) is { } extent:
                                var key = (tree, ino, extent.FileOffset);
                                if (!extents.TryGetValue(key, out var e) || e.Gen < gen) extents[key] = (gen, extent);
                                break;
                        }
                    }
                }
            }

        var byInode = extents.GroupBy(e => (e.Key.Item1, e.Key.Item2)).ToDictionary(g => g.Key, g => g.Select(x => x.Value.Extent).ToList());
        foreach (var ((tree, ino), (_, version)) in inodes)
        {
            if (_token.IsCancellationRequested) return;
            var inode = version;
            if (!inode.IsRegular) continue;
            var list = byInode.GetValueOrDefault((tree, ino)) ?? new();
            if (list.Count == 0) continue;   // בלי מיקום תוכן — אין מה לשחזר
            bool guessedSize = false;
            if (inode.Size == 0)
            {
                // כל הגרסאות עם הגודל כבר נדרסו — הגודל לפי הקטעים, בלי האפסים שבסוף הבלוק האחרון.
                long size = EstimateSize(list);
                if (size <= 0) continue;
                inode = inode with { Size = size };
                guessedSize = true;
            }

            string name = refs.TryGetValue((tree, ino), out var r) ? r.Name : $"inode-{ino}";
            string path = r.Name is null ? "?" : PathOf(tree, r.Parent, refs, 0) ?? "?";
            var file = Build(tree, inode, name, path, list, deleted: true);
            Assess(file, list);
            if (guessedSize && file.Quality != RecoveryQuality.Unrecoverable)
                file.QualityReason += " " + L.T("הגודל המקורי לא נשמר, והקובץ משוחזר עד סוף הבלוק האחרון שלו, בלי האפסים שבסופו.");
            files.Add(file);
            _fromOldBlocks++;
        }

        if (_fromOldBlocks > 0)
            _warnings.Add(L.T("{0} קבצים שנמחקו נמצאו בעותקים הישנים של הרשומות, שמערכת הקבצים משאירה בדיסק אחרי כל שינוי.",
                _fromOldBlocks.ToString("N0")));
        if (_overwritten > 0)
            _warnings.Add(L.T("אצל {0} מהם המקום שהתוכן תפס כבר נתפס מחדש, ולכן הם עלולים לחזור פגומים.", _overwritten.ToString("N0")));
    }

    /// <summary>גודל לפי הקטעים: סוף הקטע האחרון, פחות האפסים שבסוף הבלוק האחרון (לינוקס ממלא בהם את השארית).</summary>
    private long EstimateSize(List<BtrfsExtent> extents)
    {
        var last = extents.MaxBy(e => e.FileOffset + e.Length)!;
        long end = last.FileOffset + last.Length;
        if (last.Type == 0 || last.IsHole || last.IsPrealloc || last.Compression != 0) return end;
        ulong logical = last.DiskStart + (ulong)last.Offset + (ulong)last.Length - (ulong)_volume.SectorSize;
        if (_volume.ReadLogical(logical, _volume.SectorSize) is not { } block) return end;
        int zeros = 0;
        for (int i = block.Length - 1; i >= 0 && block[i] == 0; i--) zeros++;
        return zeros == block.Length ? end : end - zeros;
    }

    /// <summary>הנתיב של תיקייה: קיימת — מהסריקה; נמחקה — משמה הישן ומהתיקייה שהייתה מעליה.</summary>
    private string? PathOf(ulong tree, ulong dir, Dictionary<(ulong, ulong), (ulong Gen, ulong Parent, string Name)> refs, int depth)
    {
        if (_dirPaths.TryGetValue((tree, dir), out var path)) return path;
        if (depth > 64 || !refs.TryGetValue((tree, dir), out var r)) return null;
        string? parent = PathOf(tree, r.Parent, refs, depth + 1);
        return parent is null ? null : parent.Length == 0 ? r.Name : parent + "\\" + r.Name;
    }

    /// <summary>איכות קובץ שנמחק: האם המקום שתפס עדיין פנוי, והאם יש בו תוכן.</summary>
    private void Assess(RecoveredFile file, List<BtrfsExtent> extents)
    {
        if (file.Quality == RecoveryQuality.Unrecoverable) return;
        long used = 0, taken = 0;
        foreach (var e in extents.Where(e => e.Type != 0 && !e.IsHole))
        {
            used += (long)e.DiskLength;
            taken += Allocated(e.DiskStart, e.DiskLength);
        }
        double ratio = used == 0 ? 0 : (double)taken / used;
        if (file.ResidentData is null && file.Extents.Count > 0)
            file.Content = new ClusterStream(_volume, file.Extents, file.Size, 0).SampleContent();
        string basis = L.T("הרשומה, השם והמיקום נלקחו מעותק ישן שנשאר בדיסק.");
        if (file.Content == ContentCheck.Empty)
        {
            (file.Quality, file.QualityReason) = (RecoveryQuality.Unrecoverable, L.T("אזור הנתונים של הקובץ מכיל אפסים בלבד — התוכן נמחק. לא ניתן לשחזר."));
            return;
        }
        if (ratio > 0) _overwritten++;
        (file.Quality, file.QualityReason) = ratio switch
        {
            0 => (RecoveryQuality.Excellent, L.T("נמצאו נתונים, וכל המקום שהקובץ תפס בכונן עדיין פנוי. {0}", basis)),
            < 0.15 => (RecoveryQuality.Good, L.T("נמצאו נתונים. כ-{0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים. {1}", ratio.ToString("P0"), basis)),
            < 0.85 => (RecoveryQuality.Poor, L.T("כ-{0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים. הקובץ ישוחזר פגום.", ratio.ToString("P0"))),
            _ => (RecoveryQuality.Unrecoverable, L.T("כמעט כל המקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים.")),
        };
    }

    /// <summary>קובץ עם קטע דחוס או קטע בתוך הרשומה — התוכן כולו בזיכרון, עד גבול כולל.</summary>
    private byte[]? Resident(List<BtrfsExtent> extents, long size, out string? problem)
    {
        problem = null;
        if (BtrfsContent.Runs(_volume, extents, size) is not null) return null;
        if (size > 64 * 1024 * 1024 || size > _memoryBudget)
        {
            _unreadable++;
            problem = L.T("הקובץ דחוס וגדול מדי לפריסה בזיכרון בגרסה זו.");
            return null;
        }
        var content = BtrfsContent.Materialize(_volume, extents, size, out problem);
        if (content is null) { _unreadable++; return null; }
        _memoryBudget -= content.Length;
        return content;
    }
}
