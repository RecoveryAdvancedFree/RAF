using System.Diagnostics;
using RAF.Core.FileSystems;
using RAF.Core.Model;
using RAF.Core.Native;
using RAF.Core.Signatures;

namespace RAF.Core.Carving;

/// <summary>
/// סריקה מתקדמת — איתור קבצים לפי חתימות HEX.
///
/// הסריקה אינה נסמכת על מערכת קבצים כלל: היא עוברת על כל סקטור במחיצה
/// ומחפשת חתימות פתיחה מוכרות. לכן היא מוצאת קבצים גם לאחר פירמוט, גם
/// כשמגזר האתחול נהרס וגם כשטבלת המטא-דאטה נדרסה לחלוטין.
///
/// המחיר: אין שמות קבצים ואין נתיבי תיקייה — המידע הזה חי במטא-דאטה,
/// שאינה נקראת כאן. הקבצים מקבלים שם נגזר מסוג הקובץ וממיקומו.
/// </summary>
public sealed class FileCarver
{
    /// <summary>גודל בלוק הקריאה בסריקה הרציפה.</summary>
    private const int BlockSize = 8 * 1024 * 1024;

    /// <summary>
    /// חפיפה בין בלוקים, כדי שחתימה שנחתכה בגבול בלוק לא תלך לאיבוד.
    /// </summary>
    private const int Overlap = 4096;

    private readonly List<string> _warnings = new();

    /// <summary>המקום הפנוי במחיצה, כשסורקים רק אותו. null — סורקים הכול.</summary>
    private FreeSpaceMap? _free;

    /// <summary>
    /// אילו סוגים נכנסים לתוצאות — רעיון מהתוכנה "משיב". null — כל הסוגים. סוג שלא
    /// נבחר עדיין מזוהה ומדולג, כדי שתמונה שבתוך מסמך לא תדווח כתמונה נפרדת.
    /// </summary>
    internal Func<FileSignature, bool>? Accept { get; set; }

    /// <summary>המשך של סריקה שנעצרה: הקבצים שכבר נמצאו, ומאיפה להמשיך (Resume).</summary>
    internal ScanResult? ResumeFrom { get; set; }

    /// <summary>האפשרויות שהסריקה רצה בהן — נשמרות בנקודת ההמשך, כדי שההמשך ירוץ באותן.</summary>
    internal bool FreeSpaceOnlyOption { get; set; }
    internal List<string>? TypesOption { get; set; }

    /// <summary>היכן נעצרה הסריקה הראשית: הסקטור הבא שטרם נבדק, וגבול הקובץ האחרון.</summary>
    private long _stoppedAt = -1, _stoppedAllowed;

    /// <summary>הכונן נותק באמצע המעבר הראשי.</summary>
    private bool _disconnected;

    /// <summary>מפת הסקטורים של המעבר הראשי — להצגה בזמן הסריקה.</summary>
    private SectorMap? _map;
    private long _bytesRead;
    private long _candidates;

    /// <summary>סריקה מתקדמת של מחיצה.</summary>
    /// <param name="freeSpaceOnly">
    /// לסרוק רק את המקום שמערכת הקבצים (fileSystem) מסמנת כפנוי. כשמפת ההקצאה
    /// אינה נקראת — המחיצה נסרקת כולה, ואזהרה מסבירה זאת.
    /// </param>
    public static Task<ScanResult> ScanAsync(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        IProgress<ScanProgress>? progress, CancellationToken token,
        Action<ScanResult>? checkpoint = null,
        FileSystemKind fileSystem = FileSystemKind.Raw, bool freeSpaceOnly = false,
        Func<FileSignature, bool>? accept = null,
        ScanResult? resumeFrom = null, List<string>? types = null)
        => Task.Run(() => new FileCarver
        {
            Accept = accept, ResumeFrom = resumeFrom, FreeSpaceOnlyOption = freeSpaceOnly, TypesOption = types,
        }.Run(
            diskNumber, partitionOffset, partitionSize, sectorSize, progress, token, checkpoint,
            fileSystem, freeSpaceOnly), token);

