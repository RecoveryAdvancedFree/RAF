using System.Collections.Concurrent;

namespace RAF.Core.Native;

/// <summary>
/// מיפוי ממספר דיסק לנתיב שממנו הוא נקרא.
///
/// דיסק פיזי נקרא דרך <c>\\.\PhysicalDriveN</c>. תמונת דיסק שנפתחה בתוכנה
/// מקבלת מספר וירטואלי (1000 ומעלה) שממופה לקובץ התמונה — וכך כל המנועים,
/// מסריקה ועד שחזור ותיקון, עובדים מולה בדיוק כמו מול כונן, בלי לדעת על כך.
/// </summary>
public static class DevicePaths
{
    /// <summary>המספר הראשון שמוקצה לתמונה. גבוה בהרבה ממספר הדיסקים האפשרי.</summary>
    public const int FirstImageNumber = 1000;

    private static readonly ConcurrentDictionary<int, string> Images = new();
    private static int _next = FirstImageNumber;

    /// <summary>רישום קובץ תמונה. קובץ שכבר נרשם מקבל את אותו מספר.</summary>
    public static int RegisterImage(string imagePath)
    {
        string full = IsDevicePath(imagePath) || IsDecryptedPath(imagePath) || IsRaidPath(imagePath) ? imagePath : Path.GetFullPath(imagePath);

        foreach (var pair in Images)
            if (string.Equals(pair.Value, full, StringComparison.OrdinalIgnoreCase))
                return pair.Key;

        int number = Interlocked.Increment(ref _next) - 1;
        Images[number] = full;
        return number;
    }

    public static void UnregisterImage(int number)
    {
        if (Images.TryRemove(number, out var path))
        {
            Decrypted.TryRemove(path, out _);
            Raids.TryRemove(path, out _);
        }
    }

    /// <summary>
    /// מערך RAID שהתוכנה מרכיבה: הגאומטריה, ולכל מקום במערך — הדיסק וההיסט שבו מתחיל
    /// הכונן (null — הכונן חסר).
    /// </summary>
    internal sealed record RaidSource(Raid.IComposedVolume Volume, (int Disk, long Offset)?[] Members);

    private static readonly ConcurrentDictionary<string, RaidSource> Raids = new(StringComparer.OrdinalIgnoreCase);
    private const string RaidPrefix = "raid:";

    /// <summary>רישום מערך RAID. מערך שכבר נרשם (לפי המזהה שלו) מקבל את אותו מספר.</summary>
    internal static int RegisterRaid(string id, RaidSource source)
    {
        string path = RaidPrefix + id;
        Raids[path] = source;
        return RegisterImage(path);
    }

    /// <summary>
    /// הכוננים המורכבים (מערכים, אזורים במאגר) שקוראים מהכונן הזה — ישירות או דרך כונן מורכב
    /// אחר. כשהוא נסגר, גם הם צריכים להיסגר: אין להם יותר ממה לקרוא.
    /// </summary>
    public static List<int> DependentsOf(int number)
    {
        var result = new List<int>();
        var pending = new Queue<int>(new[] { number });
        while (pending.Count > 0)
        {
            int current = pending.Dequeue();
            foreach (var (path, source) in Raids)
            {
                if (!source.Members.Any(m => m?.Disk == current)) continue;
                int dependent = Images.FirstOrDefault(i => string.Equals(i.Value, path, StringComparison.OrdinalIgnoreCase)).Key;
                if (dependent != 0 && !result.Contains(dependent)) { result.Add(dependent); pending.Enqueue(dependent); }
            }
        }
        return result;
    }

    /// <summary>הנתיב שבו נרשם מערך לפי המזהה שלו.</summary>
    public static string RaidPathOf(string id) => RaidPrefix + id;

    internal static RaidSource? RaidSourceOf(string path)
        => path.StartsWith(RaidPrefix, StringComparison.Ordinal) && Raids.TryGetValue(path, out var s) ? s : null;

    /// <summary>נתיב של מערך RAID שהתוכנה מרכיבה מכמה כוננים — לא קובץ ולא התקן, ולקריאה בלבד.</summary>
    public static bool IsRaidPath(string path) => path.StartsWith(RaidPrefix, StringComparison.Ordinal);

    /// <summary>מחיצת BitLocker שהתוכנה פותחת בעצמה: על איזה דיסק היא, איפה, ואיך מפענחים.</summary>
    internal sealed record DecryptedSource(int Disk, long Offset, Crypto.BitLockerVolume Volume);

    private static readonly ConcurrentDictionary<string, DecryptedSource> Decrypted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// רישום מחיצת BitLocker שנפתחה במפתח. היא מקבלת מספר כמו תמונה, והנתיב שלה
    /// אינו קובץ ואינו התקן — <see cref="RawDevice"/> מזהה אותו, קורא מהדיסק שמתחתיו ומפענח.
    /// </summary>
    internal static int RegisterDecrypted(DecryptedSource source)
    {
        string path = $"{DecryptedPrefix}{source.Disk}@{source.Offset}";
        Decrypted[path] = source;
        return RegisterImage(path);
    }

    private const string DecryptedPrefix = "bitlocker:";

    internal static DecryptedSource? DecryptedSourceOf(string path)
        => path.StartsWith(DecryptedPrefix, StringComparison.Ordinal) && Decrypted.TryGetValue(path, out var s) ? s : null;

    /// <summary>נתיב של מחיצת BitLocker שהתוכנה מפענחת בעצמה.</summary>
    public static bool IsDecryptedPath(string path) => path.StartsWith(DecryptedPrefix, StringComparison.Ordinal);

    /// <summary>
    /// מחיצה מוצפנת שנקראת מפוענחת — דרך Windows או בפענוח של התוכנה. זה הכונן
    /// עצמו, לא עותק שלו: אסור לכתוב אליו, והוא אינו "תמונה".
    /// </summary>
    public static bool IsDecryptedVolume(string path) => IsVolumePath(path) || IsDecryptedPath(path);

    public static bool IsImage(int number) => number >= FirstImageNumber;

    /// <summary>נתיב קובץ התמונה, או null אם המספר אינו תמונה רשומה.</summary>
    public static string? ImagePathOf(int number)
        => Images.TryGetValue(number, out var path) ? path : null;

    /// <summary>
    /// הנתיב שממנו נקרא הדיסק. למספר וירטואלי שאינו רשום מוחזר null —
    /// לעולם לא ננחש נתיב, כי ניחוש שגוי בכתיבה היה פוגע בכונן אחר.
    /// </summary>
    public static string? PathOf(int number)
        => IsImage(number) ? ImagePathOf(number) : $@"\\.\PhysicalDrive{number}";

    /// <summary>האם הנתיב הוא התקן (ולא קובץ רגיל).</summary>
    internal static bool IsDevicePath(string path)
        => path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith("/dev/", StringComparison.Ordinal);

    /// <summary>נתיב של מחיצה מחוברת לפי האות שלה (\\.\E:) — נקראת דרך Windows, אחרי פענוח BitLocker.</summary>
    public static bool IsVolumePath(string path)
        => path.Length == 6 && IsDevicePath(path) && char.IsAsciiLetter(path[4]) && path[5] == ':';

    /// <summary>הנתיב שדרכו Windows מציג מחיצה מחוברת, לפי אות הכונן ("E:" או "E:\").</summary>
    public static string VolumePathOf(string letter) => $@"\\.\{char.ToUpperInvariant(letter.TrimStart()[0])}:";
}
