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
    private long _bytesRead;
    private long _candidates;

    /// <summary>סריקה מתקדמת של מחיצה.</summary>
    public static Task<ScanResult> ScanAsync(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        IProgress<ScanProgress>? progress, CancellationToken token)
        => Task.Run(() => new FileCarver().Run(
            diskNumber, partitionOffset, partitionSize, sectorSize, progress, token), token);

    private ScanResult Run(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        IProgress<ScanProgress>? progress, CancellationToken token)
    {
        using var reader = VolumeReader.TryOpen(
            diskNumber, partitionOffset, partitionSize, sectorSize, sequential: true)
            ?? throw new IOException(
                "לא ניתן לפתוח את הדיסק לקריאה. ודא שהתוכנה פועלת בהרשאות מנהל.");

        long length = partitionSize > 0 ? partitionSize : reader.Length;
        return Sweep(RawVolume.Open(reader, sectorSize), length, sectorSize, progress, token);
    }

    /// <summary>
    /// מעבר על המחיצה ואיתור החתימות. מופרד מפתיחת ההתקן כדי שניתן
    /// יהיה להריץ אותו גם מול תמונת בדיקה.
    /// </summary>
    internal ScanResult Sweep(
        RawVolume volume, long size, int sectorSize,
        IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var files = new List<RecoveredFile>();
        byte[] block = new byte[BlockSize + Overlap];

        // ההיסט שממנו מותר להתחיל קובץ חדש. קובץ שאורכו נקבע
        // מדלג את הסורק קדימה, כדי שלא יזהה את תוכנו הפנימי כקבצים.
        long nextAllowedStart = 0;
        long reported = 0;
        long reportEvery = Math.Max(BlockSize, size / 200);

        for (long at = 0; at < size; at += BlockSize)
        {
            if (token.IsCancellationRequested) break;

            int want = (int)Math.Min(BlockSize + Overlap, size - at);
            int read = volume.ReadRaw(at, block.AsSpan(0, want));

            if (read <= 0)
            {
                // אזור בלתי קריא — ממשיכים הלאה במקום לעצור.
                continue;
            }

            _bytesRead += read;

            // חתימות נבדקות בגבולות סקטור: מערכות קבצים מקצות קבצים
            // בגבולות אשכול, ואשכול תמיד מיושר לסקטור.
            int scanLimit = (int)Math.Min(read, BlockSize);

            for (int off = 0; off < scanLimit; off += sectorSize)
            {
                if (token.IsCancellationRequested) break;

                long absolute = at + off;
                if (absolute < nextAllowedStart) continue;

                var signature = FileSignatures.Identify(block.AsSpan(off, Math.Min(64, read - off)));
                if (signature is null) continue;

                _candidates++;

                var resolved = FileLength.Resolve(signature, volume, absolute, size - absolute);
                if (resolved.Bytes < 64) continue;

                files.Add(Materialize(signature, resolved, absolute, sectorSize, files.Count));

                // דילוג על גוף הקובץ רק כשגבולו ודאי. ניחוש אורך היה מסתיר
                // את כל מה שיושב אחרי הקובץ — ובסריקה על כונן אמיתי כך
                // נבלעו קבצים אמיתיים בתוך "קבצים" שלא היו קיימים.
                if (resolved.Confidence is LengthConfidence.Exact or LengthConfidence.Footer)
                    nextAllowedStart = absolute + resolved.Bytes;
            }

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
                });
            }
        }

        BuildWarnings(files);

        return new ScanResult
        {
            Files = files,
            Mode = ScanMode.Advanced,
            Duration = clock.Elapsed,
            Cancelled = token.IsCancellationRequested,
            FileSystem = "סריקת חתימות",
            RecordsExamined = _candidates,
            BytesRead = _bytesRead,
            Warnings = _warnings,
        };
    }

    /// <summary>בניית רשומת קובץ מתוך חתימה שזוהתה.</summary>
    private static RecoveredFile Materialize(
        FileSignature signature, ResolvedLength resolved, long offset, int sectorSize, int index)
    {
        string extension = signature.Extensions.Length > 0 ? signature.Extensions[0] : "bin";

        long startSector = offset / sectorSize;
        long sectors = (resolved.Bytes + sectorSize - 1) / sectorSize;

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

        // קובץ מפוצל אינו ניתן לחילוץ נכון בשיטה זו, וזו מגבלה מהותית.
        _warnings.Add(
            "סריקה מתקדמת מניחה שכל קובץ יושב ברצף אחד על הכונן. קובץ שהיה מפוצל " +
            "למקטעים ישוחזר חלקית בלבד — מגבלה מהותית של זיהוי לפי חתימות.");
    }
}
