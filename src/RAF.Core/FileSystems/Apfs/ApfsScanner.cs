using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.Apfs;

/// <summary>
/// סורק APFS. בכל כרך — עץ רשומות: רשומת קובץ (3), רשומת תיקייה-שם (9), וקטעי תוכן (8).
/// מפתח של רשומה: מספר העצם ב-60 הסיביות התחתונות, והסוג ב-4 העליונות.
///
/// קבצים שנמחקו (בסריקה עמוקה): APFS לא כותב במקום, ובלוקים ישנים של העץ נשארים בדיסק עד
/// שנדרסים. עוברים על כל המכולה, לוקחים כל עלה של עץ קבצים שטביעת האצבע שלו תקינה, ומוציאים
/// ממנו קבצים שכבר לא קיימים. כרך מוצפן (FileVault, או מק עם שבב T2/Apple) — לא נקרא.
/// </summary>
public sealed class ApfsScanner
{
    private sealed record Inode(ulong Id, ulong Parent, ulong Stream, long Size, ushort Mode, uint BsdFlags,
        DateTime? Created, DateTime? Modified, DateTime? Accessed);
    private sealed record Name(ulong Parent, string Text, ulong Target, int Type);

    private sealed class Records
    {
        internal readonly Dictionary<ulong, (ulong Xid, Inode Inode)> Inodes = new();
        internal readonly Dictionary<ulong, (ulong Xid, Name Name)> Names = new();          // לפי העצם שהשם מצביע עליו
        internal readonly Dictionary<ulong, List<Name>> Children = new();
        internal readonly Dictionary<(ulong Stream, ulong Logical), (ulong Xid, ulong Length, ulong Phys)> Extents = new();

        internal void Add(ApfsEntry e, ulong xid, bool children)
        {
            if (e.Key.Length < 8) return;
            ulong hdr = BinaryPrimitives.ReadUInt64LittleEndian(e.Key);
            ulong oid = hdr & 0x0FFFFFFFFFFFFFFF;
            switch ((int)(hdr >> 60))
            {
                case 3 when ParseInode(oid, e.Value) is { } inode:
                    if (!Inodes.TryGetValue(oid, out var hi) || hi.Xid <= xid) Inodes[oid] = (xid, inode);
                    break;
                case 9 when e.Key.Length >= 13 && e.Value.Length >= 18:
                {
                    int len = (int)(BinaryPrimitives.ReadUInt32LittleEndian(e.Key.AsSpan(8)) & 0x3FF);
                    if (12 + len > e.Key.Length || len < 2) break;
                    var name = new Name(oid, Encoding.UTF8.GetString(e.Key, 12, len - 1),
                        BinaryPrimitives.ReadUInt64LittleEndian(e.Value), BinaryPrimitives.ReadUInt16LittleEndian(e.Value.AsSpan(16)) & 0xF);
                    if (!Names.TryGetValue(name.Target, out var hn) || hn.Xid <= xid) Names[name.Target] = (xid, name);
                    if (children)
                    {
                        if (!Children.TryGetValue(oid, out var list)) Children[oid] = list = new();
                        list.Add(name);
                    }
                    break;
                }
                case 8 when e.Key.Length >= 16 && e.Value.Length >= 16:
                {
                    var k = (oid, BinaryPrimitives.ReadUInt64LittleEndian(e.Key.AsSpan(8)));
                    if (!Extents.TryGetValue(k, out var he) || he.Xid <= xid)
                        Extents[k] = (xid, BinaryPrimitives.ReadUInt64LittleEndian(e.Value) & 0x00FFFFFFFFFFFFFF, BinaryPrimitives.ReadUInt64LittleEndian(e.Value.AsSpan(8)));
                    break;
                }
            }
        }
    }

    private readonly List<string> _warnings = new();
    private readonly List<(long Start, long Count)> _liveBlocks = new();
    private ApfsVolume _volume = null!;

    public static Task<ScanResult> ScanAsync(int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, TrimState trim, IProgress<ScanProgress>? progress, CancellationToken token)
        => Task.Run(() => new ApfsScanner().Run(diskNumber, partitionOffset, partitionSize, sectorSize, mode, includeExisting, progress, token), token);

