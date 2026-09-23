using System.Diagnostics;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.ExFat;

/// <summary>
/// סורק exFAT.
///
/// מחיקה ב-exFAT מכבה ביט אחד בלבד בכל ערך של הקובץ, ומשאירה את השם,
/// הגודל, התאריכים ואשכול ההתחלה שלמים. לכן איכות המידע על קבצים
/// שנמחקו טובה משמעותית מזו של FAT הקלאסי.
/// </summary>
public sealed class ExFatScanner
{
    private readonly List<string> _warnings = new();
    private readonly HashSet<long> _visitedDirectories = new();

    private ExFatVolume _volume = null!;
    private long _entriesExamined;
    private long _bytesRead;
    private long _verifiedEmpty;
    private int _verifyBudget = 100_000;
    private long _syntheticId = 1;

    /// <summary>סריקת מחיצת exFAT.</summary>
    public static Task<ScanResult> ScanAsync(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, TrimState trim,
        IProgress<ScanProgress>? progress, CancellationToken token)
        => Task.Run(() => new ExFatScanner().Run(
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

        using var volume = ExFatVolume.Open(reader)
            ?? throw new InvalidDataException(
                "המחיצה אינה exFAT תקין, או שתחילת המחיצה (מגזר האתחול) פגומה.");

        _volume = volume;
        var files = new List<RecoveredFile>();

        progress?.Report(new ScanProgress
        {
            Stage = "קורא את ספריית השורש",
            Elapsed = clock.Elapsed,
        });

        byte[] root = volume.ReadRootDirectory(8 * 1024 * 1024);
        _bytesRead += root.Length;
        _visitedDirectories.Add(volume.Boot.RootCluster);

        WalkDirectory(root, "", files, includeExisting, progress, clock, token, depth: 0);

        if (mode is ScanMode.Deep or ScanMode.Advanced && !token.IsCancellationRequested)
            SweepOrphanDirectories(files, includeExisting, progress, clock, token);

        if (_verifiedEmpty > 0)
            _warnings.Add(
                $"{_verifiedEmpty:N0} קבצים נמצאו ברשומות הספרייה אך אזור הנתונים שלהם מכיל אפסים. " +
                "הם סומנו כלא ניתנים לשחזור.");

        return new ScanResult
        {
            Files = files,
            Mode = mode,
            Duration = clock.Elapsed,
            Cancelled = token.IsCancellationRequested,
            FileSystem = "exFAT",
            RecordsExamined = _entriesExamined,
            BytesRead = _bytesRead,
            Warnings = _warnings,
        };
    }

    // ------------------------------------------------------------ ספריות

    private void WalkDirectory(
        byte[] data, string path, List<RecoveredFile> files, bool includeExisting,
        IProgress<ScanProgress>? progress, Stopwatch clock, CancellationToken token, int depth)
    {
        if (token.IsCancellationRequested || depth > 64) return;

        var entries = ExFatDirectory.Parse(data);
        _entriesExamined += entries.Count;

        foreach (var entry in entries)
        {
            if (token.IsCancellationRequested) return;

            if (entry.IsDirectory)
            {
                if (!_volume.IsValidCluster(entry.FirstCluster)) continue;
                if (!_visitedDirectories.Add(entry.FirstCluster)) continue;

                var (extents, _) = ExtentsFor(entry, Math.Max(entry.Size, _volume.BytesPerCluster));
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

    /// <summary>
    /// סריקה גולמית לאיתור אשכולות ספרייה שאינם מקושרים עוד לעץ.
    /// ערך קובץ ב-exFAT מזוהה לפי סוג הערך ולפי ערך הזרם שחייב לבוא אחריו.
    /// </summary>
    private void SweepOrphanDirectories(
        List<RecoveredFile> files, bool includeExisting,
        IProgress<ScanProgress>? progress, Stopwatch clock, CancellationToken token)
    {
        long total = _volume.MaxCluster;
        int clusterSize = _volume.BytesPerCluster;
        byte[] cluster = new byte[clusterSize];

        long reportEvery = Math.Max(1, total / 200);
        int found = 0;

        for (long c = ExFatVolume.FirstCluster; c <= total; c++)
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
            if (read < ExFatDirectory.EntrySize * 2) continue;
            _bytesRead += read;

            // חתימה: ערך קובץ (0x85, או 0x05 אם נמחק) ואחריו ערך זרם.
            byte first = (byte)(cluster[0] | 0x80);
            byte second = (byte)(cluster[ExFatDirectory.EntrySize] | 0x80);
            if (first != 0x85 || second != 0xC0) continue;

            var entries = ExFatDirectory.Parse(cluster);
            if (entries.Count == 0) continue;

            _visitedDirectories.Add(c);
            found++;

            foreach (var entry in entries)
            {
                _entriesExamined++;
                if (entry.IsDirectory) continue;
                if (!includeExisting && !entry.IsDeleted) continue;

                files.Add(Materialize(entry, "?", DiscoverySource.MftOrphan));
            }
        }

        if (found > 0)
            _warnings.Add($"הסריקה העמוקה איתרה {found:N0} שרידי תיקיות שאינם מקושרים עוד לעץ התיקיות.");
    }

    // ------------------------------------------------------------ המרה

    /// <summary>מאיפה ידוע מיקום התוכן — קובע כמה אפשר לסמוך עליו.</summary>
    private enum Placement
    {
        /// <summary>הרשומה מציינת שהקובץ רציף — המיקום ודאי.</summary>
        Contiguous,

        /// <summary>קובץ קיים — שרשרת ה-FAT שלו בתוקף.</summary>
        LiveChain,

        /// <summary>קובץ שנמחק, ושרשרת ה-FAT שלו שרדה שלמה.</summary>
        SurvivingChain,

        /// <summary>קובץ שנמחק, שלא היה רציף, ושרשרתו אינה שלמה — הרצף הוא הנחה בלבד.</summary>
        AssumedContiguous,
    }

    /// <summary>
    /// בניית מקטעי הקובץ. קובץ רציף — מקטע אחד לפי הרשומה. קובץ שנמחק ולא היה
    /// רציף — לפי שרשרת ה-FAT, אם שרדה; Windows אינו מאפס אותה במחיקה, ולכן
    /// גם קובץ מפוצל חוזר מדויק. רק כשהשרשרת אינה שלמה עוד — הנחת רצף.
    /// </summary>
    private (List<DataExtent> Extents, Placement Placement) ExtentsFor(ExFatEntry entry, long size)
    {
        if (entry.NoFatChain)
            return (_volume.ContiguousExtent(entry.FirstCluster, size), Placement.Contiguous);

        if (!entry.IsDeleted)
            return (_volume.FollowChain(entry.FirstCluster), Placement.LiveChain);

        var chain = _volume.IntactChain(entry.FirstCluster, size);
        return chain is not null
            ? (chain, Placement.SurvivingChain)
            : (_volume.ContiguousExtent(entry.FirstCluster, size), Placement.AssumedContiguous);
    }

    private RecoveredFile Materialize(ExFatEntry entry, string path, DiscoverySource source)
    {
        var (extents, placement) = ExtentsFor(entry, entry.Size);
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
        };

        AssessQuality(file, entry, placement);
        return file;
    }

    private void AssessQuality(RecoveredFile file, ExFatEntry entry, Placement placement)
    {
        if (file.Size == 0)
        {
            file.Quality = RecoveryQuality.Excellent;
            file.QualityReason = "הקובץ ריק ואין לו תוכן לשחזר.";
            return;
        }

        if (entry.FirstCluster == 0 || file.Extents.Count == 0)
        {
            file.Quality = RecoveryQuality.Unrecoverable;
            file.QualityReason = "רשומת הקובץ אינה מציינת היכן בכונן מתחיל התוכן שלו.";
            return;
        }

        if (!file.IsDeleted)
        {
            file.Quality = RecoveryQuality.Excellent;
            file.Content = ContentCheck.HasData;
            file.QualityReason = "הקובץ קיים במערכת הקבצים ומיקומו ידוע במלואו.";
            return;
        }

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

        // הדירוג תלוי גם בשאלה מאיפה ידוע המיקום: רשומה שמציינת רצף, או שרשרת
        // FAT שלמה, הן נתון; הנחת רצף לקובץ שלא היה רציף היא ניחוש.
        string basis = placement switch
        {
            Placement.Contiguous => "הרשומה מציינת שהקובץ היה רציף על הכונן, ולכן מיקומו ידוע בוודאות.",
            Placement.SurvivingChain when file.Extents.Count > 1 =>
                $"הקובץ היה מפוצל ל-{file.Extents.Count} חלקים, והמפה של חלקיו שרדה במלואה — מיקום כל חלק ידוע.",
            Placement.SurvivingChain => "המפה של חלקי הקובץ שרדה במלואה, ולכן מיקומו ידוע.",
            _ => "הקובץ לא נשמר ברצף, והמפה של חלקיו אינה שלמה עוד. השחזור מניח רצף — " +
                 "ייתכן שחלק מהתוכן יהיה של קובץ אחר.",
        };

        double ratio = total == 0 ? 0 : (double)taken / total;

        (file.Quality, file.QualityReason) = ratio switch
        {
            0 => (RecoveryQuality.Excellent, $"נמצאו נתונים, וכל המקום שהקובץ תפס בכונן עדיין פנוי. {basis}"),
            < 0.15 => (RecoveryQuality.Good, $"נמצאו נתונים. כ-{ratio:P0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים. {basis}"),
            < 0.85 => (RecoveryQuality.Poor, $"כ-{ratio:P0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים. הקובץ ישוחזר פגום."),
            _ => (RecoveryQuality.Unrecoverable, "כמעט כל המקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים."),
        };

        // הנחת רצף לא תקבל יותר מ"חלש": אשכולות פנויים אינם מוכיחים שהם של הקובץ הזה.
        if (placement == Placement.AssumedContiguous && file.Quality < RecoveryQuality.Poor)
        {
            file.Quality = RecoveryQuality.Poor;
            file.QualityReason = $"נמצאו נתונים, אבל {basis}";
        }
    }

    private ContentCheck VerifyContent(RecoveredFile file)
    {
        if (_verifyBudget <= 0 || file.Size <= 0) return ContentCheck.NotChecked;
        _verifyBudget--;

        var stream = new ClusterStream(_volume, file.Extents, file.Size, 0);
        return stream.SampleContent();
    }
}
