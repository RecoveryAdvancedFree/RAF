using RAF.Core.Disks;
using RAF.Core.Model;
using RAF.Core.Native;
using RAF.Core.Raid;

namespace RAF.Core.Imaging;

/// <summary>
/// הרכבת מערך RAID של לינוקס (mdadm — שרתי אחסון ביתיים ושרתי לינוקס) מהכוננים שלו,
/// בלי לינוקס ובלי השרת. כל כונן במערך נושא כותרת שמתארת את המערך במדויק; התוכנה
/// מחפשת אותה בכל הכוננים, במחיצות ובתמונות שפתוחות, ומקבצת לפי מזהה המערך.
/// המערך המורכב מופיע ברשימה ככונן נוסף, לקריאה בלבד — וכל הסריקות עובדות עליו.
/// </summary>
public static class RaidDisk
{
    /// <summary>כונן שנמצא במערך.</summary>
    /// <param name="Stale">הכונן נפל מהמערך לפני האחרים (מונה השינויים שלו ישן) — הנתונים בו לא עדכניים.</param>
    public sealed record Member(int Disk, long Offset, long Size, string Title, int Role, bool Stale);

    /// <summary>מערך שנמצא — גם אם חסרים בו כוננים.</summary>
    /// <param name="Problem">למה אי אפשר להרכיב אותו, או null — אפשר.</param>
    public sealed record Found(
        string Id, string Name, string Level, int Disks, long Size, long Chunk,
        List<Member> Members, List<int> MissingRoles, string? Problem, DateTime? Updated)
    {
        internal MdSuperblock Super { get; init; } = null!;
        internal Dictionary<int, (Member Member, MdSuperblock Super)> ByRole { get; init; } = new();
    }

    /// <summary>
    /// חיפוש מערכים בכל הכוננים. בכל כונן — כל מחיצה, וכונן בלי מחיצות — כולו.
    /// קריאה בלבד: כמה קריאות קטנות לכל מחיצה.
    /// </summary>
    public static List<Found> Find(IEnumerable<PhysicalDiskInfo> disks)
    {
        var hits = new List<(Member Member, MdSuperblock Super)>();
        foreach (var disk in disks)
        {
            if (!disk.RawAccessible || disk.Unresponsive) continue;
            if (disk.ImagePath is { } own && DevicePaths.IsRaidPath(own)) continue;

            var places = disk.Partitions.Count > 0
                ? disk.Partitions.Select(p => (p.OffsetBytes, p.SizeBytes, Title(disk, p)))
                : new[] { (0L, disk.SizeBytes, disk.Model) };
            foreach (var (offset, size, title) in places)
            {
                if (Probe(disk.DiskNumber, offset, size, disk.LogicalSectorSize) is not { } sb) continue;
                hits.Add((new Member(disk.DiskNumber, offset, size, title, sb.Role, false), sb));
            }
        }

        var arrays = new List<Found>();
        foreach (var group in hits.GroupBy(h => h.Super.ArrayId))
        {
            ulong newest = group.Max(h => h.Super.Events);
            var reference = group.First(h => h.Super.Events == newest).Super;

            // לכל מקום במערך — הכונן העדכני ביותר. כונן שנפל מוקדם יותר נכנס רק אם אין אחר.
            var byRole = new Dictionary<int, (Member, MdSuperblock)>();
            foreach (var h in group.Where(h => h.Super.Role >= 0 && h.Super.Role < reference.RaidDisks)
                                   .OrderByDescending(h => h.Super.Events))
            {
                if (byRole.ContainsKey(h.Super.Role)) continue;
                byRole[h.Super.Role] = (h.Member with { Stale = h.Super.Events + 1 < newest }, h.Super);
            }
            // כונן ישן מאוד — רק אם בלעדיו המערך לא נקרא. אחרת הוא היה מחזיר נתונים ישנים.
            var fresh = byRole.Where(r => !r.Value.Item1.Stale).ToDictionary(r => r.Key, r => r.Value);
            var array = Geometry(reference, fresh);
            if (array.Unsupported() is not null && fresh.Count < byRole.Count && Geometry(reference, byRole).Unsupported() is null)
                array = Geometry(reference, byRole);
            else
                byRole = fresh;

            var missing = Enumerable.Range(0, reference.RaidDisks).Where(r => !byRole.ContainsKey(r)).ToList();
            string? problem = reference.Reshaping
                ? L.T("המערך היה באמצע שינוי מבנה (הוספת כונן או שינוי סוג) כשנעצר. מערך כזה עוד לא נתמך.")
                : array.Unsupported();

            arrays.Add(new Found(
                reference.ArrayId.ToString("N"),
                string.IsNullOrEmpty(reference.Name) ? "" : reference.Name,
                RaidArray.LevelName(reference.Level),
                reference.RaidDisks,
                array.Size,
                reference.ChunkBytes,
                byRole.OrderBy(r => r.Key).Select(r => r.Value.Item1).ToList(),
                missing,
                problem,
                reference.Updated)
            {
                Super = reference,
                ByRole = byRole,
            });
        }
        return arrays;
    }

