using System.Diagnostics;
using RAF.Core.Disks;
using RAF.Core.Native;

namespace RAF.Core.Imaging;

/// <summary>מקור סקטורים להעתקה — דיסק אמיתי, או מקור מדומה בבדיקות.</summary>
internal interface ISectorSource
{
    long Length { get; }
    int SectorSize { get; }

    /// <summary>מחזיר את מספר הבתים שנקראו; 0 אם הקריאה נכשלה.</summary>
    int Read(long offset, Span<byte> destination);
}

public sealed class ImagingProgress
{
    /// <summary>1 — העתקה מהירה; 2 — ניסיון חוזר באזורים שנכשלו.</summary>
    public int Pass { get; init; }
    public string Stage { get; init; } = "";
    public double Percent { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }

    /// <summary>בתים שעדיין לא נקראו בהצלחה — ממתינים למעבר השני או פגומים.</summary>
    public long ProblemBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public sealed class ImagingResult
{
    public string ImagePath { get; init; } = "";
    public string MapPath { get; init; } = "";
    public long Size { get; init; }
    public bool Complete { get; init; }
    public bool Cancelled { get; init; }
    public long UnreadableBytes { get; init; }
    public int UnreadableRanges { get; init; }
    public long NotCopiedBytes { get; init; }
    public TimeSpan Duration { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// יצירת תמונת דיסק — העתק גולמי, סקטור אחר סקטור, לקובץ על כונן אחר.
///
/// הערך העיקרי הוא בכונן גוסס: כל קריאה עלולה להיות האחרונה, ולכן קוראים
/// אותו פעם אחת בלבד, ואת כל הסריקות והשחזורים מריצים על ההעתק.
///
/// ההעתקה נעשית בשני מעברים, כמו בכלי הצלה מקצועיים:
/// 1. מעבר מהיר בבלוקים גדולים. בלוק שנכשל אינו נבדק מיד — מדלגים הלאה,
///    ומרחק הדילוג גדל בכל כישלון רצוף. כך אזור פגום אינו מבזבז את
///    הזמן הקצר שנותר לכונן, ורוב הנתונים הטובים נאספים קודם.
/// 2. חזרה לאזורים שנכשלו ודולגו, בבלוקים קטנים ואז סקטור אחר סקטור,
///    כדי להציל כל סקטור שעוד ניתן לקרוא.
///
/// סקטור שלא נקרא נשאר אפסים בתמונה ומתועד בקובץ המפה.
/// </summary>
public static class DiskImager
{
    internal const int ChunkSize = 1024 * 1024;
    internal const int RetryBlock = 64 * 1024;
    internal const long MaxSkip = 64L * 1024 * 1024;

    /// <summary>גודל הקובץ המרבי ב-FAT32.</summary>
    private const long Fat32MaxFile = 4L * 1024 * 1024 * 1024 - 1;

    /// <summary>
    /// בדיקת היעד לפני התחלה. זורק חריגה עם הסבר בעברית אם אינו מתאים.
    /// </summary>
    public static void ValidateDestination(string imagePath, int sourceDisk, long size)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new InvalidOperationException("לא נבחר קובץ יעד לתמונה.");

        if (DevicePaths.IsImage(sourceDisk))
            throw new InvalidOperationException("זו כבר תמונת דיסק. ניתן לסרוק ולשחזר ממנה ישירות.");

        string full = Path.GetFullPath(imagePath);
        string? folder = Path.GetDirectoryName(full);
        if (folder is null || !Directory.Exists(folder))
            throw new InvalidOperationException("תיקיית היעד אינה קיימת.");

        int targetDisk = DiskEnumerator.GetDiskNumberForPath(full);
        if (targetDisk >= 0 && targetDisk == sourceDisk)
            throw new InvalidOperationException(
                "התמונה חייבת להישמר על כונן אחר מזה שממנו היא נוצרת: כתיבה לאותו כונן " +
                "הייתה דורסת בדיוק את הנתונים שמנסים להציל.");

        var drive = new DriveInfo(Path.GetPathRoot(full)!);

        if (string.Equals(drive.DriveFormat, "FAT32", StringComparison.OrdinalIgnoreCase) && size > Fat32MaxFile)
            throw new InvalidOperationException(
                $"כונן היעד מפורמט ב-FAT32, שאינו מאפשר קובץ גדול מ-4GB, והתמונה תהיה בגודל " +
                $"{Size(size)}. בחרו כונן NTFS או exFAT.");

        // קובץ קיים שיידרס משחרר את מקומו.
        long reclaimed = File.Exists(full) ? new FileInfo(full).Length : 0;
        if (drive.AvailableFreeSpace + reclaimed < size)
            throw new InvalidOperationException(
                $"אין מספיק מקום בכונן היעד: התמונה דורשת {Size(size)}, " +
                $"ופנויים {Size(drive.AvailableFreeSpace)}.");
    }

    /// <summary>יצירת תמונה של דיסק שלם או של מחיצה אחת.</summary>
    /// <param name="kind">"disk" או "partition" — נשמר במפה, כדי לדעת איך לפתוח את התמונה.</param>
    public static Task<ImagingResult> CreateAsync(
        int diskNumber, long offset, long length, int sectorSize,
        string kind, string sourceDescription, string imagePath,
        IProgress<ImagingProgress>? progress, CancellationToken token)
        => Task.Run(() =>
        {
            using var reader = VolumeReader.TryOpen(diskNumber, offset, length, sectorSize, sequential: true, applyOverlay: false)
                ?? throw new IOException("לא ניתן לפתוח את הדיסק לקריאה. ודאו שהתוכנה פועלת בהרשאות מנהל.");

            return Create(new VolumeSource(reader, length, sectorSize),
                kind, sourceDescription, imagePath, progress, token);
        });

    internal static ImagingResult Create(
        ISectorSource source, string kind, string sourceDescription, string imagePath,
        IProgress<ImagingProgress>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        long length = source.Length;
        int sector = source.SectorSize;

        var pending = new List<ByteRange>();
        var unreadable = new List<ByteRange>();
        var notCopied = new List<ByteRange>();

        long readOk = 0;
        var lastReport = TimeSpan.Zero;
        var lastMapSave = TimeSpan.Zero;
        string mapPath = ImageMap.PathFor(imagePath);

        ImageMap Map(bool complete) => new()
        {
            Source = sourceDescription,
            Kind = kind,
            Size = length,
            SectorSize = sector,
            Complete = complete,
            Unreadable = unreadable.ToList(),
            NotCopied = notCopied.ToList(),
        };

        void Report(int pass, string stage, long done, long total, long problems, bool force = false)
        {
            if (!force && clock.Elapsed - lastReport < TimeSpan.FromMilliseconds(200)) return;
            lastReport = clock.Elapsed;

            progress?.Report(new ImagingProgress
            {
                Pass = pass,
                Stage = stage,
                Percent = total > 0 ? done * 100.0 / total : 100,
                BytesDone = done,
                BytesTotal = total,
                ProblemBytes = problems,
                Elapsed = clock.Elapsed,
                BytesPerSecond = clock.Elapsed.TotalSeconds > 0 ? readOk / clock.Elapsed.TotalSeconds : 0,
            });
        }

        byte[] buffer = new byte[ChunkSize];

        using (var output = new FileStream(imagePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20))
        {
            output.SetLength(length);

            void Write(long at, int count)
            {
                output.Position = at;
                output.Write(buffer, 0, count);
            }

            // ---------------------------------------------------- מעבר 1
            long pos = 0;
            long skip = ChunkSize;

            while (pos < length)
            {
                if (token.IsCancellationRequested)
                {
                    notCopied.AddRange(pending);
                    notCopied.Add(new ByteRange(pos, length - pos));
                    pending.Clear();
                    break;
                }

                int want = (int)Math.Min(ChunkSize, length - pos);
                int read = source.Read(pos, buffer.AsSpan(0, want));

                if (read == want)
                {
                    Write(pos, want);
                    pos += want;
                    readOk += want;
                    skip = ChunkSize;
                }
                else
                {
                    // מה שנקרא לפני הכישלון נשמר; הקריאה הבאה תתחיל ממקום הכישלון.
                    int good = Math.Max(0, read) / sector * sector;
                    if (good > 0)
                    {
                        Write(pos, good);
                        pos += good;
                        readOk += good;
                        continue;
                    }

                    long jump = Math.Min(skip, length - pos);
                    pending.Add(new ByteRange(pos, jump));
                    pos += jump;
                    skip = Math.Min(skip * 2, MaxSkip);
                }

                Report(1, "מעבר 1 — העתקה מהירה", pos, length, pending.Sum(r => r.Length));

                if (clock.Elapsed - lastMapSave > TimeSpan.FromSeconds(10))
                {
                    lastMapSave = clock.Elapsed;
                    SaveInterimMap(Map(false), pending, pos, length, mapPath);
                }
            }

            Report(1, "מעבר 1 — העתקה מהירה", pos, length, pending.Sum(r => r.Length), force: true);

            // ---------------------------------------------------- מעבר 2
            long retryTotal = pending.Sum(r => r.Length);
            long retryDone = 0;

            for (int i = 0; i < pending.Count; i++)
            {
                var range = pending[i];

                for (long at = range.Offset; at < range.End; at += RetryBlock)
                {
                    if (token.IsCancellationRequested)
                    {
                        notCopied.Add(new ByteRange(at, range.End - at));
                        notCopied.AddRange(pending.Skip(i + 1));
                        goto finished;
                    }

                    int n = (int)Math.Min(RetryBlock, range.End - at);

                    if (source.Read(at, buffer.AsSpan(0, n)) == n)
                    {
                        Write(at, n);
                        readOk += n;
                    }
                    else
                    {
                        // בלוק שנכשל — סקטור אחר סקטור, כדי להציל כל מה שנקרא.
                        for (long s = at; s < at + n; s += sector)
                        {
                            int k = (int)Math.Min(sector, at + n - s);
                            if (source.Read(s, buffer.AsSpan(0, k)) == k)
                            {
                                Write(s, k);
                                readOk += k;
                            }
                            else
                            {
                                AddMerged(unreadable, new ByteRange(s, k));
                            }
                        }
                    }

                    retryDone += n;
                    Report(2, "מעבר 2 — ניסיון חוזר באזורים פגומים", retryDone, retryTotal,
                        retryTotal - retryDone + unreadable.Sum(r => r.Length));
                }
            }

            finished:
            output.Flush(flushToDisk: true);
        }

        bool cancelled = notCopied.Count > 0;
        var map = Map(!cancelled);
        map.Save(mapPath);

        return new ImagingResult
        {
            ImagePath = imagePath,
            MapPath = mapPath,
            Size = length,
            Complete = !cancelled,
            Cancelled = cancelled,
            UnreadableBytes = map.UnreadableBytes,
            UnreadableRanges = map.Unreadable.Count,
            NotCopiedBytes = map.NotCopiedBytes,
            Duration = clock.Elapsed,
            Message = Describe(map, cancelled, sector),
        };
    }

    /// <summary>
    /// שמירת מפת ביניים: אם התוכנה או הכונן קורסים באמצע, נשארת מפה
    /// שמתעדת במדויק מה כבר הועתק.
    /// </summary>
    private static void SaveInterimMap(ImageMap map, List<ByteRange> pending, long pos, long length, string path)
    {
        try
        {
            map.NotCopied.AddRange(pending);
            if (pos < length) map.NotCopied.Add(new ByteRange(pos, length - pos));
            map.Save(path);
        }
        catch (IOException)
        {
            // מפת ביניים היא רשת ביטחון; כישלון בה אינו עוצר את ההעתקה.
        }
    }

    /// <summary>הוספת טווח לרשימה ממוינת, תוך איחוד עם טווח צמוד.</summary>
    internal static void AddMerged(List<ByteRange> list, ByteRange range)
    {
        if (list.Count > 0 && list[^1].End == range.Offset)
            list[^1] = new ByteRange(list[^1].Offset, list[^1].Length + range.Length);
        else
            list.Add(range);
    }

    private static string Describe(ImageMap map, bool cancelled, int sector)
    {
        if (cancelled)
            return $"ההעתקה נעצרה. {Size(map.Size - map.NotCopiedBytes)} הועתקו, " +
                   $"ו-{Size(map.NotCopiedBytes)} לא הועתקו. ניתן לסרוק את התמונה החלקית, " +
                   "אך קבצים שישבו באזורים שלא הועתקו לא ישוחזרו.";

        if (map.UnreadableBytes == 0)
            return "התמונה הושלמה. כל הסקטורים נקראו בהצלחה — התמונה זהה לכונן.";

        long sectors = map.UnreadableBytes / sector;
        return $"התמונה הושלמה. {sectors:N0} סקטורים ({Size(map.UnreadableBytes)}) לא נקראו גם " +
               "בניסיון החוזר ומולאו באפסים. כל השאר הועתק. קבצים שישבו בסקטורים האלה " +
               "יחזרו פגומים חלקית; כל השאר ישוחזרו כרגיל.";
    }

    internal static string Size(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>התאמת קורא מחיצה לממשק מקור הסקטורים.</summary>
    private sealed class VolumeSource : ISectorSource
    {
        private readonly VolumeReader _reader;

        public VolumeSource(VolumeReader reader, long length, int sectorSize)
        {
            _reader = reader;
            Length = length;
            SectorSize = sectorSize;
        }

        public long Length { get; }
        public int SectorSize { get; }
        public int Read(long offset, Span<byte> destination) => _reader.Read(offset, destination);
    }
}