    private ScanResult Run(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        IProgress<ScanProgress>? progress, CancellationToken token,
        Action<ScanResult>? checkpoint, FileSystemKind fileSystem, bool freeSpaceOnly)
    {
        using var reader = VolumeReader.TryOpen(
            diskNumber, partitionOffset, partitionSize, sectorSize, sequential: true)
            ?? throw new IOException(
                Native.RawDevice.OpenFailure());

        long length = partitionSize > 0 ? partitionSize : reader.Length;
        if (freeSpaceOnly) _free = ReadFreeSpace(diskNumber, partitionOffset, length, sectorSize, fileSystem, progress);

        return Sweep(RawVolume.Open(reader, sectorSize), length, sectorSize, progress, token, checkpoint);
    }

    /// <summary>מפת המקום הפנוי, בקורא נפרד — כדי שסגירתו לא תסגור את קורא הסריקה.</summary>
    private FreeSpaceMap? ReadFreeSpace(
        int diskNumber, long partitionOffset, long length, int sectorSize, FileSystemKind fileSystem,
        IProgress<ScanProgress>? progress)
    {
        progress?.Report(new ScanProgress { Stage = "קורא את מפת המקום הפנוי" });
        FreeSpaceMap? map = null;
        try
        {
            using var reader = VolumeReader.TryOpen(diskNumber, partitionOffset, length, sectorSize, sequential: false);
            if (reader is not null && VolumeScanner.Open(reader, fileSystem, sectorSize) is { } volume)
                using (volume) map = FreeSpaceMap.Build(volume, length);
        }
        catch
        {
            map = null;
        }

        _warnings.Add(map is null
            ? "מפת המקום הפנוי של מערכת הקבצים אינה נקראת, ולכן נסרקה המחיצה כולה."
            : $"נסרק רק המקום הפנוי — {Percent(map.FreeBytes, map.TotalBytes)} מהמחיצה. קבצים שנמחקו נמצאים שם; " +
              "המקום התפוס מכיל את הקבצים הקיימים. אחרי פירמוט, או כשמבנה המחיצה פגום, אפשר לסרוק את כולה.");
        return map;

        static string Percent(long part, long whole) => whole > 0 ? $"{part * 100.0 / whole:N0}%" : "0%";
    }

    /// <summary>
    /// מעבר על המחיצה ואיתור החתימות. מופרד מפתיחת ההתקן כדי שניתן
    /// יהיה להריץ אותו גם מול תמונת בדיקה.
    ///
    /// שני שלבים: מעבר רציף שמאתר קבצים (הדיסק הוא צוואר הבקבוק), ובזמן שהוא
    /// ממשיך — פענוח ה-JPEG שנמצאו על כל הליבות. בסוף מיושמות התוצאות:
    /// דירוג, חיבור מקטעים, וסריקה חוזרת של אזורים שדולגו בגלל קובץ שהתברר כפגום.
    /// </summary>
    /// <summary>סריקה של המקום הפנוי בלבד, לפי מפה נתונה — לבדיקות.</summary>
    internal FreeSpaceMap? FreeSpace { set => _free = value; }

