using System.Buffers.Binary;
using System.Text;
using RAF.Core.Model;

namespace RAF.Core.FileSystems;

/// <summary>
/// שמות מקוריים לקבצים שנמחקו דרך סל המחזור.
///
/// קובץ שנשלח לסל מקבל שם מקרי — ‎$RX3F9K2.jpg — ולצדו נכתב קובץ קטן בשם
/// ‎$IX3F9K2.jpg, ובו השם והנתיב המקוריים וזמן המחיקה. כשמרוקנים את הסל שניהם
/// נמחקים, ובלי הקובץ הקטן המשתמש מקבל רשימה של שמות חסרי משמעות. ב-NTFS הוא
/// שמור בתוך רשומת הקובץ עצמה (544 בתים לכל היותר), ולכן כמעט תמיד שורד.
///
/// תיקייה שנמחקה לסל נקראת ‎$R… בעצמה, והקבצים שבתוכה שומרים את שמותיהם —
/// לכן גם הנתיב שלהם מוחזר.
/// </summary>
internal static class RecycleBinNames
{
    /// <summary>מספר הקבצים הקטנים שנקראים לכל היותר — סל ענק אינו יעכב את הסריקה.</summary>
    private const int MaxInfoFiles = 20_000;

    /// <summary>
    /// החלפת השמות. read מחזיר את תחילת התוכן של קובץ (הקובץ הקטן תמיד קטן מ-4KB).
    /// מחזיר את מספר הקבצים והתיקיות שקיבלו את שמם בחזרה.
    /// </summary>
    internal static int Apply(List<RecoveredFile> files, Func<RecoveredFile, byte[]> read)
    {
        // ‎$I לפי המפתח "תיקייה\$R<סיום>" — זה בדיוק מה שיזהה את ה-$R שלו.
        var info = new Dictionary<string, (string Original, DateTime? DeletedAt, DateTime Stamp)>(
            StringComparer.OrdinalIgnoreCase);

        int examined = 0;
        foreach (var f in files)
        {
            if (f.IsDirectory || !IsMarked(f.Name, 'I') || !InRecycleBin(f.Path)) continue;
            if (f.Size is < 24 or > 4096 || ++examined > MaxInfoFiles) continue;

            byte[] data;
            try { data = read(f); } catch { continue; }
            if (Parse(data) is not { } parsed) continue;

            // אותו שם יכול להופיע בכמה רשומות מחוקות, מסבבים שונים של הסל: המאוחרת קובעת.
            string key = f.Path + "\\$R" + f.Name[2..];
            var stamp = f.Modified ?? f.Created ?? DateTime.MinValue;
            if (info.TryGetValue(key, out var existing) && existing.Stamp > stamp) continue;
            info[key] = (parsed.Path, parsed.DeletedAt, stamp);
        }

        if (info.Count == 0) return 0;

        int renamed = 0;
        var folders = new List<(string From, string To, DateTime? DeletedAt)>();

        foreach (var f in files)
        {
            if (!IsMarked(f.Name, 'R') || !InRecycleBin(f.Path)) continue;

            string key = f.Path + "\\" + f.Name;
            if (!info.TryGetValue(key, out var original)) continue;

            string relative = WithoutDrive(original.Original);
            int slash = relative.LastIndexOf('\\');
            string name = slash >= 0 ? relative[(slash + 1)..] : relative;
            if (name.Length == 0) continue;

            f.Name = name;
            f.Path = slash >= 0 ? relative[..slash] : "";
            f.RecycledAt = original.DeletedAt;
            renamed++;

            if (f.IsDirectory) folders.Add((key, relative, original.DeletedAt));
        }

        // הקבצים שבתוך תיקייה שנמחקה לסל — הנתיב שלהם עובר לנתיב המקורי.
        if (folders.Count > 0)
        {
            foreach (var f in files)
            {
                foreach (var (from, to, deletedAt) in folders)
                {
                    if (!f.Path.Equals(from, StringComparison.OrdinalIgnoreCase) &&
                        !f.Path.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase)) continue;

                    f.Path = to + f.Path[from.Length..];
                    f.RecycledAt ??= deletedAt;
                    break;
                }
            }
        }

        return renamed;
    }

    /// <summary>השם והנתיב המקוריים וזמן המחיקה מתוך ‎$I. null כשהתוכן אינו במבנה הזה.</summary>
    internal static (string Path, DateTime? DeletedAt)? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24) return null;

        long version = BinaryPrimitives.ReadInt64LittleEndian(data);
        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(data[16..]);

        ReadOnlySpan<byte> path;
        if (version == 1)
        {
            // Vista עד Windows 8: נתיב באורך קבוע של 260 תווים.
            path = data[24..Math.Min(data.Length, 24 + 520)];
        }
        else if (version == 2)
        {
            // Windows 10 ואילך: אורך הנתיב בתווים (כולל האפס שבסוף), ואחריו הנתיב.
            if (data.Length < 28) return null;
            int chars = BinaryPrimitives.ReadInt32LittleEndian(data[24..]);
            if (chars is <= 1 or > 32_768 || 28 + chars * 2 > data.Length) return null;
            path = data.Slice(28, chars * 2);
        }
        else
        {
            return null;
        }

        string text = Encoding.Unicode.GetString(path);
        int end = text.IndexOf('\0');
        if (end >= 0) text = text[..end];

        // נתיב אמיתי: "X:\..." או נתיב רשת. כל דבר אחר — הבתים אינם באמת ‎$I.
        bool looksReal = text.Length >= 4 && ((char.IsLetter(text[0]) && text[1] == ':' && text[2] == '\\')
                                              || text.StartsWith(@"\\", StringComparison.Ordinal));
        if (!looksReal || text.Any(c => c < ' ')) return null;

        DateTime? deletedAt = null;
        if (fileTime > 0)
        {
            try { deletedAt = DateTime.FromFileTime(fileTime); } catch { /* זמן לא תקין */ }
        }

        return (text, deletedAt);
    }

    /// <summary>נתיב יחסי לשורש המחיצה, כמו שאר הנתיבים בתוצאות.</summary>
    private static string WithoutDrive(string path)
    {
        if (path.Length >= 3 && path[1] == ':' && path[2] == '\\') return path[3..];
        return path.TrimStart('\\');
    }

    /// <summary>‎$R או ‎$I ואחריהם שם, כמו שהסל כותב.</summary>
    private static bool IsMarked(string name, char kind)
        => name.Length > 2 && name[0] == '$' && char.ToUpperInvariant(name[1]) == kind;

    /// <summary>הקובץ נמצא בתיקיית הסל — ב-NTFS ‎$Recycle.Bin, ב-FAT ‎$RECYCLE.BIN.</summary>
    private static bool InRecycleBin(string path)
        => path.StartsWith("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
           || path.Contains("\\$Recycle.Bin", StringComparison.OrdinalIgnoreCase);
}
