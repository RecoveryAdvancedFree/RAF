using System.Diagnostics;
using System.Text;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.Fat;

/// <summary>
/// סורק FAT12 / FAT16 / FAT32.
///
/// ב-FAT, מחיקת קובץ מאפסת את כל ערכי טבלת ההקצאה של השרשרת שלו ומשאירה
/// רק את אשכול ההתחלה ואת הגודל ברשומת הספרייה. לכן שחזור מניח הקצאה
/// רציפה — הנחה נכונה ברוב הקבצים ושגויה בקובץ שהיה מפוצל. התוכנה מציינת
/// זאת במפורש בדירוג, במקום להציג ודאות שאינה קיימת.
/// </summary>
public sealed class FatScanner
{
    private readonly List<string> _warnings = new();
    private readonly HashSet<long> _visitedDirectories = new();

    private FatVolume _volume = null!;
    private long _entriesExamined;
    private long _bytesRead;
    private long _verifiedEmpty;
    private int _verifyBudget = 100_000;
    private long _syntheticId = 1;

    /// <summary>סריקת מחיצת FAT.</summary>
    public static Task<ScanResult> ScanAsync(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, TrimState trim,
        IProgress<ScanProgress>? progress, CancellationToken token)
        => Task.Run(() => new FatScanner().Run(
            diskNumber, partitionOffset, partitionSize, sectorSize,
            mode, includeExisting, progress, token), token);

    private ScanResult Run(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting,
        IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();

        using var reader = VolumeReader.TryOpen(
            diskNumber, partitionOffset, partitionSize, sectorSize, sequential: mode != ScanMode.Quick)
            ?? throw new IOException(
                "לא ניתן לפתוח את הדיסק לקריאה. ודאו שהתוכנה פועלת בהרשאות מנהל.");

        using var volume = FatVolume.Open(reader)
            ?? throw new InvalidDataException(
                "המחיצה אינה FAT תקין, או שתחילת המחיצה (מגזר האתחול) פגומה.");

        _volume = volume;

        var files = new List<RecoveredFile>();

        progress?.Report(new ScanProgress
        {
            Stage = "קורא את ספריית השורש",
            FilesFound = 0,
            Elapsed = clock.Elapsed,
        });

        WalkDirectory(ReadRootDirectory(), "", files, includeExisting, progress, clock, token, depth: 0);

        if (mode is ScanMode.Deep or ScanMode.Advanced && !token.IsCancellationRequested)
            SweepOrphanDirectories(files, includeExisting, progress, clock, token);

        if (_verifiedEmpty > 0)
            _warnings.Add(
                $"{_verifiedEmpty:N0} קבצים נמצאו ברשומות הספרייה אך אזור הנתונים שלהם מכיל אפסים. " +
                "הם סומנו כלא ניתנים לשחזור.");

        _warnings.Add(
            "ב-FAT מחיקת קובץ מוחקת את המפה של חלקיו. השחזור מניח שהקובץ " +
            "נשמר ברצף מתחילתו — הנחה נכונה ברוב הקבצים, אך קובץ שהיה " +
            "מפוצל על פני הכונן ישוחזר פגום.");

        return new ScanResult
        {
            Files = files,
            Mode = mode,
            Duration = clock.Elapsed,
            Cancelled = token.IsCancellationRequested,
            FileSystem = volume.Boot.Kind.ToString().ToUpperInvariant(),
            RecordsExamined = _entriesExamined,
            BytesRead = _bytesRead,
            Warnings = _warnings,
        };
    }

    // ------------------------------------------------------------ ספריות

    /// <summary>קריאת ספריית השורש, שמבנה מיקומה שונה בין FAT32 לקודמיו.</summary>
    private byte[] ReadRootDirectory()
    {
        var boot = _volume.Boot;

        if (boot.Kind == FileSystemKind.Fat32)
        {
            var extents = _volume.FollowChain(boot.RootCluster);
            return _volume.ReadChain(extents, 8 * 1024 * 1024);
        }

        // ב-FAT12/16 ספריית השורש היא אזור קבוע שאינו חלק באזור הנתונים.
        int size = (int)(boot.RootDirSectors * boot.BytesPerSector);
        byte[] data = _volume.ReadBlockAt(boot.RootDirSector * boot.BytesPerSector, size);
        _bytesRead += data.Length;
        return data;
    }

