using System.Diagnostics;
using RAF.Core.FileSystems.ExFat;
using RAF.Core.FileSystems.Fat;
using RAF.Core.FileSystems.Ntfs;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Disks;

/// <summary>מחיצה שנמצאה בסריקת הכונן ואינה מופיעה בטבלת המחיצות.</summary>
public sealed class FoundPartition
{
    public long Offset { get; init; }
    public long Size { get; init; }
    public FileSystemKind FileSystem { get; init; }
    public string Label { get; init; } = "";

    /// <summary>
    /// מגזר האתחול בתחילת המחיצה פגום, והיא נמצאה לפי עותק הגיבוי שלה.
    /// כדי לקרוא אותה יש להשתמש בקריאה דרך הגיבוי או בתיקון המחיצה.
    /// </summary>
    public bool BootSectorDamaged { get; init; }

    /// <summary>המחיצה חופפת למחיצה שכבר קיימת בטבלה — לא ניתן להחזיר אותה לטבלה.</summary>
    public bool OverlapsExisting { get; set; }

    /// <summary>המחיצה יושבת בתוך מחיצה אחרת שנמצאה — ייתכן שזו תמונת דיסק בתוך קובץ.</summary>
    public bool InsideAnother { get; set; }

    /// <summary>
    /// מחיצת NTFS ששני מגזרי האתחול שלה אבדו, ונבנתה מחדש מרשומות הקבצים שלה (ראו NtfsRebuild).
    /// מגזר האתחול שחושב מוצג לקוראי המחיצה בזיכרון בלבד.
    /// </summary>
    public RebuiltNtfs? Rebuilt { get; init; }

    /// <summary>המחיצה שנבנתה מחדש היא מחיצה שכבר רשומה בטבלה ואינה נקראת — לא מחיצה נוספת.</summary>
    public bool OnExisting { get; init; }

    public long End => Offset + Size;
}