    internal ScanResult Sweep(
        RawVolume volume, long size, int sectorSize,
        IProgress<ScanProgress>? progress, CancellationToken token,
        Action<ScanResult>? checkpoint = null)
    {
        var clock = Stopwatch.StartNew();

        // המשך של סריקה שנעצרה: הקבצים שכבר נמצאו נשמרים, והמעבר ממשיך מהסקטור שבו עצר.
        var files = ResumeFrom is { } previous ? new List<RecoveredFile>(previous.Files) : new List<RecoveredFile>();
        long start = ResumeFrom?.Resume?.Offset ?? 0;
        long allowed = ResumeFrom?.Resume?.NextAllowedStart ?? start;
        var pending = new List<PendingJpeg>();

        // בהמשך סריקה: מה שכבר נסרק, והקבצים שכבר נמצאו, מופיעים במפה מההתחלה.
        _map = new SectorMap(size);
        _map.Add(0, start, SectorState.Read);
        foreach (var f in files)
            if (f.Extents.Count > 0) _map.Mark(f.Extents[0].StartCluster * sectorSize, SectorState.Found);

        SweepRange(volume, start, size, size, sectorSize, files, pending, clock, progress, token, checkpoint,
            main: true, nextAllowedStart: allowed);
        ResolveJpegs(volume, size, sectorSize, files, pending, clock, progress, token);

        BuildWarnings(files);
        bool stopped = token.IsCancellationRequested || _disconnected;
        var resume = stopped && _stoppedAt >= 0 ? ResumeAt(_stoppedAt, _stoppedAllowed, size) : null;
        if (_disconnected)
            _warnings.Insert(0, $"הכונן נותק באמצע הסריקה, אחרי {resume?.Percent ?? 0:0.#}% מהמחיצה. " +
                                "הסריקה נשמרה — חברו את הכונן שוב והמשיכו מאותה נקודה.");
        var result = Result(files, clock, stopped, _warnings, resume);
        return _disconnected ? result.WithDisconnected() : result;
    }

