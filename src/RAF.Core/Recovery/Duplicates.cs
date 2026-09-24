using System.Security.Cryptography;
using RAF.Core.Model;

namespace RAF.Core.Recovery;

/// <summary>
/// איתור קבצים כפולים בתוצאות סריקה: אותו קובץ שנמצא פעמיים (למשל גם כרשומה
/// פעילה וגם כיתומה), או עותקים שלו בתיקיות אחרות. מכל קבוצה נשאר העותק הטוב
/// ביותר, והשאר מסומנים כמוסתרים — כך השחזור אינו כותב את אותו תוכן כמה פעמים.
/// </summary>
public static class Duplicates
{
    /// <summary>כמה בתים מתחילת הקובץ נקראים להשוואה. יחד עם הגודל המדויק זה מספיק.</summary>
    public const int HeadBytes = 64 * 1024;

    /// <summary>
    /// מחזיר לכל קובץ מוסתר את מזהה העותק שנשאר. רק קבצים ניתנים לשחזור, בגודל
    /// גדול מאפס. קובץ נקרא רק אם יש לו לפחות קובץ אחר באותו גודל בדיוק — ברוב
    /// הסריקות אלה מעטים, ולכן גם ברשימות ענק החיפוש קצר.
    /// </summary>
    /// <param name="readHead">תחילת התוכן, או null אם לא ניתן לקרוא (כונן לא מחובר).</param>
    public static Dictionary<long, long> Find(
        IEnumerable<RecoveredFile> files, Func<RecoveredFile, byte[]?> readHead, CancellationToken token = default)
    {
        var hidden = new Dictionary<long, long>();

        var bySize = files
            .Where(f => !f.IsDirectory && f.Size > 0 && f.IsWorthRecovering)
            .GroupBy(f => f.Size)
            .Where(g => g.Skip(1).Any());

        foreach (var sameSize in bySize)
        {
            token.ThrowIfCancellationRequested();

            // אותו מקום על הדיסק — בוודאות אותו תוכן, בלי לקרוא. אחרת: לפי תחילת התוכן.
            var groups = sameSize
                .GroupBy(f => Location(f) ?? Fingerprint(f, readHead))
                .Where(g => g.Key is not null);

            // שני שלבים: קבוצות לפי מיקום עשויות להיות זהות בתוכן לקבוצות אחרות.
            var byContent = new Dictionary<string, List<RecoveredFile>>();
            foreach (var g in groups)
            {
                var list = g.ToList();
                string key = g.Key!.StartsWith("@", StringComparison.Ordinal)
                    ? Fingerprint(list[0], readHead) ?? g.Key
                    : g.Key;
                if (!byContent.TryGetValue(key, out var all)) byContent[key] = all = new List<RecoveredFile>();
                all.AddRange(list);
            }

            foreach (var copies in byContent.Values.Where(c => c.Count > 1))
            {
                var keep = copies.OrderBy(Rank).First();
                foreach (var f in copies)
                    if (f.Id != keep.Id) hidden[f.Id] = keep.Id;
            }
        }

        return hidden;
    }

    /// <summary>מיקום התוכן על הדיסק, לקובץ שאינו שמור בתוך הרשומה עצמה.</summary>
    private static string? Location(RecoveredFile f)
        => f.ResidentData is null && f.Extents.FirstOrDefault(e => !e.IsSparse) is { ClusterCount: > 0 } e
            ? $"@{e.StartCluster}"
            : null;

    private static string? Fingerprint(RecoveredFile f, Func<RecoveredFile, byte[]?> readHead)
    {
        byte[]? head = f.ResidentData ?? readHead(f);
        if (head is null || head.Length == 0) return null;
        int length = (int)Math.Min(head.Length, Math.Min(f.Size, HeadBytes));
        return Convert.ToHexString(SHA256.HashData(head.AsSpan(0, length)));
    }

    /// <summary>
    /// איזה עותק נשאר: האיכות הטובה ביותר, אחר כך עם שם ונתיב מקוריים (לא מסריקה
    /// מתקדמת), שם מלא, קובץ קיים לפני מחוק — ובשוויון, זה שנמצא ראשון.
    /// </summary>
    private static (RecoveryQuality, bool, bool, bool, long) Rank(RecoveredFile f)
        => (f.Quality, f.Source == DiscoverySource.Carving, f.NameIsPartial, f.IsDeleted, f.Id);
}
