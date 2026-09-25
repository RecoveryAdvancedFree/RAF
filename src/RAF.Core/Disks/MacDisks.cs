using System.Diagnostics;
using System.Xml.Linq;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Disks;

/// <summary>
/// הכוננים במק: הרשימה והמאפיינים מ-diskutil, ומבנה המחיצות — מהכונן עצמו, כמו בכל
/// כונן אחר (טבלת המחיצות ומערכות הקבצים נקראות מהסקטורים). הכונן נקרא דרך
/// ‎/dev/rdiskN — ההתקן "הגולמי", בלי המטמון של המערכת.
///
/// מיכלי APFS שהמערכת מציגה כ"כוננים" (הם בנויים מעל מחיצה של כונן אמיתי) אינם
/// מוצגים: הכונן שמתחתיהם כבר ברשימה, והתוכנה קוראת את APFS בעצמה.
/// </summary>
public static class MacDisks
{
    public static List<PhysicalDiskInfo> Enumerate()
    {
        var list = Plist(Run("diskutil", "list", "-plist"));
        if (list?.GetValueOrDefault("WholeDisks") is not List<object> whole) return new();

        var found = new List<(string Id, Dictionary<string, object> Info, string Bus)>();
        foreach (string id in whole.OfType<string>())
        {
            var info = Plist(Run("diskutil", "info", "-plist", id));
            if (info is null || info.ContainsKey("APFSPhysicalStores")) continue;
            string bus = info.GetValueOrDefault("BusProtocol") as string ?? "";
            if (info.GetValueOrDefault("VirtualOrPhysical") as string == "Virtual" && bus != "Disk Image") continue;
            found.Add((id, info, bus));
        }

        // חלון סיסמה אחד לכל הכוננים שדורשים הרשאה — ולא אחד לכל כונן.
        var locked = found.Select(f => "/dev/r" + f.Id).Where(RawDevice.MacDevice).ToList();
        if (locked.Count > 0)
            MacAuthOpen.Authorize(locked, L.T("שחזור מתקדם חינם מבקש לקרוא את הכוננים כדי למצוא בהם קבצים. " +
                                              "הקריאה בלבד — התוכנה אינה כותבת לכוננים."));

        return found.Select(f => Describe(f.Id, f.Info, f.Bus)).ToList();
    }

    /// <summary>
    /// על איזה כונן נמצאת תיקייה (לבדיקה שלא משחזרים אל הכונן שממנו משחזרים). מחיצה
    /// בתוך מיכל APFS שייכת לכונן שמתחת למיכל. -1 — לא ידוע.
    /// </summary>
    public static int DiskOfPath(string path)
    {
        var info = Plist(Run("diskutil", "info", "-plist", Path.GetFullPath(path)));
        if (info is null) return -1;
        string? whole = info.GetValueOrDefault("ParentWholeDisk") as string;
        if (whole is null) return -1;
        // מיכל APFS: הכונן האמיתי הוא זה שמחזיק את המחיצה שמתחתיו.
        var container = Plist(Run("diskutil", "info", "-plist", whole));
        if (container?.GetValueOrDefault("APFSPhysicalStores") is List<object> { Count: > 0 } stores
            && stores[0] is Dictionary<string, object> store && store.GetValueOrDefault("APFSPhysicalStore") is string partition
            && Plist(Run("diskutil", "info", "-plist", partition))?.GetValueOrDefault("ParentWholeDisk") is string physical)
            whole = physical;
        return DevicePaths.RegisterImage("/dev/r" + whole);
    }

