using System.Security.Cryptography;
using System.Text;
using RAF.Core.Disks;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Repair;

/// <summary>איזו פעולה כתבה לכונן — ומה הביטול שלה יחזיר.</summary>
public enum UndoKind
{
    /// <summary>תיקון מחיצה: מגזר האתחול הוחלף בעותק הגיבוי שלו.</summary>
    BootSector = 0,

    /// <summary>החזרת מחיצה לטבלת המחיצות.</summary>
    PartitionTable = 1,
}

/// <summary>אזור אחד בכונן: מה היה בו לפני הכתיבה, ומה נכתב אליו.</summary>
public sealed record UndoRegion(long Offset, byte[] Before, byte[] After);

/// <summary>
/// קובץ ביטול: כל מה שצריך כדי להחזיר את הכונן למצב שלפני כתיבה של התוכנה.
///
/// הקובץ שומר לא רק את מה שהיה, אלא גם את מה שנכתב ואת זהות הכונן. כך ביטול
/// שמבוצע ימים אחרי התיקון יודע למצוא את הכונן גם כשמספר הדיסק שלו השתנה
/// (כונן חיצוני שחובר ליציאה אחרת), ומסרב לכתוב כשהכונן כבר לא במצב
/// שהתיקון השאיר — אחרת הביטול עצמו היה דורס נתונים חדשים.
/// </summary>
public sealed class UndoFile
{
    private const string Signature = "RAF-UNDO-2";

    /// <summary>קבצים מגרסאות קודמות: יש בהם רק "לפני", בלי זהות הכונן ובלי "אחרי".</summary>
    private static readonly string[] LegacySignatures = { "RAF-UNDO-TABLE-1", "RAF-UNDO-1" };

    public UndoKind Kind { get; init; }

    /// <summary>מספר הדיסק בזמן הכתיבה. תקף לביטול אוטומטי מיד אחריה, לא לביטול מאוחר.</summary>
    public int DiskNumber { get; init; }

    public long DiskSize { get; init; }

    /// <summary>המספר הסידורי של הכונן, או הנתיב של קובץ התמונה. ריק כשאין אף אחד מהם.</summary>
    public string DiskIdentity { get; init; } = "";

    public int SectorSize { get; init; } = 512;

    public DateTime Created { get; init; }

    public List<UndoRegion> Regions { get; init; } = new();

    /// <summary>קובץ מגרסה קודמת — אפשר לזהות אותו, אבל לא לבטל ממנו בבטחה.</summary>
    public bool Legacy { get; init; }

    /// <summary>הזהות שנשמרת בקובץ: המספר הסידורי, או הנתיב של קובץ תמונה.</summary>
    internal static string IdentityOf(PhysicalDiskInfo disk)
        => disk.ImagePath ?? disk.SerialNumber.Trim();

    internal static UndoFile For(PhysicalDiskInfo disk, UndoKind kind, List<UndoRegion> regions) => new()
    {
        Kind = kind,
        DiskNumber = disk.DiskNumber,
        DiskSize = disk.SizeBytes,
        DiskIdentity = IdentityOf(disk),
        SectorSize = disk.LogicalSectorSize,
        Created = DateTime.Now,
        Regions = regions,
    };

    // ============================================================ שמירה וטעינה

    /// <summary>שמירה לקובץ חדש, עם טביעת אצבע בסוף — קובץ שנפגם לא ישמש לכתיבה לכונן.</summary>
    internal void Save(string path)
    {
        using var buffer = new MemoryStream();
        using (var w = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes(Signature));
            w.Write((byte)Kind);
            w.Write(DiskNumber);
            w.Write(DiskSize);
            w.Write(DiskIdentity);
            w.Write(SectorSize);
            w.Write(Created.ToUniversalTime().ToFileTimeUtc());
            w.Write(Regions.Count);
            foreach (var region in Regions)
            {
                w.Write(region.Offset);
                w.Write(region.Before.Length);
                w.Write(region.Before);
                w.Write(region.After);
            }
        }

