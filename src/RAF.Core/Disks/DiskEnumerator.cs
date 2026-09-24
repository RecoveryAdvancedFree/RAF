using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Principal;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Disks;

/// <summary>
/// מניית כל אמצעי האחסון במערכת: דיסקים פיזיים, המחיצות שעליהם,
/// וזיהוי סוג המדיה של כל דיסק (HDD / SSD / NVMe / USB).
/// </summary>
public static class DiskEnumerator
{
    private const int MaxPhysicalDrives = 64;

    /// <summary>האם התהליך רץ בהרשאות מנהל — תנאי לגישה גולמית לדיסק.</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// מספר הדיסק הפיזי שעליו יושב נתיב נתון. מחזיר ‎-1 כשלא ניתן לקבוע,
    /// למשל בכונן רשת. משמש לאכיפת הכלל שאסור לשחזר לאותו דיסק.
    /// </summary>
    public static int GetDiskNumberForPath(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return -1;

            // נתיב UNC אינו יושב על דיסק מקומי.
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) return -1;

            string devicePath = @"\\.\" + root.TrimEnd('\\', '/');

            IntPtr handle = Win32.CreateFile(
                devicePath, 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
                IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

            if (handle == Win32.INVALID_HANDLE_VALUE || handle == IntPtr.Zero) return -1;

            const int size = 12; // STORAGE_DEVICE_NUMBER
            IntPtr outBuf = Marshal.AllocHGlobal(size);
            try
            {
                bool ok = Win32.DeviceIoControl(
                    handle, Win32.IOCTL_STORAGE_GET_DEVICE_NUMBER,
                    IntPtr.Zero, 0, outBuf, size, out uint returned, IntPtr.Zero);

                if (!ok || returned < 8) return -1;

                byte[] buf = new byte[returned];
                Marshal.Copy(outBuf, buf, 0, (int)returned);
                return (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
            }
            finally
            {
                Marshal.FreeHGlobal(outBuf);
                Win32.CloseHandle(handle);
            }
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>זמן ההמתנה לשאילתת המאפיינים של כונן (שם, גודל, סוג).</summary>
    private static readonly TimeSpan DescribeLimit = TimeSpan.FromSeconds(5);

    /// <summary>זמן ההמתנה לקריאת טבלת המחיצות מהכונן.</summary>
    private static readonly TimeSpan LayoutLimit = TimeSpan.FromSeconds(8);

    /// <summary>זמן ההמתנה למידע על אמצעי אחסון לוגי (תווית, נפח פנוי).</summary>
    private static readonly TimeSpan VolumeLimit = TimeSpan.FromSeconds(3);

    /// <summary>מניית כל הדיסקים הפיזיים והמחיצות שעליהם.</summary>
    /// <summary>
    /// הדיסק הפיזי שמאחורי מספר דיסק. כונן BitLocker שנפתח דרך האות שלו מקבל
    /// מספר וירטואלי — אבל הוא יושב על דיסק פיזי, וזה הדיסק שאסור לכתוב אליו.
    /// -1 אם לא ניתן לדעת.
    /// </summary>
    public static int PhysicalDiskOf(int number)
        => DevicePaths.ImagePathOf(number) is { } path && DevicePaths.IsVolumePath(path)
            ? GetDiskNumberForPath(path[4..] + "\\")
            : number;

    public static List<PhysicalDiskInfo> EnumerateDisks() => EnumerateDisks(null);

    /// <summary>
    /// מניית כל הדיסקים, כשכונן תקוע אינו מעכב את השאר.
    ///
    /// כל כונן נבדק בתהליכון משלו, במקביל, ועם מגבלת זמן. כונן שלא ענה בזמן
    /// מוחזר מסומן "לא מגיב", עם מה שכבר ידוע עליו. אם הוא עונה מאוחר יותר,
    /// המידע המלא מגיע דרך <paramref name="late"/>.
    /// </summary>
    public static List<PhysicalDiskInfo> EnumerateDisks(Action<PhysicalDiskInfo>? late)
    {
        var volumes = EnumerateVolumes();

        var probes = Enumerable.Range(0, MaxPhysicalDrives)
            .Select(n => Task.Run(() => Probe(n, volumes, late)))
            .ToArray();

        Task.WaitAll(probes);

        return probes
            .Select(t => t.Result)
            .Where(d => d is not null)
            .Select(d => d!)
            .OrderBy(d => d.DiskNumber)
            .ToList();
    }

    /// <summary>בדיקת כונן אחד, בשני שלבים שלכל אחד מגבלת זמן משלו.</summary>
    private static PhysicalDiskInfo? Probe(int number, List<VolumeRecord> volumes, Action<PhysicalDiskInfo>? late)
    {
        // שלב 1 — מאפיינים. כונן שאינו קיים נכשל כאן מיד, ולכן חריגה מהזמן
        // פירושה כונן שקיים אך תקוע.
        bool describedInTime = Bounded.TryRun(
            () => Describe(number), DescribeLimit, out var description,
            late: d =>
            {
                if (d is null || late is null) return;
                Bounded.TryRun(() => BuildDisk(d, volumes), LayoutLimit, out var full);
                late(full ?? Unresponsive(d, volumes, "הכונן ענה באיחור, אך לא ניתן היה לקרוא ממנו את טבלת המחיצות."));
            });

        if (!describedInTime)
            return Unresponsive(new DiskDescription { Number = number }, volumes,
                "הכונן מחובר, אך אינו עונה אפילו לשאילתת המאפיינים הבסיסית. זה סימן מובהק לכונן פגום, " +
                "או לחיבור (כבל / מתאם USB) שאינו תקין.");

        if (description is null) return null;   // אין דיסק במספר הזה

        // שלב 2 — קריאה גולמית של טבלת המחיצות. כאן כונן גוסס נוטה להיתקע.
        bool readInTime = Bounded.TryRun(
            () => BuildDisk(description, volumes), LayoutLimit, out var disk,
            late: full => { if (full is not null) late?.Invoke(full); });

        return readInTime && disk is not null
            ? disk
            : Unresponsive(description, volumes,
                "הכונן זוהה, אך אינו עונה לבקשות קריאה. זה סימן לכונן פגום. התוכנה ממשיכה לנסות " +
                "ברקע, והרשימה תתעדכן אם הוא יענה.");
    }

    /// <summary>מה שידוע על כונן מתוך שאילתות מאפיינים, ללא קריאה מהמדיה.</summary>
    private sealed class DiskDescription
    {
        public int Number { get; init; }
        public string Model { get; init; } = "";
        public string Vendor { get; init; } = "";
        public string Serial { get; init; } = "";
        public string Bus { get; init; } = "";
        public MediaKind Media { get; init; }
        public TrimState Trim { get; init; }
        public bool Removable { get; init; }
        public int LogicalSector { get; init; } = 512;
        public int PhysicalSector { get; init; } = 512;
        public long Size { get; init; }
    }

    private static DiskDescription? Describe(int number)
    {
        string path = $@"\\.\PhysicalDrive{number}";

        // פתיחה ללא הרשאת גישה (dwDesiredAccess = 0) מאפשרת שאילתות מאפיינים
        // גם ללא הרשאות מנהל — כך הרשימה מוצגת למשתמש בכל מקרה.
        IntPtr probe = Win32.CreateFile(
            path, 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
            IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

        if (probe == Win32.INVALID_HANDLE_VALUE || probe == IntPtr.Zero)
            return null;

        try
        {
            var descriptor = StorageQuery.GetDeviceDescriptor(probe);
            bool? seekPenalty = StorageQuery.GetIncursSeekPenalty(probe);
            var sectors = StorageQuery.GetSectorSizes(probe);
            var bus = descriptor?.BusType ?? Win32.StorageBusType.Unknown;
            bool removable = descriptor?.RemovableMedia ?? false;
            string vendor = descriptor?.Vendor ?? "";
            string model = descriptor?.Product ?? "";

            return new DiskDescription
            {
                Number = number,
                Model = string.IsNullOrWhiteSpace(model) ? vendor : model,
                Vendor = vendor,
                Serial = descriptor?.Serial ?? "",
                Bus = BusDisplayName(bus),
                Media = StorageQuery.Classify(bus, seekPenalty, removable, $"{vendor} {model}"),
                Trim = StorageQuery.GetTrimState(probe),
                Removable = removable,
                LogicalSector = sectors?.Logical ?? 512,
                PhysicalSector = sectors?.Physical ?? sectors?.Logical ?? 512,
                Size = StorageQuery.GetDiskSize(probe),
            };
        }
        finally
        {
            Win32.CloseHandle(probe);
        }
    }

    private static PhysicalDiskInfo BuildDisk(DiskDescription d, List<VolumeRecord> volumes)
    {
        string path = $@"\\.\PhysicalDrive{d.Number}";

        // כעת ניסיון פתיחה אמיתי לקריאה גולמית — דורש הרשאות מנהל.
        var partitions = new List<PartitionInfo>();
        var scheme = PartitionScheme.Unknown;
        bool rawAccessible = false;

        using (var device = RawDevice.TryOpen(path, d.LogicalSector))
        {
            if (device is not null)
            {
                rawAccessible = true;
                var table = PartitionTableReader.Read(device, d.Number, d.Size);
                scheme = table.Scheme;
                partitions = table.Partitions;
            }
        }

        // שיוך אותיות כונן ונפח פנוי למחיצות, לפי התאמת היסט.
        partitions = MergeWithVolumes(partitions, volumes, d.Number);

        // ללא גישה גולמית, בונים רשימת מחיצות חלקית מתוך מידע ה-Volume בלבד.
        if (!rawAccessible && partitions.Count == 0)
            partitions = PartitionsFromVolumesOnly(volumes, d.Number);

        return Assemble(d, volumes, partitions, scheme, rawAccessible, problem: null);
    }

    /// <summary>כונן שלא ענה בזמן: מה שידוע עליו, ומחיצות מתוך מידע Windows בלבד.</summary>
    private static PhysicalDiskInfo Unresponsive(DiskDescription d, List<VolumeRecord> volumes, string problem)
        => Assemble(d, volumes, PartitionsFromVolumesOnly(volumes, d.Number),
            PartitionScheme.Unknown, rawAccessible: false, problem);

    private static PhysicalDiskInfo Assemble(
        DiskDescription d, List<VolumeRecord> volumes, List<PartitionInfo> partitions,
        PartitionScheme scheme, bool rawAccessible, string? problem) => new()
    {
        DiskNumber = d.Number,
        Model = d.Model,
        Vendor = d.Vendor,
        SerialNumber = d.Serial,
        BusType = d.Bus,
        SizeBytes = d.Size,
        LogicalSectorSize = d.LogicalSector,
        PhysicalSectorSize = d.PhysicalSector,
        Media = d.Media,
        Trim = d.Trim,
        IsRemovable = d.Removable,
        Scheme = scheme,
        RawAccessible = rawAccessible,
        Partitions = partitions,
        Unresponsive = problem is not null,
        Problem = problem,
    };

    /// <summary>
    /// מיזוג מחיצות שנקראו גולמית עם אמצעי האחסון שמוכרים ל-Windows.
    /// ההתאמה לפי היסט תחילת המחיצה — הזיהוי היציב ביותר.
    /// </summary>
    private static List<PartitionInfo> MergeWithVolumes(
        List<PartitionInfo> partitions, List<VolumeRecord> volumes, int diskNumber)
    {
        var merged = new List<PartitionInfo>(partitions.Count);

        foreach (var p in partitions)
        {
            var match = volumes.FirstOrDefault(
                v => v.DiskNumber == diskNumber && v.StartingOffset == p.OffsetBytes);

            if (match is null)
            {
                // מחיצה שקיימת פיזית אך Windows אינו מכיר — יעד שחזור מובהק.
                merged.Add(Clone(p, isUnmounted: true));
                continue;
            }

            merged.Add(new PartitionInfo
            {
                Index = p.Index,
                DiskNumber = p.DiskNumber,
                OffsetBytes = p.OffsetBytes,
                SizeBytes = p.SizeBytes,
                FileSystem = p.FileSystem,
                TypeName = p.TypeName,
                TypeGuid = p.TypeGuid,
                PartitionGuid = p.PartitionGuid,
                Label = !string.IsNullOrWhiteSpace(match.Label) ? match.Label : p.Label,
                DriveLetter = match.DriveLetter,
                IsBootable = p.IsBootable,
                IsHidden = p.IsHidden,
                FreeBytes = match.FreeBytes,
                IsUnmounted = string.IsNullOrEmpty(match.DriveLetter),
            });
        }

        return merged;
    }

    private static PartitionInfo Clone(PartitionInfo p, bool isUnmounted) => new()
    {
        Index = p.Index,
        DiskNumber = p.DiskNumber,
        OffsetBytes = p.OffsetBytes,
        SizeBytes = p.SizeBytes,
        FileSystem = p.FileSystem,
        TypeName = p.TypeName,
        TypeGuid = p.TypeGuid,
        PartitionGuid = p.PartitionGuid,
        Label = p.Label,
        IsBootable = p.IsBootable,
        IsHidden = p.IsHidden,
        IsUnmounted = isUnmounted,
    };

    private static List<PartitionInfo> PartitionsFromVolumesOnly(List<VolumeRecord> volumes, int diskNumber)
    {
        int i = 0;
        return volumes
            .Where(v => v.DiskNumber == diskNumber)
            .OrderBy(v => v.StartingOffset)
            .Select(v => new PartitionInfo
            {
                Index = i++,
                DiskNumber = diskNumber,
                OffsetBytes = v.StartingOffset,
                SizeBytes = v.SizeBytes,
                FileSystem = v.FileSystem,
                TypeName = FileSystemIdentifier.DisplayName(v.FileSystem),
                Label = v.Label,
                DriveLetter = v.DriveLetter,
                FreeBytes = v.FreeBytes,
                IsUnmounted = string.IsNullOrEmpty(v.DriveLetter),
            })
            .ToList();
    }

    // ---------------------------------------------------------- אמצעי אחסון לוגיים

    /// <summary>רשומת אמצעי אחסון לוגי כפי ש-Windows מכיר אותו.</summary>
    private sealed class VolumeRecord
    {
        public string VolumeName { get; init; } = "";
        public string DriveLetter { get; init; } = "";
        public string Label { get; init; } = "";
        public FileSystemKind FileSystem { get; init; }
        public long SizeBytes { get; init; }
        public long? FreeBytes { get; init; }
        public int DiskNumber { get; init; } = -1;
        public long StartingOffset { get; init; }
    }

    private static List<VolumeRecord> EnumerateVolumes()
    {
        var names = new List<string>();
        char[] buffer = new char[260];

        IntPtr find = Win32.FindFirstVolume(buffer, (uint)buffer.Length);
        if (find == Win32.INVALID_HANDLE_VALUE) return new List<VolumeRecord>();

        try
        {
            do
            {
                string volumeName = new string(buffer).TrimEnd('\0');
                if (!string.IsNullOrEmpty(volumeName)) names.Add(volumeName);
                Array.Clear(buffer);
            }
            while (Win32.FindNextVolume(find, buffer, (uint)buffer.Length));
        }
        finally
        {
            Win32.FindVolumeClose(find);
        }

        // GetVolumeInformation על כונן פגום עלול להיתקע. כל אמצעי אחסון נבדק
        // במקביל ועם מגבלת זמן, כך שכונן תקוע אחד אינו מעכב את השאר.
        var tasks = names
            .Select(name => Task.Run(() =>
                Bounded.TryRun(() => DescribeVolume(name), VolumeLimit, out var record) ? record : null))
            .ToArray();

        Task.WaitAll(tasks);
        return tasks.Select(t => t.Result).Where(r => r is not null).Select(r => r!).ToList();
    }

    private static VolumeRecord? DescribeVolume(string volumeName)
    {
        // אות הכונן, אם קיימת.
        string driveLetter = "";
        char[] pathBuffer = new char[512];
        if (Win32.GetVolumePathNamesForVolumeName(volumeName, pathBuffer, (uint)pathBuffer.Length, out _))
        {
            string first = new string(pathBuffer).Split('\0').FirstOrDefault(s => s.Length > 0) ?? "";
            if (first.Length >= 2 && first[1] == ':') driveLetter = first[..2];
        }

        // כוננים מרוחקים או תקליטורים אינם רלוונטיים לשחזור גולמי.
        uint driveType = Win32.GetDriveType(volumeName);
        if (driveType is Win32.DRIVE_REMOTE or Win32.DRIVE_CDROM) return null;

        string label = "";
        var fsKind = FileSystemKind.Unknown;
        char[] labelBuffer = new char[256];
        char[] fsBuffer = new char[64];

        if (Win32.GetVolumeInformation(volumeName, labelBuffer, (uint)labelBuffer.Length,
                out _, out _, out _, fsBuffer, (uint)fsBuffer.Length))
        {
            label = new string(labelBuffer).TrimEnd('\0');
            fsKind = ParseFsName(new string(fsBuffer).TrimEnd('\0'));
        }

        long size = 0;
        long? free = null;
        if (Win32.GetDiskFreeSpaceEx(volumeName, out _, out ulong total, out ulong totalFree))
        {
            size = (long)total;
            free = (long)totalFree;
        }

        var extent = GetVolumeDiskExtent(volumeName);

        return new VolumeRecord
        {
            VolumeName = volumeName,
            DriveLetter = driveLetter,
            Label = label,
            FileSystem = fsKind,
            SizeBytes = size,
            FreeBytes = free,
            DiskNumber = extent?.DiskNumber ?? -1,
            StartingOffset = extent?.StartingOffset ?? 0,
        };
    }

    private readonly record struct DiskExtent(int DiskNumber, long StartingOffset, long Length);

    /// <summary>מיפוי אמצעי אחסון לוגי לדיסק הפיזי ולהיסט שעליו הוא יושב.</summary>
    private static DiskExtent? GetVolumeDiskExtent(string volumeName)
    {
        // CreateFile על אמצעי אחסון דורש נתיב ללא לוכסן אחורי בסוף.
        string path = volumeName.TrimEnd('\\');

        IntPtr handle = Win32.CreateFile(
            path, 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
            IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

        if (handle == Win32.INVALID_HANDLE_VALUE || handle == IntPtr.Zero) return null;

        // מקום ל-VOLUME_DISK_EXTENTS עם מספר רשומות (מערכי RAID / Spanned).
        const int size = 8 + 24 * 16;
        IntPtr outBuf = Marshal.AllocHGlobal(size);
        try
        {
            bool ok = Win32.DeviceIoControl(
                handle, Win32.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                IntPtr.Zero, 0, outBuf, size, out uint returned, IntPtr.Zero);

            if (!ok || returned < 32) return null;

            byte[] buf = new byte[returned];
            Marshal.Copy(outBuf, buf, 0, (int)returned);

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0));
            if (count == 0) return null;

            // DISK_EXTENT הראשון: DiskNumber(4) + ריפוד(4) + StartingOffset(8) + ExtentLength(8)
            int diskNumber = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8));
            long startingOffset = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16));
            long length = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(24));

            return new DiskExtent(diskNumber, startingOffset, length);
        }
        finally
        {
            Marshal.FreeHGlobal(outBuf);
            Win32.CloseHandle(handle);
        }
    }

    private static FileSystemKind ParseFsName(string name) => name.ToUpperInvariant() switch
    {
        "NTFS" => FileSystemKind.Ntfs,
        "EXFAT" => FileSystemKind.ExFat,
        "FAT32" => FileSystemKind.Fat32,
        "FAT" or "FAT16" => FileSystemKind.Fat16,
        "FAT12" => FileSystemKind.Fat12,
        "REFS" => FileSystemKind.ReFS,
        "" => FileSystemKind.Unknown,
        _ => FileSystemKind.Raw,
    };

    private static string BusDisplayName(Win32.StorageBusType bus) => bus switch
    {
        Win32.StorageBusType.Nvme => "NVMe",
        Win32.StorageBusType.Sata => "SATA",
        Win32.StorageBusType.Ata => "ATA",
        Win32.StorageBusType.Usb => "USB",
        Win32.StorageBusType.Sas => "SAS",
        Win32.StorageBusType.Scsi => "SCSI",
        Win32.StorageBusType.RAID => "RAID",
        Win32.StorageBusType.Sd => "SD",
        Win32.StorageBusType.Mmc => "MMC",
        Win32.StorageBusType.Atapi => "ATAPI",
        Win32.StorageBusType.Virtual or Win32.StorageBusType.FileBackedVirtual => "וירטואלי",
        Win32.StorageBusType.Spaces => "Storage Spaces",
        _ => "לא ידוע",
    };
}