    /// <summary>
    /// מעבר על טווח במחיצה. הטווח המלא בסריקה הראשית; טווח חלקי — בסריקה חוזרת
    /// של אזור שדולג, כשהתברר שהקובץ שבגללו דולג אינו נמשך לשם.
    /// </summary>
    /// <param name="main">המעבר הראשי (ולא סריקה חוזרת של אזור שדולג): מדווח, שומר נקודות ביניים ונקודת המשך.</param>
    /// <param name="nextAllowedStart">
    /// ההיסט שממנו מותר להתחיל קובץ חדש. קובץ שאורכו נקבע מדלג את הסורק קדימה,
    /// כדי שלא יזהה את תוכנו הפנימי כקבצים — גם כשהסריקה ממשיכה אחרי השהיה.
    /// </param>
    private void SweepRange(
        RawVolume volume, long from, long to, long size, int sectorSize,
        List<RecoveredFile> files, List<PendingJpeg> pending, Stopwatch clock,
        IProgress<ScanProgress>? progress, CancellationToken token, Action<ScanResult>? checkpoint,
        bool main, long nextAllowedStart)
    {
        var lastCheckpoint = TimeSpan.Zero;
        long reported = from;
        long reportEvery = Math.Max(BlockSize, size / 200);
        using var ahead = new ReadAhead(volume, size);

        for (long at = from / sectorSize * sectorSize; at < to; at += BlockSize)
        {
            if (token.IsCancellationRequested)
            {
                if (main) Stopped(at, nextAllowedStart);
                break;
            }

            // מקום תפוס: דילוג על כל הבלוקים שאין בהם אף בית פנוי — בלי לקרוא אותם.
            if (_free is not null)
            {
                long next = _free.NextFree(at);
                if (next >= to)
                {
                    if (main) _map?.Add(at, to - at, SectorState.Skipped);
                    break;
                }
                if (next >= at + BlockSize)
                {
                    if (main) _map?.Add(at, next / BlockSize * BlockSize - at, SectorState.Skipped);
                    at = next / BlockSize * BlockSize - BlockSize;          // הלולאה תוסיף בלוק
                    continue;
                }
            }

            int read = ahead.Read(at, out byte[] block);
            if (at + BlockSize < to && (_free is null || _free.NextFree(at + BlockSize) < at + 2 * BlockSize))
                ahead.Prefetch(at + BlockSize);

            if (read <= 0 && volume.Disconnected)
            {
                // הכונן נותק: כמו השהיה — הסריקה נשמרת ואפשר להמשיך כשיחובר שוב.
                if (main)
                {
                    Stopped(at, nextAllowedStart);
                    _disconnected = true;
                }
                break;
            }

            if (read <= 0)
            {
                // אזור בלתי קריא — ממשיכים הלאה במקום לעצור.
                if (main) _map?.Add(at, Math.Min(BlockSize, to - at), SectorState.Bad);
                continue;
            }

            if (main)
            {
                _bytesRead += read;
                _map!.Cursor = Math.Min(at + BlockSize, to) - 1;   // סוף הבלוק — כדי שהצבע לא יעבור את הסמן
            }

            // חתימות נבדקות בגבולות סקטור: מערכות קבצים מקצות קבצים
            // בגבולות אשכול, ואשכול תמיד מיושר לסקטור.
            int scanLimit = (int)Math.Min(Math.Min(read, BlockSize), to - at);

            for (int off = 0; off < scanLimit; off += sectorSize)
            {
                long absolute = at + off;
                if (token.IsCancellationRequested)
                {
                    if (main) Stopped(absolute, nextAllowedStart);   // הסקטור הזה עוד לא נבדק
                    break;
                }

                if (absolute < nextAllowedStart || InSkipped(absolute)) continue;
                if (_free is not null && !_free.IsFree(absolute)) continue;       // קובץ שנמחק מתחיל במקום פנוי

                var signature = FileSignatures.Identify(block.AsSpan(off, Math.Min(64, read - off)));
                if (signature is null) continue;

                _candidates++;

                var resolved = FileLength.Resolve(signature, volume, absolute, size - absolute);
                if (resolved.Bytes < 64) continue;

                bool wanted = Accept is null || Accept(signature);

                // JPEG נשלח לפענוח ברקע. עד שהתוצאה חוזרת, הקובץ נרשם לפי המבנה.
                if (wanted && signature.Extensions.FirstOrDefault() == "jpg")
                {
                    var check = StartJpegCheck(volume, absolute, resolved.Bytes);
                    if (check is not null)
                        pending.Add(new PendingJpeg(files.Count, absolute, resolved, signature, check));
                }

                if (wanted)
                {
                    files.Add(Materialize(signature, resolved, absolute, sectorSize, files.Count));
                    _map?.Mark(absolute, SectorState.Found);
                }

                // דילוג על גוף הקובץ רק כשגבולו ודאי. ניחוש אורך היה מסתיר
                // את כל מה שיושב אחרי הקובץ — ובסריקה על כונן אמיתי כך
                // נבלעו קבצים אמיתיים בתוך "קבצים" שלא היו קיימים.
                if (resolved.Confidence is LengthConfidence.Exact or LengthConfidence.Footer)
                    nextAllowedStart = absolute + resolved.Bytes;
            }

            if (!main) continue;
            _map?.Add(at, scanLimit, SectorState.Read);

            if (at - reported >= reportEvery)
            {
                reported = at;
                progress?.Report(new ScanProgress
                {
                    Stage = "סורק את המחיצה אחר חתימות קבצים",
                    Percent = at * 100.0 / size,
                    FilesFound = files.Count,
                    BytesProcessed = at,
                    BytesTotal = size,
                    Elapsed = clock.Elapsed,
                    BytesPerSecond = clock.Elapsed.TotalSeconds > 0 ? _bytesRead / clock.Elapsed.TotalSeconds : 0,
                    Map = _map,
                });
            }

            // נקודת ביניים: סריקה של שעות לא תאבד בקריסה — וממנה אפשר גם להמשיך.
            // עותק של הרשימה, כי הסריקה ממשיכה להוסיף לה. הבלוק הזה נבדק כולו.
            if (checkpoint is not null && !token.IsCancellationRequested &&
                clock.Elapsed - lastCheckpoint >= CheckpointEvery && files.Count > 0)
            {
                lastCheckpoint = clock.Elapsed;
                long done = Math.Min(at + scanLimit, to);
                checkpoint(Result(files.ToList(), clock, cancelled: true, new List<string>
                {
                    $"נקודת ביניים: נשמרה אחרי {done * 100.0 / size:0.#}% מהמחיצה. קבצים שאחרי נקודה זו אינם ברשימה.",
                }, ResumeAt(done, nextAllowedStart, size)));
            }
        }
    }

