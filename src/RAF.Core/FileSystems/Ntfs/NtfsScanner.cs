using System.Buffers.Binary;
using System.Diagnostics;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.Ntfs;

/// <summary>
/// סורק NTFS. מאתר קבצים שנמחקו דרך טבלת ה-MFT,
/// ובסריקה עמוקה גם דרך רשומות יתומות שנותרו על המחיצה.
/// </summary>
public sealed class NtfsScanner
{
    /// <summary>רשומת ביניים המשמשת לשחזור עץ התיקיות.</summary>
    private readonly record struct DirEntry(string Name, long Parent, ushort ParentSequence, ushort Sequence);

    private readonly Dictionary<long, DirEntry> _directory = new();
    private readonly Dictionary<long, string> _pathCache = new();
    private readonly List<string> _warnings = new();

    private NtfsVolume _volume = null!;
    private long _recordsExamined;
    private long _bytesRead;

    /// <summary>מצב TRIM של הכונן — משפיע ישירות על משמעות הדירוג.</summary>
    private TrimState _trim;

    /// <summary>
    /// תקציב אימותי תוכן. כל אימות הוא קריאה אקראית קצרה מהדיסק,
    /// ובמחיצה עם מאות אלפי קבצים מחוקים יש להגביל את מספרם.
    /// </summary>
    private int _verifyBudget = 100_000;
    private long _verifiedEmpty;

    /// <summary>מספר רשומת ‎$UsnJrnl, אם אותרה במעבר על ה-MFT.</summary>
    private long _usnRecord = -1;

    /// <summary>
    /// צירופי תיקייה ושם שכבר נאספו, למניעת כפילויות בין המקורות.
    /// היומנים מתעדים את אותם קבצים פעמים רבות.
    /// </summary>
    private readonly HashSet<(long Parent, string Name)> _knownNames = new();

    /// <summary>תקרת רשומות שייווצרו מתוך היומנים, כדי שלא יציפו את התוצאות.</summary>
    private int _journalBudget = 50_000;

    /// <summary>
    /// סריקת מחיצת NTFS.
    /// </summary>
    /// <param name="diskNumber">מספר הדיסק הפיזי.</param>
    /// <param name="partitionOffset">היסט המחיצה מתחילת הדיסק.</param>
    /// <param name="partitionSize">גודל המחיצה בבתים.</param>
    /// <param name="sectorSize">גודל סקטור לוגי.</param>
    /// <param name="mode">סוג הסריקה שנבחר.</param>
    /// <param name="includeExisting">האם לכלול גם קבצים קיימים ולא רק מחוקים.</param>
    public static Task<ScanResult> ScanAsync(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, TrimState trim,
        IProgress<ScanProgress>? progress, CancellationToken token)
        => Task.Run(() => new NtfsScanner().Run(
            diskNumber, partitionOffset, partitionSize, sectorSize,
            mode, includeExisting, trim, progress, token), token);

