using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using RAF.Core.Model;
using RAF.Core.Native;
using RAF.Core.Repair;

namespace RAF.Core.Disks;

/// <summary>מה יקרה אם תוחזר מחיצה לטבלה — לפני שנכתב דבר.</summary>
public sealed class RestorePlan
{
    public bool CanRestore { get; init; }

    /// <summary>הסבר בעברית: למה אפשר או למה אי אפשר.</summary>
    public string Explanation { get; init; } = "";

    /// <summary>מה בדיוק ייכתב לכונן.</summary>
    public string WhatWillChange { get; init; } = "";

    internal List<(long Offset, byte[] Data)> Writes { get; init; } = new();
}

/// <summary>
/// החזרת מחיצה שנמצאה אל טבלת המחיצות, כדי ש-Windows יראה אותה שוב.
///
/// הכתיבה נוגעת רק בטבלת המחיצות — לא במחיצה עצמה ולא בקבצים. היא בנויה
/// כמו תיקון המחיצה, כך שתהיה הפיכה במלואה:
/// 1. כל סקטור שייכתב נשמר קודם לקובץ ביטול על כונן אחר.
/// 2. הכתיבה מתבצעת.
/// 3. הטבלה נקראת מחדש, ומוודאים שהמחיצה מופיעה בה בדיוק במקומה.
/// 4. אם לא — המצב הקודם מוחזר אוטומטית.
/// </summary>
public static class PartitionTableWriter
{
    private static readonly Guid GuidBasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

    public static RestorePlan Plan(PhysicalDiskInfo disk, FoundPartition found)
    {
        int sector = disk.LogicalSectorSize;

        using var reader = VolumeReader.TryOpen(
            disk.DiskNumber, 0, disk.SizeBytes, sector, sequential: false, applyOverlay: false);
        if (reader is null) return Refuse(L.T("לא ניתן לפתוח את הכונן לקריאה."));

        return Plan(reader, disk.SizeBytes, sector, disk.Partitions, found);
    }

    internal static RestorePlan Plan(
        VolumeReader reader, long diskSize, int sector, IReadOnlyList<PartitionInfo> existing, FoundPartition found)
    {
        // מגזר האתחול שלה קיים רק בזיכרון: ברישום בטבלה Windows היה רואה מחיצה ריקה ומציע לפרמט אותה.
        if (found.Rebuilt is not null)
            return Refuse(L.T("למחיצה הזו אין מגזר אתחול על הכונן — הוא חושב מחדש מרשומות הקבצים, והוא קיים בזיכרון בלבד. " +
                              "רישום שלה בטבלה לא יעזור: Windows יבקש לפרמט אותה. אפשר לסרוק אותה ולהעתיק ממנה קבצים — בלי לכתוב לכונן."));

        if (found.Offset % sector != 0 || found.Size % sector != 0)
            return Refuse(L.T("גבולות המחיצה שנמצאה אינם תקינים, ולכן אי אפשר לרשום אותה בטבלה."));

        if (found.Offset + found.Size > diskSize)
            return Refuse(L.T("המחיצה חורגת מסוף הכונן."));

        var clash = existing.FirstOrDefault(p =>
            p.SizeBytes > 0 && !p.Assumed && found.Offset < p.OffsetBytes + p.SizeBytes && p.OffsetBytes < found.End);
        if (clash is not null)
            return Refuse(
                L.T("המחיצה שנמצאה חופפת למחיצה שכבר קיימת בטבלה") +
                (string.IsNullOrEmpty(clash.DriveLetter) ? "" : $" ({clash.DriveLetter})") +
                L.T(". רישום שתיהן היה גורם לכך שכתיבה לאחת תהרוס את השנייה. " +
                "אפשר עדיין לסרוק אותה ולהעתיק ממנה קבצים — בלי לכתוב לכונן."));

        byte[] sector0 = reader.ReadBlock(0, sector);
        if (sector0.Length < 512) return Refuse(L.T("לא ניתן לקרוא את תחילת הכונן."));

        bool hasSignature = sector0[510] == 0x55 && sector0[511] == 0xAA;
        bool protective = hasSignature && Enumerable.Range(0, 4).Any(i => sector0[446 + i * 16 + 4] == 0xEE);

        if (protective)
            return PlanGpt(reader, sector, found);

        if (hasSignature && !LooksLikeBootSector(sector0))
            return PlanMbr(sector0, sector, found, fresh: false);

        // אין טבלת מחיצות כלל.
        if (found.Offset == 0)
            return Refuse(L.T("המחיצה מתחילה בתחילת הכונן, ולכן היא אינה זקוקה לטבלת מחיצות. " +
                          "אם Windows אינו מזהה אותה, השתמשו בתיקון המחיצה."));

        if (LooksLikeBootSector(sector0))
            return Refuse(L.T("בתחילת הכונן יש מערכת קבצים פעילה. יצירת טבלת מחיצות הייתה דורסת אותה."));

        return PlanMbr(sector0, sector, found, fresh: true);
    }