public sealed class HuntProgress
{
    public double Percent { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public int Found { get; init; }
    public double BytesPerSecond { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>מפת הסקטורים של הכונן: מה נבדק, איפה נמצאו מחיצות, ומה לא נקרא.</summary>
    public SectorMap? Map { get; init; }
}

public sealed class HuntResult
{
    public List<FoundPartition> Found { get; init; } = new();
    public bool Cancelled { get; init; }

    /// <summary>הכונן נותק באמצע הסריקה — מה שאחרי נקודת הניתוק לא נבדק.</summary>
    public bool Disconnected { get; init; }
    public TimeSpan Duration { get; init; }
    public long UnreadableBytes { get; init; }

    /// <summary>
    /// מגזרי אתחול שנמצאו בתוך מחיצה אחרת — שרידים של תמונות דיסק ומחיצות
    /// ישנות שנדרסו. הם אינם מוצגים כמחיצות, רק נספרים.
    /// </summary>
    public int HiddenInside { get; init; }
}

/// <summary>
/// סריקת כונן שלם לאיתור מחיצות שנמחקו או שטבלת המחיצות איבדה.
///
/// מחיצה שנמחקה מטבלת המחיצות לא נמחקה מהכונן: מגזר האתחול שלה, טבלת
/// הקבצים והקבצים עצמם נשארים במקומם. הסריקה עוברת על כל סקטור ומחפשת
/// מגזרי אתחול של NTFS, exFAT ו-FAT — וגם את עותקי הגיבוי שלהם, כך שנמצאת
/// גם מחיצה שתחילתה נהרסה.
///
/// כל מועמד מאומת מול המבנה שאחריו (טבלת ה-MFT, או טבלת ה-FAT), כדי לא
/// להציג כמחיצה שרידים של מחיצה ישנה או מגזר אתחול בתוך קובץ.
/// הסריקה קוראת בלבד.
/// </summary>
public static class PartitionHunter
{
    private const int BlockSize = 4 * 1024 * 1024;

    public static Task<HuntResult> HuntAsync(
        PhysicalDiskInfo disk, IProgress<HuntProgress>? progress, CancellationToken token)
        => Task.Run(() =>
        {
            using var reader = VolumeReader.TryOpen(
                disk.DiskNumber, 0, disk.SizeBytes, disk.LogicalSectorSize, sequential: true, applyOverlay: false)
                ?? throw new IOException(Native.RawDevice.OpenFailure(L.T("הכונן")));

            // מחיצה שהתוכנה רק הניחה (ראו PartitionInfo.Assumed) אינה מסתירה מה שנמצא בתוכה.
            var listed = disk.Partitions.Where(p => p.SizeBytes > 0 && !p.Assumed).ToList();
            var existing = listed.Select(p => (p.OffsetBytes, p.SizeBytes)).ToList();
            // מחיצה בטבלה שמערכת הקבצים שלה אינה מזוהה — ייתכן שהיא זו שתיבנה מחדש.
            var readable = listed.Where(p => p.FileSystem is not (FileSystemKind.Raw or FileSystemKind.Unknown))
                                 .Select(p => p.OffsetBytes).ToList();

            return Hunt(new ReaderSource(reader, disk.SizeBytes, disk.LogicalSectorSize), existing, progress, token, readable);
        }, token);

    /// <summary>השערה: מגזר אתחול שמעיד על מחיצה שמתחילה ב-Start.</summary>
    private sealed record Hypothesis(long Start, long Size, FileSystemKind Kind, bool FromBackup, byte[] Sector);

    /// <param name="readable">
    /// היסטים של מחיצות בטבלה שנקראות כרגיל. null — כולן. מחיצה בטבלה שאינה ביניהן
    /// יכולה להיבנות מחדש מרשומות הקבצים שלה.
    /// </param>
    internal static HuntResult Hunt(
        ISectorSource source, List<(long Offset, long Size)> existing,
        IProgress<HuntProgress>? progress, CancellationToken token,
        IReadOnlyCollection<long>? readable = null)
    {
        var clock = Stopwatch.StartNew();
        int sector = source.SectorSize;
        long length = source.Length;

        var hypotheses = new List<Hypothesis>();
        var rebuild = new NtfsRebuild(sector);
        // איסוף הרשומות רץ במקביל לקריאת הבלוק הבא, על עותק — בכונן מהיר הוא אחרת היה מאט את הסריקה.
        byte[] collectBuffer = new byte[BlockSize];
        Task? collecting = null;
        byte[] block = new byte[BlockSize];
        long unreadable = 0;
        var lastReport = TimeSpan.Zero;
        var map = new SectorMap(length);
        bool disconnected = false;

        long at = 0;
        for (; at < length && !token.IsCancellationRequested; at += BlockSize)
        {
            int want = (int)Math.Min(BlockSize, length - at);
            int read = source.Read(at, block.AsSpan(0, want));
            int before = hypotheses.Count;
            if (read < want && source.Disconnected)
            {
                disconnected = true;
                break;
            }
            map.Cursor = at + want - 1;             // סוף הבלוק — כדי שהצבע לא יעבור את הסמן

            if (read < want)
            {
                // בלוק שנכשל כולו — נבדק סקטור אחר סקטור, כדי לא לפספס מגזר
                // אתחול תקין שיושב ליד סקטור פגום.
                read = 0;
                for (int s = 0; s + sector <= want; s += sector)
                {
                    if (source.Read(at + s, block.AsSpan(s, sector)) == sector)
                    {
                        Examine(block.AsSpan(s, sector), at + s, sector, hypotheses);
                        map.Add(at + s, sector, SectorState.Read);
                    }
                    else
                    {
                        unreadable += sector;
                        map.Add(at + s, sector, SectorState.Bad);
                    }
                }
            }
            else
            {
                for (int s = 0; s + 512 <= read; s += sector)
                    Examine(block.AsSpan(s, sector), at + s, sector, hypotheses);
                collecting?.Wait();
                Buffer.BlockCopy(block, 0, collectBuffer, 0, read);
                long position = at;
                int count = read;
                collecting = Task.Run(() => rebuild.Collect(collectBuffer.AsSpan(0, count), position));
                map.Add(at, read, SectorState.Read);
            }

            // מגזר אתחול ראשי שנמצא — ריבוע בולט במפה. עותקי גיבוי אינם מחיצה נוספת.
            for (int h = before; h < hypotheses.Count; h++)
                if (!hypotheses[h].FromBackup) map.Mark(hypotheses[h].Start, SectorState.Found);

            if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(250))
            {
                lastReport = clock.Elapsed;
                progress?.Report(new HuntProgress
                {
                    Map = map,
                    Percent = at * 100.0 / length,
                    BytesDone = at,
                    BytesTotal = length,
                    Found = hypotheses.Count(h => !h.FromBackup),
                    Elapsed = clock.Elapsed,
                    BytesPerSecond = clock.Elapsed.TotalSeconds > 0 ? at / clock.Elapsed.TotalSeconds : 0,
                });
            }
        }

        collecting?.Wait();

        var all = Resolve(source, hypotheses, existing);

        // מחיצה שיושבת כולה בתוך מחיצה אחרת היא כמעט תמיד שריד: תמונת דיסק
        // שנשמרה כקובץ (למשל אזור האתחול בתוך קובץ ISO), או מחיצה ישנה שנדרסה.
        // הצגתה כ"מחיצה שנמצאה" רק מבלבלת — הקבצים שבה נגישים דרך המחיצה שמכילה אותה.
        var found = all.Where(f => !f.InsideAnother && !InsideExisting(f, existing)).ToList();

        // מחיצות NTFS בלי אף מגזר אתחול — מהרשומות שנאספו בדרך. רק אם הסריקה הגיעה לסוף:
        // חישוב על חצי כונן היה מחמיץ, או ממקם לא נכון, את מה שבחצי השני.
        if (!token.IsCancellationRequested && !disconnected)
        {
            var known = all.Select(f => f.Offset)
                .Concat(readable ?? existing.Select(e => e.Offset))
                .ToHashSet();
            foreach (var r in rebuild.Infer(length, known))
            {
                bool onExisting = existing.Any(e => e.Offset == r.Offset);
                var f = new FoundPartition
                {
                    Offset = r.Offset,
                    Size = r.Size,
                    FileSystem = FileSystemKind.Ntfs,
                    Rebuilt = r,
                    OnExisting = onExisting,
                    OverlapsExisting = !onExisting && existing.Any(e => r.Offset < e.Offset + e.Size && e.Offset < r.Offset + r.Size),
                };
                if (onExisting || !InsideExisting(f, existing)) found.Add(f);
            }
            found = found.OrderBy(f => f.Offset).ToList();
        }

        progress?.Report(new HuntProgress
        {
            Percent = 100, BytesDone = Math.Min(at, length), BytesTotal = length, Found = found.Count,
            Elapsed = clock.Elapsed, Map = map,
        });

        return new HuntResult
        {
            Found = found,
            Cancelled = token.IsCancellationRequested || disconnected,
            Disconnected = disconnected,
            Duration = clock.Elapsed,
            UnreadableBytes = unreadable,
            HiddenInside = all.Count - found.Count,
        };
    }

    /// <summary>בדיקת סקטור אחד: האם הוא מגזר אתחול, ומה הוא מעיד.</summary>
    private static void Examine(ReadOnlySpan<byte> s, long position, int sectorSize, List<Hypothesis> into)
    {
        // כל מגזרי האתחול הנתמכים מסתיימים ב-55 AA — סינון זול לפני פענוח.
        if (s.Length < 512 || s[510] != 0x55 || s[511] != 0xAA) return;

        if (NtfsBootSector.Parse(s) is { } ntfs && ntfs.BytesPerSector == sectorSize)
        {
            long bps = ntfs.BytesPerSector;
            long size = (ntfs.TotalSectors + 1) * bps;   // הסקטור האחרון שמור לעותק הגיבוי
            byte[] copy = s[..512].ToArray();

            into.Add(new Hypothesis(position, size, FileSystemKind.Ntfs, false, copy));

            // אם זה עותק הגיבוי — הוא יושב בסקטור האחרון של המחיצה.
            long start = position - ntfs.TotalSectors * bps;
            if (start >= 0) into.Add(new Hypothesis(start, size, FileSystemKind.Ntfs, true, copy));
            return;
        }

        if (ExFatBootSector.Parse(s) is { } exfat && exfat.BytesPerSector == sectorSize)
        {
            long bps = exfat.BytesPerSector;
            long size = exfat.VolumeLengthSectors * bps;
            byte[] copy = s[..512].ToArray();

            into.Add(new Hypothesis(position, size, FileSystemKind.ExFat, false, copy));

            // אזור האתחול המשני מתחיל 12 סקטורים אחרי הראשי.
            long start = position - 12 * bps;
            if (start >= 0) into.Add(new Hypothesis(start, size, FileSystemKind.ExFat, true, copy));
            return;
        }

        if (FatBootSector.Parse(s) is { } fat && fat.BytesPerSector == sectorSize)
        {
            long bps = fat.BytesPerSector;
            long size = fat.TotalSectors * bps;
            byte[] copy = s[..512].ToArray();

            into.Add(new Hypothesis(position, size, fat.Kind, false, copy));

            // ב-FAT32 מיקום העותק רשום בכותרת (בדרך כלל סקטור 6).
            if (fat.Kind == FileSystemKind.Fat32)
            {
                int backupSector = s[0x32] | (s[0x33] << 8);
                if (backupSector is > 0 and < 64)
                {
                    long start = position - backupSector * bps;
                    if (start >= 0) into.Add(new Hypothesis(start, size, fat.Kind, true, copy));
                }
            }
        }
    }

    /// <summary>
    /// איחוד ההשערות למחיצות ואימות כל אחת מול המבנה שלה.
    /// </summary>
    private static List<FoundPartition> Resolve(
        ISectorSource source, List<Hypothesis> hypotheses, List<(long Offset, long Size)> existing)
    {
        var result = new List<FoundPartition>();
        long length = source.Length;
        int sector = source.SectorSize;

        foreach (var group in hypotheses.GroupBy(h => (h.Start, h.Kind, h.Size)))
        {
            var (start, kind, size) = group.Key;

            // מחיצה שכבר רשומה בטבלה — אינה "אבודה".
            if (existing.Any(e => e.Offset == start)) continue;

            // מחיצה שחורגת מסוף הכונן אינה אמיתית (NTFS מקבל סובלנות של סקטור).
            if (start + size > length + sector) continue;

            // מגזר אתחול תקין בתחילת המחיצה, או רק עותק גיבוי?
            var primary = group.FirstOrDefault(h => !h.FromBackup);
            var evidence = primary ?? group.First();

            if (!Confirms(source, start, kind, evidence.Sector)) continue;

            bool damaged = primary is null;
            string label = Disks.FileSystemIdentifier.Identify(evidence.Sector).Label;

            result.Add(new FoundPartition
            {
                Offset = start,
                Size = Math.Min(size, length - start),
                FileSystem = kind,
                Label = label,
                BootSectorDamaged = damaged,
                OverlapsExisting = existing.Any(e => start < e.Offset + e.Size && e.Offset < start + size),
            });
        }

        // השערה "ראשית" שבעצם הייתה עותק גיבוי של מחיצה אחרת נפסלת באימות;
        // אם בכל זאת נותרו שתי מחיצות באותה נקודת התחלה — עדיפה זו שמגזרה תקין.
        result = result
            .GroupBy(f => f.Offset)
            .Select(g => g.OrderBy(f => f.BootSectorDamaged).First())
            .OrderBy(f => f.Offset)
            .ToList();

        foreach (var f in result)
            f.InsideAnother = result.Any(o => !ReferenceEquals(o, f) && o.Offset < f.Offset && f.End <= o.End);

        return result;
    }

    private static bool InsideExisting(FoundPartition f, List<(long Offset, long Size)> existing)
        => existing.Any(e => e.Offset <= f.Offset && f.End <= e.Offset + e.Size);

    /// <summary>
    /// אימות מגזר אתחול מול המבנה שאחריו. מגזר אתחול לבדו אינו מוכיח
    /// מחיצה חיה: שרידים של מחיצות ישנות ומגזרים בתוך קבצים נראים זהים.
    /// </summary>
    private static bool Confirms(ISectorSource source, long start, FileSystemKind kind, byte[] boot)
    {
        switch (kind)
        {
            case FileSystemKind.Ntfs:
            {
                var b = NtfsBootSector.Parse(boot);
                if (b is null) return false;

                // הרשומה הראשונה בטבלת ה-MFT (או במראה שלה) מתחילה ב-"FILE".
                return StartsWith(source, start + b.MftOffset, "FILE"u8)
                       || StartsWith(source, start + b.MftMirrorCluster * b.BytesPerSector * b.SectorsPerCluster, "FILE"u8);
            }

            case FileSystemKind.ExFat:
            {
                var b = ExFatBootSector.Parse(boot);
                if (b is null) return false;

                // שתי הרשומות הראשונות בטבלת ה-FAT: F8 FF FF FF ו-FF FF FF FF.
                byte[] fat = Read(source, start + b.FatOffsetSectors * b.BytesPerSector, 8);
                return fat.Length == 8 && fat[0] == 0xF8 && fat.AsSpan(1).IndexOfAnyExcept((byte)0xFF) < 0;
            }

            case FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32:
            {
                var b = FatBootSector.Parse(boot);
                if (b is null) return false;

                // הבית הראשון בטבלת ה-FAT הוא סוג המדיה, והבא אחריו FF.
                byte[] fat = Read(source, start + (long)b.ReservedSectors * b.BytesPerSector, 4);
                return fat.Length == 4 && fat[0] >= 0xF0 && fat[1] == 0xFF;
            }

            default:
                return false;
        }
    }

    private static bool StartsWith(ISectorSource source, long offset, ReadOnlySpan<byte> prefix)
    {
        byte[] data = Read(source, offset, prefix.Length);
        return data.Length == prefix.Length && data.AsSpan().SequenceEqual(prefix);
    }

    private static byte[] Read(ISectorSource source, long offset, int count)
    {
        if (offset < 0 || offset >= source.Length) return Array.Empty<byte>();

        int sector = source.SectorSize;
        long aligned = offset / sector * sector;
        int skew = (int)(offset - aligned);
        byte[] buffer = new byte[(skew + count + sector - 1) / sector * sector];

        int read = source.Read(aligned, buffer);
        return read >= skew + count ? buffer.AsSpan(skew, count).ToArray() : Array.Empty<byte>();
    }

    private sealed class ReaderSource(VolumeReader reader, long length, int sectorSize) : ISectorSource
    {
        public long Length => length;
        public int SectorSize => sectorSize;
        public int Read(long offset, Span<byte> destination) => reader.Read(offset, destination);
        public bool Disconnected => reader.Disconnected;
    }
}