    private ScanResult Run(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, TrimState trim,
        IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        _trim = trim;

        // סריקה עמוקה עוברת על כל המחיצה ברצף; סריקה מהירה ניגשת רק לאזור ה-MFT.
        bool sequential = mode != ScanMode.Quick;

        using var reader = VolumeReader.TryOpen(diskNumber, partitionOffset, partitionSize, sectorSize, sequential)
            ?? throw new IOException(
                "לא ניתן לפתוח את הדיסק לקריאה. ודאו שהתוכנה פועלת בהרשאות מנהל.");

        using var volume = NtfsVolume.Open(reader)
            ?? throw new InvalidDataException(
                "המחיצה אינה NTFS תקין, או שתחילת המחיצה (מגזר האתחול) פגומה.");

        _volume = volume;
        volume.LoadClusterBitmap();

        var files = new List<RecoveredFile>();
        var seen = new HashSet<long>();

        ScanMftTable(files, seen, includeExisting, progress, clock, token);

        if (mode is ScanMode.Deep or ScanMode.Advanced && !token.IsCancellationRequested)
        {
            int beforeOrphans = files.Count;
            long bytesBefore = _bytesRead;

            ScanOrphanRecords(files, seen, includeExisting, progress, clock, token);

            // דיווח על התמורה בפועל: הסריקה העמוקה קוראת את כל המחיצה,
            // ולעיתים התוספת מזערית. המשתמש זכאי לדעת זאת.
            int gained = files.Count - beforeOrphans;
            double gb = (_bytesRead - bytesBefore) / 1024.0 / 1024 / 1024;
            _warnings.Add(
                $"הסריקה העמוקה קראה {gb:F1} GB מהמחיצה ואיתרה {gained:N0} קבצים נוספים " +
                "שרשומתם כבר אינה בטבלת הקבצים (MFT).");
        }

        // היומנים נסרקים אחרונים: הם מאתרים קבצים שרשומת ה-MFT שלהם כבר
        // נדרסה, ובנוסף מספקים שמות תיקיות שמשפרים את שחזור הנתיבים.
        if (mode is ScanMode.Deep or ScanMode.Advanced && !token.IsCancellationRequested)
        {
            ScanUsnJournal(files, seen, progress, clock, token);
            ScanLogFile(files, progress, clock, token);
        }

        // שחזור הנתיבים מתבצע רק לאחר שכל הרשומות נאספו,
        // כדי שתיקיות שנמצאו מאוחר בסריקה ישמשו גם לקבצים שנמצאו מוקדם.
        ResolvePaths(files, progress, clock, token);

        if (_verifiedEmpty > 0)
        {
            _warnings.Add(
                $"{_verifiedEmpty:N0} קבצים נמצאו ברשומות המטא-דאטה אך תוכנם כבר אינו קיים על הכונן. " +
                (_trim == TrimState.Enabled
                    ? "הכונן הזה מוחק מעצמו את התוכן של קבצים שנמחקו (TRIM). "
                    : "המקום שלהם בכונן נדרס או אופס. ") +
                "קבצים אלה סומנו כלא ניתנים לשחזור ולא יוצעו לשחזור.");
        }

        return new ScanResult
        {
            Files = files,
            Mode = mode,
            Duration = clock.Elapsed,
            Cancelled = token.IsCancellationRequested,
            FileSystem = "NTFS",
            RecordsExamined = _recordsExamined,
            BytesRead = _bytesRead,
            Warnings = _warnings,
        };
    }

    // ------------------------------------------------------- מעבר על ה-MFT

    /// <summary>מעבר סדרתי על כל רשומות ה-MFT הפעיל.</summary>
    private void ScanMftTable(
        List<RecoveredFile> files, HashSet<long> seen, bool includeExisting,
        IProgress<ScanProgress>? progress, Stopwatch clock, CancellationToken token)
    {
        long total = _volume.RecordCount;
        int recordSize = _volume.Boot.MftRecordSize;

        // קריאה באצוות במקום רשומה-רשומה, כדי לצמצם פניות לדיסק.
        const int batchRecords = 256;
        long reportEvery = Math.Max(1, total / 200);

        for (long index = 0; index < total; index += batchRecords)
        {
            if (token.IsCancellationRequested) return;

            int count = (int)Math.Min(batchRecords, total - index);
            byte[] batch = _volume.ReadExtentsForMft(index * recordSize, count * recordSize);
            if (batch.Length == 0) break;

            _bytesRead += batch.Length;
            int usable = batch.Length / recordSize;

            for (int i = 0; i < usable; i++)
            {
                long recordNumber = index + i;

                byte[] slice = new byte[recordSize];
                Buffer.BlockCopy(batch, i * recordSize, slice, 0, recordSize);

                var record = MftRecord.Parse(slice, _volume.Boot.BytesPerSector, recordNumber);
                _recordsExamined++;
                if (record is null) continue;

                Index(record);
                NoteSystemRecord(record);

                if (!ShouldCollect(record, includeExisting)) continue;
                if (!seen.Add(record.RecordNumber)) continue;

                var file = Materialize(record, DiscoverySource.MftActive);
                if (file is not null)
                {
                    files.Add(file);
                    _knownNames.Add((file.ParentId, file.Name));
                }
            }

            if (index % reportEvery < batchRecords)
            {
                progress?.Report(new ScanProgress
                {
                    Stage = "קורא את טבלת הקבצים",
                    Percent = total > 0 ? index * 100.0 / total : null,
                    FilesFound = files.Count,
                    BytesProcessed = _bytesRead,
                    Elapsed = clock.Elapsed,
                    BytesPerSecond = clock.Elapsed.TotalSeconds > 0 ? _bytesRead / clock.Elapsed.TotalSeconds : 0,
                });
            }
        }
    }