    private static string Title(PhysicalDiskInfo disk, PartitionInfo p)
        => disk.Partitions.Count == 1 && p.OffsetBytes == 0 ? disk.Model : L.T("{0} · מחיצה {1}", disk.Model, p.Index + 1);

    private static MdSuperblock? Probe(int disk, long offset, long size, int sectorSize)
    {
        try
        {
            using var reader = VolumeReader.TryOpen(disk, offset, size, sectorSize, sequential: false, applyOverlay: false);
            if (reader is null) return null;
            return MdSuperblock.Find((at, length) => reader.ReadBlock(at, length), size);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static RaidArray Geometry(MdSuperblock sb, Dictionary<int, (Member Member, MdSuperblock Super)> byRole)
    {
        int n = sb.RaidDisks;
        var offsets = new long[n];
        var sizes = new long[n];
        var present = new bool[n];
        for (int r = 0; r < n; r++)
        {
            if (byRole.TryGetValue(r, out var m))
            {
                offsets[r] = m.Super.DataOffset;
                sizes[r] = m.Super.DataSize;
                present[r] = true;
            }
            else
            {
                // כונן חסר: אותם ערכים כמו בכונן שנמצא (בשרשור הגודל שלו לא ידוע — ואז המערך ממילא לא נקרא).
                offsets[r] = sb.DataOffset;
                sizes[r] = sb.DataSize;
            }
        }
        return new RaidArray(sb.Level, sb.Layout, sb.ChunkBytes, n, sb.ComponentSize, offsets, sizes, present);
    }

    /// <summary>
    /// מבנה שזוהה למערך בלי כותרת (כרטיס RAID). Order — מספרי הכוננים לפי מקומם במערך.
    /// Confident — הניחוש הזה בלבד מתיישב עם מערכת הקבצים; אחרת יש כמה אפשרויות.
    /// </summary>
    public sealed record Detected(string Level, int LevelNumber, int Layout, long Chunk, List<int> Order, string FileSystem, bool Confident);

    /// <summary>
    /// זיהוי המבנה של מערך בלי כותרת מהכוננים שהמשתמש בחר (כוננים שלמים; הנתונים מתחילים
    /// בתחילתם). קריאה בלבד. ריק — לא זוהה.
    /// </summary>
    public static List<Detected> Detect(IReadOnlyList<PhysicalDiskInfo> disks, CancellationToken token = default)
    {
        var readers = disks.Select(d => VolumeReader.TryOpen(d.DiskNumber, 0, d.SizeBytes, d.LogicalSectorSize, sequential: false, applyOverlay: false)
            ?? throw new IOException(RawDevice.OpenFailure(d.Model))).ToList();
        try
        {
            var guesses = RaidDetector.Detect((disk, at, length) => readers[disk].ReadBlock(at, length),
                disks.Select(d => d.SizeBytes).ToArray(), token);
            if (guesses.Count == 0) return new();
            // בטוח: הניחוש הטוב ביותר עדיף בבירור על הבא אחריו.
            int best = guesses[0].Matches - 10 * guesses[0].Mismatches;
            bool confident = guesses.Count == 1 || guesses[1].Matches - 10 * guesses[1].Mismatches < best * 0.9;
            return guesses.Take(5).Select((g, i) => new Detected(
                RaidArray.LevelName(g.Level), g.Level, g.Layout, g.Chunk,
                g.Order.Select(o => disks[o].DiskNumber).ToList(), g.FileSystem, i == 0 && confident)).ToList();
        }
        finally
        {
            foreach (var r in readers) r.Dispose();
        }
    }

    /// <summary>הרכבת מערך בלי כותרת לפי מבנה שזוהה (או שהמשתמש קבע). לקריאה בלבד.</summary>
    public static PhysicalDiskInfo Assemble(Detected detected, IReadOnlyList<PhysicalDiskInfo> disks)
    {
        var ordered = detected.Order.Select(n => disks.First(d => d.DiskNumber == n)).ToList();
        int count = ordered.Count;
        var array = new RaidArray(detected.LevelNumber, detected.Layout, detected.Chunk, count, 0,
            new long[count], ordered.Select(d => d.SizeBytes).ToArray(), Enumerable.Repeat(true, count).ToArray());
        string id = "hw-" + string.Join("-", detected.Order) + $"-{detected.LevelNumber}-{detected.Layout}-{detected.Chunk}";
        var members = ordered.Select(d => ((int, long)?)(d.DiskNumber, 0L)).ToArray();
        return Register(id, array, members, detected.Level, L.T("מערך {0} בלי כותרת (של כרטיס RAID), שהתוכנה זיהתה והרכיבה מ-{1} כוננים: רצועה של {2}KB. הקריאה בלבד — שום דבר לא נכתב לכוננים.",
            detected.Level, count, detected.Chunk / 1024), null);
    }

    /// <summary>הרכבת המערך. הוא מופיע ברשימה ככונן נוסף, לקריאה בלבד.</summary>
    public static PhysicalDiskInfo Assemble(Found found)
    {
        if (found.Problem is not null) throw new InvalidOperationException(found.Problem);

        var array = Geometry(found.Super, found.ByRole);
        var members = new (int Disk, long Offset)?[found.Disks];
        foreach (var (role, (member, _)) in found.ByRole) members[role] = (member.Disk, member.Offset);
        return Register(found.Id, array, members, found.Name.Length > 0 ? found.Name : found.Level, null, found);
    }

    private static PhysicalDiskInfo Register(string id, RaidArray array, (int Disk, long Offset)?[] members, string name, string? fixedNote, Found? found)
    {
        int number = DevicePaths.RegisterRaid(id, new DevicePaths.RaidSource(array, members));
        try
        {
            using var device = RawDevice.TryOpen(DevicePaths.ImagePathOf(number)!, 512, sequential: false)
                ?? throw new IOException(RawDevice.OpenFailure(L.T("אחד מכונני המערך")));

            var table = PartitionTableReader.Read(device, number, array.Size);
            var partitions = table.Partitions;
            var scheme = table.Scheme;
            byte[] head = device.ReadBlock(0, FileSystemIdentifier.HeadBytes);
            if (partitions.Count == 0)
            {
                var fs = FileSystemIdentifier.Identify(head);
                var kind = fs.Kind is FileSystemKind.Unknown ? FileSystemKind.Raw : fs.Kind;
                scheme = PartitionScheme.SuperFloppy;
                partitions = new List<PartitionInfo>
                {
                    new()
                    {
                        Index = 0,
                        DiskNumber = number,
                        OffsetBytes = 0,
                        SizeBytes = array.Size,
                        FileSystem = kind,
                        Label = fs.Label,
                        TypeName = FileSystemIdentifier.DisplayName(kind),
                    },
                };
            }

            string note = fixedNote ?? L.T("מערך {0} של לינוקס, שהתוכנה הרכיבה מ-{1} כוננים. הקריאה בלבד — שום דבר לא נכתב לכוננים.",
                found!.Level, found.Members.Count);
            if (found is { MissingRoles.Count: > 0 })
                note += " " + (array.Level is 1 or 10
                    ? L.T("חסרים {0} כוננים — הנתונים נקראים מהעותקים שבכוננים האחרים.", found.MissingRoles.Count)
                    : L.T("חסר כונן אחד — התוכן שלו מחושב מהזוגיות שבכוננים האחרים. הקריאה איטית יותר, וכל פגם נוסף באחד הכוננים יפגע בקבצים."));
            if (found is not null && found.Members.Any(m => m.Stale))
                note += " " + L.T("אחד הכוננים נפל מהמערך לפני האחרים, והנתונים בו אינם עדכניים — קבצים שנכתבו אחרי שנפל עלולים לחזור פגומים.");
            if (partitions.Any(p => p.FileSystem == FileSystemKind.Lvm))
                note += " " + L.T("בתוך המערך יש מאגר לוגי — כמו ברוב שרתי האחסון הביתיים. לחצו עליו כדי לפתוח את האזורים שבו.");

            return new PhysicalDiskInfo
            {
                DiskNumber = number,
                Model = L.T("{0} — מערך מורכב", name),
                BusType = "מערך RAID",   // לא לתרגום: תווית, הממשק מתרגם
                SizeBytes = array.Size,
                LogicalSectorSize = 512,
                PhysicalSectorSize = 512,
                Media = MediaKind.Image,
                Trim = TrimState.NotSupported,
                Scheme = scheme,
                RawAccessible = true,
                Partitions = partitions,
                ImagePath = DevicePaths.ImagePathOf(number),
                ImageNote = note,
            };
        }
        catch
        {
            DevicePaths.UnregisterImage(number);
            throw;
        }
    }
}