    private static PhysicalDiskInfo Describe(string id, Dictionary<string, object> info, string bus)
    {
        string path = "/dev/r" + id;
        long size = Long(info, "TotalSize") ?? Long(info, "Size") ?? 0;
        int sector = (int)(Long(info, "DeviceBlockSize") ?? 512);
        bool removable = Bool(info, "RemovableMedia") || Bool(info, "Removable") || Bool(info, "Ejectable");
        bool ssd = Bool(info, "SolidState");
        bool image = bus == "Disk Image";

        var media = image ? MediaKind.Virtual
            : bus == "USB" ? (removable ? MediaKind.UsbFlash : ssd ? MediaKind.Ssd : MediaKind.HardDisk)
            : bus is "Secure Digital" or "SD" ? MediaKind.MemoryCard
            : bus.Contains("PCI", StringComparison.OrdinalIgnoreCase) || bus == "Apple Fabric" ? MediaKind.NvmeSsd
            : ssd ? MediaKind.Ssd : MediaKind.HardDisk;

        int number = DevicePaths.RegisterImage(path);
        var disk = new PhysicalDiskInfo
        {
            DiskNumber = number,
            Model = info.GetValueOrDefault("MediaName") as string is { Length: > 0 } name ? name : id,
            BusType = bus,
            SizeBytes = size,
            LogicalSectorSize = sector,
            PhysicalSectorSize = sector,
            Media = media,
            Trim = ssd ? TrimState.Enabled : TrimState.NotSupported,
            IsRemovable = removable,
            ImagePath = path,
            ImageNote = Bool(info, "Internal") && !image
                ? L.T("הכונן הפנימי של המק. במחשבי מק חדשים הוא מוצפן בחומרה, ומה שנמחק ממנו מתנקה מיד — " +
                      "ולכן כמעט אי אפשר לשחזר ממנו קבצים שנמחקו.")
                : null,
        };

        // הכונן עצמו נפתח רק עכשיו — ובמק זה מבקש את סיסמת המנהל (authopen), פעם אחת לכל כונן.
        using var device = RawDevice.TryOpen(path, sector);
        if (device is null)
            return Copy(disk, accessible: false,
                problem: L.T("אין הרשאה לקרוא את הכונן. בפתיחה הבאה של הרשימה תתבקש סיסמת המנהל של המק."));

        var (scheme, partitions) = ImageDisk.ReadLayout(device, number, size, null);
        return Copy(disk, accessible: true, scheme: scheme, partitions: partitions);
    }

    private static PhysicalDiskInfo Copy(PhysicalDiskInfo d, bool accessible, string? problem = null,
        PartitionScheme scheme = default, List<PartitionInfo>? partitions = null) => new()
    {
        DiskNumber = d.DiskNumber, Model = d.Model, BusType = d.BusType, SizeBytes = d.SizeBytes,
        LogicalSectorSize = d.LogicalSectorSize, PhysicalSectorSize = d.PhysicalSectorSize, Media = d.Media,
        Trim = d.Trim, IsRemovable = d.IsRemovable, ImagePath = d.ImagePath, ImageNote = d.ImageNote,
        RawAccessible = accessible, Problem = problem, Scheme = scheme, Partitions = partitions ?? new(),
    };

    private static long? Long(Dictionary<string, object> d, string key) => d.GetValueOrDefault(key) as long?;
    private static bool Bool(Dictionary<string, object> d, string key) => d.GetValueOrDefault(key) is true;

    private static string Run(string tool, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return output;
        }
        catch (Exception) { return ""; }
    }

    /// <summary>קריאת plist (XML) — מילון, מערך, מחרוזת, מספר ואמת/שקר.</summary>
    internal static Dictionary<string, object>? Plist(string xml)
    {
        try
        {
            var root = XDocument.Parse(xml).Root?.Elements().FirstOrDefault();
            return root is null ? null : Value(root) as Dictionary<string, object>;
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private static object Value(XElement e) => e.Name.LocalName switch
    {
        "dict" => e.Elements().Chunk(2).Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Value, p => Value(p[1])),
        "array" => e.Elements().Select(Value).ToList(),
        "integer" => long.TryParse(e.Value, out long v) ? v : 0L,
        "true" => true,
        "false" => false,
        _ => e.Value,
    };
}
