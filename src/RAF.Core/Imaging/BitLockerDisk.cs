using RAF.Core.Crypto;
using RAF.Core.Disks;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Imaging;

/// <summary>
/// פתיחת מחיצת BitLocker נעולה במפתח השחזור או בסיסמה — בלי Windows. כך נפתח
/// גם כונן ש-Windows לא מצליח לפתוח: מחיצה שנמחקה מהטבלה, כונן שמערכת הקבצים
/// שבתוכו ניזוקה, או תמונת דיסק. המחיצה המפוענחת מופיעה ברשימה ככונן נוסף.
/// </summary>
public static class BitLockerDisk
{
    /// <summary>מה ידוע על המחיצה לפני הפתיחה.</summary>
    /// <param name="Protectors">איך אפשר לפתוח אותה, במילים.</param>
    /// <param name="TypedKey">יש לה מפתח שחזור או סיסמה — משהו שאפשר להקליד.</param>
    /// <param name="Suspended">ההגנה מושהית: נפתחת בלי מפתח.</param>
    /// <param name="Problem">אזור הניהול לא נקרא, והסבר למה. null — נקרא.</param>
    public sealed record Info(List<string> Protectors, bool TypedKey, bool Suspended, string? Problem);

    public static Info Inspect(int disk, long offset, long size, int sectorSize)
    {
        try
        {
            using var reader = Open(disk, offset, size, sectorSize);
            var metadata = BitLockerMetadata.Read(reader.ReadBlock, size);
            var kinds = metadata.Protectors.Select(p => p.Kind).Distinct().ToList();
            return new Info(
                kinds.Select(BitLockerMetadata.Describe).ToList(),
                kinds.Any(k => k is BitLockerMetadata.ProtectorKind.RecoveryPassword or BitLockerMetadata.ProtectorKind.Password),
                metadata.HasClearKey,
                null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new Info(new List<string>(), false, false, ex.Message);
        }
    }

    /// <summary>
    /// פתיחה במפתח. מפתח שגוי — חריגה עם הסבר; המחיצה נפתחת רק כשהחותמת
    /// הקריפטוגרפית של המפתח אימתה אותו, ולכן לעולם לא "תיפתח" לתוכן אקראי.
    /// </summary>
    public static PhysicalDiskInfo Unlock(int disk, long offset, long size, int sectorSize, string key, string title)
    {
        BitLockerVolume volume;
        using (var reader = Open(disk, offset, size, sectorSize))
        {
            var metadata = BitLockerMetadata.Read(reader.ReadBlock, size);
            var cipher = metadata.Unlock(key.Trim())
                ?? throw new InvalidOperationException(string.IsNullOrWhiteSpace(key)
                    ? L.T("הקלידו את מפתח השחזור או את הסיסמה של הכונן.")
                    : BitLockerMetadata.RecoveryKey(key) is null && key.Count(char.IsAsciiDigit) >= 40
                    ? L.T("מפתח השחזור לא הוקלד נכון: יש בו 8 קבוצות של 6 ספרות, וכל קבוצה מתחלקת ב-11. בדקו שוב את הספרות.")
                    : L.T("המפתח או הסיסמה אינם נכונים לכונן הזה. בדקו שהמפתח שייך לכונן הזה — לכל כונן מוצפן מפתח שחזור משלו."));

            volume = BitLockerVolume.Open(metadata, cipher, size, (at, buffer) => reader.Read(at, buffer));
        }

        int number = DevicePaths.RegisterDecrypted(new DevicePaths.DecryptedSource(disk, offset, volume));
        var kind = volume.Inner.Kind is FileSystemKind.Unknown ? FileSystemKind.Raw : volume.Inner.Kind;

        return new PhysicalDiskInfo
        {
            DiskNumber = number,
            Model = L.T("{0} — BitLocker מפוענח", title),
            BusType = "פענוח בתוכנה",   // לא לתרגום: תווית, הממשק מתרגם
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
                    Label = volume.Inner.Label,
                    TypeName = FileSystemIdentifier.DisplayName(kind),
                },
            },
            ImagePath = DevicePaths.ImagePathOf(number),
            ImageNote = (kind == FileSystemKind.Raw
                    ? L.T("המפתח נכון והכונן פוענח, אבל תחילת המחיצה שבתוכו פגומה. סריקה מתקדמת תמצא את הקבצים לפי סוג. ")
                    : "") +
                L.T("התוכנה מפענחת את הכונן בעצמה ({0}), בלי Windows. הקריאה בלבד — שום דבר לא נכתב אליו.", volume.Cipher.Name),
        };
    }

    private static VolumeReader Open(int disk, long offset, long size, int sectorSize)
        => VolumeReader.TryOpen(disk, offset, size, sectorSize, sequential: false, applyOverlay: false)
           ?? throw new IOException(RawDevice.OpenFailure(L.T("הכונן")));
}