    /// <summary>מעבר רקורסיבי על ספרייה ועל תיקיות המשנה שלה.</summary>
    private void WalkDirectory(
        byte[] data, string path, List<RecoveredFile> files, bool includeExisting,
        IProgress<ScanProgress>? progress, Stopwatch clock, CancellationToken token, int depth)
    {
        if (token.IsCancellationRequested) return;
        if (depth > 64) return; // הגנה מפני מבנה ספריות פגום

        var entries = FatDirectory.Parse(data);
        _entriesExamined += entries.Count;

        foreach (var entry in entries)
        {
            if (token.IsCancellationRequested) return;
            if (entry.IsDotEntry) continue;

            if (entry.IsDirectory)
            {
                // ספרייה שנמחקה איבדה את שרשרת האשכולות שלה, ולכן נקרא
                // רק את האשכול הראשון שלה — מה שעדיין חושף את תוכנה.
                if (!_volume.IsValidCluster(entry.FirstCluster)) continue;
                if (!_visitedDirectories.Add(entry.FirstCluster)) continue;

                var extents = entry.IsDeleted
                    ? _volume.ContiguousExtent(entry.FirstCluster, _volume.BytesPerCluster)
                    : _volume.FollowChain(entry.FirstCluster);

                byte[] child = _volume.ReadChain(extents, 4 * 1024 * 1024);
                _bytesRead += child.Length;

                string childPath = string.IsNullOrEmpty(path) ? entry.Name : path + "\\" + entry.Name;
                WalkDirectory(child, childPath, files, includeExisting, progress, clock, token, depth + 1);
                continue;
            }

            if (!includeExisting && !entry.IsDeleted) continue;

            files.Add(Materialize(entry, path, DiscoverySource.MftActive));

            if (files.Count % 500 == 0)
            {
                progress?.Report(new ScanProgress
                {
                    Stage = "עובר על ספריות מערכת הקבצים",
                    FilesFound = files.Count,
                    BytesProcessed = _bytesRead,
                    Elapsed = clock.Elapsed,
                });
            }
        }
    }

    // --------------------------------------------- סריקת ספריות יתומות

    /// <summary>
    /// סריקה גולמית של אזור הנתונים לאיתור אשכולות ספרייה שאינם מקושרים
    /// עוד לעץ. אשכול כזה מזוהה לפי ערכי הנקודה שבתחילתו, שהם חתימה
    /// חד-משמעית של ספריית משנה ב-FAT.
    /// </summary>
    private void SweepOrphanDirectories(
        List<RecoveredFile> files, bool includeExisting,
        IProgress<ScanProgress>? progress, Stopwatch clock, CancellationToken token)
    {
        var boot = _volume.Boot;
        long total = _volume.MaxCluster;
        int clusterSize = _volume.BytesPerCluster;

        byte[] cluster = new byte[clusterSize];
        long reportEvery = Math.Max(1, total / 200);
        int found = 0;

        for (long c = FatVolume.FirstCluster; c <= total; c++)
        {
            if (token.IsCancellationRequested) return;

            // הדיווח בראש הלולאה: כמעט כל אשכול מדולג באחד התנאים שבהמשך, ודיווח
            // שהיה אחריהם כמעט לא הגיע — והסריקה נראתה תקועה על "קורא את ספריית השורש".
            if (c % reportEvery == 0)
            {
                progress?.Report(new ScanProgress
                {
                    Stage = "סורק ספריות יתומות על פני המחיצה",
                    Percent = c * 100.0 / total,
                    FilesFound = files.Count,
                    BytesProcessed = _bytesRead,
                    BytesTotal = total * clusterSize,
                    Elapsed = clock.Elapsed,
                });
            }

            if (_visitedDirectories.Contains(c)) continue;

            int read = _volume.ReadRaw(_volume.ClusterToOffset(c), cluster);
            if (read < FatDirectory.EntrySize * 2) continue;
            _bytesRead += read;

            if (!LooksLikeDirectoryCluster(cluster)) continue;

            _visitedDirectories.Add(c);
            found++;

            foreach (var entry in FatDirectory.Parse(cluster, stopAtEnd: false))
            {
                _entriesExamined++;
                if (entry.IsDotEntry || entry.IsDirectory) continue;
                if (!includeExisting && !entry.IsDeleted) continue;

                files.Add(Materialize(entry, "?", DiscoverySource.MftOrphan));
            }
        }

        if (found > 0)
            _warnings.Add($"הסריקה העמוקה איתרה {found:N0} שרידי תיקיות שאינם מקושרים עוד לעץ התיקיות.");
    }

    /// <summary>
    /// ספריית משנה ב-FAT פותחת תמיד בשני ערכים: "." ו-"..".
    /// זו חתימה חזקה שכמעט אינה מופיעה בנתונים אקראיים.
    /// </summary>
    private static bool LooksLikeDirectoryCluster(ReadOnlySpan<byte> cluster)
    {
        if (cluster.Length < FatDirectory.EntrySize * 2) return false;

        // הערך הראשון: נקודה אחת ואחריה רווחים, עם תכונת ספרייה.
        if (cluster[0] is not ((byte)'.' or 0xE5)) return false;
        if ((cluster[11] & 0x10) == 0) return false;

        for (int i = 1; i < 11; i++)
            if (cluster[i] != (byte)' ') return false;

        // הערך השני: שתי נקודות ואחריהן רווחים.
        int second = FatDirectory.EntrySize;
        if (cluster[second] is not ((byte)'.' or 0xE5)) return false;
        if (cluster[second + 1] != (byte)'.') return false;
        if ((cluster[second + 11] & 0x10) == 0) return false;

        for (int i = 2; i < 11; i++)
            if (cluster[second + i] != (byte)' ') return false;

        return true;
    }