    /// <summary>
    /// קריאה מוקדמת: הבלוק הבא נקרא ברקע בזמן שהנוכחי נסרק. בלי זה הכונן עומד
    /// בכל זמן הבדיקה של הבלוק. על כונן USB: מהירות זהה או גבוהה יותר בכל מדידה, עם אותם קבצים.
    /// שני מאגרים מתחלפים: אחד בידי הסורק, והשני מתמלא.
    /// </summary>
    private sealed class ReadAhead(RawVolume volume, long size) : IDisposable
    {
        private byte[] _current = new byte[BlockSize + Overlap];
        private byte[] _next = new byte[BlockSize + Overlap];
        private Task<int>? _pending;
        private long _pendingAt = -1;

        private int Want(long at) => (int)Math.Min(BlockSize + Overlap, size - at);

        public int Read(long at, out byte[] block)
        {
            int read;
            if (_pending is not null && _pendingAt == at)
            {
                read = _pending.GetAwaiter().GetResult();
                (_current, _next) = (_next, _current);
            }
            else
            {
                _pending?.Wait();
                read = volume.ReadRaw(at, _current.AsSpan(0, Want(at)));
            }

            _pending = null;
            block = _current;
            return read;
        }

        public void Prefetch(long at)
        {
            byte[] target = _next;
            int want = Want(at);
            _pendingAt = at;
            // תהליכון משלו: כשפענוח ה-JPEG תופס את מאגר התהליכונים, הקריאה לא תמתין בתור.
            _pending = Task.Factory.StartNew(
                () => volume.ReadRaw(at, target.AsSpan(0, want)),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>המתנה לקריאה שעוד רצה — ההתקן נסגר מיד אחרי הסריקה.</summary>
        public void Dispose()
        {
            try { _pending?.Wait(); } catch { }
        }
    }

    /// <summary>מרווח בין נקודות ביניים בסריקה ארוכה.</summary>
    internal static TimeSpan CheckpointEvery { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>הנקודה הראשונה שבה נעצר המעבר הראשי — הסריקה ממשיכה בדיוק ממנה.</summary>
    private void Stopped(long at, long allowed)
    {
        if (_stoppedAt >= 0) return;
        _stoppedAt = at;
        _stoppedAllowed = allowed;
    }

    private ScanResume ResumeAt(long offset, long allowed, long size) => new()
    {
        Offset = offset,
        NextAllowedStart = Math.Max(allowed, offset),
        Percent = size > 0 ? offset * 100.0 / size : 0,
        FreeSpaceOnly = FreeSpaceOnlyOption,
        Types = TypesOption,
    };

    private ScanResult Result(List<RecoveredFile> files, Stopwatch clock, bool cancelled, List<string> warnings,
        ScanResume? resume = null)
        => new()
        {
            Resume = resume,
            Files = files,
            Mode = ScanMode.Advanced,
            Duration = clock.Elapsed,
            Cancelled = cancelled,
            FileSystem = "סריקת חתימות",
            RecordsExamined = _candidates,
            BytesRead = _bytesRead,
            Warnings = warnings,
        };

    // ------------------------------------------------------------ אימות JPEG

    /// <summary>מה עלה באימות JPEG: תקין, מפוצל שחובר, או משתבש בלי המשך.</summary>
    private sealed record JpegOutcome(bool Verified, JpegFragmentPair? Pair, bool Broken, long ErrorOffset, double Fraction);

    /// <summary>JPEG שנמצא, ופענוחו רץ ברקע.</summary>
    private sealed record PendingJpeg(
        int Index, long Offset, ResolvedLength Resolved, FileSignature Signature, Task<JpegCheck> Check);

    /// <summary>JPEG גדול מזה אינו מפוענח — ממילא נדיר, והזמן עדיף לסריקה.</summary>
    private const int MaxJpegCheck = 64 * 1024 * 1024;

    /// <summary>כמה רחוק אחרי המקטע הראשון מחפשים את השני.</summary>
    private const int MaxFragmentSearch = 24 * 1024 * 1024;

    /// <summary>זמן חיפוש מקטעים — לקובץ אחד ולכל הסריקה. בלי גבול, כרטיס מלא בתמונות פגומות היה נתקע.</summary>
    internal static TimeSpan FragmentBudgetPerFile { get; set; } = TimeSpan.FromSeconds(2);
    internal static TimeSpan FragmentBudgetPerScan { get; set; } = TimeSpan.FromMinutes(2);
    private readonly Stopwatch _fragmentClock = new();

    /// <summary>
    /// פענוחים שרצים במקביל. הגבלה לפי מספר הליבות — וגם בלימה טבעית: כשכולן
    /// תפוסות, הסריקה ממתינה, ואין הצטברות של עשרות קבצים בזיכרון.
    /// </summary>
    private readonly SemaphoreSlim _decodeSlots = new(Math.Max(1, Environment.ProcessorCount - 1));

    /// <summary>מקטעים שניים של קבצים מפוצלים — תוכנם אינו נסרק כקבצים חדשים.</summary>
    private readonly List<(long Start, long End)> _skipped = new();

    private bool InSkipped(long offset) => _skipped.Any(r => offset >= r.Start && offset < r.End);

    /// <summary>
    /// קריאת ה-JPEG ושליחתו לפענוח ברקע. הקריאה מהדיסק נעשית כאן, בתהליכון
    /// הסריקה — הפענוח בלבד עובר לליבות האחרות.
    /// </summary>
    private Task<JpegCheck>? StartJpegCheck(RawVolume volume, long offset, long length)
    {
        if (length > MaxJpegCheck) return null;

        byte[] data = new byte[length];
        if (volume.ReadRaw(offset, data) != length) return null;

        _decodeSlots.Wait();
        return Task.Run(() =>
        {
            try { return JpegDecoder.Check(JpegBytes.Of(data)); }
            finally { _decodeSlots.Release(); }
        });
    }

    /// <summary>
    /// יישום תוצאות הפענוח. JPEG שנגמר בחתימת סיום נראה שלם — אבל אם היה מפוצל,
    /// מחציתו נתונים זרים. כאן מחפשים את המקטע השני, ובכל מקרה של קובץ פגום
    /// סורקים מחדש את האזור שדולג בגללו: שם עשויים לשבת קבצים אחרים.
    /// </summary>
    private void ResolveJpegs(
        RawVolume volume, long size, int sectorSize, List<RecoveredFile> files, List<PendingJpeg> pending,
        Stopwatch clock, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        // התור גדל בזמן העבודה: סריקה חוזרת עשויה למצוא JPEG נוספים.
        for (int i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            var check = p.Check.GetAwaiter().GetResult();
            if (token.IsCancellationRequested) continue;

            progress?.Report(new ScanProgress
            {
                Stage = "מאמת את התמונות שנמצאו",
                Percent = 100.0 * (i + 1) / pending.Count,
                FilesFound = files.Count,
                BytesProcessed = size,
                BytesTotal = size,
                Elapsed = clock.Elapsed,
            });

            if (check.Verdict == JpegVerdict.Unsupported) continue;

            var outcome = check.Verdict == JpegVerdict.Complete
                ? new JpegOutcome(true, null, false, 0, 1)
                : SearchFragments(volume, p.Offset, size - p.Offset, check, sectorSize);

            files[p.Index] = Materialize(p.Signature, p.Resolved, p.Offset, sectorSize, p.Index, outcome);
            if (outcome.Verified) continue;

            // הסריקה הראשית דילגה על כל אורך הקובץ. בפועל הקובץ נגמר מוקדם יותר —
            // בסוף המקטע הראשון, או בנקודת השיבוש — ומה שאחרי שייך לקבצים אחרים.
            bool skipped = p.Resolved.Confidence is LengthConfidence.Exact or LengthConfidence.Footer;
            if (!skipped) continue;

            long keep = outcome.Pair is { } pair
                ? pair.Split
                : Math.Max(sectorSize, outcome.ErrorOffset / sectorSize * sectorSize);

            if (outcome.Pair is { } fragments)
                _skipped.Add((p.Offset + fragments.Split + fragments.Gap, p.Offset + fragments.Gap + fragments.Length));

            long rescanFrom = p.Offset + keep, rescanTo = p.Offset + p.Resolved.Bytes;
            if (rescanTo > rescanFrom)
                SweepRange(volume, rescanFrom, rescanTo, size, sectorSize, files, pending, clock, null, token, null,
                    main: false, nextAllowedStart: rescanFrom);
        }
    }

    /// <summary>חיפוש המקטע השני של JPEG שמשתבש. בלי המשך — הקובץ "משתבש".</summary>
    private JpegOutcome SearchFragments(RawVolume volume, long offset, long available, JpegCheck check, int sectorSize)
    {
        if (_fragmentClock.Elapsed < FragmentBudgetPerScan)
        {
            long window = Math.Min(available, check.Offset + MaxFragmentSearch);
            byte[] wide = new byte[window];
            if (volume.ReadRaw(offset, wide) == window)
            {
                _fragmentClock.Start();
                var (again, decoder) = JpegDecoder.CheckWithCheckpoints(JpegBytes.Of(wide));
                var pair = JpegFragments.Find(wide, again, decoder, sectorSize, FragmentBudgetPerFile);
                _fragmentClock.Stop();

                if (pair is not null) return new JpegOutcome(false, pair, false, 0, 1);
            }
        }

        return new JpegOutcome(false, null, true, check.Offset, check.Fraction);
    }

    /// <summary>בניית רשומת קובץ מתוך חתימה שזוהתה.</summary>
    private static RecoveredFile Materialize(
        FileSignature signature, ResolvedLength resolved, long offset, int sectorSize, int index,
        JpegOutcome? jpeg = null)
    {
        string extension = signature.Extensions.Length > 0 ? signature.Extensions[0] : "bin";

        long startSector = offset / sectorSize;
        long sectors = (resolved.Bytes + sectorSize - 1) / sectorSize;

        if (jpeg?.Pair is { } pair)
        {
            long second = (offset + pair.Split + pair.Gap) / sectorSize;
            long secondSectors = (pair.Length - pair.Split + sectorSize - 1) / sectorSize;

            return new RecoveredFile
            {
                Id = index + 1,
                ParentId = -1,
                Name = $"{index + 1:D6}_{offset:X}.{extension}",
                Path = signature.Name,
                Size = pair.Length,
                IsDeleted = true,
                Source = DiscoverySource.Carving,
                Extents = new List<DataExtent>
                {
                    new(startSector, pair.Split / sectorSize, false),
                    new(second, secondSectors, false),
                },
                Quality = RecoveryQuality.Good,
                QualityReason =
                    $"הקובץ היה מפוצל לשני חלקים על הכונן. החלק השני נמצא {Kb(pair.Gap)} אחרי סוף הראשון, " +
                    "והחיבור אומת בפענוח של כל התמונה עד סופה.",
                Content = ContentCheck.HasData,
            };
        }

        if (jpeg is { Broken: true })
        {
            return new RecoveredFile
            {
                Id = index + 1,
                ParentId = -1,
                Name = $"{index + 1:D6}_{offset:X}.{extension}",
                Path = signature.Name,
                Size = resolved.Bytes,
                IsDeleted = true,
                Source = DiscoverySource.Carving,
                Extents = new List<DataExtent> { new(startSector, sectors, false) },
                Quality = RecoveryQuality.Poor,
                QualityReason =
                    $"נתוני התמונה משתבשים אחרי כ-{jpeg.Fraction:P0} ממנה. כנראה הקובץ היה מפוצל והמשכו " +
                    "לא נמצא, או שחלקו נדרס. החלק העליון של התמונה ייפתח, והשאר יוצג משובש.",
                Content = ContentCheck.HasData,
            };
        }

        string reason = resolved.Confidence switch
        {
            LengthConfidence.Exact =>
                $"הקובץ זוהה כ{signature.Name} לפי חתימתו, ומבנהו נקרא מתחילתו ועד סופו — " +
                "כלומר גם הזיהוי וגם האורך מאומתים.",
            LengthConfidence.Declared =>
                $"הקובץ זוהה כ{signature.Name} לפי חתימתו, ואורכו נקרא משדה הגודל שבכותרת. " +
                "לקובץ אמיתי האורך מדויק.",
            LengthConfidence.Footer =>
                $"הקובץ זוהה כ{signature.Name} לפי חתימתו, ואורכו נקבע לפי חתימת הסיום שנמצאה.",
            _ =>
                $"הקובץ זוהה כ{signature.Name} לפי חתימת הפתיחה בלבד. הפורמט אינו נושא את אורכו, " +
                "ולכן נלקח גודל מרבי סביר — ייתכן שהקובץ יכיל נתונים עודפים בסופו.",
        };

        if (jpeg is { Verified: true })
            reason += " נתוני התמונה עצמם פוענחו מתחילתם ועד סופם — התמונה שלמה.";

        return new RecoveredFile
        {
            Id = index + 1,
            ParentId = -1,

            // אין שם מקורי: המידע הזה חי במטא-דאטה, שאינה נקראת בסריקה זו.
            Name = $"{index + 1:D6}_{offset:X}.{extension}",
            Path = signature.Name,
            Size = resolved.Bytes,
            IsDeleted = true,
            Source = DiscoverySource.Carving,
            Extents = new List<DataExtent> { new(startSector, sectors, false) },
            Quality = resolved.Confidence == LengthConfidence.Guess
                ? RecoveryQuality.Good
                : RecoveryQuality.Excellent,
            QualityReason = reason,
            Content = ContentCheck.HasData,
        };
    }

    private static string Kb(long bytes) => RAF.Core.Imaging.DiskImager.Size(bytes);

    private void BuildWarnings(List<RecoveredFile> files)
    {
        _warnings.Add(
            "בסריקה מתקדמת אין שמות קבצים ואין נתיבי תיקייה: המידע הזה נשמר במטא-דאטה " +
            "של מערכת הקבצים, והסריקה הזו אינה קוראת אותה. הקבצים מקבלים שם לפי סוגם " +
            "ומיקומם על הכונן.");

        if (files.Count == 0)
        {
            _warnings.Add("לא אותרו חתימות קבצים מוכרות במחיצה.");
            return;
        }

        int guessed = files.Count(f => f.Quality == RecoveryQuality.Good);
        if (guessed > 0)
            _warnings.Add(
                $"ב-{guessed:N0} קבצים לא ניתן היה לקבוע את האורך המדויק מתוך מבנה הקובץ. " +
                "הם ישוחזרו בגודל מרבי, וייתכן שיכילו נתונים עודפים בסופם — " +
                "ברוב הפורמטים הדבר אינו מפריע לפתיחת הקובץ.");

        // קובץ מפוצל: JPEG בשני מקטעים מחובר ומאומת; בשאר הפורמטים זו עדיין מגבלה מהותית.
        _warnings.Add(
            "קובץ שהיה מפוצל על הכונן: תמונת JPEG בשני חלקים מחוברת ומאומתת בפענוח. " +
            "בשאר סוגי הקבצים הסריקה מניחה שהקובץ רציף — קובץ מפוצל ישוחזר חלקית בלבד.");
    }
}
