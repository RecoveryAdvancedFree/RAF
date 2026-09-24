using System.Text.RegularExpressions;
using RAF.Core.Carving;
using RAF.Core.Model;
using RAF.Core.Signatures;

namespace RAF.Core.FileSystems;

/// <summary>
/// קבצים שבדיקת הדיסק של Windows השאירה.
///
/// כשבדיקת הדיסק מוצאת נתונים ששייכים לקובץ אבל אין להם כבר רשומה בתיקייה,
/// היא שומרת אותם בתיקייה FOUND.000 בשמות FILE0000.CHK, FILE0001.CHK וכן הלאה.
/// השם והסיומת המקוריים אבדו, ו-Windows לא יודע לפתוח אותם — אבל התוכן לרוב שלם.
/// כאן מזהים לפי התוכן מה הסוג האמיתי של כל קובץ, מחזירים לו סיומת, ובמידת
/// האפשר גם שם מתוך המידע שבתוכו. בדיקת הדיסק שומרת את כל שרשרת האשכולות,
/// ולכן הקובץ ארוך מהמקור — האורך האמיתי נקרא מהמבנה, כמו בסריקה המתקדמת.
/// </summary>
internal static partial class ChkFiles
{
    /// <summary>מספר הקבצים שנבדקים לכל היותר — תיקייה ענקית לא תעכב את הסריקה.</summary>
    private const int MaxFiles = 50_000;

    [GeneratedRegex(@"^FOUND\.\d{3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FoundFolder();

    [GeneratedRegex(@"^FILE\d{4}\.CHK$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChkName();

    /// <summary>תיקייה שבדיקת הדיסק יוצרת בשורש הכונן: FOUND.000, FOUND.001…</summary>
    internal static bool IsFoundFolder(string name) => FoundFolder().IsMatch(name);

    /// <summary>שם שבדיקת הדיסק נותנת לנתונים שמצאה: FILE0000.CHK…</summary>
    internal static bool IsChkName(string? name) => name is not null && ChkName().IsMatch(name);

    /// <summary>הנתיב נמצא בתוך תיקייה של בדיקת הדיסק.</summary>
    internal static bool InFoundFolder(string path)
        => path.Split('\\').Any(IsFoundFolder);

    /// <summary>הקבצים שבדיקת הדיסק השאירה ושיש מה לקרוא מהם.</summary>
    internal static List<RecoveredFile> Candidates(List<RecoveredFile> files)
        => files
            .Where(f => !f.IsDirectory && !f.IsCompressed && f.HasContent && f.Size > 0 &&
                        IsChkName(f.Name) && InFoundFolder(f.Path))
            .Take(MaxFiles)
            .ToList();

    /// <summary>זיהוי הסוג של כל קובץ ברשימה. מחזיר את מספר הקבצים שזוהו.</summary>
    internal static int Apply(List<RecoveredFile> candidates, IClusterVolume volume)
    {
        int identified = 0;
        foreach (var file in candidates)
        {
            try
            {
                if (Identify(file, volume)) identified++;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
            {
                // קובץ אחד שלא נקרא לא עוצר את השאר — הוא פשוט נשאר עם השם שלו.
            }
        }
        return identified;
    }

    /// <summary>זיהוי קובץ אחד מתוך התוכן שלו. false — הסוג לא זוהה, והקובץ לא השתנה.</summary>
    internal static bool Identify(RecoveredFile file, IClusterVolume volume)
    {
        using var content = new FileContentVolume(volume, file);

        byte[] head = new byte[(int)Math.Min(8192, file.Size)];
        int got = content.ReadRaw(0, head);
        if (got <= 0) return false;

        var signature = FileSignatures.Identify(head.AsSpan(0, got));
        if (signature is null) return false;

        // אורך שנקרא מהמבנה: בדיקת הדיסק מעגלת לסוף האשכול האחרון בשרשרת, ולפעמים
        // השרשרת ממשיכה לנתונים של קובץ אחר. בלי עדות אמיתית לאורך — לא נוגעים בגודל.
        var resolved = FileLength.Resolve(signature, content, 0, file.Size);
        long length = resolved.Bytes > 0 && resolved.Bytes <= file.Size &&
                      resolved.Confidence != LengthConfidence.Guess
            ? resolved.Bytes
            : file.Size;

        var info = CarvedMetadata.Read(signature, length, (at, count) =>
        {
            if (at < 0 || at >= length || count <= 0) return Array.Empty<byte>();
            byte[] buffer = new byte[(int)Math.Min(count, length - at)];
            int read = content.ReadRaw(at, buffer);
            return read == buffer.Length ? buffer : buffer.AsSpan(0, Math.Max(0, read)).ToArray();
        });

        string extension = info?.Extension ?? (signature.Extensions.Length > 0 ? signature.Extensions[0] : "bin");
        string stem = file.Name[..file.Name.LastIndexOf('.')];

        // השם מתוך הקובץ, ולצדו השם של בדיקת הדיסק — כך אין שני קבצים באותו שם.
        file.Name = (info?.Name is { } found ? $"{found} ({stem})" : stem) + "." + extension;
        file.Size = length;
        file.QualityReason = Explain(signature, file.QualityReason);
        return true;
    }

    private static string Explain(FileSignature signature, string previous)
    {
        string note = $"בדיקת הדיסק של Windows שמרה את הקובץ הזה בלי השם המקורי. לפי התוכן זה {signature.Name}.";
        return string.IsNullOrEmpty(previous) ? note : note + " " + previous;
    }

    /// <summary>
    /// התוכן של קובץ אחד, כאילו היה מחיצה נפרדת שמתחילה בבית הראשון שלו —
    /// כך קביעת האורך וקריאת המידע שבקובץ עובדות עליו כמו על קובץ מהסריקה המתקדמת.
    /// </summary>
    private sealed class FileContentVolume : IClusterVolume
    {
        private readonly IClusterVolume _volume;
        private readonly RecoveredFile _file;

        internal FileContentVolume(IClusterVolume volume, RecoveredFile file)
        {
            _volume = volume;
            _file = file;
        }

        public int BytesPerCluster => _volume.BytesPerCluster;

        public long ClusterToOffset(long cluster) => cluster * BytesPerCluster;

        public bool? IsClusterAllocated(long cluster) => null;

        public int ReadRaw(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset >= _file.Size) return 0;
            int wanted = (int)Math.Min(destination.Length, _file.Size - offset);

            if (_file.ResidentData is { } resident)
            {
                int take = (int)Math.Max(0, Math.Min(wanted, resident.Length - offset));
                resident.AsSpan((int)offset, take).CopyTo(destination);
                return take;
            }

            int cluster = BytesPerCluster;
            int done = 0;
            long extentStart = 0;

            foreach (var extent in _file.Extents)
            {
                long extentBytes = extent.ClusterCount * cluster;
                long position = offset + done;

                while (done < wanted && position >= extentStart && position < extentStart + extentBytes)
                {
                    long inside = position - extentStart;
                    int take = (int)Math.Min(wanted - done, extentBytes - inside);
                    var target = destination.Slice(done, take);

                    if (extent.IsSparse)
                    {
                        target.Clear();
                    }
                    else
                    {
                        int read = _volume.ReadRaw(_volume.ClusterToOffset(extent.StartCluster) + inside, target);
                        if (read < take) return done + Math.Max(0, read);
                    }

                    done += take;
                    position += take;
                }

                if (done >= wanted) break;
                extentStart += extentBytes;
            }

            return done;
        }

        /// <summary>המחיצה עצמה שייכת למי שפתח אותה.</summary>
        public void Dispose() { }
    }
}