    // ------------------------------------------------------------ המרה

    private RecoveredFile Materialize(FatEntry entry, string path, DiscoverySource source)
    {
        // בקובץ חי השרשרת שלמה; בקובץ מחוק היא אופסה ויש להניח רציפות.
        var extents = entry.IsDeleted
            ? _volume.ContiguousExtent(entry.FirstCluster, entry.Size)
            : TruncateToSize(_volume.FollowChain(entry.FirstCluster), entry.Size);

        var file = new RecoveredFile
        {
            Id = _syntheticId++,
            ParentId = -1,
            Name = entry.Name,
            Path = path,
            Size = entry.Size,
            IsDirectory = false,
            IsDeleted = entry.IsDeleted,
            Created = entry.Created,
            Modified = entry.Modified,
            Accessed = entry.Accessed,
            Source = source,
            Extents = extents,
            NameIsPartial = entry.NameIsPartial,
        };

        AssessQuality(file, entry);

        if (entry.NameIsPartial)
            file.QualityReason +=
                " שימו לב: שם הקובץ נשמר בתבנית הקצרה בלבד, ומחיקה ב-FAT דורסת את " +
                "האות הראשונה שלו. התוכן שלם, אך האות הראשונה בשם הוחלפה בקו תחתון.";
        return file;
    }

    /// <summary>קיצוץ שרשרת לגודל הקובץ, כדי לא לכתוב אשכול עודף.</summary>
    private List<DataExtent> TruncateToSize(List<DataExtent> extents, long size)
    {
        if (size <= 0) return extents;

        long needed = (size + _volume.BytesPerCluster - 1) / _volume.BytesPerCluster;
        var result = new List<DataExtent>();
        long taken = 0;

        foreach (var extent in extents)
        {
            if (taken >= needed) break;

            long take = Math.Min(extent.ClusterCount, needed - taken);
            result.Add(new DataExtent(extent.StartCluster, take, extent.IsSparse));
            taken += take;
        }

        return result;
    }

    private void AssessQuality(RecoveredFile file, FatEntry entry)
    {
        if (entry.FirstCluster == 0 || file.Extents.Count == 0)
        {
            file.Quality = file.Size == 0 ? RecoveryQuality.Excellent : RecoveryQuality.Unrecoverable;
            file.QualityReason = file.Size == 0
                ? "הקובץ ריק ואין לו תוכן לשחזר."
                : "רשומת הקובץ אינה מציינת היכן בכונן מתחיל התוכן שלו, ולכן לא ניתן לאתר אותו.";
            return;
        }

        if (!file.IsDeleted)
        {
            file.Quality = RecoveryQuality.Excellent;
            file.Content = ContentCheck.HasData;
            file.QualityReason = "הקובץ קיים במערכת הקבצים, והמפה של חלקיו שלמה.";
            return;
        }

        // שלב א: האם יש בכלל נתונים באזור?
        file.Content = VerifyContent(file);

        if (file.Content == ContentCheck.Empty)
        {
            _verifiedEmpty++;
            file.Quality = RecoveryQuality.Unrecoverable;
            file.QualityReason = "אזור הנתונים של הקובץ מכיל אפסים בלבד — התוכן נמחק. לא ניתן לשחזר.";
            return;
        }

        if (file.Content == ContentCheck.Unreadable)
        {
            file.Quality = RecoveryQuality.Poor;
            file.QualityReason = "לא ניתן היה לקרוא את אזור הנתונים של הקובץ.";
            return;
        }

        // שלב ב: כמה מהאשכולות שהנחנו כבר הוקצו לקובץ אחר?
        long total = 0, taken = 0;

        foreach (var extent in file.Extents)
        {
            long step = Math.Max(1, extent.ClusterCount / 64);

            for (long i = 0; i < extent.ClusterCount; i += step)
            {
                bool? allocated = _volume.IsClusterAllocated(extent.StartCluster + i);
                if (allocated is null) break;

                total++;
                if (allocated.Value) taken++;
            }
        }

        double ratio = total == 0 ? 0 : (double)taken / total;

        (file.Quality, file.QualityReason) = ratio switch
        {
            0 => (RecoveryQuality.Excellent,
                  "נמצאו נתונים, וכל המקום שהקובץ תפס בכונן עדיין פנוי. " +
                  "השחזור מניח שהקובץ היה רציף על הכונן."),
            < 0.15 => (RecoveryQuality.Good,
                  $"נמצאו נתונים. כ-{ratio:P0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים."),
            < 0.85 => (RecoveryQuality.Poor,
                  $"כ-{ratio:P0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים. הקובץ ישוחזר פגום."),
            _ => (RecoveryQuality.Unrecoverable,
                  "כמעט כל המקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים."),
        };
    }

    private ContentCheck VerifyContent(RecoveredFile file)
    {
        if (_verifyBudget <= 0 || file.Size <= 0) return ContentCheck.NotChecked;
        _verifyBudget--;

        var stream = new ClusterStream(_volume, file.Extents, file.Size, 0);
        return stream.SampleContent();
    }
}
