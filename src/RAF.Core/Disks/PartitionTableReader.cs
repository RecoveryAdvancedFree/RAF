using System.Buffers.Binary;
using System.Text;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Disks;

/// <summary>
/// קריאת טבלת המחיצות ישירות מהדיסק הגולמי — MBR או GPT.
/// הקריאה אינה מסתמכת על Windows, ולכן מזהה גם מחיצות שהמערכת אינה מכירה.
/// </summary>
internal static class PartitionTableReader
{
    private static readonly Guid GuidBasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    private static readonly Guid GuidEfiSystem = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    private static readonly Guid GuidMsReserved = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    private static readonly Guid GuidWinRecovery = new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");
    private static readonly Guid GuidLinuxData = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    private static readonly Guid GuidLinuxSwap = new("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F");
    private static readonly Guid GuidAppleApfs = new("7C3457EF-0000-11AA-AA11-00306543ECAC");
    private static readonly Guid GuidAppleHfs = new("48465300-0000-11AA-AA11-00306543ECAC");

    internal readonly record struct TableResult(PartitionScheme Scheme, List<PartitionInfo> Partitions);

    /// <summary>קריאה וניתוח של טבלת המחיצות של דיסק פתוח.</summary>
    internal static TableResult Read(RawDevice device, int diskNumber, long diskSize)
    {
        byte[] sector0 = device.ReadBlock(0, Math.Max(device.SectorSize, 512));
        if (sector0.Length < 512)
            return new TableResult(PartitionScheme.Unknown, new List<PartitionInfo>());

        bool hasMbrSignature = sector0[510] == 0x55 && sector0[511] == 0xAA;

        // MBR מגן (Protective MBR) עם רשומה מסוג 0xEE מצביע על דיסק GPT.
        bool looksLikeGpt = hasMbrSignature && HasProtectiveEntry(sector0);
        if (looksLikeGpt || IsGptHeaderPresent(device))
        {
            var gpt = ReadGpt(device, diskNumber);
            if (gpt.Count > 0) return new TableResult(PartitionScheme.Gpt, gpt);
        }

        if (hasMbrSignature)
        {
            var mbr = ReadMbr(device, sector0, diskNumber, diskSize);
            if (mbr.Count > 0) return new TableResult(PartitionScheme.Mbr, mbr);
        }

        // ללא טבלת מחיצות: ייתכן שמערכת הקבצים יושבת ישירות על ההתקן (נפוץ בכרטיסי זיכרון).
        var direct = FileSystemIdentifier.Identify(device.ReadBlock(0, 2048));
        if (direct.Kind is not (FileSystemKind.Unknown or FileSystemKind.Raw))
        {
            return new TableResult(PartitionScheme.SuperFloppy, new List<PartitionInfo>
            {
                new()
                {
                    Index = 0, DiskNumber = diskNumber, OffsetBytes = 0, SizeBytes = diskSize,
                    FileSystem = direct.Kind, Label = direct.Label,
                    TypeName = FileSystemIdentifier.DisplayName(direct.Kind),
                }
            });
        }

        return new TableResult(PartitionScheme.Unknown, new List<PartitionInfo>());
    }

    private static bool HasProtectiveEntry(byte[] sector0)
    {
        for (int i = 0; i < 4; i++)
            if (sector0[446 + i * 16 + 4] == 0xEE) return true;
        return false;
    }

    private static bool IsGptHeaderPresent(RawDevice device)
    {
        byte[] header = device.ReadBlock(device.SectorSize, 512);
        return header.Length >= 8 && Encoding.ASCII.GetString(header, 0, 8) == "EFI PART";
    }

    // ---------------------------------------------------------------- GPT