        byte[] body = buffer.ToArray();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(body);
        stream.Write(SHA256.HashData(body));
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// טעינת קובץ ביטול. null — זה לא קובץ ביטול של התוכנה, או שהוא נפגם.
    /// קובץ מגרסה קודמת נטען עם Legacy, בלי אזורים.
    /// </summary>
    public static UndoFile? Load(string path)
    {
        byte[] file;
        try { file = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        foreach (string legacy in LegacySignatures)
            if (file.Length >= legacy.Length && Encoding.ASCII.GetString(file, 0, legacy.Length) == legacy)
                return new UndoFile
                {
                    Legacy = true,
                    Kind = legacy.Contains("TABLE") ? UndoKind.PartitionTable : UndoKind.BootSector,
                    Created = File.GetCreationTime(path),
                };

        if (file.Length < Signature.Length + 32 ||
            Encoding.ASCII.GetString(file, 0, Signature.Length) != Signature) return null;

        var body = file.AsSpan(0, file.Length - 32);
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(file.AsSpan(file.Length - 32))) return null;

        try
        {
            using var r = new BinaryReader(new MemoryStream(body.ToArray()), Encoding.UTF8);
            r.ReadBytes(Signature.Length);

            var kind = (UndoKind)r.ReadByte();
            int diskNumber = r.ReadInt32();
            long diskSize = r.ReadInt64();
            string identity = r.ReadString();
            int sectorSize = r.ReadInt32();
            var created = DateTime.FromFileTimeUtc(r.ReadInt64()).ToLocalTime();

            int count = r.ReadInt32();
            if (count is < 1 or > 64) return null;

            var regions = new List<UndoRegion>();
            for (int i = 0; i < count; i++)
            {
                long offset = r.ReadInt64();
                int length = r.ReadInt32();
                if (offset < 0 || length is <= 0 or > 1024 * 1024) return null;
                regions.Add(new UndoRegion(offset, r.ReadBytes(length), r.ReadBytes(length)));
            }

            return new UndoFile
            {
                Kind = kind, DiskNumber = diskNumber, DiskSize = diskSize, DiskIdentity = identity,
                SectorSize = sectorSize, Created = created, Regions = regions,
            };
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    // ============================================================ כתיבה

    /// <summary>
    /// החזרת "לפני" לכונן שמספרו נתון. לביטול אוטומטי מיד אחרי הכתיבה — אז המספר
    /// עוד תקף. ביטול מאוחר עובר דרך UndoCheck, שמוודא קודם שזה הכונן הנכון.
    /// </summary>
    internal bool WriteBefore(int diskNumber)
    {
        string? target = DevicePaths.PathOf(diskNumber);
        if (target is null) return false;

        using var writer = RawWriter.TryOpen(target, SectorSize);
        if (writer is null) return false;

        foreach (var region in Regions)
            if (!writer.Write(region.Offset, region.Before)) return false;

        return true;
    }
}

/// <summary>מה ביטול מאוחר ימצא בכונן.</summary>
public enum UndoState
{
    /// <summary>הכונן בדיוק במצב שהכתיבה השאירה — אפשר לבטל.</summary>
    Ready = 0,

    /// <summary>הכונן כבר במצב שלפני הכתיבה — אין מה לבטל.</summary>
    AlreadyUndone,

    /// <summary>הכונן השתנה מאז — ביטול היה דורס את מה שנכתב אחר כך.</summary>
    Changed,

    /// <summary>הכונן שהקובץ שייך אליו לא מחובר.</summary>
    DiskMissing,

    /// <summary>אין דרך לדעת בוודאות לאיזה כונן הקובץ שייך.</summary>
    DiskAmbiguous,

    /// <summary>הקובץ אינו קובץ ביטול, נפגם, או מגרסה קודמת.</summary>
    Unusable,
}

/// <summary>תוצאת הבדיקה שלפני ביטול מאוחר.</summary>
public sealed class UndoCheck
{
    public UndoState State { get; init; }
    public string Message { get; init; } = "";
    public UndoFile? File { get; init; }
    public PhysicalDiskInfo? Disk { get; init; }
    public bool CanUndo => State == UndoState.Ready;
}

/// <summary>
/// ביטול מאוחר של תיקון מחיצה או של החזרת מחיצה לטבלה, מתוך קובץ הביטול.
///
/// כתיבה לכונן לפי קובץ היא מסוכנת בדיוק כמו התיקון עצמו, ולכן לפני כל כתיבה:
/// 1. הכונן מזוהה לפי המספר הסידורי והגודל — לא לפי מספר הדיסק, שמשתנה.
/// 2. התוכן הנוכחי חייב להיות זהה בדיוק למה שהתיקון כתב.
/// 3. אחרי הכתיבה התוכן נקרא בחזרה ומושווה ל"לפני". אם הכתיבה נכשלה באמצע,
///    מה שהתיקון כתב מוחזר, כך שהכונן לא נשאר בחצי הדרך.
/// </summary>
public static class UndoService
{
    public static UndoCheck Check(string path, IReadOnlyList<PhysicalDiskInfo> disks)
    {
        var file = UndoFile.Load(path);
        if (file is null)
            return Unusable("זה לא קובץ ביטול של התוכנה, או שהקובץ נפגם. לא נכתב דבר.");

        if (file.Legacy)
            return Unusable("זה קובץ ביטול מגרסה קודמת של התוכנה. אין בו את הזהות של הכונן ואת מה שנכתב אליו, " +
                            "ולכן אי אפשר לוודא שהביטול ייכתב לכונן הנכון. לא נכתב דבר.", file);

        var candidates = disks
            .Where(d => d.SizeBytes == file.DiskSize && !d.Unresponsive)
            .Where(d => file.DiskIdentity.Length > 0
                ? UndoFile.IdentityOf(d).Equals(file.DiskIdentity, StringComparison.OrdinalIgnoreCase)
                : d.DiskNumber == file.DiskNumber)
            .ToList();

        if (candidates.Count == 0)
            return new UndoCheck
            {
                State = UndoState.DiskMissing, File = file,
                Message = "הכונן שהתיקון נעשה בו לא מחובר עכשיו. חברו אותו, לחצו על רענון ונסו שוב.",
            };

        if (candidates.Count > 1)
            return new UndoCheck
            {
                State = UndoState.DiskAmbiguous, File = file,
                Message = "יותר מכונן אחד מתאים לקובץ הזה, ואין דרך לדעת בוודאות לאיזה מהם הוא שייך. " +
                          "נתקו את הכוננים האחרים ונסו שוב.",
            };

        var disk = candidates[0];
        var current = ReadCurrent(file, disk);
        if (current is null)
            return new UndoCheck
            {
                State = UndoState.Changed, File = file, Disk = disk,
                Message = "לא ניתן לקרוא מהכונן את האזור שהתיקון שינה, ולכן לא נכתב דבר.",
            };

        if (Same(current, file.Regions.Select(r => r.After)))
            return new UndoCheck
            {
                State = UndoState.Ready, File = file, Disk = disk,
                Message = "הכונן נמצא בדיוק במצב שהתיקון השאיר, ואפשר להחזיר אותו למצב שלפניו.",
            };

        if (Same(current, file.Regions.Select(r => r.Before)))
            return new UndoCheck
            {
                State = UndoState.AlreadyUndone, File = file, Disk = disk,
                Message = "הכונן כבר במצב שלפני התיקון — אין מה לבטל.",
            };

        return new UndoCheck
        {
            State = UndoState.Changed, File = file, Disk = disk,
            Message = "הכונן השתנה מאז התיקון (למשל פורמט, או תיקון נוסף). ביטול עכשיו היה דורס את מה שנכתב אחר כך, " +
                      "ולכן לא נכתב דבר.",
        };
    }

    /// <summary>ביצוע הביטול, אחרי בדיקה חוזרת מול הכונן.</summary>
    public static RepairResult Apply(string path, IReadOnlyList<PhysicalDiskInfo> disks)
    {
        var check = Check(path, disks);
        if (!check.CanUndo) return new RepairResult { Message = check.Message };

        var file = check.File!;
        var disk = check.Disk!;

        // מגזר אתחול של מחיצה מחוברת: Windows חוסם כתיבה אליו עד שהמחיצה ננעלת ומנותקת.
        var locks = new List<VolumeLock>();
        try
        {
            if (file.Kind == UndoKind.BootSector)
                foreach (var part in disk.Partitions.Where(p => file.Regions.Any(r =>
                             r.Offset >= p.OffsetBytes && r.Offset < p.OffsetBytes + p.SizeBytes)))
                    locks.Add(VolumeLock.Acquire(part.DriveLetter));

            bool written = file.WriteBefore(disk.DiskNumber);
            var after = ReadCurrent(file, disk);

            if (written && after is not null && Same(after, file.Regions.Select(r => r.Before)))
            {
                if (file.Kind == UndoKind.PartitionTable) PartitionTableWriter.RefreshLayout(disk.DiskNumber);
                return new RepairResult
                {
                    Succeeded = true,
                    Message = (file.Kind == UndoKind.PartitionTable
                                  ? "טבלת המחיצות הוחזרה למצב שלפני ההחזרה. "
                                  : "תחילת המחיצה הוחזרה למצב שלפני התיקון. ") +
                              "ייתכן שיהיה צורך לנתק ולחבר מחדש את הכונן כדי ש-Windows יזהה את השינוי.",
                };
            }

            // הכתיבה לא הושלמה: מחזירים את מה שהתיקון כתב, כדי שהכונן לא יישאר באמצע.
            bool back = WriteAfter(file, disk.DiskNumber);
            string status = locks.Select(l => l.Status).FirstOrDefault(s => s.Length > 0) ?? "";
            return new RepairResult
            {
                RolledBack = back,
                Message = back
                    ? $"הכתיבה לכונן נכשלה (שגיאת Windows {RawWriter.LastError}). {status} הכונן נשאר כמו שהתיקון השאיר אותו."
                    : "הכתיבה לכונן נכשלה באמצע, וגם החזרת המצב נכשלה. אל תכתבו לכונן — " +
                      "קובץ הביטול עדיין שמור, ואפשר לנסות שוב אחרי ניתוק וחיבור של הכונן.",
            };
        }
        finally
        {
            foreach (var l in locks) l.Dispose();
        }
    }

    private static bool WriteAfter(UndoFile file, int diskNumber)
    {
        string? target = DevicePaths.PathOf(diskNumber);
        if (target is null) return false;

        using var writer = RawWriter.TryOpen(target, file.SectorSize);
        if (writer is null) return false;

        foreach (var region in file.Regions)
            if (!writer.Write(region.Offset, region.After)) return false;
        return true;
    }

    /// <summary>התוכן הנוכחי של כל אזור בקובץ. null כשאזור כלשהו לא נקרא במלואו.</summary>
    private static List<byte[]>? ReadCurrent(UndoFile file, PhysicalDiskInfo disk)
    {
        using var reader = VolumeReader.TryOpen(
            disk.DiskNumber, 0, disk.SizeBytes, disk.LogicalSectorSize, sequential: false, applyOverlay: false);
        if (reader is null) return null;

        var result = new List<byte[]>();
        foreach (var region in file.Regions)
        {
            byte[] data = reader.ReadBlock(region.Offset, region.Before.Length);
            if (data.Length != region.Before.Length) return null;
            result.Add(data);
        }
        return result;
    }

    private static bool Same(List<byte[]> current, IEnumerable<byte[]> expected)
        => current.Zip(expected).All(p => p.First.AsSpan().SequenceEqual(p.Second));

    private static UndoCheck Unusable(string message, UndoFile? file = null)
        => new() { State = UndoState.Unusable, Message = message, File = file };
}
