using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.Hfs;

/// <summary>
/// סורק HFS+. כל הקבצים והתיקיות רשומים ב"קטלוג" — עץ של צמתים בגודל קבוע, שבעליו רשומה
/// לכל קובץ ותיקייה: התיקייה שמעליה, השם, התאריכים, ושמונת המקטעים הראשונים של התוכן
/// (השאר — בעץ "מקטעים נוספים").
///
/// קבצים שנמחקו: המחיקה מוציאה את הרשומה מהעץ, אבל צמתים שהתפנו שומרים את תוכנם הישן,
/// וביומן יש עותקים של צמתים מלפני השינוי. עוברים על כל הצמתים ועל כל היומן, ולוקחים רשומות
/// של קבצים שכבר לא קיימים — עם השם, התיקייה והמקטעים.
/// </summary>
public sealed class HfsScanner
{
    private sealed record FileRec(uint Parent, string Name, uint Id, long Size, List<DataExtent> Extents,
        DateTime? Created, DateTime? Modified, DateTime? Accessed, bool Compressed, uint LinkTo);

    private readonly Dictionary<uint, (uint Parent, string Name)> _folders = new();
    private readonly Dictionary<uint, List<(uint Start, List<DataExtent> Extents)>> _overflow = new();
    private readonly List<string> _warnings = new();
    private HfsVolume _volume = null!;
    private int _nodeSize;
    private long _examined;

    public static Task<ScanResult> ScanAsync(int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, TrimState trim, IProgress<ScanProgress>? progress, CancellationToken token)
        => Task.Run(() => new HfsScanner().Run(diskNumber, partitionOffset, partitionSize, sectorSize, mode, includeExisting, progress, token), token);

    private ScanResult Run(int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        using var reader = VolumeReader.TryOpen(diskNumber, partitionOffset, partitionSize, sectorSize, sequential: false)
            ?? throw new IOException(RawDevice.OpenFailure());
        using var volume = HfsVolume.Open(reader)
            ?? throw new InvalidDataException(L.T("המחיצה אינה HFS+ תקינה, או שהכותרת שלה פגומה."));
        _volume = volume;

        // עץ "מקטעים נוספים": קבצים מפוצלים ליותר משמונה קטעים — כולל הקטלוג עצמו.
        foreach (var (_, data) in Leaves(volume.Extents, HeaderOf(volume.Extents), token))
            // מפתח: סוג המזלג (0 — נתונים), ריפוד, מספר הקובץ, הבלוק שממנו הרשומה ממשיכה.
            if (data.Key.Length >= 10 && data.Key[0] == 0)
            {
                uint id = BinaryPrimitives.ReadUInt32BigEndian(data.Key.AsSpan(2));
                uint start = BinaryPrimitives.ReadUInt32BigEndian(data.Key.AsSpan(6));
                if (!_overflow.TryGetValue(id, out var l)) _overflow[id] = l = new();
                l.Add((start, HfsVolume.Record(data.Data)));
            }
        var catalog = volume.Catalog.Concat(Overflow(4)).ToList();
        var header = HeaderOf(catalog) ?? throw new InvalidDataException(L.T("הקטלוג של המחיצה אינו נקרא."));

        progress?.Report(new ScanProgress { Stage = L.T("קורא את הקטלוג"), Elapsed = clock.Elapsed });
        var live = new Dictionary<uint, FileRec>();
        foreach (var (_, rec) in Leaves(catalog, header, token))
        {
            _examined++;
            if (Parse(rec) is not { } parsed) continue;
            if (parsed.Folder is { } f) _folders[f.Id] = (f.Parent, f.Name);
            else if (parsed.File is { } file) live[file.Id] = file;
        }

        var files = new List<RecoveredFile>();
        // קישורים קשיחים: הרשומה מצביעה על קובץ "iNode<מספר>" בתיקייה הפרטית של מערכת הקבצים.
        var privateFolders = _folders.Where(f => f.Value.Name.StartsWith('\0')).Select(f => f.Key).ToHashSet();
        var linkTargets = live.Values.Where(f => privateFolders.Contains(f.Parent)).ToDictionary(f => f.Name, f => f);
        if (includeExisting)
            foreach (var file in live.Values)
            {
                if (privateFolders.Contains(file.Parent) || Path(file.Parent) is not { } path) continue;
                var source = file.LinkTo != 0 && linkTargets.TryGetValue($"iNode{file.LinkTo}", out var target) ? target with { Name = file.Name, Parent = file.Parent } : file;
                files.Add(Build(source, path, deleted: false));
            }

        if (!token.IsCancellationRequested) Deleted(catalog, header, live, files, progress, clock, token);

        return new ScanResult
        {
            Files = files,
            Mode = mode,
            Duration = clock.Elapsed,
            Cancelled = token.IsCancellationRequested,
            FileSystem = "HFS+",
            RecordsExamined = _examined,
            Warnings = _warnings,
        };
    }

