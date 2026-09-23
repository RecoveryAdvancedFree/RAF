using RAF.Core.Disks;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Imaging;

/// <summary>
/// פתיחת קובץ תמונה כדיסק. התמונה מקבלת מספר דיסק וירטואלי, ומשם והלאה
/// היא מופיעה ברשימה, נסרקת, משוחזרת ואף מתוקנת בדיוק כמו כונן אמיתי —
/// כשהכונן המקורי אינו נקרא יותר כלל.
/// </summary>
public static class ImageDisk
{
    public static PhysicalDiskInfo Open(string imagePath)
    {
        string full = Path.GetFullPath(imagePath);
        if (!File.Exists(full))
            throw new FileNotFoundException("קובץ התמונה לא נמצא.", full);

        long size = new FileInfo(full).Length;
        if (size < 512)
            throw new InvalidOperationException("הקובץ קטן מכדי להיות תמונת דיסק.");

        var map = ImageMap.TryLoad(ImageMap.PathFor(full));
        int number = DevicePaths.RegisterImage(full);

        try
        {
            int sectorSize = map?.SectorSize ?? DetectSectorSize(full);

            using var device = RawDevice.TryOpen(full, sectorSize)
                ?? throw new IOException("לא ניתן לפתוח את קובץ התמונה לקריאה. ייתכן שהוא בשימוש בתוכנה אחרת.");

            var (scheme, partitions) = ReadLayout(device, number, size, map);

            return new PhysicalDiskInfo
            {
                DiskNumber = number,
                Model = Path.GetFileName(full),
                BusType = "קובץ תמונה",
                SizeBytes = size,
                LogicalSectorSize = sectorSize,
                PhysicalSectorSize = sectorSize,
                Media = MediaKind.Image,
                Trim = TrimState.NotSupported,
                Scheme = scheme,
                RawAccessible = true,
                Partitions = partitions,
                ImagePath = full,
                ImageNote = Describe(map),
                ImageDamaged = map is not null && (map.UnreadableBytes > 0 || !map.Complete),
            };
        }
        catch
        {
            DevicePaths.UnregisterImage(number);
            throw;
        }
    }

    public static void Close(int number) => DevicePaths.UnregisterImage(number);

    /// <summary>
    /// תמונה של מחיצה בודדת נקראת כמחיצה אחת. זה חשוב: מגזר האתחול של
    /// מחיצה נושא את החתימה 55 AA כמו MBR, וקריאתו כטבלת מחיצות הייתה
    /// מפענחת קוד אתחול כאילו היו אלה רשומות מחיצה.
    /// </summary>
    private static (PartitionScheme, List<PartitionInfo>) ReadLayout(
        RawDevice device, int number, long size, ImageMap? map)
    {
        if (map?.Kind != "partition")
        {
            var table = PartitionTableReader.Read(device, number, size);
            if (table.Partitions.Count > 0) return (table.Scheme, table.Partitions);

            // תמונה של כונן שלם שאין בה טבלה: מחיצה "בגודל כל הכונן" הייתה
            // מטעה — ומסתירה את המחיצות האמיתיות שסריקת הכונן תמצא בה.
            if (map?.Kind == "disk") return (table.Scheme, new List<PartitionInfo>());
        }

        // מחיצה בודדת, או תמונה שאין בה טבלה מזוהה: כל הקובץ הוא מחיצה אחת.
        // גם אם מערכת הקבצים אינה מזוהה (מחיצת RAW) — כך ניתן לאבחן, לתקן
        // ולהריץ עליה סריקה מתקדמת.
        var fs = FileSystemIdentifier.Identify(device.ReadBlock(0, 2048));
        var kind = fs.Kind == FileSystemKind.Unknown ? FileSystemKind.Raw : fs.Kind;

        return (PartitionScheme.SuperFloppy, new List<PartitionInfo>
        {
            new()
            {
                Index = 0,
                DiskNumber = number,
                OffsetBytes = 0,
                SizeBytes = size,
                FileSystem = kind,
                Label = fs.Label,
                TypeName = FileSystemIdentifier.DisplayName(kind),
            },
        });
    }

    /// <summary>
    /// תמונה ללא מפה (למשל מכלי אחר): דיסק GPT עם סקטורים של 4096 בתים
    /// מזוהה לפי כותרת ה-GPT בסקטור הלוגי הראשון.
    /// </summary>
    private static int DetectSectorSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            byte[] probe = new byte[8];

            stream.Position = 512;
            if (stream.Read(probe) == 8 && probe.AsSpan().SequenceEqual("EFI PART"u8)) return 512;

            stream.Position = 4096;
            if (stream.Read(probe) == 8 && probe.AsSpan().SequenceEqual("EFI PART"u8)) return 4096;
        }
        catch (IOException)
        {
        }

        return 512;
    }

    private static string Describe(ImageMap? map)
    {
        if (map is null)
            return "תמונה ללא קובץ מפה — לא ידוע אם כל הסקטורים נקראו בעת יצירתה.";

        string origin = string.IsNullOrWhiteSpace(map.Source) ? "" : $"נוצרה מ: {map.Source}. ";

        if (!map.Complete)
            return origin + $"התמונה חלקית: {DiskImager.Size(map.NotCopiedBytes)} לא הועתקו. " +
                   "קבצים שישבו באזורים האלה לא ישוחזרו.";

        if (map.UnreadableBytes > 0)
            return origin + $"{map.UnreadableBytes / map.SectorSize:N0} סקטורים לא נקראו בעת היצירה " +
                   "ומולאו באפסים. קבצים שישבו בהם יחזרו פגומים חלקית.";

        return origin + "כל הסקטורים נקראו בהצלחה.";
    }
}
