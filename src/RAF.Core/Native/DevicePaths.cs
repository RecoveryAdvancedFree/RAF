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
        string full = IsDevicePath(imagePath) ? imagePath : Path.GetFullPath(imagePath);

        foreach (var pair in Images)
            if (string.Equals(pair.Value, full, StringComparison.OrdinalIgnoreCase))
                return pair.Key;

        int number = Interlocked.Increment(ref _next) - 1;
        Images[number] = full;
        return number;
    }

    public static void UnregisterImage(int number) => Images.TryRemove(number, out _);

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
    internal static bool IsDevicePath(string path) => path.StartsWith(@"\\.\", StringComparison.Ordinal);

    /// <summary>נתיב של מחיצה מחוברת לפי האות שלה (\\.\E:) — נקראת דרך Windows, אחרי פענוח BitLocker.</summary>
    public static bool IsVolumePath(string path)
        => path.Length == 6 && IsDevicePath(path) && char.IsAsciiLetter(path[4]) && path[5] == ':';

    /// <summary>הנתיב שדרכו Windows מציג מחיצה מחוברת, לפי אות הכונן ("E:" או "E:\").</summary>
    public static string VolumePathOf(string letter) => $@"\\.\{char.ToUpperInvariant(letter.TrimStart()[0])}:";
}