    private IEnumerable<DataExtent> Overflow(uint id)
        => _overflow.TryGetValue(id, out var list) ? list.OrderBy(l => l.Start).SelectMany(l => l.Extents) : Enumerable.Empty<DataExtent>();

    // ------------------------------------------------------------ העץ

    /// <summary>צומת הכותרת (צומת 0): גודל צומת, העלה הראשון, מספר הצמתים.</summary>
    private (int NodeSize, uint FirstLeaf, uint Total)? HeaderOf(List<DataExtent> file)
    {
        var node = _volume.ReadFile(file, 0, 512);
        if (node is null || (sbyte)node[8] != 1) return null;
        int nodeSize = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(32));
        if (nodeSize < 512 || (nodeSize & (nodeSize - 1)) != 0) return null;
        return (nodeSize, BinaryPrimitives.ReadUInt32BigEndian(node.AsSpan(24)), BinaryPrimitives.ReadUInt32BigEndian(node.AsSpan(36)));
    }

    private readonly record struct Rec(byte[] Key, byte[] Data);

    /// <summary>העלים בסדר, לפי השרשרת שמתחילה בעלה הראשון.</summary>
    private IEnumerable<(uint Node, Rec Rec)> Leaves(List<DataExtent> file, (int NodeSize, uint FirstLeaf, uint Total)? header, CancellationToken token)
    {
        if (header is not { } h) yield break;
        _nodeSize = h.NodeSize;
        var seen = new HashSet<uint>();
        for (uint n = h.FirstLeaf; n != 0 && n < h.Total && seen.Add(n) && !token.IsCancellationRequested; )
        {
            var node = _volume.ReadFile(file, (long)n * h.NodeSize, h.NodeSize);
            if (node is null || (sbyte)node[8] != -1) yield break;
            foreach (var rec in Records(node)) yield return (n, rec);
            n = BinaryPrimitives.ReadUInt32BigEndian(node);
        }
    }

    /// <summary>הרשומות בצומת עלה: טבלת ההיסטים בסוף הצומת, מהסוף להתחלה.</summary>
    private static IEnumerable<Rec> Records(byte[] node)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(10));
        if (count == 0 || 14 + count * 2 > node.Length) yield break;
        for (int i = 0; i < count; i++)
        {
            int at = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(node.Length - 2 * (i + 1)));
            int end = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(node.Length - 2 * (i + 2)));
            if (at < 14 || end <= at || end > node.Length - 2 * (count + 1)) yield break;
            int keyLength = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(at));
            int data = at + 2 + keyLength;
            if (data + (data & 1) >= end) continue;
            data += data & 1;
            yield return new Rec(node.AsSpan(at + 2, keyLength).ToArray(), node.AsSpan(data, end - data).ToArray());
        }
    }

    /// <summary>רשומת קטלוג: תיקייה (1) או קובץ (2). הרשומות "חוט" (3, 4) — מדלגים.</summary>
    private static (FileRec? File, (uint Id, uint Parent, string Name)? Folder)? Parse(Rec rec)
    {
        if (rec.Key.Length < 6 || rec.Data.Length < 2) return null;
        uint parent = BinaryPrimitives.ReadUInt32BigEndian(rec.Key);
        int nameLength = BinaryPrimitives.ReadUInt16BigEndian(rec.Key.AsSpan(4));
        if (nameLength > 255 || 6 + nameLength * 2 > rec.Key.Length) return null;
        string name = Encoding.BigEndianUnicode.GetString(rec.Key, 6, nameLength * 2);
        try { name = name.Normalize(NormalizationForm.FormC); } catch (ArgumentException) { }
        var d = rec.Data;
        switch (BinaryPrimitives.ReadInt16BigEndian(d))
        {
            case 1 when d.Length >= 88:
                return (null, (BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(8)), parent, name));
            case 2 when d.Length >= 248 && (BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(42)) & 0xF000) == 0xA000:
                return null;   // קיצור דרך — אין בו תוכן לשחזר
            case 2 when d.Length >= 248:
                bool link = d.AsSpan(48, 4).SequenceEqual("hlnk"u8) && d.AsSpan(52, 4).SequenceEqual("hfs+"u8);
                return (new FileRec(parent, name, BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(8)),
                    (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(88)), HfsVolume.ForkExtents(d.AsSpan(88)),
                    Date(d, 12), Date(d, 16), Date(d, 24), (d[41] & 0x20) != 0,
                    link ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(44)) : 0), null);
            default:
                return null;
        }
    }

    /// <summary>תאריך: שניות מ-1 בינואר 1904, בשעון עולמי.</summary>
    private static DateTime? Date(byte[] d, int at)
    {
        uint s = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at));
        return s == 0 ? null : new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s).ToLocalTime();
    }

    /// <summary>נתיב של תיקייה, מהשורש (2). null — לא ידוע.</summary>
    private string? Path(uint folder, int depth = 0)
    {
        if (folder == 2) return "";
        if (depth > 128 || !_folders.TryGetValue(folder, out var f)) return null;
        string? parent = Path(f.Parent, depth + 1);
        return parent is null ? null : parent.Length == 0 ? f.Name : parent + "\\" + f.Name;
    }

    // ------------------------------------------------------------ קבצים

    private RecoveredFile Build(FileRec f, string path, bool deleted)
    {
        var extents = f.Extents.Concat(deleted ? Enumerable.Empty<DataExtent>() : Overflow(f.Id)).ToList();
        var file = new RecoveredFile
        {
            Id = f.Id,
            Name = f.Name,
            Path = path,
            Size = f.Size,
            IsDeleted = deleted,
            Created = f.Created,
            Modified = f.Modified,
            Accessed = f.Accessed,
            Source = DiscoverySource.MftActive,
            Extents = extents,
            Quality = RecoveryQuality.Excellent,
            Content = ContentCheck.HasData,
            QualityReason = L.T("הקובץ קיים במערכת הקבצים ומיקומו ידוע במלואו."),
        };
        if (f.Compressed)
        {
            file.Quality = RecoveryQuality.Unrecoverable;
            file.Content = ContentCheck.NotChecked;
            file.QualityReason = L.T("הקובץ דחוס בדחיסה של macOS (בדרך כלל קובצי מערכת ותוכנות), שעוד לא נתמכת.");
        }
        return file;
    }

    /// <summary>
    /// רשומות של קבצים שכבר לא קיימים: בכל צומת של הקטלוג (גם בצמתים שהתפנו) וביומן, שבו
    /// נשמרים עותקים של צמתים מלפני השינוי.
    /// </summary>
    private void Deleted(List<DataExtent> catalog, (int NodeSize, uint FirstLeaf, uint Total) header,
        Dictionary<uint, FileRec> live, List<RecoveredFile> files, IProgress<ScanProgress>? progress, Stopwatch clock, CancellationToken token)
    {
        var found = new Dictionary<uint, FileRec>();
        void Take(byte[] node)
        {
            if ((sbyte)node[8] != -1 || node[9] != 1) return;
            foreach (var rec in Records(node))
            {
                if (Parse(rec) is not { } p) continue;
                if (p.Folder is { } f) _folders.TryAdd(f.Id, (f.Parent, f.Name));   // תיקייה שנמחקה — לנתיב
                else if (p.File is { } file && file.Id > 15 && !live.ContainsKey(file.Id) && file.Extents.Count > 0
                         && file.Extents.All(e => e.StartCluster + e.ClusterCount <= _volume.TotalBlocks)
                         && (!found.TryGetValue(file.Id, out var had) || (had.Modified ?? default) < (file.Modified ?? default)))
                    found[file.Id] = file;
            }
        }

        progress?.Report(new ScanProgress { Stage = L.T("מחפש קבצים שנמחקו בקטלוג ובעותקים הישנים של הרשומות"), FilesFound = files.Count, Elapsed = clock.Elapsed });
        for (uint n = 1; n < header.Total && !token.IsCancellationRequested; n++)
            if (_volume.ReadFile(catalog, (long)n * header.NodeSize, header.NodeSize) is { } node) Take(node);

        // היומן: אזור רציף שבו נכתבים עותקים של בלוקים לפני שהם נכתבים למקומם.
        if (_volume.Journaled && _volume.JournalInfoBlock != 0)
        {
            var info = _volume.Read((long)_volume.JournalInfoBlock * _volume.BlockSize, 52);
            if (info.Length == 52 && (BinaryPrimitives.ReadUInt32BigEndian(info) & 1) != 0)
            {
                long start = (long)BinaryPrimitives.ReadUInt64BigEndian(info.AsSpan(36));
                long size = Math.Min((long)BinaryPrimitives.ReadUInt64BigEndian(info.AsSpan(44)), 1L << 30);
                const int chunk = 1 << 20;
                for (long at = 0; at < size && !token.IsCancellationRequested; at += chunk)
                {
                    var data = _volume.Read(start + at, (int)Math.Min(chunk + header.NodeSize, size - at));
                    for (int i = 0; i + header.NodeSize <= data.Length && i < chunk; i += 512)
                        if ((sbyte)data[i + 8] == -1 && data[i + 9] == 1)
                            Take(data.AsSpan(i, header.NodeSize).ToArray());
                }
            }
        }

        int count = 0;
        foreach (var f in found.Values)
        {
            var file = Build(f, Path(f.Parent) ?? "?", deleted: true);
            Assess(file);
            files.Add(file);
            count++;
        }
        if (count > 0)
            _warnings.Add(L.T("{0} קבצים שנמחקו נמצאו ברשומות ישנות שנשארו בקטלוג וביומן של מערכת הקבצים.", count.ToString("N0")));
    }

    /// <summary>איכות קובץ שנמחק: האם המקום שתפס עדיין פנוי, והאם יש בו תוכן.</summary>
    private void Assess(RecoveredFile file)
    {
        if (file.Quality == RecoveryQuality.Unrecoverable) return;
        long total = 0, taken = 0;
        foreach (var e in file.Extents)
            for (long i = 0; i < e.ClusterCount; i += Math.Max(1, e.ClusterCount / 64))
                if (_volume.IsClusterAllocated(e.StartCluster + i) is { } allocated) { total++; if (allocated) taken++; }
        file.Content = new ClusterStream(_volume, file.Extents, file.Size, 0).SampleContent();
        string basis = L.T("הרשומה, השם והמיקום נלקחו מעותק ישן שנשאר בדיסק.");
        if (file.Content == ContentCheck.Empty)
        {
            (file.Quality, file.QualityReason) = (RecoveryQuality.Unrecoverable, L.T("אזור הנתונים של הקובץ מכיל אפסים בלבד — התוכן נמחק. לא ניתן לשחזר."));
            return;
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
}
