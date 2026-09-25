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
            throw new FileNotFoundException(L.T("קובץ התמונה לא נמצא."), full);

        // קובץ התיאור של VMDK הוא טקסט של כמה מאות בתים — הנתונים בקבצים שהוא מפרט.
        long size = new FileInfo(full).Length;
        if (size < 512 && !full.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("הקובץ קטן מכדי להיות תמונת דיסק."));

        var map = ImageMap.TryLoad(ImageMap.PathFor(full));
        int number = DevicePaths.RegisterImage(full);

        try
        {
            int sectorSize = map?.SectorSize ?? DetectSectorSize(full);

            var device = RawDevice.TryOpen(full, sectorSize)
                ?? throw new IOException(L.T("לא ניתן לפתוח את קובץ התמונה לקריאה. ייתכן שהוא בשימוש בתוכנה אחרת."));

            // כונן וירטואלי: הגודל וגודל הסקטור הם של הדיסק שבתוכו, לא של הקובץ.
            if (device.Virtual is { } virtualDisk)
            {
                size = virtualDisk.Size;
                if (virtualDisk.SectorSize != sectorSize)
                {
                    device.Dispose();
                    sectorSize = virtualDisk.SectorSize;
                    device = RawDevice.TryOpen(full, sectorSize)!;
                }
            }

            using var _ = device;
            var (scheme, partitions) = ReadLayout(device, number, size, map);

            return new PhysicalDiskInfo
            {
                DiskNumber = number,
                Model = Path.GetFileName(full),
                BusType = "קובץ תמונה",   // לא לתרגום: תווית, הממשק מתרגם
                SizeBytes = size,
                LogicalSectorSize = sectorSize,
                PhysicalSectorSize = sectorSize,
                Media = MediaKind.Image,
                Trim = TrimState.NotSupported,
                Scheme = scheme,
                RawAccessible = true,
                Partitions = partitions,
                ImagePath = full,
                ImageNote = device.Virtual is { Format: "E01" } ewf
                    ? L.T("תמונה של כלי חקירה (E01) — נקראת ישירות מהקובץ, ושום דבר לא נכתב אליה.") +
                      (ewf.Md5 is not null ? L.T(" הכלי שיצר אותה שמר בה טביעת אצבע של הכונן המקורי.") : "")
                    : device.Virtual is { } vd
                    ? L.T("כונן וירטואלי ({0}) — נקרא ישירות מהקובץ, בלי לחבר אותו ל-Windows, ושום דבר לא נכתב אליו.", vd.Format) +
                      (vd.Dirty ? L.T(" הכונן לא נסגר כראוי בפעם האחרונה, ולכן ייתכן שהשינויים האחרונים שנעשו בו חסרים.") : "")
                    : Describe(map),
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
    /// מחיצת BitLocker שנפתחה ב-Windows, כדיסק וירטואלי שנקרא דרך האות שלה.
    /// קריאה מהדיסק הפיזי מחזירה תוכן מוצפן; דרך <c>\\.\E:</c> Windows מפענח
    /// כל סקטור — גם במקום הפנוי, שבו יושבים הקבצים שנמחקו. כך כל הסריקות,
    /// כולל סריקה מתקדמת, עובדות עליה כרגיל. הכתיבה אליה חסומה ב-<see cref="RawWriter"/>.
    /// </summary>
    public static PhysicalDiskInfo OpenUnlockedVolume(string letter, long size, int sectorSize, string title)
    {
        string path = DevicePaths.VolumePathOf(letter);
        int number = DevicePaths.RegisterImage(path);

        try
        {
            using var device = RawDevice.TryOpen(path, sectorSize)
                ?? throw new IOException(RawDevice.OpenFailure(L.T("הכונן")));

            var fs = FileSystemIdentifier.Identify(device.ReadBlock(0, Math.Max(2048, sectorSize)));
            if (fs.Kind == FileSystemKind.BitLocker)
                throw new InvalidOperationException(L.T("הכונן עדיין נעול. פתחו אותו קודם ב-Windows, עם הסיסמה או מפתח השחזור."));

            var kind = fs.Kind == FileSystemKind.Unknown ? FileSystemKind.Raw : fs.Kind;
            string drive = path[4..];

            return new PhysicalDiskInfo
            {
                DiskNumber = number,
                Model = L.T("{0} — BitLocker פתוח", title),
                BusType = "דרך Windows",   // לא לתרגום: תווית, הממשק מתרגם
                SizeBytes = size,
                LogicalSectorSize = sectorSize,
                PhysicalSectorSize = sectorSize,
                Media = MediaKind.Image,
                Trim = TrimState.NotSupported,
                Scheme = PartitionScheme.SuperFloppy,
                RawAccessible = true,
                Partitions = new List<PartitionInfo>
                {
                    new()
                    {
                        Index = 0,
                        DiskNumber = number,
                        OffsetBytes = 0,
                        SizeBytes = size,
                        FileSystem = kind,
                        Label = fs.Label,
                        DriveLetter = drive,
                        TypeName = FileSystemIdentifier.DisplayName(kind),
                    },
                },
                ImagePath = path,
                ImageNote = L.T("הכונן המוצפן {0} נקרא דרך Windows, שמפענח אותו. " +
                            "הקריאה בלבד — שום דבר לא נכתב אליו. אל תנעלו אותו מחדש עד סוף השחזור.", drive),
            };
        }
        catch
        {
            DevicePaths.UnregisterImage(number);
            throw;
        }
    }

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

        byte[] start = device.ReadBlock(0, FileSystemIdentifier.HeadBytes);
        var fs = FileSystemIdentifier.Identify(start);

        // תמונה בלי מפה (מכלי אחר) שמתחילה בטבלת מחיצות ריקה: זה כונן שלם שהמחיצות
        // שלו נמחקו — בדיוק המקום לחפש אותן, ולא מחיצה אחת בגודל כל הקובץ.
        // חתימת הסיום לבדה לא מבדילה בין טבלה ריקה למחיצה שתחילתה נפגעה; פקודת הקפיצה
        // שבתחילת כל מחיצה — כן.
        if (map is null && fs.Kind == FileSystemKind.Raw && !StartsWithJump(start))
            return (PartitionScheme.Mbr, new List<PartitionInfo>());

        // מחיצה בודדת, או תמונה שאין בה טבלה מזוהה: כל הקובץ הוא מחיצה אחת.
        // גם אם מערכת הקבצים אינה מזוהה (מחיצת RAW) — כך ניתן לאבחן, לתקן
        // ולהריץ עליה סריקה מתקדמת. בלי מערכת קבצים ובלי מפה שאומרת "מחיצה"
        // זו רק הנחה: ייתכן שזה כונן שלם שתחילתו נמחקה.
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
                Assumed = kind == FileSystemKind.Raw && map?.Kind != "partition",
            },
        });
    }

    /// <summary>תחילת מחיצה פותחת בפקודת קפיצה אל קוד האתחול שלה; טבלת מחיצות — לא.</summary>
    private static bool StartsWithJump(byte[] sector)
        => sector.Length >= 3 && ((sector[0] == 0xEB && sector[2] == 0x90) || sector[0] == 0xE9);

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
            return L.T("תמונה ללא קובץ מפה — לא ידוע אם הכונן כולו נקרא בעת יצירתה.");

        string origin = string.IsNullOrWhiteSpace(map.Source) ? "" : L.T("נוצרה מ: {0}. ", map.Source);

        if (!map.Complete)
            return origin + L.T("התמונה חלקית: {0} לא הועתקו. " +
                   "קבצים שישבו באזורים האלה לא ישוחזרו.", DiskImager.Size(map.NotCopiedBytes));

        if (map.UnreadableBytes > 0)
            return origin + L.T("{0} לא נקראו מהכונן בעת היצירה " +
                   "ומולאו באפסים. קבצים שישבו בהם יחזרו פגומים חלקית.", DiskImager.Size(map.UnreadableBytes));

        return origin + L.T("הכונן כולו נקרא בהצלחה.");
    }
}