    // --------------------------------------------- סריקת רשומות יתומות

    /// <summary>
    /// סריקה גולמית של המחיצה כולה לאיתור רשומות FILE שאינן חלק מה-MFT הנוכחי.
    /// אלו רשומות של קבצים שנמחקו ושה-MFT כבר לא מצביע עליהן.
    /// </summary>
    private void ScanOrphanRecords(
        List<RecoveredFile> files, HashSet<long> seen, bool includeExisting,
        IProgress<ScanProgress>? progress, Stopwatch clock, CancellationToken token)
    {
        long volumeSize = _volume.VolumeLength > 0
            ? _volume.VolumeLength
            : _volume.Boot.TotalSectors * _volume.Boot.BytesPerSector;

        int sectorSize = _volume.Boot.BytesPerSector;
        int recordSize = _volume.Boot.MftRecordSize;

        const int blockSize = 4 * 1024 * 1024;
        byte[] block = new byte[blockSize];

        long lastReport = 0;
        long reportEvery = Math.Max(blockSize, volumeSize / 200);
        var map = new SectorMap(volumeSize);

        for (long offset = 0; offset < volumeSize; offset += blockSize)
        {
            if (token.IsCancellationRequested) return;

            int want = (int)Math.Min(blockSize, volumeSize - offset);
            int read = _volume.ReadRaw(offset, block.AsSpan(0, want));
            map.Cursor = offset;

            if (read <= 0)
            {
                // סקטור פגום או אזור בלתי קריא — מדלגים וממשיכים.
                map.Add(offset, want, SectorState.Bad);
                continue;
            }
            map.Add(offset, read, SectorState.Read);

            _bytesRead += read;

            // רשומות MFT מיושרות לגבול סקטור.
            for (int at = 0; at + 8 <= read; at += sectorSize)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(at)) != MftRecord.SignatureFile)
                    continue;

                if (at + recordSize > read) break;

                byte[] slice = new byte[recordSize];
                Buffer.BlockCopy(block, at, slice, 0, recordSize);

                var record = MftRecord.Parse(slice, sectorSize);
                _recordsExamined++;
                if (record is null) continue;

                Index(record);

                if (!ShouldCollect(record, includeExisting)) continue;
                if (!seen.Add(record.RecordNumber)) continue;

