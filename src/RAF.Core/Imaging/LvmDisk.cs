using RAF.Core.Disks;
using RAF.Core.Lvm;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Imaging;

/// <summary>
/// מאגר לוגי של לינוקס (LVM) — שכבה שמחלקת כונן או מערך לאזורים. היא קיימת ברוב שרתי
/// האחסון הביתיים (מעל מערך ה-RAID) ובהרבה מחשבי לינוקס. התוכנה קוראת את התיאור שנשמר
/// בכוננים, מקבצת את הכוננים של כל מאגר, ופותחת כל אזור ככונן נוסף, לקריאה בלבד.
/// </summary>
public static class LvmDisk
{
    public sealed record Pv(int Disk, long Offset, string Title);

    /// <summary>אזור במאגר. Problem — למה אי אפשר לפתוח אותו, או null.</summary>
    public sealed record Volume(string Name, long Size, string? Problem);

    /// <summary>מאגר שנמצא: הכוננים שלו (null — חסר) והאזורים.</summary>
    public sealed record Found(string Id, string Name, List<Pv?> Pvs, List<string> Missing, List<Volume> Volumes)
    {
        internal LvmGroup Group { get; init; } = null!;
    }

    public static List<Found> Find(IEnumerable<PhysicalDiskInfo> disks)
    {
        var hits = new List<(Pv Pv, LvmLabel Label, LvmGroup Group)>();
        foreach (var disk in disks)
        {
            if (!disk.RawAccessible || disk.Unresponsive) continue;
            if (disk.ImagePath is { } own && DevicePaths.IsRaidPath(own) && own.Contains(":lvm-", StringComparison.Ordinal)) continue;

            var places = disk.Partitions.Count > 0
                ? disk.Partitions.Select(p => (p.OffsetBytes, p.SizeBytes, disk.Partitions.Count == 1 && p.OffsetBytes == 0 ? disk.Model : L.T("{0} · מחיצה {1}", disk.Model, p.Index + 1)))
                : new[] { (0L, disk.SizeBytes, disk.Model) };
            foreach (var (offset, size, title) in places)
            {
                if (Probe(disk.DiskNumber, offset, size, disk.LogicalSectorSize) is not { Metadata: { } text } label) continue;
                if (LvmGroup.Parse(text) is not { } group) continue;
                hits.Add((new Pv(disk.DiskNumber, offset, title), label, group));
            }
        }

        var found = new List<Found>();
        foreach (var byGroup in hits.GroupBy(h => h.Group.Id))
        {
            // התיאור העדכני ביותר (מספר הגרסה הגבוה) קובע.
            var group = byGroup.OrderByDescending(h => h.Group.Sequence).First().Group;
            var pvs = group.Pvs.Select(p => (Pv?)byGroup.FirstOrDefault(h => h.Label.PvId == p.Id).Pv).ToList();
            var missing = group.Pvs.Where((p, i) => pvs[i] is null).Select(p => p.Name).ToList();

            var volumes = group.Volumes.Where(v => v.Visible).Select(v => new Volume(v.Name, v.Size,
                v.Problem ?? (v.UsesPvs.Any(i => pvs[i] is null)
                    ? L.T("חלק מהאזור יושב על כונן שלא נמצא. חברו את כל הכוננים של השרת ולחצו חיפוש שוב.")
                    : null))).ToList();
            found.Add(new Found(group.Id, group.Name, pvs, missing, volumes) { Group = group });
        }
        return found;
    }

    private static LvmLabel? Probe(int disk, long offset, long size, int sectorSize)
    {
        try
        {
            using var reader = VolumeReader.TryOpen(disk, offset, size, sectorSize, sequential: false, applyOverlay: false);
            return reader is null ? null : LvmLabel.Read((at, length) => reader.ReadBlock(at, length), size);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>פתיחת אזור במאגר ככונן נוסף, לקריאה בלבד.</summary>
    public static PhysicalDiskInfo Open(Found found, string volumeName)
    {
        var info = found.Volumes.FirstOrDefault(v => v.Name == volumeName)
            ?? throw new InvalidOperationException(L.T("האזור לא נמצא. חפשו שוב."));
        if (info.Problem is not null) throw new InvalidOperationException(info.Problem);
        var volume = found.Group.Volumes.First(v => v.Name == volumeName);

        var members = found.Pvs.Select(p => p is null ? ((int, long)?)null : (p.Disk, p.Offset)).ToArray();
        int number = DevicePaths.RegisterRaid($"lvm-{found.Id}-{volumeName}", new DevicePaths.RaidSource(volume, members));
        try
        {
            using var device = RawDevice.TryOpen(DevicePaths.ImagePathOf(number)!, 512, sequential: false)
                ?? throw new IOException(RawDevice.OpenFailure(L.T("אחד מכונני המאגר")));

            var table = PartitionTableReader.Read(device, number, volume.Size);
            var partitions = table.Partitions;
            var scheme = table.Scheme;
            if (partitions.Count == 0)
            {
                var fs = FileSystemIdentifier.Identify(device.ReadBlock(0, FileSystemIdentifier.HeadBytes));
                var kind = fs.Kind is FileSystemKind.Unknown ? FileSystemKind.Raw : fs.Kind;
                scheme = PartitionScheme.SuperFloppy;
                partitions = new List<PartitionInfo>
                {
                    new()
                    {
                        Index = 0, DiskNumber = number, OffsetBytes = 0, SizeBytes = volume.Size,
                        FileSystem = kind, Label = fs.Label, TypeName = FileSystemIdentifier.DisplayName(kind),
                    },
                };
            }

            int pvCount = found.Pvs.Count(p => p is not null);
            return new PhysicalDiskInfo
            {
                DiskNumber = number,
                Model = $"{found.Name} / {volumeName}",
                BusType = "מאגר לוגי",   // לא לתרגום: תווית, הממשק מתרגם
                SizeBytes = volume.Size,
                LogicalSectorSize = 512,
                PhysicalSectorSize = 512,
                Media = MediaKind.Image,
                Trim = TrimState.NotSupported,
                Scheme = scheme,
                RawAccessible = true,
                Partitions = partitions,
                ImagePath = DevicePaths.ImagePathOf(number),
                ImageNote = L.T("אזור \"{0}\" במאגר הלוגי \"{1}\" של לינוקס, שהתוכנה פתחה מ-{2} כוננים. הקריאה בלבד — שום דבר לא נכתב לכוננים.",
                    volumeName, found.Name, pvCount),
            };
        }
        catch
        {
            DevicePaths.UnregisterImage(number);
            throw;
        }
    }
}