    private static List<PartitionInfo> ReadGpt(RawDevice device, int diskNumber)
    {
        var result = new List<PartitionInfo>();
        int sectorSize = device.SectorSize;

        byte[] header = device.ReadBlock(sectorSize, 512);
        if (header.Length < 92 || Encoding.ASCII.GetString(header, 0, 8) != "EFI PART")
            return result;

        long entryArrayLba = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(72));
        int entryCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
        int entrySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));

        // הגנה מפני כותרת פגומה שתגרום להקצאת זיכרון עצומה.
        if (entryCount is <= 0 or > 512 || entrySize is < 128 or > 4096) return result;

        byte[] entries = device.ReadBlock(entryArrayLba * sectorSize, entryCount * entrySize);
        int index = 0;

        for (int i = 0; i < entryCount; i++)
        {
            int at = i * entrySize;
            if (at + entrySize > entries.Length) break;

            var typeGuid = new Guid(entries.AsSpan(at, 16));
            if (typeGuid == Guid.Empty) continue; // רשומה ריקה

            var partGuid = new Guid(entries.AsSpan(at + 16, 16));
            long firstLba = BinaryPrimitives.ReadInt64LittleEndian(entries.AsSpan(at + 32));
            long lastLba = BinaryPrimitives.ReadInt64LittleEndian(entries.AsSpan(at + 40));
            ulong attributes = BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(at + 48));

            string name = Encoding.Unicode.GetString(entries, at + 56, 72).TrimEnd('\0').Trim();

            long offset = firstLba * sectorSize;
            long size = (lastLba - firstLba + 1) * sectorSize;
            if (size <= 0) continue;

            var fs = FileSystemIdentifier.Identify(device.ReadBlock(offset, 2048));

            result.Add(new PartitionInfo
            {
                Index = index++,
                DiskNumber = diskNumber,
                OffsetBytes = offset,
                SizeBytes = size,
                FileSystem = fs.Kind,
                TypeGuid = typeGuid,
                PartitionGuid = partGuid,
                TypeName = GptTypeName(typeGuid),
                Label = !string.IsNullOrEmpty(name) ? name : fs.Label,
                // ביט 62 = "מוסתר", ביט 0 = "נדרש למערכת"
                IsHidden = (attributes & (1UL << 62)) != 0,
                IsBootable = typeGuid == GuidEfiSystem,
            });
        }

        return result;
    }

    // לא לתרגום: מכאן — תוויות קצרות; הממשק מתרגם (lang-en.js), ו"שמור למערכת" גם מושווה שם
    private static string GptTypeName(Guid type)
    {
        if (type == GuidBasicData) return "נתונים בסיסיים";
        if (type == GuidEfiSystem) return "מחיצת מערכת EFI";
        if (type == GuidMsReserved) return "שמור למערכת";
        if (type == GuidWinRecovery) return "שחזור Windows";
        if (type == GuidLinuxData) return "נתוני Linux";
        if (type == GuidLinuxSwap) return "Linux Swap";
        if (type == GuidAppleApfs) return "Apple APFS";
        if (type == GuidAppleHfs) return "Apple HFS+";
        return "מחיצה";
    }
    // לא לתרגום: עד כאן

    // ---------------------------------------------------------------- MBR

    private static List<PartitionInfo> ReadMbr(RawDevice device, byte[] sector0, int diskNumber, long diskSize)
    {
        var result = new List<PartitionInfo>();
        int sectorSize = device.SectorSize;
        int index = 0;

        for (int i = 0; i < 4; i++)
        {
            int at = 446 + i * 16;
            byte status = sector0[at];
            byte type = sector0[at + 4];
            uint startLba = BinaryPrimitives.ReadUInt32LittleEndian(sector0.AsSpan(at + 8));
            uint sectors = BinaryPrimitives.ReadUInt32LittleEndian(sector0.AsSpan(at + 12));

            if (type == 0 || sectors == 0) continue;
            if (type == 0xEE) continue; // רשומת MBR מגן — טופלה כבר במסלול GPT

            // מחיצה מורחבת: יש לעקוב אחרי שרשרת ה-EBR כדי למצוא את המחיצות הלוגיות.
            if (type is 0x05 or 0x0F or 0x85)
            {
                ReadExtendedChain(device, (long)startLba * sectorSize, diskNumber, ref index, result);
                continue;
            }

            long offset = (long)startLba * sectorSize;
            long size = (long)sectors * sectorSize;
            var fs = FileSystemIdentifier.Identify(device.ReadBlock(offset, 2048));

            result.Add(new PartitionInfo
            {
                Index = index++,
                DiskNumber = diskNumber,
                OffsetBytes = offset,
                SizeBytes = size,
                FileSystem = fs.Kind,
                TypeName = MbrTypeName(type, fs.Kind),
                Label = fs.Label,
                IsBootable = status == 0x80,
                IsHidden = type is 0x11 or 0x14 or 0x16 or 0x17 or 0x1B or 0x1C or 0x1E,
            });
        }

        return result;
    }

    /// <summary>מעבר על שרשרת רשומות ה-EBR של מחיצה מורחבת.</summary>
    private static void ReadExtendedChain(
        RawDevice device, long extendedBase, int diskNumber, ref int index, List<PartitionInfo> result)
    {
        int sectorSize = device.SectorSize;
        long current = extendedBase;
        var visited = new HashSet<long>();

        // תקרת איטרציות כהגנה מפני שרשרת פגומה או מעגלית.
        for (int guard = 0; guard < 128; guard++)
        {
            if (!visited.Add(current)) break;

            byte[] ebr = device.ReadBlock(current, 512);
            if (ebr.Length < 512 || ebr[510] != 0x55 || ebr[511] != 0xAA) break;

            // רשומה ראשונה: המחיצה הלוגית עצמה, יחסית לתחילת ה-EBR הנוכחי.
            byte type = ebr[446 + 4];
            uint startLba = BinaryPrimitives.ReadUInt32LittleEndian(ebr.AsSpan(446 + 8));
            uint sectors = BinaryPrimitives.ReadUInt32LittleEndian(ebr.AsSpan(446 + 12));

            if (type != 0 && sectors != 0)
            {
                long offset = current + (long)startLba * sectorSize;
                long size = (long)sectors * sectorSize;
                var fs = FileSystemIdentifier.Identify(device.ReadBlock(offset, 2048));

                result.Add(new PartitionInfo
                {
                    Index = index++,
                    DiskNumber = diskNumber,
                    OffsetBytes = offset,
                    SizeBytes = size,
                    FileSystem = fs.Kind,
                    TypeName = MbrTypeName(type, fs.Kind),
                    Label = fs.Label,
                });
            }

            // רשומה שנייה: מצביעה ל-EBR הבא, יחסית לבסיס המחיצה המורחבת.
            byte nextType = ebr[462 + 4];
            uint nextStart = BinaryPrimitives.ReadUInt32LittleEndian(ebr.AsSpan(462 + 8));
            if (nextType is not (0x05 or 0x0F or 0x85) || nextStart == 0) break;

            current = extendedBase + (long)nextStart * sectorSize;
        }
    }

    /// <summary>
    /// שם סוג המחיצה. הסוג 0x07 משותף ל-NTFS ול-exFAT — כשמערכת הקבצים זוהתה
    /// מתוך המחיצה עצמה, מוצג רק מה שנמצא בפועל.
    /// </summary>
    private static string MbrTypeName(byte type, FileSystemKind detected) => type switch
    {
        0x01 => "FAT12",
        0x04 or 0x06 or 0x0E => "FAT16",
        0x07 when detected == FileSystemKind.Ntfs => "NTFS",
        0x07 when detected == FileSystemKind.ExFat => "exFAT",
        0x07 => "NTFS / exFAT",
        0x0B or 0x0C => "FAT32",
        0x11 or 0x14 or 0x16 or 0x17 or 0x1B or 0x1C or 0x1E => "מחיצה מוסתרת",   // לא לתרגום: תווית
        0x27 => "שחזור Windows",   // לא לתרגום: תווית
        0x82 => "Linux Swap",
        0x83 => "Linux",
        0x8E => "Linux LVM",
        0xA5 or 0xA6 => "BSD",
        0xAF => "Apple HFS+",
        0xFD => "Linux RAID",
        _ => L.T("סוג 0x{0}", type.ToString("X2")),
    };
}