                var file = Materialize(record, DiscoverySource.MftOrphan);
                if (file is not null)
                {
                    files.Add(file);
                    map.Mark(offset + at, SectorState.Found);
                }
            }

            if (offset - lastReport >= reportEvery)
            {
                lastReport = offset;
                progress?.Report(new ScanProgress
                {
                    Map = map,
                    Stage = "סורק רשומות יתומות על פני המחיצה",
                    Percent = offset * 100.0 / volumeSize,
                    FilesFound = files.Count,
                    BytesProcessed = offset,
                    BytesTotal = volumeSize,
                    Elapsed = clock.Elapsed,
                    BytesPerSecond = clock.Elapsed.TotalSeconds > 0 ? _bytesRead / clock.Elapsed.TotalSeconds : 0,
                });
            }
        }
    }

    // ------------------------------------------------------------ המרה

    private static bool ShouldCollect(MftRecord record, bool includeExisting)
    {
        // רשומות הרחבה אינן קבצים בפני עצמן; הן שייכות לרשומת הבסיס.
        if (record.BaseRecord != 0) return false;

        // קובצי המערכת של NTFS אינם מעניינים את המשתמש.
        if (record.RecordNumber < 16) return false;

        if (record.FileNames.Count == 0) return false;

        return includeExisting || !record.InUse;
    }

    /// <summary>רישום הרשומה במפת התיקיות, לשימוש בשחזור הנתיבים.</summary>
    private void Index(MftRecord record)
    {
        var name = record.PreferredName();
        if (name is null) return;

        // רשומה שנמצאה פעמיים: מעדיפים את הגרסה בעלת מונה הגרסה הגבוה יותר.
        if (_directory.TryGetValue(record.RecordNumber, out var existing) &&
            existing.Sequence >= record.SequenceNumber)
            return;

        _directory[record.RecordNumber] = new DirEntry(
            name.Value.Name, name.Value.ParentRecord, name.Value.ParentSequence, record.SequenceNumber);
    }

    private RecoveredFile? Materialize(MftRecord record, DiscoverySource source)
    {
        var name = record.PreferredName();
        if (name is null) return null;

        var data = record.PrimaryData();
        long size = data?.RealSize ?? name.Value.RealSize;

        var extents = data is { IsNonResident: true } ? data.Extents : new List<DataExtent>();
        byte[]? resident = data is { IsNonResident: false } ? data.ResidentValue : null;

        var file = new RecoveredFile
        {
            Id = record.RecordNumber,
            ParentId = name.Value.ParentRecord,
            Name = name.Value.Name,
            Size = record.IsDirectory ? 0 : size,
            IsDirectory = record.IsDirectory,
            IsDeleted = !record.InUse,
            Created = record.Created ?? name.Value.Created,
            Modified = record.Modified ?? name.Value.Modified,
            Accessed = record.Accessed ?? name.Value.Accessed,
            Source = source,
            Extents = extents,
            ResidentData = resident,
            IsCompressed = data?.IsCompressed ?? false,
            CompressionUnitClusters = data?.CompressionUnitClusters ?? 0,
        };

        AssessQuality(file);
        return file;
    }

    /// <summary>
    /// קביעת דירוג השחזור.
    ///
    /// הדירוג נקבע בשני שלבים: תחילה נדגם התוכן בפועל מהדיסק, ורק אם נמצאו
    /// שם נתונים אמיתיים נבדק כמה מהאשכולות הוקצו מחדש. ללא הדגימה הזו,
    /// קובץ שנמחק מכונן SSD עם TRIM היה מקבל דירוג מושלם — רשומת המטא-דאטה
    /// שלו שלמה — בעוד שהבקר כבר מחק את הנתונים והקריאה מחזירה אפסים בלבד.
    /// </summary>
    private void AssessQuality(RecoveredFile file)
    {
        if (file.IsDirectory)
        {
            file.Quality = RecoveryQuality.Excellent;
            return;
        }

        // תוכן רזידנטי שמור בתוך רשומת ה-MFT עצמה, שאותה כבר קראנו בהצלחה.
        if (file.ResidentData is not null)
        {
            file.Quality = RecoveryQuality.Excellent;
            file.Content = ContentCheck.HasData;
            file.QualityReason = "תוכן הקובץ שמור בתוך רשומת המטא-דאטה ונקרא במלואו.";
            return;
        }

        if (file.Extents.Count == 0)
        {
            file.Quality = RecoveryQuality.Unrecoverable;
            file.QualityReason = "לא נמצא מידע על מיקום תוכן הקובץ על הדיסק.";
            return;
        }

        // קובץ קיים אינו זקוק להערכה — תוכנו שלם מעצם היותו בשימוש.
        if (!file.IsDeleted)
        {
            file.Quality = RecoveryQuality.Excellent;
            file.Content = ContentCheck.HasData;
            file.QualityReason = "הקובץ קיים במערכת הקבצים ותוכנו שלם.";
            return;
        }

        // --- שלב א: האם הנתונים בכלל עדיין שם? ---
        file.Content = VerifyContent(file);

        switch (file.Content)
        {
            case ContentCheck.Empty:
                _verifiedEmpty++;
                file.Quality = RecoveryQuality.Unrecoverable;
                file.QualityReason = _trim == TrimState.Enabled
                    ? "רשומת הקובץ שרדה, אך אזור הנתונים שלו מכיל אפסים בלבד. " +
                      "הכונן הזה מוחק מעצמו את התוכן של קבצים שנמחקו (TRIM), והתוכן כבר אינו קיים. לא ניתן לשחזר."
                    : "אזור הנתונים של הקובץ מכיל אפסים בלבד — התוכן נמחק או אופס. לא ניתן לשחזר.";
                return;

            case ContentCheck.Unreadable:
                file.Quality = RecoveryQuality.Poor;
                file.QualityReason = "לא ניתן היה לקרוא את אזור הנתונים של הקובץ לצורך בדיקה.";
                return;

            case ContentCheck.NotChecked:
                file.Quality = RecoveryQuality.Good;
                file.QualityReason = "תוכן הקובץ לא אומת מול הדיסק. ייתכן שהשחזור יניב קובץ ריק.";
                return;
        }

        // --- שלב ב: נמצאו נתונים. כמה מהאשכולות כבר הוקצו מחדש? ---
        long total = 0, taken = 0;

        foreach (var extent in file.Extents)
        {
            if (extent.IsSparse) continue;

            // דגימה של עד 64 אשכולות לכל מקטע — בדיקה מלאה איטית מדי
            // בקבצים גדולים, והדגימה מספיקה להערכה.
            long step = Math.Max(1, extent.ClusterCount / 64);

            for (long i = 0; i < extent.ClusterCount; i += step)
            {
                bool? allocated = _volume.IsClusterAllocated(extent.StartCluster + i);
                if (allocated is null)
                {
                    file.Quality = RecoveryQuality.Good;
                    file.QualityReason = "נמצאו נתונים בקובץ. לא ניתן היה לבדוק אם קבצים אחרים נכתבו במקומו.";
                    return;
                }

                total++;
                if (allocated.Value) taken++;
            }
        }

        if (total == 0)
        {
            file.Quality = RecoveryQuality.Good;
            file.QualityReason = "נמצאו נתונים בקובץ.";
            return;
        }

        double ratio = (double)taken / total;

        (file.Quality, file.QualityReason) = ratio switch
        {
            0 => (RecoveryQuality.Excellent,
                  "נמצאו נתונים בקובץ, וכל המקום שהוא תפס בכונן עדיין פנוי."),
            < 0.15 => (RecoveryQuality.Good,
                  $"נמצאו נתונים בקובץ. כ-{ratio:P0} מהמקום שהוא תפס בכונן כבר תפוס על ידי קבצים אחרים."),
            < 0.85 => (RecoveryQuality.Poor,
                  $"כ-{ratio:P0} מהמקום שהקובץ תפס בכונן כבר תפוס על ידי קבצים אחרים. הקובץ ישוחזר פגום."),
            _ => (RecoveryQuality.Unrecoverable,
                  "כמעט כל המקום שהקובץ תפס בכונן נדרס על ידי קבצים אחרים."),
        };
    }

    /// <summary>
    /// דגימת התוכן בפועל מהדיסק, בכפוף לתקציב הקריאות.
    /// זו הבדיקה שמבדילה בין מטא-דאטה ששרדה לבין נתונים שקיימים.
    /// </summary>
    private ContentCheck VerifyContent(RecoveredFile file)
    {
        if (_verifyBudget <= 0) return ContentCheck.NotChecked;
        if (file.Size <= 0) return ContentCheck.NotChecked;

        _verifyBudget--;

        var stream = new ClusterStream(
            _volume, file.Extents, file.Size,
            file.IsCompressed ? file.CompressionUnitClusters : 0);

        return stream.SampleContent();
    }

    // ------------------------------------------------------------ יומנים

    /// <summary>איתור רשומות מערכת שיידרשו בהמשך הסריקה.</summary>
    private void NoteSystemRecord(MftRecord record)
    {
        if (_usnRecord >= 0) return;

        var name = record.PreferredName();
        if (name is null) return;

        if (name.Value.Name == "$UsnJrnl" && name.Value.ParentRecord == UsnJournalReader.ExtendRecord)
            _usnRecord = record.RecordNumber;
    }

    /// <summary>מזהה ייחודי לרשומות שמקורן ביומנים, כדי שלא יתנגשו במספרי MFT.</summary>
    private long _syntheticId = -1000;

    /// <summary>
    /// סריקת יומן השינויים. היומן מתעד שמות של קבצים שנמחקו, גם כאשר
    /// רשומת ה-MFT שלהם כבר הוקצתה מחדש ואין ממנה זכר.
    /// </summary>
    private void ScanUsnJournal(
        List<RecoveredFile> files, HashSet<long> seen,
        IProgress<ScanProgress>? progress, Stopwatch clock, CancellationToken token)
    {
        if (_usnRecord < 0)
        {
            _warnings.Add("יומן השינויים ($UsnJrnl) אינו קיים במחיצה זו, ולכן לא נסרק.");
            return;
        }

        var record = _volume.ReadParsedRecord(_usnRecord);
        var journal = record?.AlternateStreams()
            .FirstOrDefault(a => a.Name == UsnJournalReader.JournalStreamName);

        if (journal is null)
        {
            _warnings.Add("יומן השינויים אותר אך זרם הנתונים שלו אינו קריא.");
            return;
        }

        progress?.Report(new ScanProgress
        {
            Stage = "קורא את יומן השינויים של מערכת הקבצים",
            Percent = null,
            FilesFound = files.Count,
            Elapsed = clock.Elapsed,
        });

        int added = 0;
        long lastReport = 0;

        long total = UsnJournalReader.Read(_volume, journal,
            entry =>
            {
                // כל רשומה ביומן היא גם עדות לשם של תיקייה או קובץ,
                // ולכן היא משפרת את שחזור עץ הנתיבים.
                RegisterFromJournal(entry.FileRecord, entry.FileSequence,
                    entry.FileName, entry.ParentRecord, entry.ParentSequence);

                if (!entry.IsDelete || entry.IsDirectory) return;
                if (_journalBudget <= 0) return;
                if (!_knownNames.Add((entry.ParentRecord, entry.FileName))) return;

                _journalBudget--;
                added++;

                files.Add(new RecoveredFile
                {
                    Id = _syntheticId--,
                    ParentId = entry.ParentRecord,
                    Name = entry.FileName,
                    Size = 0,
                    IsDeleted = true,
                    Modified = entry.Timestamp,
                    Source = DiscoverySource.UsnJournal,
                    Quality = RecoveryQuality.Unrecoverable,
                    QualityReason =
                        "הקובץ אותר ביומן השינויים של מערכת הקבצים: הוא היה קיים ונמחק. " +
                        "היומן שומר את שמו ואת תיקיית האב שלו, אך אינו שומר היכן תוכנו ישב על הדיסק, " +
                        "ולכן לא ניתן לשחזר אותו. זוהי עדות לקיומו בלבד.",
                });
            },
            bytes =>
            {
                if (bytes - lastReport < 32 * 1024 * 1024) return;
                lastReport = bytes;

                progress?.Report(new ScanProgress
                {
                    Stage = "קורא את יומן השינויים של מערכת הקבצים",
                    Percent = null,
                    FilesFound = files.Count,
                    BytesProcessed = bytes,
                    Elapsed = clock.Elapsed,
                });
            },
            token);

        if (total > 0)
            _warnings.Add(
                $"יומן השינויים נסרק: {total:N0} רשומות נבדקו, ומתוכן {added:N0} קבצים שנמחקו " +
                "ואינם קיימים עוד ברשומות המטא-דאטה. שמותיהם ידועים, אך תוכנם אינו ניתן לאיתור.");
    }

    /// <summary>
    /// סריקת ‎$LogFile. יומן הטרנזקציות שומר שרידי רשומות מטא-דאטה,
    /// וניתן לחלץ מהם שמות קבצים שכבר נעלמו לחלוטין מה-MFT.
    /// </summary>
    private void ScanLogFile(
        List<RecoveredFile> files,
        IProgress<ScanProgress>? progress, Stopwatch clock, CancellationToken token)
    {
        var record = _volume.ReadParsedRecord(LogFileReader.LogFileRecord);
        var data = record?.PrimaryData();

        if (data is null || !data.IsNonResident)
        {
            _warnings.Add("יומן הטרנזקציות ($LogFile) אינו קריא במחיצה זו.");
            return;
        }

        progress?.Report(new ScanProgress
        {
            Stage = "סורק את יומן הטרנזקציות",
            Percent = null,
            FilesFound = files.Count,
            Elapsed = clock.Elapsed,
        });

        int added = 0;
        long lastReport = 0;

        long total = LogFileReader.Read(_volume, data,
            entry =>
            {
                if (entry.IsDirectory) return;
                if (_journalBudget <= 0) return;
                if (!_knownNames.Add((entry.ParentRecord, entry.Name))) return;

                _journalBudget--;
                added++;

                files.Add(new RecoveredFile
                {
                    Id = _syntheticId--,
                    ParentId = entry.ParentRecord,
                    Name = entry.Name,
                    Size = entry.RealSize,
                    IsDeleted = true,
                    Created = entry.Created,
                    Modified = entry.Modified,
                    Source = DiscoverySource.LogFile,
                    Quality = RecoveryQuality.Unrecoverable,
                    QualityReason =
                        "הקובץ אותר בשריד רשומה ביומן הטרנזקציות. ידועים שמו, גודלו ותאריכיו, " +
                        "אך לא נשמר בו מיקום תוכנו על הדיסק, ולכן לא ניתן לשחזר אותו. " +
                        "זוהי עדות לקיומו בלבד.",
                });
            },
            bytes =>
            {
                if (bytes - lastReport < 8 * 1024 * 1024) return;
                lastReport = bytes;

                progress?.Report(new ScanProgress
                {
                    Stage = "סורק את יומן הטרנזקציות",
                    Percent = null,
                    FilesFound = files.Count,
                    BytesProcessed = bytes,
                    Elapsed = clock.Elapsed,
                });
            },
            token);

        if (added > 0)
            _warnings.Add(
                $"יומן הטרנזקציות נסרק: {added:N0} שמות קבצים נוספים חולצו משרידי רשומות. " +
                "גם עבורם ידוע השם בלבד, ללא מיקום התוכן.");
        else if (total == 0)
            _warnings.Add("לא אותרו שרידי רשומות ביומן הטרנזקציות.");
    }

    /// <summary>
    /// רישום שם מתוך יומן למפת התיקיות, לשיפור שחזור הנתיבים.
    /// רשומה שכבר נאספה מה-MFT גוברת — היא אמינה יותר.
    /// </summary>
    private void RegisterFromJournal(
        long record, ushort sequence, string name, long parent, ushort parentSequence)
    {
        if (record <= 0 || name.Length == 0) return;

        if (_directory.TryGetValue(record, out var existing) && existing.Sequence >= sequence)
            return;

        _directory[record] = new DirEntry(name, parent, parentSequence, sequence);
    }

    // ------------------------------------------------------ שחזור נתיבים

    /// <summary>בניית הנתיב המלא של כל קובץ מתוך שרשרת רשומות ההורה.</summary>
    private void ResolvePaths(
        List<RecoveredFile> files, IProgress<ScanProgress>? progress,
        Stopwatch clock, CancellationToken token)
    {
        progress?.Report(new ScanProgress
        {
            Stage = "משחזר את עץ התיקיות",
            Percent = null,
            FilesFound = files.Count,
            Elapsed = clock.Elapsed,
        });

        foreach (var file in files)
        {
            if (token.IsCancellationRequested) return;
            file.Path = BuildPath(file.ParentId);
        }
    }

    /// <summary>
    /// בניית נתיב תיקייה מתוך מספר רשומה, עם זיכרון תוצאות ועם הגנה
    /// מפני שרשרת מעגלית שנוצרת ברשומות פגומות.
    /// </summary>
    private string BuildPath(long recordNumber)
    {
        if (recordNumber < 0) return "?";
        if (recordNumber == NtfsVolume.RecordRoot) return "";
        if (_pathCache.TryGetValue(recordNumber, out string? cached)) return cached;

        var parts = new List<string>();
        var visited = new HashSet<long>();
        long current = recordNumber;
        bool uncertain = false;

        while (current != NtfsVolume.RecordRoot && current >= 0)
        {
            if (!visited.Add(current)) { uncertain = true; break; }
            if (parts.Count > 128) { uncertain = true; break; }

            if (!_directory.TryGetValue(current, out var entry))
            {
                // התיקייה עצמה נדרסה ואינה ניתנת לשחזור.
                uncertain = true;
                break;
            }

            // אי-התאמה במונה הגרסה: רשומת ההורה הוחזרה לשימוש עבור קובץ אחר,
            // ולכן הנתיב שמעליה אינו אמין.
            if (_directory.TryGetValue(entry.Parent, out var parentEntry) &&
                entry.ParentSequence != 0 && parentEntry.Sequence != entry.ParentSequence)
            {
                parts.Add(entry.Name);
                uncertain = true;
                break;
            }

            parts.Add(entry.Name);
            current = entry.Parent;
        }

        parts.Reverse();
        string path = string.Join('\\', parts);
        if (uncertain) path = string.IsNullOrEmpty(path) ? "?" : "?\\" + path;

        _pathCache[recordNumber] = path;
        return path;
    }
}