    private ScanResult Run(int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        using var reader = VolumeReader.TryOpen(diskNumber, partitionOffset, partitionSize, sectorSize, sequential: false)
            ?? throw new IOException(RawDevice.OpenFailure());
        using var volume = ApfsVolume.Open(reader)
            ?? throw new InvalidDataException(L.T("המחיצה אינה מכולת APFS תקינה, או שהכותרת שלה פגומה."));
        _volume = volume;

        var omap = volume.LoadOmap(volume.OmapOid);
        var files = new List<RecoveredFile>();
        var live = new HashSet<ulong>();
        var dirs = new Dictionary<ulong, string>();
        var volumes = new List<(string Name, Records Records)>();
        bool anyEncrypted = false;

        foreach (ulong fsOid in volume.FsOids)
        {
            if (!omap.TryGetValue(fsOid, out ulong at) || volume.Block(at) is not { } sb || !sb.AsSpan(32, 4).SequenceEqual("APSB"u8)) continue;
            string name = Encoding.UTF8.GetString(sb.AsSpan(704, 256)).TrimEnd('\0');
            if ((BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(264)) & 1) == 0)
            {
                anyEncrypted = true;
                _warnings.Add(L.T("הכרך \"{0}\" מוצפן (FileVault, או מק עם שבב אבטחה של Apple). בלי המפתח של המחשב הזה אין דרך לקרוא אותו.", name));
                continue;
            }
            progress?.Report(new ScanProgress { Stage = L.T("קורא את הכרך {0}", name), FilesFound = files.Count, Elapsed = clock.Elapsed });
            var vomap = volume.LoadOmap(BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(128)));
            if (!vomap.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(136)), out ulong root)) continue;

            var records = new Records();
            foreach (var e in volume.Walk(root, c => vomap.TryGetValue(c, out var p) ? p : null, token)) records.Add(e, 0, children: true);
            volumes.Add((name, records));
        }

        bool prefix = volumes.Count > 1;
        foreach (var (name, records) in volumes)
        {
            string top = prefix ? name : "";
            var seen = new HashSet<ulong>();
            void Walk(ulong dir, string path, int depth)
            {
                if (depth > 256 || !seen.Add(dir) || !records.Children.TryGetValue(dir, out var children)) return;
                dirs.TryAdd(dir, path);
                foreach (var c in children)
                {
                    if (token.IsCancellationRequested) return;
                    string child = path.Length == 0 ? c.Text : path + "\\" + c.Text;
                    if (c.Type == 4) { Walk(c.Target, child, depth + 1); continue; }
                    if (c.Type != 8 || !records.Inodes.TryGetValue(c.Target, out var inode)) continue;
                    live.Add(inode.Inode.Id);
                    var extents = records.Extents.Where(x => x.Key.Stream == inode.Inode.Stream).ToList();
                    foreach (var x in extents) if (x.Value.Phys != 0) _liveBlocks.Add(((long)x.Value.Phys, (long)(x.Value.Length / (ulong)_volume.BlockSize)));
                    if (includeExisting) files.Add(Build(inode.Inode, c.Text, path, extents.Select(x => (x.Key.Logical, x.Value.Length, x.Value.Phys)), deleted: false));
                }
            }
            Walk(2, top, 0);
            foreach (var (id, _) in records.Inodes) live.Add(id);
        }
        _liveBlocks.Sort();
        volume.Allocation = cluster => Used(cluster, 1) > 0 ? true : null;

        if (mode == ScanMode.Deep && !anyEncrypted && !token.IsCancellationRequested)
            Deleted(live, dirs, files, progress, clock, token);
        else if (mode != ScanMode.Deep)
            _warnings.Add(L.T("קבצים שנמחקו מ-APFS נמצאים רק בסריקה עמוקה: היא עוברת על כל הכונן ומחפשת עותקים ישנים של הרשומות."));

        return new ScanResult
        {
            Files = files,
            Mode = mode,
            Duration = clock.Elapsed,
            Cancelled = token.IsCancellationRequested,
            FileSystem = "APFS",
            Warnings = _warnings,
        };
    }

    private long Used(long start, long count)
    {
        long used = 0;
        foreach (var (s, c) in _liveBlocks)
        {
            if (s >= start + count) break;
            long from = Math.Max(s, start), to = Math.Min(s + c, start + count);
            if (to > from) used += to - from;
        }
        return used;
    }

    private static Inode? ParseInode(ulong id, byte[] v)
    {
        if (v.Length < 92) return null;
        long size = 0;
        int num = BinaryPrimitives.ReadUInt16LittleEndian(v.AsSpan(92));
        int data = 96 + num * 4;
        for (int i = 0; i < num && 96 + i * 4 + 4 <= v.Length; i++)
        {
            int type = v[96 + i * 4], length = BinaryPrimitives.ReadUInt16LittleEndian(v.AsSpan(96 + i * 4 + 2));
            if (type == 8 && data + 8 <= v.Length) size = (long)BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(data));   // מידע על זרם התוכן: הגודל
            data += (length + 7) & ~7;
        }
        return new Inode(id, BinaryPrimitives.ReadUInt64LittleEndian(v), BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(8)), size,
            BinaryPrimitives.ReadUInt16LittleEndian(v.AsSpan(80)), BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(68)),
            Time(v, 16), Time(v, 24), Time(v, 40));
    }

    private static DateTime? Time(byte[] v, int at)
    {
        ulong ns = BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(at));
        return ns == 0 ? null : DateTime.UnixEpoch.AddTicks((long)(ns / 100)).ToLocalTime();
    }

    private RecoveredFile Build(Inode inode, string name, string path, IEnumerable<(ulong Logical, ulong Length, ulong Phys)> extents, bool deleted)
    {
        long bs = _volume.BlockSize, at = 0, end = (inode.Size + bs - 1) / bs;
        var runs = new List<DataExtent>();
        foreach (var (logical, length, phys) in extents.OrderBy(x => x.Logical))
        {
            long first = (long)logical / bs, count = (long)(length + (ulong)bs - 1) / bs;
            if (first >= end || first < at) continue;
            if (first > at) runs.Add(new DataExtent(0, first - at, true));
            count = Math.Min(count, end - first);
            runs.Add(phys == 0 ? new DataExtent(0, count, true) : new DataExtent((long)phys, count, false));
            at = first + count;
        }
        if (at < end) runs.Add(new DataExtent(0, end - at, true));

        var file = new RecoveredFile
        {
            Id = (long)inode.Id,
            Name = name,
            Path = path,
            Size = inode.Size,
            IsDeleted = deleted,
            Created = inode.Created,
            Modified = inode.Modified,
            Accessed = inode.Accessed,
            Source = DiscoverySource.MftActive,
            Extents = runs,
            Quality = RecoveryQuality.Excellent,
            Content = ContentCheck.HasData,
            QualityReason = L.T("הקובץ קיים במערכת הקבצים ומיקומו ידוע במלואו."),
        };
        if ((inode.BsdFlags & 0x20) != 0)
        {
            file.Quality = RecoveryQuality.Unrecoverable;
            file.Content = ContentCheck.NotChecked;
            file.QualityReason = L.T("הקובץ דחוס בדחיסה של macOS (בדרך כלל קובצי מערכת ותוכנות), שעוד לא נתמכת.");
        }
        return file;
    }

    /// <summary>עוברים על כל המכולה: כל עלה של עץ קבצים שטביעת האצבע שלו תקינה.</summary>
    private void Deleted(HashSet<ulong> live, Dictionary<ulong, string> dirs, List<RecoveredFile> files,
        IProgress<ScanProgress>? progress, Stopwatch clock, CancellationToken token)
    {
        var old = new Records();
        int bs = _volume.BlockSize, batch = Math.Max(1, (4 << 20) / bs);
        for (long b = 0; b < _volume.BlockCount && !token.IsCancellationRequested; b += batch)
        {
            if (b % (batch * 64L) == 0)
                progress?.Report(new ScanProgress
                {
                    Stage = L.T("מחפש קבצים שנמחקו בעותקים הישנים של הרשומות"),
                    Percent = b * 100.0 / _volume.BlockCount,
                    FilesFound = files.Count,
                    Elapsed = clock.Elapsed,
                });
            var data = new byte[(int)Math.Min(batch, _volume.BlockCount - b) * bs];
            int got = _volume.ReadRaw(b * bs, data);
            for (int i = 0; (i + 1) * bs <= got; i++)
            {
                var span = data.AsSpan(i * bs, bs);
                // צומת של עץ (סוג 2 או 3), מסוג "עץ קבצים" (0x0E), עלה, עם טביעת אצבע תקינה.
                int type = BinaryPrimitives.ReadUInt16LittleEndian(span[24..]);
                if (type is not (2 or 3) || BinaryPrimitives.ReadUInt32LittleEndian(span[28..]) != 0x0E) continue;
                if ((BinaryPrimitives.ReadUInt16LittleEndian(span[32..]) & 2) == 0 || !ApfsVolume.ChecksumOk(span)) continue;
                ulong xid = BinaryPrimitives.ReadUInt64LittleEndian(span[16..]);
                foreach (var e in ApfsVolume.Entries(span.ToArray())) old.Add(e, xid, children: false);
            }
        }

        int count = 0;
        foreach (var (id, (_, inode)) in old.Inodes)
        {
            if (live.Contains(id) || (inode.Mode & 0xF000) != 0x8000 || inode.Size == 0) continue;
            var extents = old.Extents.Where(x => x.Key.Stream == inode.Stream).Select(x => (x.Key.Logical, x.Value.Length, x.Value.Phys)).ToList();
            if (extents.Count == 0) continue;
            string name = old.Names.TryGetValue(id, out var n) ? n.Name.Text : $"inode-{id}";
            string path = PathOf(inode.Parent, dirs, old, 0) ?? "?";
            var file = Build(inode, name, path, extents, deleted: true);
            long total = file.Extents.Where(e => !e.IsSparse).Sum(e => e.ClusterCount);
            double ratio = total == 0 ? 0 : (double)file.Extents.Where(e => !e.IsSparse).Sum(e => Used(e.StartCluster, e.ClusterCount)) / total;
            file.Content = new ClusterStream(_volume, file.Extents, file.Size, 0).SampleContent();
            string basis = L.T("הרשומה, השם והמיקום נלקחו מעותק ישן שנשאר בדיסק.");
            (file.Quality, file.QualityReason) = file.Content == ContentCheck.Empty
                ? (RecoveryQuality.Unrecoverable, L.T("אזור הנתונים של הקובץ מכיל אפסים בלבד — התוכן נמחק. לא ניתן לשחזר."))
                : ratio switch
                {
                    0 => (RecoveryQuality.Good, L.T("נמצאו נתונים, והמקום שהקובץ תפס לא משמש קובץ קיים. {0}", basis)),
                    < 0.85 => (RecoveryQuality.Poor, L.T("כ-{0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים. הקובץ ישוחזר פגום.", ratio.ToString("P0"))),
                    _ => (RecoveryQuality.Unrecoverable, L.T("כמעט כל המקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים.")),
                };
            files.Add(file);
            count++;
        }
        if (count > 0)
            _warnings.Add(L.T("{0} קבצים שנמחקו נמצאו בעותקים הישנים של הרשומות, שמערכת הקבצים משאירה בדיסק אחרי כל שינוי.", count.ToString("N0")));
    }

    private static string? PathOf(ulong dir, Dictionary<ulong, string> dirs, Records old, int depth)
    {
        if (dirs.TryGetValue(dir, out var path)) return path;
        if (depth > 64 || !old.Names.TryGetValue(dir, out var n)) return null;
        string? parent = PathOf(n.Name.Parent, dirs, old, depth + 1);
        return parent is null ? null : parent.Length == 0 ? n.Name.Text : parent + "\\" + n.Name.Text;
    }
}