    // ---------------------------------------------------------------- MBR

    private static RestorePlan PlanMbr(byte[] sector0, int sector, FoundPartition found, bool fresh)
    {
        long startLba = found.Offset / sector;
        long count = found.Size / sector;

        if (startLba > uint.MaxValue || count > uint.MaxValue)
            return Refuse(L.T("המחיצה נמצאת מעבר ל-2TB הראשונים של הכונן, ולכן טבלת MBR אינה יכולה לתאר אותה."));

        byte[] updated = fresh ? new byte[sector] : (byte[])sector0.Clone();

        if (fresh)
        {
            // חתימת דיסק חדשה, כדי ש-Windows יבחין בינו לבין דיסקים אחרים.
            BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(440), (uint)Random.Shared.Next(1, int.MaxValue));
            updated[510] = 0x55;
            updated[511] = 0xAA;
        }

        int slot = -1;
        for (int i = 0; i < 4; i++)
        {
            if (updated[446 + i * 16 + 4] == 0) { slot = i; break; }
        }

        if (slot < 0)
            return Refuse(L.T("כל ארבע הרשומות בטבלת המחיצות של הכונן תפוסות, ואין מקום לרשום מחיצה נוספת."));

        int at = 446 + slot * 16;
        updated.AsSpan(at, 16).Clear();
        updated[at + 1] = 0xFE; updated[at + 2] = 0xFF; updated[at + 3] = 0xFF;   // CHS — "השתמש ב-LBA"
        updated[at + 4] = MbrType(found.FileSystem);
        updated[at + 5] = 0xFE; updated[at + 6] = 0xFF; updated[at + 7] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(at + 8), (uint)startLba);
        BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(at + 12), (uint)count);

        return new RestorePlan
        {
            CanRestore = true,
            Explanation = fresh
                ? L.T("לכונן אין טבלת מחיצות. תיווצר טבלה חדשה, ובה המחיצה שנמצאה.")
                : L.T("בטבלת המחיצות של הכונן יש רשומה פנויה, והמחיצה תירשם בה."),
            WhatWillChange = fresh
                ? L.T("תיכתב טבלת מחיצות חדשה בתחילת הכונן. המחיצה עצמה והקבצים לא ישתנו.")
                : L.T("תשתנה רשומה אחת בטבלת המחיצות, בתחילת הכונן. המחיצה עצמה והקבצים לא ישתנו."),
            Writes = { (0, updated) },
        };
    }

    private static byte MbrType(FileSystemKind kind) => kind switch
    {
        FileSystemKind.Fat32 => 0x0C,
        FileSystemKind.Fat16 => 0x0E,
        FileSystemKind.Fat12 => 0x01,
        _ => 0x07,   // NTFS ו-exFAT
    };

    // ---------------------------------------------------------------- GPT

    private static RestorePlan PlanGpt(VolumeReader reader, int sector, FoundPartition found)
    {
        byte[] header = reader.ReadBlock(sector, sector);
        if (header.Length < 92 || Encoding.ASCII.GetString(header, 0, 8) != "EFI PART")
            return Refuse(L.T("כותרת טבלת ה-GPT של הכונן פגומה, ולכן לא ניתן לרשום בה מחיצה בבטחה."));

        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        long alternateLba = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32));
        long firstUsable = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40));
        long lastUsable = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(48));
        long entriesLba = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(72));
        int entryCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
        int entrySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));

        if (headerSize is < 92 or > 512 || entryCount is <= 0 or > 1024 || entrySize is < 128 or > 1024)
            return Refuse(L.T("כותרת טבלת ה-GPT מכילה ערכים לא תקינים."));

        long firstLba = found.Offset / sector;
        long lastLba = (found.Offset + found.Size) / sector - 1;

        if (firstLba < firstUsable || lastLba > lastUsable)
            return Refuse(L.T("המחיצה חורגת מהאזור שטבלת ה-GPT מאפשרת למחיצות."));

        int arrayBytes = entryCount * entrySize;
        int arraySectors = (arrayBytes + sector - 1) / sector;
        byte[] entries = reader.ReadBlock(entriesLba * sector, arraySectors * sector);
        if (entries.Length < arrayBytes) return Refuse(L.T("לא ניתן לקרוא את רשומות טבלת ה-GPT."));

        int slot = -1;
        for (int i = 0; i < entryCount; i++)
        {
            if (entries.AsSpan(i * entrySize, 16).IndexOfAnyExcept((byte)0) < 0) { slot = i; break; }
        }
        if (slot < 0) return Refuse(L.T("כל הרשומות בטבלת ה-GPT תפוסות."));

        int at = slot * entrySize;
        entries.AsSpan(at, entrySize).Clear();
        GuidBasicData.TryWriteBytes(entries.AsSpan(at));
        Guid.NewGuid().TryWriteBytes(entries.AsSpan(at + 16));
        BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(at + 32), firstLba);
        BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(at + 40), lastLba);
        string name = string.IsNullOrWhiteSpace(found.Label) ? "Recovered" : found.Label;
        byte[] nameBytes = Encoding.Unicode.GetBytes(name.Length > 36 ? name[..36] : name);
        nameBytes.CopyTo(entries, at + 56);

        uint arrayCrc = Crc32.Compute(entries.AsSpan(0, arrayBytes));

        byte[] primary = (byte[])header.Clone();
        SealHeader(primary, headerSize, arrayCrc);

        var writes = new List<(long, byte[])>
        {
            (sector, primary),
            (entriesLba * sector, entries),
        };

        // העותק המשני בסוף הכונן: מעדכנים אותו אם הוא תקין, ובונים אותו מחדש מהראשי אם לא.
        byte[] backup = reader.ReadBlock(alternateLba * sector, sector);
        bool backupValid = backup.Length >= 92 && Encoding.ASCII.GetString(backup, 0, 8) == "EFI PART";

        long backupEntriesLba = backupValid
            ? BinaryPrimitives.ReadInt64LittleEndian(backup.AsSpan(72))
            : lastUsable + 1;

        if (!backupValid)
        {
            backup = (byte[])header.Clone();
            BinaryPrimitives.WriteInt64LittleEndian(backup.AsSpan(24), alternateLba);
            BinaryPrimitives.WriteInt64LittleEndian(backup.AsSpan(32), 1);
            BinaryPrimitives.WriteInt64LittleEndian(backup.AsSpan(72), backupEntriesLba);
        }

        SealHeader(backup, headerSize, arrayCrc);
        writes.Add((backupEntriesLba * sector, entries));
        writes.Add((alternateLba * sector, backup));

        return new RestorePlan
        {
            CanRestore = true,
            Explanation = L.T("בטבלת ה-GPT של הכונן יש רשומה פנויה, והמחיצה תירשם בה."),
            WhatWillChange = L.T("תתווסף רשומה אחת לטבלת ה-GPT — בעותק הראשי שבתחילת הכונן ובעותק המשני שבסופו. " +
                             "המחיצה עצמה והקבצים לא ישתנו."),
            Writes = writes,
        };
    }

    /// <summary>עדכון סכום הביקורת של הרשומות ושל הכותרת עצמה.</summary>
    private static void SealHeader(byte[] header, int headerSize, uint arrayCrc)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(88), arrayCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), Crc32.Compute(header.AsSpan(0, headerSize)));
    }

    // ---------------------------------------------------------------- ביצוע

    public static RepairResult Restore(PhysicalDiskInfo disk, FoundPartition found, string undoFolder)
    {
        int sector = disk.LogicalSectorSize;

        int undoDisk = DiskEnumerator.GetDiskNumberForPath(undoFolder);
        if (undoDisk >= 0 && undoDisk == disk.DiskNumber)
            return new RepairResult { Message = L.T("תיקיית הגיבוי חייבת להיות על כונן אחר מהכונן שמשתנה.") };

        RestorePlan plan;
        var before = new List<(long Offset, byte[] Data)>();

        using (var reader = VolumeReader.TryOpen(
                   disk.DiskNumber, 0, disk.SizeBytes, sector, sequential: false, applyOverlay: false))
        {
            if (reader is null) return new RepairResult { Message = L.T("לא ניתן לפתוח את הכונן לקריאה.") };

            plan = Plan(reader, disk.SizeBytes, sector, disk.Partitions, found);
            if (!plan.CanRestore) return new RepairResult { Message = plan.Explanation };

            foreach (var (offset, data) in plan.Writes)
            {
                byte[] current = reader.ReadBlock(offset, data.Length);
                if (current.Length != data.Length)
                    return new RepairResult { Message = L.T("לא ניתן לקרוא את מה שעומד להשתנות בכונן כדי לגבות אותו, ולכן לא נכתב דבר.") };
                before.Add((offset, current));
            }
        }

        // --- שלב 1: גיבוי ---
        var undo = UndoFile.For(disk, UndoKind.PartitionTable,
            before.Zip(plan.Writes, (b, a) => new UndoRegion(b.Offset, b.Data, a.Data)).ToList());

        string undoPath;
        try
        {
            Directory.CreateDirectory(undoFolder);
            undoPath = Path.Combine(undoFolder,
                $"RAF-undo-table-disk{disk.DiskNumber}-{DateTime.Now:yyyyMMdd-HHmmss}.bin");
            undo.Save(undoPath);
        }
        catch (Exception ex)
        {
            return new RepairResult { Message = L.T("לא ניתן ליצור קובץ גיבוי, ולכן דבר לא נכתב: {0}", ex.Message) };
        }

        // --- שלב 2: כתיבה ---
        if (!WriteAll(disk.DiskNumber, sector, plan.Writes))
        {
            bool undone = Undo(undoPath, sector);
            return new RepairResult
            {
                RolledBack = undone,
                UndoFile = undoPath,
                Message = L.T("הכתיבה לכונן נכשלה (שגיאת Windows {0}). ", RawWriter.LastError) +
                          (undone ? L.T("המצב הקודם הוחזר.") : L.T("קובץ הגיבוי שמור ב: {0}", undoPath)),
            };
        }

        RefreshLayout(disk.DiskNumber);

        // --- שלב 3: אימות ---
        bool listed;
        using (var device = RawDevice.TryOpen(DevicePaths.PathOf(disk.DiskNumber)!, sector))
        {
            listed = device is not null && PartitionTableReader.Read(device, disk.DiskNumber, disk.SizeBytes)
                .Partitions.Any(p => p.OffsetBytes == found.Offset && p.SizeBytes == found.Size);
        }

        if (listed)
        {
            return new RepairResult
            {
                Succeeded = true,
                UndoFile = undoPath,
                Message = L.T("המחיצה הוחזרה לטבלת המחיצות. ") +
                          (found.BootSectorDamaged
                              ? L.T("תחילת המחיצה (מגזר האתחול) עדיין פגומה, ולכן Windows יציג אותה כמחיצה שדורשת פירמוט — " +
                                "אל תפרמט. השלב הבא הוא תיקון המחיצה, או העתקת הקבצים דרך עותק הגיבוי. ")
                              : L.T("אם היא עדיין לא מופיעה ב-Windows, נתקו וחברו את הכונן או הפעילו מחדש את המחשב. ")) +
                          L.T("גיבוי המצב הקודם נשמר ב: {0}", undoPath),
            };
        }

        // --- שלב 4: ביטול ---
        bool restored = Undo(undoPath, sector);
        RefreshLayout(disk.DiskNumber);

        return new RepairResult
        {
            RolledBack = restored,
            UndoFile = undoPath,
            Message = restored
                ? L.T("הטבלה נכתבה, אך המחיצה לא הופיעה בה כמצופה — ולכן המצב הקודם הוחזר אוטומטית. הכונן נותר כפי שהיה.")
                : L.T("הטבלה נכתבה, המחיצה לא הופיעה בה, וגם החזרת המצב הקודם נכשלה. קובץ הגיבוי שמור ב: {0}", undoPath),
        };
    }

    /// <summary>
    /// ביטול מיד אחרי הכתיבה: כתיבת כל הסקטורים המקוריים חזרה למקומם, לפי מספר הדיסק
    /// שבקובץ. ביטול מאוחר עובר דרך UndoService, שמוודא קודם שזה עדיין הכונן הנכון.
    /// </summary>
    public static bool Undo(string undoPath, int sectorSize)
    {
        var undo = UndoFile.Load(undoPath);
        if (undo is null || undo.Legacy) return false;

        bool ok = undo.WriteBefore(undo.DiskNumber);
        RefreshLayout(undo.DiskNumber);
        return ok;
    }

    private static bool WriteAll(int diskNumber, int sectorSize, List<(long Offset, byte[] Data)> regions)
    {
        string? target = DevicePaths.PathOf(diskNumber);
        if (target is null) return false;

        using var writer = RawWriter.TryOpen(target, sectorSize);
        if (writer is null) return false;

        foreach (var (offset, data) in regions)
            if (!writer.Write(offset, data)) return false;

        return true;
    }

    /// <summary>בקשה מ-Windows לקרוא מחדש את טבלת המחיצות של הכונן.</summary>
    internal static void RefreshLayout(int diskNumber)
    {
        if (DevicePaths.IsImage(diskNumber)) return;

        IntPtr handle = Win32.CreateFile(
            $@"\\.\PhysicalDrive{diskNumber}", Win32.GENERIC_READ | Win32.GENERIC_WRITE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle == Win32.INVALID_HANDLE_VALUE || handle == IntPtr.Zero) return;

        try
        {
            Win32.DeviceIoControl(handle, Win32.IOCTL_DISK_UPDATE_PROPERTIES,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    /// <summary>האם הסקטור הוא מגזר אתחול של מערכת קבצים (ולא MBR).</summary>
    private static bool LooksLikeBootSector(byte[] sector0)
        => FileSystemIdentifier.Identify(sector0).Kind is not (FileSystemKind.Unknown or FileSystemKind.Raw);

    private static RestorePlan Refuse(string why) => new() { CanRestore = false, Explanation = why };
}

/// <summary>CRC-32 (IEEE 802.3), כפי שטבלת GPT דורשת.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
